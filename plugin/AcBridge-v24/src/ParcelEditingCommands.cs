using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_parcel_* editing tools.
///
/// Civil 3D API notes:
///   Parcel/site collection members vary by Civil 3D version, so this implementation
///   relies on reflection for site/parcel enumeration and creation fallbacks.
/// </summary>
public static class ParcelEditingCommands
{
  // -------------------------------------------------------------------------
  // listParcelSites
  // -------------------------------------------------------------------------

  public static Task<object?> ListParcelSitesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rows = new List<Dictionary<string, object?>>();
      foreach (ObjectId sid in EnumerateSiteIds(civilDoc))
      {
        var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, sid, OpenMode.ForRead);
        rows.Add(new Dictionary<string, object?>
        {
          ["name"] = site.Name,
          ["handle"] = CivilObjectUtils.GetHandle(site),
          ["parcelCount"] = EnumerateParcelIds(site, transaction).Count(),
        });
      }

      return new Dictionary<string, object?>
      {
        ["sites"] = rows,
      };
    });
  }

  // -------------------------------------------------------------------------
  // listParcels
  // -------------------------------------------------------------------------

  public static Task<object?> ListParcelsAsync(JsonObject? parameters)
  {
    // 2026-11-18 P32: siteName 以前是必填 —— 客户 AI 面对只有 1 个 site 的图也得先猜名字，
    // 猜错就是 OBJECT_NOT_FOUND。现在省了就取唯一 site，并在结果里回 siteNameSource。
    var requestedSite = PluginRuntime.GetOptionalString(parameters, "siteName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var siteName = ResolveSiteName(civilDoc, transaction, requestedSite, out var siteNameSource);
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcels = new List<Dictionary<string, object?>>();

      foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
      {
        var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, OpenMode.ForRead);
        parcels.Add(new Dictionary<string, object?>
        {
          ["name"] = parcel.Name,
          ["handle"] = CivilObjectUtils.GetHandle(parcel),
          ["number"] = GetParcelNumber(parcel),
          ["area"] = parcel.Area,
          ["perimeter"] = GetParcelPerimeter(parcel),
          ["style"] = CivilObjectUtils.GetStringProperty(parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
        });
      }

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["siteNameSource"] = siteNameSource,
        ["parcelCount"] = parcels.Count,
        ["parcels"] = parcels,
        ["units"] = new Dictionary<string, object?>
        {
          ["area"] = string.Equals(CivilObjectUtils.LinearUnits(database), "meters", StringComparison.OrdinalIgnoreCase) ? "sqm" : "sqft",
          ["length"] = CivilObjectUtils.LinearUnits(database) == "meters" ? "m" : "ft",
          ["drawingLinearUnits"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // getParcel
  // -------------------------------------------------------------------------

  public static Task<object?> GetParcelAsync(JsonObject? parameters)
  {
    var requestedSite = PluginRuntime.GetOptionalString(parameters, "siteName");
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");
    var debug = PluginRuntime.GetOptionalBool(parameters, "debug") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var siteName = ResolveSiteName(civilDoc, transaction, requestedSite, out var siteNameSource);
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName, OpenMode.ForRead);

      var payload = new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["name"] = parcel.Name,
        ["handle"] = CivilObjectUtils.GetHandle(parcel),
        ["number"] = GetParcelNumber(parcel),
        ["area"] = parcel.Area,
        ["perimeter"] = GetParcelPerimeter(parcel),
        ["style"] = CivilObjectUtils.GetStringProperty(parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
      };
      payload["siteNameSource"] = siteNameSource;
      if (debug) payload["debug"] = DescribeParcelDebug(parcel);
      return payload;
    });
  }

  // -------------------------------------------------------------------------
  // createParcel
  // -------------------------------------------------------------------------

  public static Task<object?> CreateParcelAsync(JsonObject? parameters)
  {
    // 参数照描述的契约读出来（描述里仍声明 siteName/points/name/sourceHandle/style/areaLabelStyle），
    // 再明确告诉调用方这个能力在托管 API 里不存在 —— 不做假实现。
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    var requestedName = PluginRuntime.GetOptionalString(parameters, "name");
    var sourceHandle = PluginRuntime.GetOptionalString(parameters, "sourceHandle");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");
    var areaLabelStyle = PluginRuntime.GetOptionalString(parameters, "areaLabelStyle");

    //   Parcel 没有 Create / CreateFromEntity（离线元数据核对：只有 ParcelAreaLabel/ParcelSegmentLabel 的 Create），
    //   Site 只有 Create(CivilDocument, string)（建 site），ParcelTable/ParcelSegmentTable 是表格对象。
    // 旧实现反射猜了三个不存在的入口（CreateParcelFromEntity / Parcels.Add / Parcel.Create），永远拿不到 id，
    // 于是无论输入多正确都回 "Parcel was not created"（假实现 + 误导性错误）。
    // 现在直接说清楚：只能走界面命令（Create Parcels From Objects / Parcel Layout Tools）。
    throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED",
      "createParcel is not supported: Civil 3D's managed API has no parcel-creation entry point (Parcel.Create / Parcel.CreateFromEntity do not exist; Site.Create only creates a site). Create the parcel with the 'Create Parcels From Objects' command or Parcel Layout Tools in Civil 3D, then use listParcels / getParcel / editParcel / deleteParcel to work with it.");

  }

  // -------------------------------------------------------------------------
  // editParcel
  // -------------------------------------------------------------------------

  public static Task<object?> DeleteParcelAsync(JsonObject? parameters)
  {
    // 2026-11-18 P33: 参数名原来是 site/parcel，和全库（siteName/parcelName）不一致 ——
    // 客户按其它地块方法的习惯传 siteName/parcelName 会得到 "Missing required parameter 'site'"。
    // 现在两个名字都认（canonical 优先），并且删完必须回读验证。
    // 描述里声明的参数名是 site/parcel；为和其他地块方法一致，siteName/parcelName 也认（canonical 优先）
    var legacySite = PluginRuntime.GetOptionalString(parameters, "site");
    var legacyParcel = PluginRuntime.GetOptionalString(parameters, "parcel");
    var requestedSite = PluginRuntime.GetOptionalString(parameters, "siteName") ?? legacySite;
    var parcelName = PluginRuntime.GetOptionalString(parameters, "parcelName") ?? legacyParcel;
    if (string.IsNullOrWhiteSpace(parcelName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "deleteParcel requires 'parcelName' (legacy alias: 'parcel').");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 2026-11-18 P33: 真机上 parcel.Erase() 抛 eNotApplicable（无消息），site 以 ForWrite 打开也一样；
      // 托管 API 里 Parcel/Site 也**没有** Delete/Remove/Erase 入口（离线元数据核对）。
      // 所以"删地块"只能走界面（Delete Parcel / Parcel Layout Tools）。不再假装删除：
      // 解析出目标地块的真实信息（handle / 面积），让用户知道该在界面删哪一个。
      var siteName = ResolveSiteName(civilDoc, transaction, requestedSite, out var siteNameSource);
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName!, OpenMode.ForRead);
      throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED",
        $"deleteParcel is not supported: Civil 3D's managed API cannot erase a parcel (Parcel.Erase() raises eNotApplicable; Parcel/Site expose no Delete/Remove method). " +
        $"Target: '{parcel.Name}' (handle {CivilObjectUtils.GetHandle(parcel)}, area {parcel.Area:F2}, site '{siteName}' [{siteNameSource}]). " +
        "Delete it in Civil 3D (select the parcel and press Delete, or use Parcel Layout Tools), then run getParcel again to confirm it is gone.");
    });
  }

  public static Task<object?> EditParcelAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");
    var newName = PluginRuntime.GetOptionalString(parameters, "newName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var areaLabelStyle = PluginRuntime.GetOptionalString(parameters, "areaLabelStyle");
    var description = PluginRuntime.GetOptionalString(parameters, "description");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName, OpenMode.ForWrite);

      // 2026-11-18 P33: 改名可能撞名（C3D 行为不确定，可能静默改成 "X (1)"）—— 先查同名。
      if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, parcelName, StringComparison.OrdinalIgnoreCase))
      {
        foreach (ObjectId otherId in EnumerateParcelIds(site, transaction))
        {
          var other = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, otherId, OpenMode.ForRead);
          if (string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase))
            throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
              $"Parcel '{newName}' already exists in site '{siteName}' (handle {CivilObjectUtils.GetHandle(other)}). Pick another name or edit that parcel instead.");
        }
      }

      // 2026-11-18 P33: TrySetName 会把异常吞掉 → 改名失败时只看到"没生效"，看不到原因。
      // 真机实测：C3D 拒绝 "SINGLE-FAMILY: 114" / "X: 1" 这类 **名字 + ": " + 编号** 的自动命名格式，
      // 但允许 "SINGLE-FAMILY 114"。这里直接赋值，把库给的原始错误 + 规则一起抛出来。
      if (!string.IsNullOrWhiteSpace(newName))
      {
        try
        {
          parcel.Name = newName;
        }
        catch (Exception ex)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Civil 3D refused the parcel name '{newName}': {ex.GetType().Name}: {ex.Message}. " +
            "Civil 3D rejects some characters in object names (: is the one hit here) - pick a name without ': '.");
        }
      }
      if (!string.IsNullOrWhiteSpace(style)) ApplyStyleToParcel(parcel, style, civilDoc, transaction);
      if (!string.IsNullOrWhiteSpace(areaLabelStyle)) ApplyLabelStyleToParcel(parcel, areaLabelStyle, civilDoc, transaction);
      if (!string.IsNullOrWhiteSpace(description))
      {
        parcel.Description = description;
      }

      // 回读校验：改名/样式/描述都得真的落地（P29 教训：写操作说谎最贵）
      var applied = new Dictionary<string, object?>();
      var mismatches = new List<string>();
      if (!string.IsNullOrWhiteSpace(newName))
      {
        applied["name"] = parcel.Name;
        if (!string.Equals(parcel.Name, newName, StringComparison.OrdinalIgnoreCase))
          mismatches.Add($"name requested '{newName}' but reads back '{parcel.Name}'");
      }
      var styleName = CivilObjectUtils.GetStringProperty(
        parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name");
      applied["style"] = styleName;
      if (!string.IsNullOrWhiteSpace(style) && !string.Equals(styleName, style, StringComparison.OrdinalIgnoreCase))
        mismatches.Add($"style requested '{style}' but reads back '{styleName}'");
      if (!string.IsNullOrWhiteSpace(description))
      {
        applied["description"] = parcel.Description;
        if (!string.Equals(parcel.Description, description, StringComparison.Ordinal))
          mismatches.Add("description did not persist");
      }

      if (mismatches.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"editParcel did not fully apply: {string.Join("; ", mismatches)}.");

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["name"] = parcel.Name,
        ["handle"] = CivilObjectUtils.GetHandle(parcel),
        ["area"] = parcel.Area,
        ["applied"] = applied,
        ["updated"] = true,
        ["verified"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // adjustParcelLotLine
  // -------------------------------------------------------------------------

  public static Task<object?> AdjustParcelLotLineAsync(JsonObject? parameters)
  {
    var requestedSite = PluginRuntime.GetOptionalString(parameters, "siteName");
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");
    var targetAreaSqFt = PluginRuntime.GetRequiredDouble(parameters, "targetAreaSqFt");
    var lotLineHandle = PluginRuntime.GetOptionalString(parameters, "lotLineHandle");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 1.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var siteName = ResolveSiteName(civilDoc, transaction, requestedSite, out _);
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName, OpenMode.ForRead);

      // Try Civil 3D lot line slide API
      // 2026-11-18 P32: Parcel 上既没有 AdjustLotLine 也没有 SlideAngle（运行期成员表里查无此名），
      // 旧实现 InvokeMethod 返回 null 后照样回 "attempted"，converged 恒 false —— 客户以为是"调了但没收敛"，
      // 实际是"根本没调"。现在直接抛 NOT_SUPPORTED 并给出当前面积。
      var result = CivilObjectUtils.InvokeMethod(parcel, "AdjustLotLine",
        targetAreaSqFt, tolerance, lotLineHandle)
        ?? CivilObjectUtils.InvokeMethod(parcel, "SlideAngle", targetAreaSqFt, tolerance);

      if (result == null)
        throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED",
          $"Civil 3D's managed API exposes no lot-line slide/adjust method on Parcel (checked AdjustLotLine, SlideAngle). Parcel '{parcelName}' area is {parcel.Area:F2} drawing units²; adjust the lot line with Parcel Layout Tools in the UI, then re-query.");

      var actualArea = parcel.Area;
      var converged = Math.Abs(actualArea - TargetAreaToDrawingUnits(targetAreaSqFt, doc)) < tolerance;

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["parcelName"] = parcelName,
        ["targetAreaSqFt"] = targetAreaSqFt,
        ["actualArea"] = actualArea,
        ["converged"] = converged,
        ["message"] = converged
          ? $"Lot line adjusted. Parcel area is now {actualArea:F2} drawing units²."
          : $"Lot line API returned but the area did not reach the target (actual {actualArea:F2}). Re-query the parcel before assuming success.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // reportParcels
  // -------------------------------------------------------------------------

  public static Task<object?> ReportParcelsAsync(JsonObject? parameters)
  {
    var requestedSite = PluginRuntime.GetOptionalString(parameters, "siteName");
    var parcelNamesNode = PluginRuntime.GetParameter(parameters, "parcelNames") as JsonArray;
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var includeCoordinates = PluginRuntime.GetOptionalBool(parameters, "includeCoordinates") ?? false;
    // 2026-11-18 P32: corners = 真实顶点（按曲线参数，地块边有圆弧时拐点数仍准）；
    // dense = 按弧长等距采样（圆弧用折线逼近，给导出/下游几何用，也用于周长互验）。
    var vertexMode = (PluginRuntime.GetOptionalString(parameters, "vertexMode") ?? "corners").Trim().ToLowerInvariant();
    var sampleCount = PluginRuntime.GetOptionalInt(parameters, "sampleCount") ?? 256;
    var units = PluginRuntime.GetOptionalString(parameters, "units") ?? "sqft";
    var overwrite = PluginRuntime.GetAiOverwrite(parameters);

    var filterNames = parcelNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var siteName = ResolveSiteName(civilDoc, transaction, requestedSite, out var siteNameSource);
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var rows = new List<Dictionary<string, object?>>();

      foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
      {
        var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, OpenMode.ForRead);
        if (filterNames != null && filterNames.Count > 0 && !filterNames.Contains(parcel.Name)) continue;

        var areaInUnits = ConvertArea(parcel.Area, units, database);

        var row = new Dictionary<string, object?>
        {
          ["name"] = parcel.Name,
          ["handle"] = CivilObjectUtils.GetHandle(parcel),
          ["area"] = areaInUnits,
          ["areaUnits"] = units,
          ["perimeter"] = GetParcelPerimeter(parcel),
          ["style"] = CivilObjectUtils.GetStringProperty(
            parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
        };

        if (includeCoordinates)
        {
          // 2026-11-18 P32: 旧实现靠 GetBoundary/GetVertices —— Parcel 上没有这两个方法，
          // vertices 恒为 []（客户会以为地块没有边界）。现在按曲线参数取真实顶点并回源。
          var vertices = vertexMode == "dense"
            ? SampleParcelDense(parcel, sampleCount, out var verticesSource)
            : GetParcelVertices(parcel, out verticesSource);
          row["vertices"] = vertices;
          row["verticesSource"] = verticesSource;
          row["vertexCount"] = vertices.Count;
          row["vertexMode"] = vertexMode;
          var fromVertices = VerticesPerimeter(vertices);
          if (fromVertices.HasValue) row["perimeterFromVertices"] = fromVertices.Value;
        }

        rows.Add(row);
      }

      if (!string.IsNullOrWhiteSpace(outputPath))
      {
        outputPath = WriteCsv(outputPath, rows, includeCoordinates, overwrite);
      }

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["siteNameSource"] = siteNameSource,
        ["parcelCount"] = rows.Count,
        ["parcels"] = rows,
        ["outputPath"] = outputPath,
        ["units"] = units,
        ["drawingLinearUnits"] = CivilObjectUtils.LinearUnits(database),
        ["drawingAreaUnits"] = string.Equals(CivilObjectUtils.LinearUnits(database), "meters", StringComparison.OrdinalIgnoreCase) ? "sqm" : "sqft",
      };
    });
  }

  // -------------------------------------------------------------------------
  // P32 debug: 运行期成员转储（反射猜属性名是负债 —— Parcel 没有 Perimeter，
  //   GetParcelPerimeter 原来反射取 Perimeter 属性，而 Parcel 上没有这个属性 → 恒 null）
  // -------------------------------------------------------------------------

  private static Dictionary<string, object?> DescribeParcelDebug(Parcel parcel)
  {
    // 反射走兼容边界（门禁禁止边界外直接反射）
    var info = Civil3DCompatibility.DescribeMembers(parcel);

    void Try(string key, Func<object?> fn)
    {
      try { info[key] = fn(); }
      catch (Exception ex) { info[key + "Error"] = ex.GetType().Name + ": " + ex.Message; }
    }

    Try("startParam", () => parcel.StartParam);
    Try("endParam", () => parcel.EndParam);
    Try("lengthViaParam", () => parcel.GetDistanceAtParameter(parcel.EndParam) - parcel.GetDistanceAtParameter(parcel.StartParam));
    Try("geCurve", () =>
    {
      var gc = parcel.GetGeCurve();
      if (gc == null) return null;
      if (gc is Autodesk.AutoCAD.Geometry.CompositeCurve3d composite)
        return "composite(" + string.Join(",", composite.GetCurves().Select(c => c.GetType().Name).Take(40)) + ")";
      return "single:" + gc.GetType().Name;
    });
    Try("spline", () =>
    {
      var sp = parcel.Spline;
      if (sp == null) return null;
      return "controlPoints=" + sp.NumControlPoints + ",fitPoints=" + sp.NumFitPoints + ",degree=" + sp.Degree;
    });
    return info;
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// 2026-11-18 P32: siteName 可省 —— 只有 1 个 site 时自动取它（回 siteNameSource=only-site），
  /// 0 个 / 多个时报可行动的错误（★ 列出所有 site 名，别让客户 AI 猜）。
  /// </summary>
  private static string ResolveSiteName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction, string? requested, out string source)
  {
    if (!string.IsNullOrWhiteSpace(requested))
    {
      source = "explicit";
      return requested!.Trim();
    }

    var names = new List<string>();
    foreach (ObjectId sid in EnumerateSiteIds(civilDoc))
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, sid, OpenMode.ForRead);
      if (!string.IsNullOrWhiteSpace(site.Name)) names.Add(site.Name);
    }

    if (names.Count == 1)
    {
      source = "only-site";
      return names[0];
    }
    if (names.Count == 0)
    {
      source = "none";
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        "No parcel sites found in this drawing. Create a site (or a parcel on one) first, then retry.");
    }

    source = "ambiguous";
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"siteName is required when the drawing has more than one parcel site (found {names.Count}). Sites: {string.Join(", ", names.Take(10))}{(names.Count > 10 ? ", ..." : "")}.");
  }

  private static Site FindSiteByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction, string siteName, OpenMode mode = OpenMode.ForRead)
  {
    foreach (ObjectId sid in EnumerateSiteIds(civilDoc))
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, sid, mode);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase)) return site;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Site '{siteName}' was not found.");
  }

  private static Parcel FindParcelByName(Site site, Transaction transaction, string parcelName, OpenMode mode)
  {
    foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
    {
      var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, mode);
      if (string.Equals(parcel.Name, parcelName, StringComparison.OrdinalIgnoreCase)) return parcel;
    }
    // 2026-11-18 P32: 名字找不到时只报 OBJECT_NOT_FOUND，客户 AI 只能靠猜继续。
    // 现在把近似名一起列出来（包含匹配 + 同名编号），一次就能改对。
    var all = new List<string>();
    foreach (ObjectId anyId in EnumerateParcelIds(site, transaction))
    {
      var any = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, anyId, OpenMode.ForRead);
      all.Add(any.Name);
    }
    var query = parcelName.Trim();
    var tokens = query.Split(new[] { ' ', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
    var similar = all
      .Where(n => !string.Equals(n, query, StringComparison.OrdinalIgnoreCase))
      .Select(n => new
      {
        Name = n,
        Score =
          (n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 10 : 0) +
          tokens.Count(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) +
          (tokens.Any(t => int.TryParse(t, out _) && n.EndsWith(t, StringComparison.Ordinal)) ? 5 : 0),
      })
      .Where(x => x.Score > 0)
      .OrderByDescending(x => x.Score).ThenBy(x => x.Name)
      .Take(5).Select(x => x.Name).ToList();

    var hint = similar.Count > 0
      ? $" Did you mean: {string.Join(", ", similar)}?"
      : (all.Count > 0
        ? $" Site '{site.Name}' has {all.Count} parcels, e.g. {string.Join(", ", all.Take(3))}."
        : $" Site '{site.Name}' has no parcels.");
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parcel '{parcelName}' was not found in site '{site.Name}'.{hint}");
  }


  private static void ApplyStyleToParcel(Parcel parcel, string styleName,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    parcel.StyleId = LookupUtils.GetParcelStyleId(civilDoc, transaction, styleName);
  }

  private static void ApplyLabelStyleToParcel(Parcel parcel, string labelStyleName,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    parcel.AreaSelectionLabelStyleId = LookupUtils.GetParcelAreaLabelStyleId(civilDoc, transaction, labelStyleName);
  }

  private static IEnumerable<ObjectId> EnumerateSiteIds(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      if (objectId != ObjectId.Null)
        yield return objectId;
    }
  }

  private static IEnumerable<ObjectId> EnumerateParcelIds(Site site, Transaction transaction)
  {
    var documentedIds = new HashSet<ObjectId>();
    foreach (ObjectId objectId in site.GetParcelIds())
    {
      if (objectId != ObjectId.Null && documentedIds.Add(objectId))
        yield return objectId;
    }

    // Compatibility fallbacks are retained for older host variations, but
    // de-duplicate anything already returned by the documented 2026 API.
    foreach (var memberName in new[] { "Parcels", "ParcelCollection", "ParcelIds" })
    {
      var value = GetNamedMember(site, memberName) ?? CivilObjectUtils.InvokeMethod(site, memberName);

      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null && documentedIds.Add(objectId))
          yield return objectId;
      }

      if (value is System.Collections.IEnumerable enumerable)
      {
        foreach (var item in enumerable)
        {
          if (item is Parcel parcel)
          {
            if (documentedIds.Add(parcel.ObjectId)) yield return parcel.ObjectId;
          }
          else if (item is Autodesk.AutoCAD.DatabaseServices.DBObject dbObject)
          {
            if (documentedIds.Add(dbObject.ObjectId)) yield return dbObject.ObjectId;
          }
        }
      }
    }
  }

  private static object? GetNamedMember(object? value, string memberName)
  {
    if (value == null) return null;

    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  /// <summary>
  /// 2026-11-18 P32: Parcel 没有 Perimeter 属性（它是 Autodesk.AutoCAD.DatabaseServices.Curve 派生，
  /// Area 就来自 Curve.Area）。旧实现用反射读 Perimeter 属性 + 猜 GetBoundary 方法，
  /// 两者在 Parcel 上都不存在 → 34/34 地块 perimeter 恒 null（静默错值）。
  /// 真实周长 = Curve 参数弧长；曲线参数范围 [StartParam, EndParam]。
  /// </summary>
  private static double? GetParcelPerimeter(Parcel parcel)
  {
    try
    {
      var length = parcel.GetDistanceAtParameter(parcel.EndParam)
                 - parcel.GetDistanceAtParameter(parcel.StartParam);
      if (length > 0 && !double.IsNaN(length) && !double.IsInfinity(length)) return length;
    }
    catch { }
    return null;
  }

  /// <summary>
  /// 地块边界顶点。Parcel.GetGeCurve()/Spline 在本版本都是 eNotImplementedYet / eNotApplicable，
  /// 只能走 Curve 参数取样：C3D 里地块参数按「线段序号」编（5 条边 → EndParam=5），
  /// 因此按整数参数取点就是真实顶点；参数不是整数时才退化成按弧长采样。
  /// </summary>
  private static List<Dictionary<string, object?>> GetParcelVertices(Parcel parcel, out string? source)
  {
    source = null;
    var list = new List<Dictionary<string, object?>>();
    try
    {
      var start = parcel.StartParam;
      var end = parcel.EndParam;
      var span = end - start;
      if (span > 0 && Math.Abs(span - Math.Round(span)) < 1e-9 && span <= 400)
      {
        var count = (int)Math.Round(span);
        for (var i = 0; i <= count; i++)
        {
          var pt = parcel.GetPointAtParameter(start + i);
          list.Add(new Dictionary<string, object?> { ["x"] = Math.Round(pt.X, 6), ["y"] = Math.Round(pt.Y, 6) });
        }
        source = "curve-parameter";
        return list;
      }

      var length = GetParcelPerimeter(parcel);
      if (!length.HasValue || length.Value <= 0) { source = "unavailable"; return list; }
      const int samples = 32;
      for (var i = 0; i <= samples; i++)
      {
        var pt = parcel.GetPointAtDist(length.Value * i / samples);
        list.Add(new Dictionary<string, object?> { ["x"] = Math.Round(pt.X, 6), ["y"] = Math.Round(pt.Y, 6) });
      }
      source = "arc-length-sampled";
    }
    catch (Exception ex)
    {
      source = "error:" + ex.GetType().Name;
    }
    return list;
  }

  /// <summary>
  /// 按弧长等距采样曲线（圆弧用折线逼近）。样本数钳制 16..2000。
  /// 与 GetParcelPerimeter 走的是不同 API 路径（GetPointAtDist + 逐段求和），可做交叉校验。
  /// </summary>
  private static List<Dictionary<string, object?>> SampleParcelDense(Parcel parcel, int requested, out string? source)
  {
    source = null;
    var list = new List<Dictionary<string, object?>>();
    try
    {
      var length = GetParcelPerimeter(parcel);
      if (!length.HasValue || length.Value <= 0) { source = "unavailable"; return list; }
      var samples = Math.Min(Math.Max(requested, 16), 2000);
      for (var i = 0; i <= samples; i++)
      {
        var pt = parcel.GetPointAtDist(length.Value * i / samples);
        list.Add(new Dictionary<string, object?> { ["x"] = Math.Round(pt.X, 6), ["y"] = Math.Round(pt.Y, 6) });
      }
      source = "arc-length-sampled:" + samples;
    }
    catch (Exception ex) { source = "error:" + ex.GetType().Name; }
    return list;
  }

  /// <summary>顶点闭合多段线长度（与 GetParcelPerimeter 交叉校验用）。</summary>
  private static double? VerticesPerimeter(List<Dictionary<string, object?>> vertices)
  {
    if (vertices.Count < 3) return null;
    double sum = 0;
    for (var i = 1; i < vertices.Count; i++)
    {
      var a = vertices[i - 1];
      var b = vertices[i];
      var dx = Convert.ToDouble(b["x"]) - Convert.ToDouble(a["x"]);
      var dy = Convert.ToDouble(b["y"]) - Convert.ToDouble(a["y"]);
      sum += Math.Sqrt(dx * dx + dy * dy);
    }
    var first = vertices[0];
    var last = vertices[^1];
    var cdx = Convert.ToDouble(first["x"]) - Convert.ToDouble(last["x"]);
    var cdy = Convert.ToDouble(first["y"]) - Convert.ToDouble(last["y"]);
    var closing = Math.Sqrt(cdx * cdx + cdy * cdy);
    if (closing > 1e-9) sum += closing;
    return sum;
  }


  private static int GetParcelNumber(Parcel parcel)
  {
    return parcel.Number;
  }

  /// <summary>
  /// 2026-11-18 P32: 旧实现写死「图纸单位 = 英尺」——米制图上 units:"sqm" 会把 m² 再乘 0.0929（差 10.76 倍），
  /// 未知单位还会原样返回却照样打上 areaUnits 标签（静默错值）。现在按图纸线性单位换算，未知单位直接报错。
  /// 1 ft = 0.3048 m；1 sqft = 0.09290304 m²；1 acre = 43560 sqft；1 ha = 10000 m²。
  /// </summary>
  private static double ConvertArea(double areaInDrawingUnits, string units, Autodesk.AutoCAD.DatabaseServices.Database database)
  {
    var isMetric = string.Equals(CivilObjectUtils.LinearUnits(database), "meters", StringComparison.OrdinalIgnoreCase);
    var sqft = isMetric ? areaInDrawingUnits * 10.763910416709722 : areaInDrawingUnits;
    var sqm = isMetric ? areaInDrawingUnits : areaInDrawingUnits * 0.09290304;
    return (units ?? "sqft").Trim().ToLowerInvariant() switch
    {
      "sqft" => sqft,
      "sqm" => sqm,
      "acres" => sqft / 43560.0,
      "ha" => sqm / 10000.0,
      var other => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Unknown area units '{other}'. Use sqft | sqm | acres | ha. (Drawing linear units: {CivilObjectUtils.LinearUnits(database)}.)"),
    };
  }

  private static double TargetAreaToDrawingUnits(double sqFt, Autodesk.AutoCAD.ApplicationServices.Document doc)
  {
    // Drawing units are usually sq ft already for US drawings
    return sqFt;
  }

  private static string WriteCsv(
    string outputPath,
    List<Dictionary<string, object?>> rows,
    bool includeCoordinates,
    bool overwrite)
  {
    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Name,Area,AreaUnits,Perimeter,Style");
    foreach (var row in rows)
    {
      csv.AppendLine(string.Join(",",
        row.GetValueOrDefault("name"), row.GetValueOrDefault("area"),
        row.GetValueOrDefault("areaUnits"), row.GetValueOrDefault("perimeter"),
        row.GetValueOrDefault("style")));
    }

    return FileBoundary.WriteAllTextAtomic(
      outputPath, csv.ToString(), System.Text.Encoding.UTF8, overwrite, ".csv");
  }

  private static Type? FindType(string typeName)
  {
    return Civil3DCompatibility.FindLoadedType(typeName);
  }
}
