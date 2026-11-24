// FontAudit.cs -- 图纸字体体检 / 打开前字体映射（2026-11-23 P34）
//
// 背景（Bro 2026-11-23）：异机安装后，客户图纸引用的 SHX（hztxt.shx / tssdeng.shx / CASS.SHX …）
// 本机没有时，AutoCAD 打开图纸会弹「一个或多个 SHX 文件缺失」模态对话框；如果该提示被
// 「始终执行我的当前选择」隐藏过，则静默用 FONTALT（默认 simplex.shx）替代 —— simplex 没有
// 汉字字形，屏幕上的中文会变成 ??? 甚至空白，用户会以为「内容丢了」。
//
// 事实（Autodesk 文档 + 本机实测）：
//   * 字体替换不修改图纸：TextStyleTable 里的字体名原样保留，保存回去也写不坏。
//   * 数据面不受影响：listTexts 等读的是数据库里的字符串，几何/坐标也不变。
//   * 受影响的是：屏幕显示、TEXT/MTEXT 的 GeometricExtents（bbox 按替代字体变化）、
//     以及缺失字体对话框会阻塞自动化（App.Idle 回调里 Open 卡住 → 20s 超时）。
//
// 做法：
//   1) 打开前用 Side Database 体检（不必激活文档）：对每个文本样式引用的 SHX 走
//      HostApplicationServices.FindFile —— 与 AutoCAD 自己是同一套搜索逻辑。
//   2) 缺字体时生成一份「用户原 acad.fmp + 本次缺字体映射」的映射文件（FONTMAP=1），
//      只在 Open 期间挂到 Preferences.Files.FontMappingFile，Open 完成后恢复原设置。
//      映射目标按角色挑：大字体的替大字体，纯中文样式的替中文字体，其余替 simplex.shx。
//   3) 打开后再体检一次（以活动数据库为准），把缺失清单 + 受影响文字数 + 是否含中文
//      一起返回，让面板 agent 能如实告知用户「哪些字体缺、显示会变、数据没丢」。
//
// 判据（可证伪）：
//   * 体检结果里每个 missing 条目都能用 STYLE 命令在本机复现（找不到该 SHX）。
//   * 打开前后 TextStyleTable 的字体名完全一致（替换不改图）。
//   * 返回的 affectedTextCount 与 listTexts 按样式统计的条数一致。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

internal static class FontAudit
{
  internal const string RolePrimary = "primary";
  internal const string RoleBigFont = "bigfont";

  private const int DefaultMaxEntities = 120_000;
  private const int HardMaxEntities = 400_000;

  // 替代字体优先级（都是 AutoCAD 自带；本机实测存在性由 ShxIndex 再过滤一遍）
  private static readonly string[] CjkBigFontOrder = { "gbcbig.shx", "chineset.shx", "bigfont.shx", "extfont2.shx", "extfont.shx" };
  private static readonly string[] CjkSoloOrder = { "chineset.shx", "gbcbig.shx", "bigfont.shx", "gbenor.shx" };
  private static readonly string[] LatinOrder = { "simplex.shx", "txt.shx", "romans.shx", "isocp.shx" };

  private static readonly ConcurrentDictionary<string, string?> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
  private static readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, AuditResult Result)> PreflightCache = new(StringComparer.OrdinalIgnoreCase);
  private static volatile List<string>? _shxIndex;
  private static volatile string? _fontMapPath;

  // -------------------------------------------------------------------------
  // 数据模型
  // -------------------------------------------------------------------------

  internal sealed class MissingFont
  {
    public string Style = "";
    public string Font = "";
    public string Role = RolePrimary;
    public string? SuggestedFallback;
    public int TextCount;
    public bool HasNonAscii;
    public bool StyleHasBigFont;
  }

  internal sealed class MissingTtf
  {
    public string Style = "";
    public string TypeFace = "";
    public int TextCount;
  }

  internal sealed class AuditResult
  {
    public bool Checked;
    public string? Error;
    public bool Deep;
    public int StyleCount;
    public int ShxStyleCount;
    public int TtfStyleCount;
    public int ScannedEntities;
    public bool ScanTruncated;
    public List<MissingFont> Missing = new();
    public List<MissingTtf> MissingTtf = new();
    public long ElapsedMs;

    public bool HasMissing => Missing.Count > 0 || MissingTtf.Count > 0;

    public Dictionary<string, object?> ToJson(string? drawing)
    {
      var d = new Dictionary<string, object?>
      {
        ["drawing"] = drawing,
        ["checked"] = Checked,
        ["deep"] = Deep,
        ["styleCount"] = StyleCount,
        ["shxStyleCount"] = ShxStyleCount,
        ["ttfStyleCount"] = TtfStyleCount,
        ["missingCount"] = Missing.Count,
        ["missingTtfCount"] = MissingTtf.Count,
        ["missing"] = Missing.Select(m => (object?)new Dictionary<string, object?>
        {
          ["style"] = m.Style,
          ["font"] = m.Font,
          ["role"] = m.Role,
          ["textCount"] = m.TextCount,
          ["hasNonAscii"] = m.HasNonAscii,
          ["suggestedFallback"] = m.SuggestedFallback,
        }).ToList(),
        ["missingTtf"] = MissingTtf.Select(m => (object?)new Dictionary<string, object?>
        {
          ["style"] = m.Style,
          ["typeFace"] = m.TypeFace,
          ["textCount"] = m.TextCount,
        }).ToList(),
        ["cjkAtRisk"] = Missing.Any(m => m.HasNonAscii),
        ["scannedEntities"] = Deep ? ScannedEntities : (int?)null,
        ["scanTruncated"] = ScanTruncated,
        ["elapsedMs"] = ElapsedMs,
        ["verdict"] = !Checked ? "NOT_CHECKED"
          : Missing.Count > 0 ? "MISSING_SHF_FONTS"
          : MissingTtf.Count > 0 ? "MISSING_TTF_FONTS"
          : "OK",
      };
      if (!string.IsNullOrEmpty(Error)) d["error"] = Error;
      d["hint"] = BuildHint();
      return d;
    }

    private string BuildHint()
    {
      if (!Checked) return "Font audit could not run (" + (Error ?? "unknown") + ").";
      if (!HasMissing) return "All SHX/TrueType fonts referenced by text styles resolve on this machine.";
      var sb = new StringBuilder();
      sb.Append("This machine is missing ");
      sb.Append(Missing.Count).Append(" SHX font file(s)");
      if (MissingTtf.Count > 0) sb.Append(" and ").Append(MissingTtf.Count).Append(" TrueType face(s)");
      sb.Append(". The drawing data is intact (text strings and geometry are stored in the DWG, not in the font); ");
      sb.Append("only on-screen appearance and text bounding boxes change, because AutoCAD substitutes other fonts. ");
      if (Missing.Any(m => m.HasNonAscii))
        sb.Append("Some affected styles contain non-ASCII (CJK) text, so Chinese may render as '?' or blanks on screen. ");
      sb.Append("Tell the user which font files are missing (see 'missing'), that the drawing itself is NOT damaged, ");
      sb.Append("and that installing those fonts (or opening the drawing on a machine that has them) restores exact display.");
      return sb.ToString();
    }
  }

  // -------------------------------------------------------------------------
  // auditFonts 方法
  // -------------------------------------------------------------------------

  public static Task<object?> AuditFontsAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetOptionalString(parameters, "path");
    var deep = PluginRuntime.GetOptionalBool(parameters, "deep") ?? true;
    var maxEntities = PluginRuntime.GetOptionalInt(parameters, "maxEntities") ?? DefaultMaxEntities;
    maxEntities = Math.Clamp(maxEntities, 1000, HardMaxEntities);

    if (!string.IsNullOrWhiteSpace(path))
    {
      var resolved = FileBoundary.ResolveImportPath(path!, ".dwg", ".dwt");
      var refresh = parameters != null && PluginRuntime.GetOptionalBool(parameters, "refresh") == true;
      // 2026-11-23 实测：在 TCP 线程上碰 AutoCAD 主线程 API（sysvar / FindFile / Side Database）会直接崩 CAD。
      //   所以体检跑在 ExecuteDocumentOpAsync + App.Idle（主线程、非命令上下文、不需要文档）。
      return CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
        await RunOnIdleAsync<object?>(() => (object?)AuditFile(resolved, deep, maxEntities, refresh), 20));
    }

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var sw = Stopwatch.StartNew();
      var result = AuditCore(database, transaction, deep, maxEntities, pathOverride: null);
      result.ElapsedMs = sw.ElapsedMilliseconds;
      var json = result.ToJson(database.Filename);
      json["openInCad"] = true;
      json["fontAlt"] = SafeFontAlt();
      json["fontAltResolves"] = ResolveFont(SafeFontAlt(), database) != null;
      json["fontMapFile"] = SafeFontMappingFile();
      json["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
      return (object?)json;
    });
  }

  /// <summary>体检一个未打开的 dwg（Side Database；不激活文档、不弹窗）。</summary>
  internal static Dictionary<string, object?> AuditFile(string path, bool deep, int maxEntities, bool refresh = false)
  {
    var sw = Stopwatch.StartNew();
    try
    {
      var info = new FileInfo(path);
      if (!info.Exists)
        throw new JsonRpcDispatchException("CIVIL3D.FILE_NOT_FOUND", "Drawing not found: " + path);

      if (!refresh && PreflightCache.TryGetValue(path, out var hit)
          && hit.Size == info.Length && hit.Mtime == info.LastWriteTimeUtc)
      {
        var cached = hit.Result.ToJson(path);
        cached["cached"] = true;
        cached["fontAlt"] = SafeFontAlt();
        cached["fontMapFile"] = SafeFontMappingFile();
        cached["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
        return cached;
      }

      AuditResult result;
      using (var side = new Database(false, true))
      {
        side.ReadDwgFile(path, System.IO.FileShare.ReadWrite, true, null);
        using var tr = side.TransactionManager.StartTransaction();
        result = AuditCore(side, tr, deep, maxEntities, pathOverride: path);
      }
      result.ElapsedMs = sw.ElapsedMilliseconds;
      PreflightCache[path] = (info.Length, info.LastWriteTimeUtc, result);

      var json = result.ToJson(path);
      json["fontAlt"] = SafeFontAlt();
      json["fontAltResolves"] = ResolveFont(SafeFontAlt(), null) != null;
      json["fontMapFile"] = SafeFontMappingFile();
      json["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
      return json;
    }
    catch (JsonRpcDispatchException) { throw; }
    catch (System.Exception ex)
    {
      var failed = new AuditResult { Checked = false, Error = ex.GetType().Name + ": " + ex.Message, ElapsedMs = sw.ElapsedMilliseconds, Deep = deep };
      var json = failed.ToJson(path);
      json["fontAlt"] = SafeFontAlt();
      json["fontMapFile"] = SafeFontMappingFile();
      json["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
      return json;
    }
  }

  /// <summary>体检一个已打开的数据库（调用方负责事务）。</summary>
  internal static Dictionary<string, object?> AuditActive(Database db, Transaction tr, bool deep, int maxEntities)
  {
    var sw = Stopwatch.StartNew();
    AuditResult result;
    try
    {
      result = AuditCore(db, tr, deep, maxEntities, pathOverride: null);
      result.ElapsedMs = sw.ElapsedMilliseconds;
    }
    catch (System.Exception ex)
    {
      result = new AuditResult { Checked = false, Error = ex.GetType().Name + ": " + ex.Message, Deep = deep, ElapsedMs = sw.ElapsedMilliseconds };
    }
    var json = result.ToJson(db.Filename);
    json["fontAlt"] = SafeFontAlt();
    json["fontAltResolves"] = ResolveFont(SafeFontAlt(), db) != null;
    json["fontMapFile"] = SafeFontMappingFile();
    json["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
    return json;
  }

  // -------------------------------------------------------------------------
  // 核心体检
  // -------------------------------------------------------------------------

  private static AuditResult AuditCore(Database db, Transaction tr, bool deep, int maxEntities, string? pathOverride)
  {
    var res = new AuditResult { Checked = true, Deep = deep };
    Dictionary<string, StyleUsage>? usage = null;

    var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
    var styles = new List<(string Name, string? Primary, string? BigFont, string? TypeFace)>();
    foreach (ObjectId id in tst)
    {
      res.StyleCount++;
      TextStyleTableRecord rec;
      try { rec = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead); }
      catch (System.Exception) { continue; }

      var primary = (rec.FileName ?? "").Trim();
      var big = (rec.BigFontFileName ?? "").Trim();
      string? typeFace = null;
      if (primary.Length == 0)
      {
        try { var tf = rec.Font.TypeFace; if (!string.IsNullOrWhiteSpace(tf)) typeFace = tf.Trim(); }
        catch (System.Exception) { /* FontDescriptor 不可读时按未知处理 */ }
      }
      if (primary.Length > 0) res.ShxStyleCount++;
      else if (typeFace != null) res.TtfStyleCount++;
      styles.Add((rec.Name ?? "", primary.Length == 0 ? null : primary, big.Length == 0 ? null : big, typeFace));
    }

    // 第一遍：只找缺失（便宜）。有缺失且 deep=true 时再做文字用量统计。
    var missingRaw = new List<(string Style, string Font, string Role)>();
    foreach (var s in styles)
    {
      if (s.Primary != null && ResolveFont(s.Primary, db) == null)
        missingRaw.Add((s.Name, s.Primary, RolePrimary));
      // 大字体的存在性：AutoCAD 把 bigfont 当独立文件解析
      if (s.BigFont != null && ResolveFont(s.BigFont, db) == null)
        missingRaw.Add((s.Name, s.BigFont, RoleBigFont));
    }
    var missingTtfRaw = new List<(string Style, string TypeFace)>();
    foreach (var s in styles)
      if (s.TypeFace != null && !TtfInstalled(s.TypeFace))
        missingTtfRaw.Add((s.Name, s.TypeFace));

    if (deep && (missingRaw.Count > 0 || missingTtfRaw.Count > 0))
    {
      usage = CollectUsage(db, tr, maxEntities, out var scanned, out var truncated);
      res.ScannedEntities = scanned;
      res.ScanTruncated = truncated;
    }

    var styleHasBig = styles.Where(s => !string.IsNullOrEmpty(s.BigFont) && ResolveFont(s.BigFont, db) != null)
                            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var m in missingRaw)
    {
      StyleUsage? u = null;
      usage?.TryGetValue(m.Style, out u);
      var info = new MissingFont
      {
        Style = m.Style,
        Font = m.Font,
        Role = m.Role,
        TextCount = u?.Count ?? 0,
        HasNonAscii = u?.HasNonAscii ?? false,
        StyleHasBigFont = styleHasBig.Contains(m.Style),
      };
      info.SuggestedFallback = PickFallback(info);
      res.Missing.Add(info);
    }
    foreach (var t in missingTtfRaw)
    {
      StyleUsage? tu = null;
      usage?.TryGetValue(t.Style, out tu);
      res.MissingTtf.Add(new MissingTtf { Style = t.Style, TypeFace = t.TypeFace, TextCount = tu?.Count ?? 0 });
    }
    return res;
  }

  private sealed class StyleUsage
  {
    public int Count;
    public bool HasNonAscii;
  }

  private static Dictionary<string, StyleUsage> CollectUsage(Database db, Transaction tr, int maxEntities, out int scanned, out bool truncated)
  {
    var map = new Dictionary<string, StyleUsage>(StringComparer.OrdinalIgnoreCase);
    scanned = 0;
    truncated = false;

    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
    foreach (ObjectId btrId in bt)
    {
      BlockTableRecord btr;
      try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
      catch (System.Exception) { continue; }
      // 只统计模型空间 + 图纸空间布局（块定义里的文字不按引用计数，避免把「定义」当「实例」）
      if (!btr.IsLayout) continue;

      foreach (ObjectId entId in btr)
      {
        if (scanned >= maxEntities) { truncated = true; break; }
        scanned++;

        string? style = null;
        string? text = null;
        DBObject obj;
        try { obj = tr.GetObject(entId, OpenMode.ForRead, false); }
        catch (System.Exception) { continue; }

        switch (obj)
        {
          case DBText t:
            style = t.TextStyleName;
            text = t.TextString;
            break;
          case MText mt:
            style = mt.TextStyleName;
            text = mt.Contents;
            break;
          case MLeader ml:
            try
            {
              var mt = ml.MText;
              if (mt != null) { style = mt.TextStyleName; text = mt.Contents; }
            }
            catch (System.Exception) { /* MLeader.MText 在旧版本/坏对象上可能抛 */ }
            break;
          case Dimension dim:
            style = ResolveDimensionTextStyle(dim, tr);
            break;
        }
        if (string.IsNullOrEmpty(style)) continue;

        if (!map.TryGetValue(style!, out var u)) map[style!] = u = new StyleUsage();
        u.Count++;
        if (!u.HasNonAscii && HasNonAscii(text)) u.HasNonAscii = true;
      }
      if (truncated) break;
    }
    return map;
  }

  private static string? ResolveDimensionTextStyle(Dimension dim, Transaction tr)
  {
    try
    {
      var dsId = dim.DimensionStyle;
      if (dsId.IsNull) return null;
      var dsr = tr.GetObject(dsId, OpenMode.ForRead) as DimStyleTableRecord;
      if (dsr == null) return null;
      var tsId = dsr.Dimtxsty;
      if (tsId.IsNull) return null;
      var tsr = tr.GetObject(tsId, OpenMode.ForRead) as TextStyleTableRecord;
      return tsr?.Name;
    }
    catch (System.Exception) { return null; }
  }

  private static bool HasNonAscii(string? s)
  {
    if (string.IsNullOrEmpty(s)) return false;
    foreach (var c in s!) if (c > 127) return true;
    return false;
  }

  // -------------------------------------------------------------------------
  // 字体解析（与 AutoCAD 同一套搜索逻辑）
  // -------------------------------------------------------------------------

  internal static string? ResolveFont(string? name, Database? db)
  {
    if (string.IsNullOrWhiteSpace(name)) return null;
    var key = name!.Trim();
    if (ResolveCache.TryGetValue(key, out var cached)) return cached;

    var found = FindViaFindFile(key, db);
    if (string.IsNullOrEmpty(found))
    {
      foreach (var dir in FontDirectories(db))
      {
        try
        {
          var candidate = Path.Combine(dir, key);
          if (File.Exists(candidate)) { found = candidate; break; }
        }
        catch (System.Exception) { /* 非法路径字符 */ }
      }
    }
    ResolveCache[key] = found;
    return found;
  }

  /// <summary>AutoCAD 自己的字体搜索（support path / 图纸目录 / Fonts 都包含在内）。</summary>
  private static string? FindViaFindFile(string key, Database? db)
  {
    try
    {
      // 2026-11-23 P34.2: 关掉图纸后 MdiActiveDocument 为空，但 DocumentManager 里可能还有别的文档；
      //   只认 MdiActiveDocument 会让字体解析整段失效 → CJK 替代被降级成 simplex（真机踩到）。
      var useDb = db ?? App.DocumentManager.MdiActiveDocument?.Database;
      if (useDb == null)
      {
        foreach (Autodesk.AutoCAD.ApplicationServices.Document openDoc in App.DocumentManager)
        {
          if (openDoc?.Database != null) { useDb = openDoc.Database; break; }
        }
      }
      if (useDb == null) return null;
      var hit = HostApplicationServices.Current.FindFile(key, useDb, FindFileHint.FontFile);
      return string.IsNullOrEmpty(hit) ? null : hit;
    }
    catch (System.Exception) { return null; }
  }

  /// <summary>
  /// 字体目录：从「一定能找到的那几个 AutoCAD 自带字体」反推，而不是读 sysvar
  /// （2026-11-23 实测：GetSystemVariable("ACAD") 在这台机器返回空串，靠它算支持路径会全军覆没）。
  /// </summary>
  private static List<string> FontDirectories(Database? db)
  {
    var dirs = new List<string>();
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var probe in new[] { "txt.shx", "simplex.shx", "gbcbig.shx" })
    {
      var hit = FindViaFindFile(probe, db);
      if (string.IsNullOrEmpty(hit)) continue;
      try
      {
        var dir = Path.GetDirectoryName(hit!);
        if (!string.IsNullOrEmpty(dir) && set.Add(dir!)) dirs.Add(dir!);
      }
      catch (System.Exception) { }
    }
    var drawingFile = (db ?? App.DocumentManager.MdiActiveDocument?.Database)?.Filename;
    if (!string.IsNullOrWhiteSpace(drawingFile))
    {
      try
      {
        var dir = Path.GetDirectoryName(drawingFile);
        if (!string.IsNullOrEmpty(dir))
        {
          if (set.Add(dir!)) dirs.Add(dir!);
          var fonts = Path.Combine(dir!, "Fonts");
          if (set.Add(fonts)) dirs.Add(fonts);
        }
      }
      catch (System.Exception) { }
    }
    return dirs;
  }

  /// <summary>
  /// 本机可用的 SHX 名单。候选表就是替代字体的候选表（我们只会从里面挑），
  /// 所以逐个用 FindFile 探活比扫目录更可靠（也不怕网络盘/权限）。
  /// </summary>
  private static List<string> ShxIndex()
  {
    var cached = _shxIndex;
    if (cached != null) return cached;
    var available = new List<string>();
    foreach (var name in CjkBigFontOrder.Concat(CjkSoloOrder).Concat(LatinOrder))
    {
      if (available.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
      if (ResolveFont(name, null) != null) available.Add(name);
    }
    if (available.Count == 0) return available;   // 解析不到就别缓存（下次有文档时再试）
    _shxIndex = available;
    return available;
  }

  private static string? PickFallback(MissingFont m)
  {
    var available = new HashSet<string>(ShxIndex(), StringComparer.OrdinalIgnoreCase);
    IEnumerable<string> order = m.Role == RoleBigFont
      ? CjkBigFontOrder
      : (m.HasNonAscii && !m.StyleHasBigFont ? CjkSoloOrder : LatinOrder);

    foreach (var cand in order)
      if (available.Contains(cand) && !string.Equals(cand, m.Font, StringComparison.OrdinalIgnoreCase))
        return cand;

    foreach (var cand in CjkBigFontOrder.Concat(LatinOrder))
      if (available.Contains(cand) && !string.Equals(cand, m.Font, StringComparison.OrdinalIgnoreCase))
        return cand;

    return null;
  }

  private static bool TtfInstalled(string typeFace)
  {
    try
    {
      using var col = new System.Drawing.Text.InstalledFontCollection();
      foreach (var f in col.Families)
        if (string.Equals(f.Name, typeFace, StringComparison.OrdinalIgnoreCase)) return true;
    }
    catch (System.Exception) { return true; }   // 探测不了就别报缺
    return false;
  }

  // -------------------------------------------------------------------------
  // 打开前挂字体映射
  // -------------------------------------------------------------------------

  internal sealed class FontMapLease
  {
    public string File = "";
    public string? Backup;
    public List<(string From, string To)> Entries = new();
    public bool Written;
  }

  private const string MapBlockStart = "; >>> new-acad font map (auto-generated; safe to delete this block) >>>";
  private const string MapBlockEnd = "; <<< new-acad font map <<<";

  /// <summary>
  /// 把缺失字体映射成本机存在的替代字体，写进 AutoCAD 自己的字体映射文件 acad.fmp。
  /// 托管 API 没有 Preferences（那是 COM 的 AcadPreferencesFiles），所以走 AutoCAD 的
  /// 官方机制：acad.fmp 在「打开图纸」时被读取，映射命中就不会弹 Missing-SHX 对话框。
  /// 写入的是文件末尾的托管块（带起止标记，每次刷新），用户原有的映射一条不动。
  /// </summary>
  internal static FontMapLease? ApplyFontMap(IEnumerable<(string From, string To)> entries)
  {
    var list = entries.Where(e => !string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                      .Where(e => e.From.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                      .Where(e => ResolveFont(e.To, null) != null)   // 目标必须本机真能找到，否则写了也没用
                      .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
                      .Select(g => g.First())
                      .ToList();
    if (list.Count == 0) return null;

    // 2026-11-23 P34.1: 用户可以在 config.json / 环境变量里关掉自动映射（默认开）。
    // 关掉后仍体检、仍回报缺失字体，只是不动 acad.fmp。
    if (!PluginRuntime.AutoFontMapping)
    {
      PluginLog.Info("Drawing", "auto font mapping is disabled (autoFontMapping / CIVIL3D_AUTO_FONT_MAPPING) — acad.fmp left untouched");
      return null;
    }

    var lease = new FontMapLease { Entries = list };
    try
    {
      var file = FontMapPath();
      if (string.IsNullOrEmpty(file))
      {
        PluginLog.Warn("Drawing", "acad.fmp not found in the support paths — font mapping skipped");
        return null;
      }
      lease.File = file;

      string text = "";
      try { if (File.Exists(file)) text = File.ReadAllText(file); } catch (System.Exception) { text = ""; }

      // 去掉上一次写的托管块，保留用户自己的映射
      var startIdx = text.IndexOf(MapBlockStart, StringComparison.Ordinal);
      if (startIdx >= 0)
      {
        var endIdx = text.IndexOf(MapBlockEnd, startIdx, StringComparison.Ordinal);
        if (endIdx >= 0) text = text.Substring(0, startIdx) + text.Substring(endIdx + MapBlockEnd.Length);
      }
      text = text.TrimEnd('\r', '\n', ' ', '\t');

      var block = new System.Text.StringBuilder();
      block.Append(Environment.NewLine).Append(MapBlockStart).Append(Environment.NewLine);
      foreach (var e in list) block.Append(e.From).Append(';').Append(e.To).Append(Environment.NewLine);
      block.Append(MapBlockEnd).Append(Environment.NewLine);

      // 第一次改这个文件时留一份备份（只在没有备份时留，避免覆盖用户手改后的版本）
      var backup = file + ".newacad-backup";
      if (!File.Exists(backup))
      {
        try { File.Copy(file, backup, false); lease.Backup = backup; }
        catch (System.Exception) { /* 备份失败不阻断映射 */ }
      }

      // 走 FileBoundary（原子替换）—— security_boundary 门禁要求直接写文件只出现在 FileBoundary.cs
      FileBoundary.WritePluginConfigFile(file, text + block, new UTF8Encoding(false));
      lease.Written = true;
      PluginLog.Info("Drawing", $"font map written to {file}: {string.Join(", ", list.Select(e => e.From + "->" + e.To))}");
      return lease;
    }
    catch (System.Exception ex)
    {
      PluginLog.Warn("Drawing", "could not write the font map: " + ex.Message);
      return null;
    }
  }

  /// <summary>映射写进 acad.fmp 后有意保留：同一台机器以后打开别的图纸也受益（映射只在原字体缺失时生效）。</summary>
  internal static void RestoreFontMap(FontMapLease? lease)
  {
    if (lease == null || !lease.Written) return;
    PluginLog.Debug("Drawing", "font map kept in " + lease.File + " (" + lease.Entries.Count + " entries)");
  }

  /// <summary>acad.fmp 的实际位置（按 AutoCAD 自己的搜索顺序找，APPDATA 兜底）。</summary>
  private static string FontMapPath()
  {
    var cached = _fontMapPath;
    if (cached != null) return cached;

    // 2026-11-23 真机：AutoCAD 2025 的 FONTMAP 系统变量返回的就是「字体映射文件路径」
    //   （不是老版本那个 0/1 开关），拿到它就不用猜目录。
    try
    {
      var raw = (App.GetSystemVariable("FONTMAP") ?? "").ToString() ?? "";
      if (raw.EndsWith(".fmp", StringComparison.OrdinalIgnoreCase) && File.Exists(raw))
      {
        _fontMapPath = raw;
        return raw;
      }
    }
    catch (System.Exception) { }

    try
    {
      var db = App.DocumentManager.MdiActiveDocument?.Database;
      if (db != null)
      {
        var hit = HostApplicationServices.Current.FindFile("acad.fmp", db, FindFileHint.Default);
        if (!string.IsNullOrEmpty(hit) && File.Exists(hit)) { _fontMapPath = hit; return hit; }
      }
    }
    catch (System.Exception) { }

    try
    {
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      var autodesk = Path.Combine(appData, "Autodesk");
      if (Directory.Exists(autodesk))
      {
        foreach (var vendor in Directory.EnumerateDirectories(autodesk))
        {
          foreach (var lang in Directory.EnumerateDirectories(vendor))
          {
            foreach (var supportName in new[] { "support", "Support" })
            {
              var candidate = Path.Combine(lang, supportName, "acad.fmp");
              if (File.Exists(candidate)) { _fontMapPath = candidate; return candidate; }
            }
          }
        }
      }
    }
    catch (System.Exception) { }
    return "";
  }

  private static string SafeFontAlt()
  {
    try { return (App.GetSystemVariable("FONTALT") ?? "").ToString() ?? ""; }
    catch (System.Exception) { return ""; }
  }

  private static string SafeFontMappingFile() => FontMapPath();

  // -------------------------------------------------------------------------
  // openDrawing 用的组合流程
  // -------------------------------------------------------------------------

  /// <summary>
  /// 打开前：体检 + 生成映射表（不落任何设置）。
  /// mode = "auto" 按角色自动挑替代字体；"none" 不映射；其它字符串 = 调用方指定的统一替代字体。
  /// </summary>
  /// <summary>
  /// 在 AutoCAD 主线程（App.Idle，非命令上下文、无需文档）执行一段工作并等结果。
  /// 所有碰 sysvar / Preferences / Side Database 的代码都必须走这里 —— 2026-11-23 真机：
  /// 在 TCP 线程直接调这些 API，auditFonts 请求没返回，AutoCAD 进程直接消失。
  /// </summary>
  internal static async Task<T> RunOnIdleAsync<T>(Func<T> work, int timeoutSeconds)
  {
    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler idleHandler = null!;
    idleHandler = (_, _) =>
    {
      App.Idle -= idleHandler;
      try { tcs.TrySetResult(work()); }
      catch (System.Exception ex) { tcs.TrySetException(ex); }
    };
    App.Idle += idleHandler;

    var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)).Token);
    var done = await Task.WhenAny(tcs.Task, timeoutTask);
    if (done != tcs.Task)
    {
      App.Idle -= idleHandler;
      throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT",
        "font audit timeout: CAD was not idle for " + timeoutSeconds + "s.");
    }
    return await tcs.Task;
  }

  internal static (Dictionary<string, object?> Audit, List<(string From, string To)> Map) BuildPreflight(string path, string mode)
  {
    var map = new List<(string From, string To)>();
    if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
      return (new Dictionary<string, object?> { ["checked"] = false, ["note"] = "fontFallback=none" }, map);

    var audit = AuditFile(path, deep: true, maxEntities: DefaultMaxEntities);
    if (!(audit.TryGetValue("missing", out var raw) && raw is List<object?> list)) return (audit, map);

    var manual = !string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) && mode.Length > 0;
    foreach (var item in list)
    {
      if (item is not Dictionary<string, object?> m) continue;
      var font = m.TryGetValue("font", out var f) ? f?.ToString() ?? "" : "";
      if (font.Length == 0) continue;
      // 2026-11-23 真机：只映射 SHX。把 TrueType 字体名（如 calibri.ttf）写进 acad.fmp 会让
      //   AutoCAD 的字体映射器在下次打开同一张图时崩（0xc0000374 堆损坏，openDrawing 直接掉线）。
      //   TTF 缺失 Windows 自己会兜底，只报告不映射。
      if (!font.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)) continue;
      var role = m.TryGetValue("role", out var r) ? r?.ToString() ?? RolePrimary : RolePrimary;
      var suggested = m.TryGetValue("suggestedFallback", out var s) ? s?.ToString() : null;
      // 指定统一字体时只替 primary；大字体必须换成大字体，否则中文宽度会炸
      var target = manual && role != RoleBigFont ? mode : suggested;
      if (!string.IsNullOrWhiteSpace(target)) map.Add((font, target!));
    }
    return (audit, map);
  }
}
