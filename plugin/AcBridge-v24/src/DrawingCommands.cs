using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class DrawingCommands
{
  public static Task<object?> GetCivil3DHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    var doc = App.DocumentManager.MdiActiveDocument;
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var jobs = JobRegistry.GetStats();

    object response = new Dictionary<string, object?>
    {
      ["connected"] = true,
      ["civil3dVersion"] = App.GetSystemVariable("ACADVER")?.ToString(),
      ["pluginVersion"] = typeof(PluginEntry).Assembly.GetName().Version?.ToString(),
      // 2026-10-20 审计：编译期 build stamp —— 区分「文件换了」与「进程真加载了新代码」
      ["buildStamp"] = PluginRuntime.BuildStamp(),
      // 2026-10-27 v1.11.0-fix: 纯 AutoCAD 宿主（无 AeccDbMgd）下为 false，
      // 面板/AI 据此避免调用 Civil 3D 方法（那些方法会返回 CIVIL3D.CIVIL_NOT_INSTALLED）。
      ["civilAvailable"] = CivilSupport.Available,
      ["civilApiDetail"] = CivilSupport.Detail,
      ["hostProduct"] = CivilSupport.HostProduct(),
      // 2026-11-23 P35: 宿主矩阵的运行时维度（安装包 net8 / net10 两段）
      ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
      // 2026-10-27 (gate): palette-side状态，供真机门禁断言“目录真的加载了”（后端对 ≠ 用户看到对）
      ["palette"] = HankPalette.CatalogState(),
      // 2026-10-28 (P2.6 S6): 文档生命周期状态（门禁断言"切图真的被处理了"）
      ["documentLifecycle"] = DocumentLifecycle.State(),
      // 2026-11-18 P36: 长操作（大导出等）的进度。插件没有主动推送通道，
      //   面板本来就在轮询 health，把进度挂这里最省事也最稳（不新增方法）。
      ["operations"] = OperationProgress.Snapshot(),
      // 2026-10-19: 暴露解析出的根目录（排障一眼可见；装在哪都该指向真实安装）
      ["projectRoot"] = PluginRuntime.ProjectRoot(),
      ["rootSource"] = PluginRuntime.RootSource(),
      ["drawingLoaded"] = doc != null,
      ["operationInProgress"] = status.OperationInProgress,
      ["currentOperation"] = status.CurrentOperation,
      ["queueDepth"] = status.QueueDepth,
      ["queueCapacity"] = status.QueueCapacity,
      ["currentOperationStartedAtUnixMs"] = status.CurrentOperationStartedAtUnixMs,
      ["currentRequestId"] = status.CurrentRequestId,
      ["currentOperationDurationMs"] = status.CurrentOperationStartedAtUnixMs is long startedAt
        ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt)
        : null,
      ["memoryUsageMb"] = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
      // 2026-10-10 (批次6.1): 指挥命令上下文健康 —— busy 状态 + 最近一次超时恢复快照，
      // 便于 AI/用户判断“超时后是否已自动恢复”，无需盲目重启 C3D。
      ["hostExecution"] = CivilExecution.GetHostRecoveryState(),
      ["logFilePath"] = PluginLog.LogFilePath,
      ["fileLoggingHealthy"] = PluginLog.IsFileLoggingHealthy,
      ["fileLoggingError"] = PluginLog.LastFileError,
      ["jobs"] = new Dictionary<string, object?>
      {
        ["total"] = jobs.Total,
        ["running"] = jobs.Running,
        ["completed"] = jobs.Completed,
        ["failed"] = jobs.Failed,
        ["cancelled"] = jobs.Cancelled,
        ["capacity"] = jobs.Capacity,
        ["terminalRetentionMinutes"] = jobs.TerminalRetentionMinutes,
      },
    };

    return Task.FromResult<object?>(response);
  }

  public static Task<object?> GetDrawingInfoAsync()
  {
    if (CivilSupport.Available)
    {
      return GetDrawingInfoCivilAsync();
    }

    // Plain AutoCAD host: same shape, Civil 3D collections reported as unavailable.
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
      BuildDrawingInfoCore(doc, database, transaction));
  }

  // Civil 3D-only branch, kept in its own method so a plain-AutoCAD session never JITs it.
  private static Task<object?> GetDrawingInfoCivilAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildDrawingInfo(doc, civilDoc, database, transaction));
  }

  public static Task<object?> GetProjectContextAsync(JsonObject? parameters)
  {
    var requestedLimit = PluginRuntime.GetOptionalInt(parameters, "selectedObjectLimit") ?? 25;
    var selectedObjectLimit = Math.Clamp(requestedLimit, 1, 100);

    if (CivilSupport.Available)
    {
      return GetProjectContextCivilAsync(selectedObjectLimit);
    }

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
      new Dictionary<string, object?>
      {
        ["drawingInfo"] = BuildDrawingInfoCore(doc, database, transaction),
        ["objectTypes"] = BuildCivilObjectTypes(),
        ["selectedObjects"] = BuildSelectedCivilObjects(doc, transaction, selectedObjectLimit, out var selectedTotal, out _, out var selectedSkipped),
        ["selectedObjectTotal"] = selectedTotal,
        ["selectedObjectSkippedErased"] = selectedSkipped,
      });
  }

  private static Task<object?> GetProjectContextCivilAsync(int selectedObjectLimit)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?>
      {
        ["drawingInfo"] = BuildDrawingInfo(doc, civilDoc, database, transaction),
        ["objectTypes"] = BuildCivilObjectTypes(),
        ["selectedObjects"] = BuildSelectedCivilObjects(doc, transaction, selectedObjectLimit, out var selectedTotal, out _, out var selectedSkipped),
        ["selectedObjectTotal"] = selectedTotal,
        ["selectedObjectSkippedErased"] = selectedSkipped,
      });
  }

  public static Task<object?> GetDrawingSettingsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var currentLayer = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, database.Clayer, OpenMode.ForRead);
      var coords = ReadCoordinateSystemFields(civilDoc);

      return new Dictionary<string, object?>
      {
        ["coordinateSystem"] = coords.code,
        ["coordinateZone"] = coords.zone,
        ["datum"] = coords.datum,
        ["scaleFactor"] = Convert.ToDouble(App.GetSystemVariable("DIMSCALE") ?? 1d),
        ["elevationReference"] = coords.verticalDatum,
        ["defaultLayer"] = currentLayer.Name,
        ["units"] = CivilObjectUtils.LinearUnits(database),   // 2026-10-15: 供 TS 侧计算（如 size_network 选径）按单位取值
        ["defaultStyles"] = new Dictionary<string, object?>
        {
          ["surface"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.SurfaceStyles, transaction),
          ["alignment"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.AlignmentStyles, transaction),
          ["profile"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.ProfileStyles, transaction),
          ["corridor"] = null,
          ["pipeNetwork"] = PipeNetworkCommands.GetFirstPipeNetworkStyleName(civilDoc, transaction),
        },
      };
    });
  }

  public static Task<object?> SaveDrawingAsync(JsonObject? parameters)
  {
    var overwrite = PluginRuntime.GetAiOverwrite(parameters);
    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      // 2026-11-16 P1: save must prove the file landed
      //   以前 database.SaveAs 一返回就报 saved=true —— 路径写错、目录不存在、只读介质这些情况
      //   只有在下一轮 openDrawing 才会暴露（真实踩过：脚本打印"已保存"，文件根本不存在）。
      //   现在：保存后回读 FileInfo（存在 + 非空 + 时间戳前进），并返回 sizeBytes/verified。
      //   同时接受 "path" 作为别名：指向当前文件时按"保存当前"处理（不触发覆盖检查）。
      var startedAtUtc = DateTime.UtcNow;
      var requestedPath = PluginRuntime.GetOptionalString(parameters, "saveAs")
        ?? PluginRuntime.GetOptionalString(parameters, "path");
      var currentFile = database.Filename;
      var isCurrentFile = !string.IsNullOrWhiteSpace(requestedPath)
        && !string.IsNullOrWhiteSpace(currentFile)
        && string.Equals(Path.GetFullPath(requestedPath), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
      var targetPath = string.IsNullOrWhiteSpace(requestedPath) || isCurrentFile ? currentFile : requestedPath;
      if (string.IsNullOrWhiteSpace(targetPath))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "saveDrawing requires 'saveAs' (or 'path') when the drawing has never been saved.");
      }

      // Saving the active drawing is not an export conflict: its current file
      // necessarily exists. Boundary/overwrite checks apply only to Save As.
      targetPath = string.IsNullOrWhiteSpace(requestedPath) || isCurrentFile
        ? Path.GetFullPath(targetPath)
        : FileBoundary.ResolveExportPath(targetPath, overwrite, ".dwg");
      try
      {
        database.SaveAs(targetPath, true, DwgVersion.Current, database.SecurityParameters);
      }
      catch (System.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR",
          Loc.Tk("error.saveFailedReadOnly", ("msg", ex.Message), ("path", targetPath)));
      }

      // 回读校验（最多 3 次 × 200 ms，给网络盘/杀软留一点写入延迟）
      FileInfo? info = null;
      for (var attempt = 0; attempt < 3; attempt++)
      {
        try
        {
          var candidate = new FileInfo(targetPath);
          if (candidate.Exists && candidate.Length > 0
              && candidate.LastWriteTimeUtc >= startedAtUtc.AddSeconds(-3))
          {
            info = candidate;
            break;
          }
        }
        catch (System.Exception) { /* fall through to retry */ }
        System.Threading.Thread.Sleep(200);
      }
      if (info == null)
      {
        var exists = false;
        long len = 0;
        try { var f = new FileInfo(targetPath); exists = f.Exists; len = f.Exists ? f.Length : 0; } catch (System.Exception) { }
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR",
          $"saveDrawing reported success but the file did not land: path={targetPath}, exists={exists}, sizeBytes={len}. " +
          "Check the directory exists and is writable.");
      }

      return new Dictionary<string, object?>
      {
        ["saved"] = true,
        ["verified"] = true,
        ["filePath"] = targetPath,
        ["sizeBytes"] = info.Length,
        ["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("o"),
      };
    });
  }

  public static async Task<object?> NewDrawingAsync(JsonObject? parameters)
  {
    // 2026-10-28 (P2.6 S6): 不再在这里清日志 —— 清空只有一个入口 DocumentLifecycle，
    // 它同时覆盖"用户在 CAD 里自己切图"这条以前被漏掉的路径。
    // 2026-11-23 P34.1: 模板也要做字体体检 —— 项目模板常带自定义 SHX，本机没有就同样会中文变问号。
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    if (!string.IsNullOrWhiteSpace(templatePath))
    {
      templatePath = FileBoundary.ResolveImportPath(templatePath, ".dwt", ".dwg");
    }
    var useTemplate = templatePath;
    var doAudit = PluginRuntime.GetOptionalBool(parameters, "auditFonts") ?? true;
    var fontFallback = (PluginRuntime.GetOptionalString(parameters, "fontFallback") ?? "auto").Trim();

    // 2026-09-10 fix: DocumentManager.Add 必须在 AutoCAD UI 线程执行——
    //   ExecuteDocumentOpAsync 里直接调 Add 时线程是 TCP 线程池线程，
    //   会抛 WPF InvalidOperationException（acmgdEnableCmdLine: 调用线程无法访问该对象）。
    //   与 openDrawing 同理：把 Add 放进 App.Idle 回调（UI 线程、非命令上下文）。
    var created = await CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
    {
      var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        FontAudit.FontMapLease? lease = null;
        try
        {
          // 模板体检（Side DB，主线程）：缺 SHX 就先落字体映射，再 Add 模板
          Dictionary<string, object?>? preAudit = null;
          var fontMap = new List<(string From, string To)>();
          if (doAudit && !string.IsNullOrWhiteSpace(useTemplate))
          {
            try
            {
              var pre = FontAudit.BuildPreflight(useTemplate!, fontFallback);
              preAudit = pre.Audit;
              fontMap = pre.Map;
            }
            catch (Exception ex)
            {
              preAudit = new Dictionary<string, object?> { ["checked"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
            lease = FontAudit.ApplyFontMap(fontMap);
          }

          var createdDocument = string.IsNullOrWhiteSpace(useTemplate)
            ? App.DocumentManager.Add(string.Empty)
            : App.DocumentManager.Add(useTemplate);
          App.DocumentManager.MdiActiveDocument = createdDocument;

          var result = new Dictionary<string, object?>
          {
            ["drawingName"] = createdDocument.Name,
            ["filePath"] = createdDocument.Database.Filename,
            ["templatePath"] = useTemplate,
            ["note"] = "newDrawing runs on the UI thread (Idle)",
          };
          if (preAudit != null)
          {
            result["fontPreflight"] = preAudit;
            result["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
          }
          if (lease != null && fontMap.Count > 0) result["fontMappingApplied"] = fontMap.Select(e => e.From + "->" + e.To).ToList();
          else if (fontMap.Count > 0) result["fontMappingSkipped"] = "auto font mapping is disabled (autoFontMapping / CIVIL3D_AUTO_FONT_MAPPING) — acad.fmp was not modified";
          tcs.TrySetResult(result);
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        finally
        {
          FontAudit.RestoreFontMap(lease);
        }
      };
      App.Idle += idleHandler;

      // fallback: CAD busy -> Idle never fires. 模板体检要多花几秒，开了体检把等待放宽到 90s。
      var waitSeconds = doAudit && !string.IsNullOrWhiteSpace(useTemplate) ? 90 : 20;
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT",
          "newDrawing timeout: CAD not idle, Add not executed." + (string.IsNullOrWhiteSpace(useTemplate) ? "" : " template=" + useTemplate));
      }
      return await tcs.Task;
    });

    // 建完再体检一次新图（模板的样式已经进库）
    if (created is Dictionary<string, object?> okResult && doAudit)
    {
      try
      {
        okResult["fontAudit"] = await CivilExecution.ReadAsync<object?>((doc, db, tr) => FontAudit.AuditActive(db, tr, false, 120000));   // 2026-11-23: 浅层复检（样式表级）；要条数让 AI 自己 auditFonts{deep:true}
      }
      catch (System.Exception ex)
      {
        okResult["fontAudit"] = new Dictionary<string, object?> { ["checked"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
      }
    }
    return created;
  }

  public static async Task<object?> OpenDrawingAsync(JsonObject? parameters)
  {
    // 2026-10-28 (P2.6 S6): 清日志由 DocumentLifecycle 统一处置。
    // 2026-11-23 P34: 打开前体检字体（缺 SHX 时写 acad.fmp 映射，防模态框 + 中文不丢），
    //   打开后再体检活动数据库，把缺失清单交给面板 agent 如实告知用户。
    //   体检必须跑在 App.Idle（主线程）：Side Database / sysvar / FindFile 在 TCP 线程上会崩 CAD。
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var readOnly = PluginRuntime.GetOptionalBool(parameters, "readOnly") ?? false;
    var doAudit = PluginRuntime.GetOptionalBool(parameters, "auditFonts") ?? true;
    var fontFallback = (PluginRuntime.GetOptionalString(parameters, "fontFallback") ?? "auto").Trim();
    var resolved = FileBoundary.ResolveImportPath(path, ".dwg", ".dwt");

    var openResult = await CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
    {
      var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        FontAudit.FontMapLease? lease = null;
        try
        {
          // save current drawing first (prevents OPEN save-prompt; skips template/unnamed)
          var savedPrevious = false;
          var current = App.DocumentManager.MdiActiveDocument;
          if (current?.Database != null && !string.IsNullOrWhiteSpace(current.Database.Filename))
          {
            try { current.Database.Save(); savedPrevious = true; }
            catch (Exception ex)
            {
              PluginLog.Debug("Drawing", "could not pre-save the current drawing before open: " + ex.Message);
            }
          }

          // 2026-09-11: target already open -> just activate it. Re-opening an already-open dwg
          // makes AutoCAD create a READ-ONLY duplicate (window title "<name>:2 - 只读"), and the
          // active doc becomes read-only -> every later save fails eFileAccessErr and unsaved work is lost.
          var existing = FindOpenDocument(resolved);
          if (existing != null)
          {
            App.DocumentManager.MdiActiveDocument = existing;
            tcs.TrySetResult(new Dictionary<string, object?>
            {
              ["drawingName"] = existing.Name,
              ["filePath"] = existing.Database?.Filename,
              ["alreadyOpen"] = true,
              ["savedPreviousDrawing"] = savedPrevious,
              ["note"] = "Already open in CAD — switched to it instead of opening a second copy: " + existing.Name,
            });
            return;
          }

          // ---- 2026-11-23 P34 字体体检（Side DB，主线程）----
          Dictionary<string, object?>? preAudit = null;
          var fontMap = new List<(string From, string To)>();
          if (doAudit)
          {
            try
            {
              var pre = FontAudit.BuildPreflight(resolved, fontFallback);
              preAudit = pre.Audit;
              fontMap = pre.Map;
            }
            catch (Exception ex)
            {
              preAudit = new Dictionary<string, object?> { ["checked"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
          }

          // 缺字体 → 写 acad.fmp 映射（AutoCAD 打开图纸时读取），并保证 FONTALT 不是 "."
          //   （"." 会让 AutoCAD 每次缺字体都弹模态框，在 App.Idle 里必卡死）
          lease = FontAudit.ApplyFontMap(fontMap);

          var opened = App.DocumentManager.Open(resolved, readOnly);
          App.DocumentManager.MdiActiveDocument = opened;

          var result = new Dictionary<string, object?>
          {
            ["drawingName"] = opened.Name,
            ["filePath"] = opened.Database.Filename,
            ["readOnly"] = readOnly,
            ["savedPreviousDrawing"] = savedPrevious,
            ["note"] = "Switched to " + opened.Name + (savedPrevious ? " (previous auto-saved)" : ""),
          };
          if (preAudit != null)
          {
            result["fontPreflight"] = preAudit;
            result["fontMappingEnabled"] = PluginRuntime.AutoFontMapping;
          }
          if (lease != null && fontMap.Count > 0) result["fontMappingApplied"] = fontMap.Select(e => e.From + "->" + e.To).ToList();
          else if (fontMap.Count > 0) result["fontMappingSkipped"] = "auto font mapping is disabled (autoFontMapping / CIVIL3D_AUTO_FONT_MAPPING) — acad.fmp was not modified";
          tcs.TrySetResult(result);
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        finally
        {
          FontAudit.RestoreFontMap(lease);
        }
      };
      App.Idle += idleHandler;

      // fallback: CAD busy -> Idle never fires. 体检要多花几秒，所以开了体检时把等待放宽到 90s。
      var waitSeconds = doAudit ? 90 : 20;
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT",
          "openDrawing timeout: CAD not idle, OPEN not executed: " + resolved
          + " — if a modal 'Missing SHX Files' dialog is on screen, dismiss it and retry");
      }
      return await tcs.Task;
    });

    if (openResult is Dictionary<string, object?> result && doAudit)
    {
      try
      {
        result["fontAudit"] = await CivilExecution.ReadAsync<object?>((doc, db, tr) => FontAudit.AuditActive(db, tr, false, 120000));   // 2026-11-23: 浅层复检（样式表级）；要条数让 AI 自己 auditFonts{deep:true}
      }
      catch (System.Exception ex)
      {
        result["fontAudit"] = new Dictionary<string, object?> { ["checked"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
      }
    }
    return openResult;
  }

  // 2026-09-11: find an already-open Document whose file path matches (case-insensitive full path).
  /// <summary>
  /// 2026-11-03: close one drawing silently. CLOSE raises a modal "save changes?" dialog, which
  /// blocks the whole plugin chain, so debug leftovers could only be cleaned up by hand.
  /// Guard rail: a drawing that HAS a file path is refused unless force=true (and then it is saved
  /// first, never discarded) -- this is for unsaved scratch drawings, not for real files.
  /// </summary>
  public static Task<object?> CloseDrawingAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name") ?? "";
    var force = PluginRuntime.GetOptionalBool(parameters, "force") ?? false;

    return CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
    {
      var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        try
        {
          var doc = string.IsNullOrWhiteSpace(name) ? App.DocumentManager.MdiActiveDocument : FindOpenDocumentByName(name);
          if (doc == null)
          {
            tcs.TrySetException(new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
              string.IsNullOrWhiteSpace(name) ? "no active drawing to close" : "no open drawing matches: " + name));
            return;
          }
          var closedName = doc.Name;
          var file = doc.Database?.Filename ?? "";
          var isNamed = !string.IsNullOrWhiteSpace(file);
          if (isNamed && !force)
          {
            tcs.TrySetException(new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              "refusing to close " + closedName + ": it has a file path (" + file + "). Pass force=true only if you really mean it."));
            return;
          }
          var saved = false;
          if (isNamed)
          {
            try
            {
              // 2026-11-03 真机实测两次：Database.Save() 在 App.Idle 里稳定抛 eFileInternalErr，
              // 加不加文档锁都一样。插件里唯一被真机验证过的存盘路径是 saveDrawing 的
              // database.SaveAs(existingPath, ...) —— 照抄同一条路径，别再造第二种存盘法。
              using (doc.LockDocument())
              {
                doc.Database.SaveAs(file, true, DwgVersion.Current, doc.Database.SecurityParameters);
              }
              saved = true;
            }
            catch (Exception ex)
            {
              tcs.TrySetException(new JsonRpcDispatchException("CIVIL3D.INTERNAL_ERROR",
                "refusing to close " + closedName + ": saving it first failed (" + ex.Message + "), closing would drop the changes"));
              return;
            }
          }
          var before = App.DocumentManager.Count;
          doc.CloseAndDiscard();
          var after = App.DocumentManager.Count;
          tcs.TrySetResult(new Dictionary<string, object?>
          {
            ["closed"] = closedName,
            ["hadFilePath"] = isNamed,
            ["saved"] = saved,
            ["documentsBefore"] = before,
            ["documentsAfter"] = after,
            ["note"] = "closed without a save prompt (CLOSE would have raised a modal dialog)",
          });
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
      };
      App.Idle += idleHandler;

      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT", "closeDrawing timeout: CAD not idle, nothing was closed.");
      }
      return await tcs.Task;
    });
  }

  private static Document? FindOpenDocumentByName(string wanted)
  {
    var t = (wanted ?? "").Trim();
    if (t.Length == 0) return null;
    foreach (Document d in App.DocumentManager)
    {
      try
      {
        if (string.Equals(d.Name, t, StringComparison.OrdinalIgnoreCase)) return d;
        var fn = d.Database?.Filename ?? "";
        if (fn.Length > 0 && string.Equals(Path.GetFullPath(fn), Path.GetFullPath(t), StringComparison.OrdinalIgnoreCase)) return d;
      }
      catch
      {
        /* a document whose name cannot be read is simply not a match */
      }
    }
    return null;
  }
  private static Document? FindOpenDocument(string fullPath)
  {
    var target = Path.GetFullPath(fullPath);
    foreach (Document d in App.DocumentManager)
    {
      try
      {
        var fn = d?.Database?.Filename;
        if (!string.IsNullOrWhiteSpace(fn) &&
            string.Equals(Path.GetFullPath(fn), target, StringComparison.OrdinalIgnoreCase))
          return d;
      }
      catch {
        /* best-effort: a document whose filename cannot be read is simply not a match */
      }
    }
    return null;
  }

  public static Task<object?> UndoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.UNDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "undo",
      };
    });
  }

  public static Task<object?> RedoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.REDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "redo",
      };
    });
  }

  public static Task<object?> ListCivilObjectTypesAsync()
  {
    return Task.FromResult<object?>(BuildCivilObjectTypes());
  }

  public static Task<object?> ListTextsAsync(JsonObject? parameters)
  {
    // 2026-08-10: 支持 layer 过滤（子串匹配）+ maxCount/offset 分页——解决大数据量结果被 AI 侧截断拿不全（统计标注场景）
    // 2026-11-12 v1.16.0: 补 position/rotation + pattern 正则 + bbox 区域过滤。实战来由：
    //   2,405 条纵断面标注拿不到坐标，就没法把「BVC/EVCE/K/Type」按变坡点归类，只能 getEntityInfo
    //   逐条读 = 15 轮往返。带坐标后按 bbox 一页就能整套拿走。
    var layerFilter = PluginRuntime.GetOptionalString(parameters, "layer");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount");
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;
    var pattern = PluginRuntime.GetOptionalString(parameters, "pattern");
    var bbox = ParseBboxFilter(parameters == null ? null : parameters["bbox"]);

    System.Text.RegularExpressions.Regex? regex = null;
    if (!string.IsNullOrWhiteSpace(pattern))
    {
      try
      {
        regex = new System.Text.RegularExpressions.Regex(
          pattern,
          System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
          TimeSpan.FromSeconds(2));
      }
      catch (ArgumentException ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pattern is not a valid regular expression: {ex.Message} (nothing was changed).");
      }
    }

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      var texts = new List<Dictionary<string, object?>>();
      // 2026-11-16 P13: 子串匹配下要告诉调用方"实际匹配到了哪些图层"，不然 0 条无法自证
      var matchedLayers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (ObjectId id in ms)
      {
        try
        {
          var ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
          if (ent == null) continue;
          var rclass = ent.GetRXClass().Name;
          if (!rclass.Contains("Text") && !rclass.Contains("ATTRIB")) continue;
          // 2026-11-16 P13: 以前是大小写敏感的 Contains —— layer:"shap" 匹配不到 "C-ROAD-SHAP"，
          //   和其它所有名字过滤（都是 OrdinalIgnoreCase）不一致，客户会以为这类图层没有文字。
          if (!string.IsNullOrEmpty(layerFilter) &&
              ent.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

          var info = new Dictionary<string, object?>
          {
            ["handle"] = id.Handle.ToString(),
            ["type"] = rclass,
            ["layer"] = ent.Layer,
          };
          if (ent is Autodesk.AutoCAD.DatabaseServices.DBText txt) { info["text"] = txt.TextString; info["height"] = txt.Height; }
          else if (ent is Autodesk.AutoCAD.DatabaseServices.MText mtext) { info["text"] = mtext.Text; info["height"] = mtext.TextHeight; }

          var position = TryGetTextPosition(ent);
          if (position.HasValue)
          {
            info["position"] = new Dictionary<string, object?>
            {
              ["x"] = position.Value.X,
              ["y"] = position.Value.Y,
              ["z"] = position.Value.Z,
            };
            info["rotation"] = TryGetTextRotation(ent);
          }

          // pattern / bbox 在“拿到内容与位置之后”再判：先过滤可少算，但绝不能因为拿不到内容就误放行
          if (regex != null)
          {
            var value = info.TryGetValue("text", out var t) ? t as string : null;
            if (string.IsNullOrEmpty(value) || !regex.IsMatch(value)) continue;
          }
          if (bbox.HasValue && (!position.HasValue || !IsInsideBbox(bbox.Value, position.Value))) continue;

          matchedLayers[ent.Layer] = matchedLayers.TryGetValue(ent.Layer, out var mc) ? mc + 1 : 1;
          texts.Add(info);
        }
        catch {
          /* best-effort: an unreadable text entity is skipped in the listing */
        }
      }
      var total = texts.Count;
      IEnumerable<Dictionary<string, object?>> page = texts;
      if (offset > 0) page = page.Skip(offset);
      if (maxCount.HasValue) page = page.Take(maxCount.Value);
      var arr = page.ToList();
      // 2026-11-17 P24: 自己按字节预算裁（只留前 N 条，翻页顺序不变），不交给 ResultSummarizer 切半
      var bulkBudget = BulkResponse.ResolveBudget(parameters);
      var (keptCount, byteLimited) = BulkResponse.Trim(arr, bulkBudget);
      var matchedLayerList = matchedLayers
        .OrderByDescending(kv => kv.Value)
        .Take(20)
        .Select(kv => new Dictionary<string, object?> { ["layer"] = kv.Key, ["count"] = kv.Value })
        .ToList();
      return new
      {
        count = total,
        returned = arr.Count,
        returnedCount = keptCount,
        nextOffset = offset + arr.Count,
        hasMore = offset + arr.Count < total,
        byteLimited,
        responseByteBudget = bulkBudget,
        truncated = offset + arr.Count < total,
        filtered = regex != null || bbox.HasValue || !string.IsNullOrEmpty(layerFilter),
        layerFilter,
        matchedLayers = matchedLayerList,
        hint = (total == 0 && !string.IsNullOrEmpty(layerFilter))
          ? $"No text entity on a layer whose name contains '{layerFilter}' (matching is case-insensitive substring). matchedLayers is empty - try leaving layer empty, or check the layer name."
          : (string?)null,
        texts = arr
      };
    });
  }

  private static Point3d? TryGetTextPosition(Autodesk.AutoCAD.DatabaseServices.Entity ent)
  {
    return ent switch
    {
      Autodesk.AutoCAD.DatabaseServices.DBText t => t.Position,
      Autodesk.AutoCAD.DatabaseServices.MText mt => mt.Location,
      _ => null,
    };
  }

  private static double? TryGetTextRotation(Autodesk.AutoCAD.DatabaseServices.Entity ent)
  {
    try
    {
      return ent switch
      {
        Autodesk.AutoCAD.DatabaseServices.DBText t => t.Rotation,
        Autodesk.AutoCAD.DatabaseServices.MText mt => mt.Rotation,
        _ => null,
      };
    }
    catch { return null; }
  }

  // bbox: {x1,y1,x2,y2}（也接受 minX/minY/maxX/maxY 别名）；只判平面范围，不看 Z。
  private static bool IsInsideBbox((double X1, double Y1, double X2, double Y2) box, Point3d p)
    => p.X >= box.X1 && p.X <= box.X2 && p.Y >= box.Y1 && p.Y <= box.Y2;

  private static (double X1, double Y1, double X2, double Y2)? ParseBboxFilter(JsonNode? node)
  {
    if (node is not JsonObject obj) return null;
    double? Read(string a, string b)
    {
      var raw = PluginRuntime.JsonNodeToString(obj[a]) ?? PluginRuntime.JsonNodeToString(obj[b]);
      return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    }
    var x1 = Read("x1", "minX");
    var y1 = Read("y1", "minY");
    var x2 = Read("x2", "maxX");
    var y2 = Read("y2", "maxY");
    if (!x1.HasValue || !y1.HasValue || !x2.HasValue || !y2.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "bbox needs x1/y1/x2/y2 (or minX/minY/maxX/maxY) as numbers (nothing was changed).");
    }
    return (Math.Min(x1.Value, x2.Value), Math.Min(y1.Value, y2.Value), Math.Max(x1.Value, x2.Value), Math.Max(y1.Value, y2.Value));
  }

  // 2026-10-15: 统一走 PluginRuntime.SelectionSnapshotPath()（bundle 布局下 asmDir\..\.. 会差一级）
  private static string SelFile => PluginRuntime.SelectionSnapshotPath();

  /// <summary>
  /// 选中集变化时自动保存到文件（ImpliedSelectionChanged 事件触发）
  /// </summary>
  public static void OnSelectionChanged(object? sender, EventArgs e)
  {
    try
    {
      // 用事件来源文档（sender），避免多图纸切换时读到新文档的选择集
      var doc = sender as Autodesk.AutoCAD.ApplicationServices.Document ?? App.DocumentManager.MdiActiveDocument;
      if (doc == null) { PluginLog.Debug("Selection", "No active doc"); return; }
      var result = doc.Editor.SelectImplied();
      if (result.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || result.Value == null)
      {
        // 不直接清空 _sel.json：SelectImplied 在失焦/后台时返回空，
        // 清空会误伤“用户刚框选好、正要发指令”的数据。
        // 面板发送前已主动捕获（HankPalette.Send → SaveSelectionAsync），这里保留旧值即可。
        PluginLog.Debug("Selection", "SelectImplied empty, keeping existing _sel.json");
        return;
      }

      var ids = result.Value.GetObjectIds();
      PluginLog.Debug("Selection", $"selection resolved: {ids.Length} entities");
      if (ids.Length == 0) return;
      // 2026-11-19 P2：selectByCriteria 刚用 ssget 建立的全量选择集会触发本事件；
      //   如果它覆盖掉刚写的 maxCount 限定快照，“选了 3 条 → 导出 2354 条”。
      if (AcadCommands.ShouldSkipImpliedSelectionSave(ids.Length))
      {
        PluginLog.Debug("Selection", "implied selection matches the programmatic ssget call; keeping the selectByCriteria snapshot");
        return;
      }

      // 保存全部图元的 handle（不限于文字）
      var entries = new List<Dictionary<string, string?>>();
      foreach (var id in ids)
      {
        try
        {
          entries.Add(new Dictionary<string, string?>
          {
            { "handle", id.Handle.ToString() },
            { "rxClass", id.ObjectClass.Name },
          });
        }
        catch (Exception ex2) { PluginLog.Debug("Selection", $"Entity error: {ex2.Message}"); }
      }

      var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
      {
        ["docName"] = doc.Name,
        ["savedAt"] = PluginTime.StoredNow(),
        ["items"] = entries,
      });
      FileBoundary.WriteAllTextAtomicInternal(SelFile, json, System.Text.UTF8Encoding.UTF8, "json");
      PluginLog.Debug("Selection", $"saved {entries.Count} entities to {SelFile} (doc: {doc.Name})");
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Selection", $"Error: {ex.Message}");
    }
  }

  /// <summary>
  /// 读取选中的文字（从文件，不受失焦影响）
  /// </summary>
  public static Task<object?> ListSelectedTextsAsync()
  {
    try
    {
      if (!System.IO.File.Exists(SelFile)) return Task.FromResult<object?>(new { count = 0, texts = new List<object>() });
      var json = System.IO.File.ReadAllText(SelFile);
      // 2026-10-15: 快照已改为对象格式 {docName, savedAt, items:[...]}；旧代码按数组反序列化会抛异常 → 恒返回 0。
      var texts = new List<Dictionary<string, string?>>();
      try
      {
        var obj = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (obj is System.Text.Json.Nodes.JsonObject jo && jo.ContainsKey("items"))
        {
          if (jo["items"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var it in arr)
              if (it is System.Text.Json.Nodes.JsonObject item)
                texts.Add(new Dictionary<string, string?>
                {
                  ["handle"] = PluginRuntime.JsonNodeToString(item["handle"]),
                  ["rxClass"] = PluginRuntime.JsonNodeToString(item["rxClass"]),
                });
        }
        else
        {
          texts = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json) ?? new();
        }
      }
      catch { texts = new(); }
      return Task.FromResult<object?>(new { count = texts.Count, texts });
    }
    catch
    {
      return Task.FromResult<object?>(new { count = 0, texts = new List<object>() });
    }
  }

  public static Task<object?> GetSelectedCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100;
    // 2026-11-17 P26: 以前 `.Take(limit)` 静默截断 —— 默认 100 条，6 万条选择集也只回 100 条，
    //   调用方完全不知道还剩多少。默认形状仍是"数组"（不改老调用方），
    //   传 details:true 返回 {selectionTotal, returned, truncated, limit, objects}。
    var details = PluginRuntime.GetOptionalBool(parameters, "details") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var objects = BuildSelectedCivilObjects(doc, transaction, limit, out var selectionTotal, out var source, out var skippedErased);
      if (!details) return objects;
      return new Dictionary<string, object?>
      {
        ["selectionTotal"] = selectionTotal,
        ["skippedErasedHandles"] = skippedErased,
        ["source"] = source,
        ["returned"] = objects.Count,
        ["truncated"] = selectionTotal > objects.Count,
        ["limit"] = limit,
        ["objects"] = objects,
      };
    });
  }

  /// <summary>
  /// Plain-AutoCAD variant of BuildDrawingInfo (2026-10-27 v1.11.0-fix): no CivilDocument,
  /// no Civil 3D collections. Same keys so callers do not branch on host type.
  /// </summary>
  private static Dictionary<string, object?> BuildDrawingInfoCore(
    Document doc,
    Database database,
    Transaction transaction)
  {
    var unsavedChanges = Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    var angularUnits = CivilObjectUtils.AngularUnits(Convert.ToInt16(App.GetSystemVariable("AUNITS") ?? 0));

    return new Dictionary<string, object?>
    {
      ["fileName"] = Path.GetFileName(database.Filename),
      ["filePath"] = database.Filename,
      ["coordinateSystem"] = null,
      ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
      ["angularUnits"] = angularUnits,
      ["unsavedChanges"] = unsavedChanges,
      ["objectCounts"] = null,
      ["drawingName"] = doc.Name,
      ["docSessionId"] = DocumentLifecycle.DocIdFor(doc),   // 2026-11-02: 回放判同图的稳定身份
      ["projectName"] = null,
      ["units"] = CivilObjectUtils.LinearUnits(database),
      ["civilAvailable"] = false,
    };
  }

  private static Dictionary<string, object?> BuildDrawingInfo(
    Document doc,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction)
  {
    var unsavedChanges = Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    var angularUnits = CivilObjectUtils.AngularUnits(Convert.ToInt16(App.GetSystemVariable("AUNITS") ?? 0));
    var coordinateSystemCode = ReadCoordinateSystemCode(civilDoc);

    return new Dictionary<string, object?>
    {
      ["fileName"] = Path.GetFileName(database.Filename),
      ["filePath"] = database.Filename,
      ["coordinateSystem"] = coordinateSystemCode,
      ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
      ["angularUnits"] = angularUnits,
      ["unsavedChanges"] = unsavedChanges,
      ["objectCounts"] = new Dictionary<string, object?>
      {
        ["surfaces"] = civilDoc.GetSurfaceIds().Count,
        ["alignments"] = civilDoc.GetAlignmentIds().Count,
        ["profiles"] = CountProfiles(civilDoc, transaction),
        ["corridors"] = civilDoc.CorridorCollection.Count,
        ["pipeNetworks"] = PipeNetworkCommands.CountPipeNetworks(civilDoc),
        ["points"] = civilDoc.CogoPoints.Count,
        ["parcels"] = CountParcels(civilDoc, transaction),
      },
      ["drawingName"] = doc.Name,
      ["docSessionId"] = DocumentLifecycle.DocIdFor(doc),   // 2026-11-02: 回放判同图的稳定身份
      ["projectName"] = null,
      ["units"] = CivilObjectUtils.LinearUnits(database),
      ["civilAvailable"] = true,
    };
  }

  private static object[] BuildCivilObjectTypes() =>
  [
    "Alignment",
    "Surface",
    "Profile",
    "Corridor",
    "CogoPoint",
    "SampleLineGroup",
    "FeatureLine",
    "Parcel",
    "PipeNetwork",
  ];

  private static List<Dictionary<string, object?>> BuildSelectedCivilObjects(
    Document doc,
    Transaction transaction,
    int limit,
    out int selectionTotal,
    out string source)
  {
    return BuildSelectedCivilObjects(doc, transaction, limit, out selectionTotal, out source, out _);
  }

  /// <summary>
  /// 2026-11-18 P32: 快照里的 handle 可能指向**已擦除对象**（换图后 handle 被复用，或对象在记录后被删）。
  /// transaction.GetObject 对这类 id 抛 eWasErased 而不是回 null —— 旧实现让整个方法炸掉，
  /// 于是 getProjectContext / getSelectedCivilObjectsInfo 在门禁里被算成"真问题"。现在逐个跳过并计数。
  /// </summary>
  private static List<Dictionary<string, object?>> BuildSelectedCivilObjects(
    Document doc,
    Transaction transaction,
    int limit,
    out int selectionTotal,
    out string source,
    out int skippedErased)
  {
    selectionTotal = 0;
    skippedErased = 0;
    source = "selection";
    var result = new List<Dictionary<string, object?>>();
    var objectIds = new List<ObjectId>();
    var selection = doc.Editor.SelectImplied();
    if (selection.Status == PromptStatus.OK && selection.Value != null)
    {
      objectIds.AddRange(selection.Value.GetObjectIds());
    }

    // 2026-11-17 P26: 程序化选择集（LISP sssetfirst 投递的）在 Editor.SelectImplied 里读不到 ——
    //   真机 60,798 条选择这里返回 0 条，调用方会得出"什么都没选"的结论。
    //   兜底读 _sel.json 快照（与 getSelectionDetail / exportSelectionToFile 同源），并把来源写进 source。
    if (objectIds.Count == 0)
    {
      source = "snapshot";
      objectIds.AddRange(ReadSelectionSnapshotObjectIds(doc.Database));
    }

    var usable = new List<ObjectId>(objectIds.Count);
    foreach (var id in objectIds)
    {
      try
      {
        if (id.IsNull) continue;
        if (id.IsErased) { skippedErased++; continue; }
        usable.Add(id);
      }
      catch { skippedErased++; }
    }

    selectionTotal = usable.Count;
    foreach (var objectId in usable.Take(limit))
    {
      Autodesk.AutoCAD.DatabaseServices.DBObject? dbObject;
      try { dbObject = transaction.GetObject(objectId, OpenMode.ForRead); }
      catch { skippedErased++; continue; }
      if (dbObject == null || dbObject.IsErased)
      {
        skippedErased++;
        continue;
      }

      result.Add(new Dictionary<string, object?>
      {
        ["handle"] = dbObject.Handle.ToString(),
        ["objectType"] = dbObject.GetType().Name,
        ["name"] = CivilObjectUtils.GetName(dbObject),
        ["description"] = CivilObjectUtils.GetStringProperty(dbObject, "Description"),
      });
    }

    return result;
  }

  /// <summary>把 _sel.json 里的 handle 解析成 ObjectId（解析失败的跳过）。</summary>
  private static List<ObjectId> ReadSelectionSnapshotObjectIds(Database database)
  {
    var ids = new List<ObjectId>();
    var selFile = PluginRuntime.SelectionSnapshotPath();
    if (!System.IO.File.Exists(selFile)) return ids;
    try
    {
      var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(selFile));
      if (node is System.Text.Json.Nodes.JsonObject jo && jo["items"] is System.Text.Json.Nodes.JsonArray arr)
      {
        foreach (var it in arr)
        {
          if (it is not System.Text.Json.Nodes.JsonObject item) continue;
          var handleText = PluginRuntime.JsonNodeToString(item["handle"]);
          if (string.IsNullOrWhiteSpace(handleText)) continue;
          try
          {
            var id = database.GetObjectId(false, new Handle(long.Parse(handleText!, System.Globalization.NumberStyles.HexNumber)), 0);
            if (!id.IsNull) ids.Add(id);
          }
          catch { }
        }
      }
    }
    catch (Exception ex)
    {
      PluginLog.Warn("DrawingCommands", "selection snapshot unreadable: " + ex.Message);
    }
    return ids;
  }

  private static int CountProfiles(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      count += alignment.GetProfileIds().Count;
    }

    return count;
  }

  private static int CountParcels(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;

    foreach (ObjectId siteId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
      count += site.GetParcelIds().Count;
    }

    return count;
  }

  // Single-field coordinate system code (for getDrawingInfo).
  private static string? ReadCoordinateSystemCode(CivilDocument civilDoc)
  {
    return ReadCoordinateSystemFields(civilDoc).code;
  }

  private static (string? code, string? zone, string? datum, string? verticalDatum) ReadCoordinateSystemFields(CivilDocument civilDoc)
  {
    var unitZone = civilDoc.Settings.DrawingSettings.UnitZoneSettings;
    var code = unitZone.CoordinateSystemCode;
    if (string.IsNullOrWhiteSpace(code))
    {
      return (null, null, null, null);
    }

    var coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
    return (code, coordinateSystem.Category, coordinateSystem.Datum, null);
  }

  public static Task<object?> CreateCircleAsync(System.Text.Json.Nodes.JsonObject? parameters)
  {
    var centerArr = parameters?["center"] as System.Text.Json.Nodes.JsonArray;
    var diameter = parameters?["diameter"]?.GetValue<double>() ?? 0;

    if (centerArr == null || centerArr.Count < 2 || diameter <= 0)
      return Task.FromResult<object?>(new System.Text.Json.Nodes.JsonObject { ["error"] = "center[] and diameter required" });

    var cx = centerArr[0]?.GetValue<double>() ?? 0;
    var cy = centerArr[1]?.GetValue<double>() ?? 0;
    var cz = centerArr.Count > 2 ? centerArr[2]?.GetValue<double>() ?? 0 : 0;

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var btr = (BlockTableRecord)transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var circle = new Circle(
        new Point3d(cx, cy, cz),
        new Vector3d(0, 0, 1),
        diameter / 2.0);
      btr.AppendEntity(circle);
      transaction.AddNewlyCreatedDBObject(circle, true);

      return new Dictionary<string, object?> { ["handle"] = CivilObjectUtils.GetHandle(circle) };
    });
  }  public static Task<object?> CreateEllipseAsync(JsonObject? parameters)
  {
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "centerX") ?? 0;
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "centerY") ?? 0;
    var centerZ = PluginRuntime.GetOptionalDouble(parameters, "centerZ") ?? 0;
    var majorRadius = PluginRuntime.GetOptionalDouble(parameters, "majorRadius") ?? 0;
    var ratio = PluginRuntime.GetOptionalDouble(parameters, "ratio") ?? 0;
    var rotationDeg = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    if (majorRadius <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createEllipse requires majorRadius > 0.");
    }

    if (ratio <= 0 || ratio > 1)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createEllipse requires ratio in (0, 1] (minor/major).");
    }

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var rotation = rotationDeg * Math.PI / 180.0;
      var majorAxis = new Vector3d(Math.Cos(rotation) * majorRadius, Math.Sin(rotation) * majorRadius, 0);

      var ellipse = new Ellipse(new Point3d(centerX, centerY, centerZ), new Vector3d(0, 0, 1), majorAxis, ratio, 0.0, 2.0 * Math.PI);

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        ellipse.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      }

      var ellipseId = modelSpace.AppendEntity(ellipse);
      transaction.AddNewlyCreatedDBObject(ellipse, true);

      return new Dictionary<string, object?> { ["handle"] = CivilObjectUtils.GetHandle(ellipse) };
    });
  }

}
