using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

public static class SurfaceCommands
{
  public static Task<object?> ListSurfacesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surfaces = civilDoc.GetSurfaceIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead))
        .Select(surface => new Dictionary<string, object?>
        {
          ["name"] = surface.Name,
          ["handle"] = CivilObjectUtils.GetHandle(surface),
          ["type"] = MapSurfaceType(surface),
          ["isReference"] = surface.IsReferenceObject,
          ["sourcePath"] = CivilObjectUtils.GetStringProperty(surface, "ReferencePath"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["surfaces"] = surfaces,
      };
    });
  }

  public static Task<object?> GetSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      // 2026-09-28: 廊道曲面的 GeometricExtents 可能不可用 → 容错，不让整个 getSurface 挂掉
      var terrainProperties = GetTerrainProperties(surface);
      var haveExtents = TryGetExtents(surface, out var ex0, out var ey0, out var ex1, out var ey1);

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["type"] = MapSurfaceType(surface),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(surface.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = surface.Layer,
        ["statistics"] = new Dictionary<string, object?>
        {
          ["minimumElevation"] = generalProperties.MinimumElevation,
          ["maximumElevation"] = generalProperties.MaximumElevation,
          ["meanElevation"] = generalProperties.MeanElevation,
          ["area2d"] = terrainProperties?.SurfaceArea2D,
          ["area3d"] = terrainProperties?.SurfaceArea3D,
          ["numberOfPoints"] = generalProperties.NumberOfPoints,
          ["numberOfTriangles"] = GetTriangleCount(surface),
        },
        ["boundingBox"] = haveExtents
          ? new Dictionary<string, object?>
          {
            ["minX"] = ex0,
            ["minY"] = ey0,
            ["maxX"] = ex1,
            ["maxY"] = ey1,
          }
          : null,
        ["boundingBoxUnavailable"] = !haveExtents,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["isReference"] = surface.IsReferenceObject,
        ["dependentAlignments"] = new List<string>(),
        ["dependentCorridors"] = new List<string>(),
      };
    });
  }

  public static Task<object?> GetSurfaceElevationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var elevation = InvokeSurfaceElevation(surface, x, y);
      return new Dictionary<string, object?>
      {
        ["elevation"] = elevation,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["surfaceName"] = surface.Name,
      };
    });
  }

  public static Task<object?> GetSurfaceElevationsAlongAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "getSurfaceElevationsAlong requires 'points'.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var samples = new List<Dictionary<string, object?>>();
      foreach (var point in pointsNode.OfType<JsonObject>())
      {
        var x = PluginRuntime.GetRequiredDoubleFromNode(point["x"], "points[].x");
        var y = PluginRuntime.GetRequiredDoubleFromNode(point["y"], "points[].y");
        samples.Add(new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["elevation"] = InvokeSurfaceElevation(surface, x, y),
        });
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> GetSurfaceStatisticsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var analysisType = PluginRuntime.GetOptionalString(parameters, "analysisType");
    // 2026-11-16 P2: 量算自证。面积/体积这类数字，客户没法自己判对错 —— 让调用方把
    //   "应该是多少"一起传进来（桩号长度 x 宽度），这里回报偏差与结论。
    var expectLength = PluginRuntime.GetOptionalDouble(parameters, "expectLength");
    var expectWidth = PluginRuntime.GetOptionalDouble(parameters, "expectWidth");
    var expectArea2d = PluginRuntime.GetOptionalDouble(parameters, "expectArea2d");
    var tolerancePct = PluginRuntime.GetOptionalDouble(parameters, "tolerancePct") ?? 2.0;
    if (expectArea2d == null && expectLength.HasValue && expectWidth.HasValue)
      expectArea2d = expectLength.Value * expectWidth.Value;

    return CivilExecution.ReadAsyncLight<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      // 2026-11-12: 刚建的曲面没有任何点/三角面时，GetGeneralProperties() 会抛
      // InvalidOperationException("The surface has no point or triangle.")，旧实现直接冒成
      // INTERNAL_ERROR —— 调用方只会以为"插件坏了"。空曲面是合法状态，这里如实返回 0/null。
      string? emptyNote = null;
      Dictionary<string, object?> payload;
      try
      {
        var generalProperties = surface.GetGeneralProperties();
        // 2026-09-28: 廊道曲面的 GeometricExtents 可能不可用 → 容错，不让整个 getSurface 挂掉
        var terrainProperties = GetTerrainProperties(surface);
        payload = new Dictionary<string, object?>
        {
          ["surfaceName"] = surface.Name,
          ["analysisType"] = analysisType,
          ["minimumElevation"] = generalProperties.MinimumElevation,
          ["maximumElevation"] = generalProperties.MaximumElevation,
          ["meanElevation"] = generalProperties.MeanElevation,
          ["area2d"] = terrainProperties?.SurfaceArea2D,
          ["area3d"] = terrainProperties?.SurfaceArea3D,
          ["numberOfPoints"] = generalProperties.NumberOfPoints,
          ["numberOfTriangles"] = GetTriangleCount(surface),
        };
      }
      catch (InvalidOperationException ex)
      {
        emptyNote = "surface has no point or triangle yet: " + ex.Message;
        payload = new Dictionary<string, object?>
        {
          ["surfaceName"] = surface.Name,
          ["analysisType"] = analysisType,
          ["minimumElevation"] = null,
          ["maximumElevation"] = null,
          ["meanElevation"] = null,
          ["area2d"] = null,
          ["area3d"] = null,
          ["numberOfPoints"] = 0,
          ["numberOfTriangles"] = 0,
        };
      }
      if (emptyNote != null) payload["note"] = emptyNote;

      // 2026-11-16 P2: 期望值对账（只在调用方给了期望时出现）
      if (expectArea2d.HasValue && expectArea2d.Value > 0)
      {
        var expected = expectArea2d.Value;
        payload["expectedArea2d"] = expected;
        if (payload["area2d"] is double actual)
        {
          var devPct = (actual - expected) / expected * 100.0;
          payload["deviationPct"] = Math.Round(devPct, 3);
          payload["tolerancePct"] = tolerancePct;
          var abs = Math.Abs(devPct);
          payload["verdict"] = abs <= tolerancePct ? "ok" : (abs <= tolerancePct * 2 ? "warn" : "fail");
          if (abs > tolerancePct)
          {
            payload["verdictNote"] =
              $"area2d {actual:F2} vs expected {expected:F2} ({devPct:+0.00;-0.00}%). " +
              "Corridor surfaces are only trustworthy when built from ONE purpose group of link codes " +
              "(e.g. Surface+DitchTop for a finish surface). Mounting every code double-counts the face.";
          }
        }
        else
        {
          payload["deviationPct"] = null;
          payload["verdict"] = "fail";
          payload["verdictNote"] = "surface has no area yet (no point or triangle)";
        }
      }

      return payload;
    });
  }

  public static Task<object?> CreateSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var surfaceId = CreateTinSurface(name, styleId);
      var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfaceId, OpenMode.ForWrite);
      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layerName))
      {
        surface.Layer = layerName;
      }

      var description = PluginRuntime.GetOptionalString(parameters, "description");
      if (!string.IsNullOrWhiteSpace(description))
      {
        surface.Description = description;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> RenameSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      CivilObjectUtils.TrySetName(surface, newName);
      return new Dictionary<string, object?>
      {
        ["oldName"] = name,
        ["newName"] = CivilObjectUtils.GetName(surface),
        ["renamed"] = true,
      };
    });
  }

  public static Task<object?> DeleteSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      surface.Erase();
      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  public static Task<object?> AddSurfacePointsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var points = ParseRequiredPoint3dArray(parameters, "points", "addSurfacePoints requires at least one point.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding TIN vertices is not supported for surface type '{surface.GetType().Name}'.");
      }
      tinSurface.AddVertices(points);

      SetSurfaceDescription(surface, description);
      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["pointsAdded"] = points.Count,
        ["description"] = surface.Description,
      };
    });
  }

  // 2026-08-07: 文件通道——从任意文本文件批量加曲面点（AI 判断格式→插件按参数解析，大文件不经过 LLM 上下文）
  public static Task<object?> AddSurfacePointsFromFileAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var filePath = PluginRuntime.GetRequiredString(parameters, "filePath");
    // 2026-08-12: 套 FileBoundary（全盘已放开, 补防穿越/规范化护栏; 旧实现裸读无检查）
    filePath = FileBoundary.ResolveImportPath(filePath, "csv", "txt", "pnezd", "penz", "xyz", "xyzd");
    var delimiter = (PluginRuntime.GetOptionalString(parameters, "delimiter") ?? "auto").ToLowerInvariant();
    var hasHeader = PluginRuntime.GetOptionalBool(parameters, "hasHeader") ?? false;
    var xCol = PluginRuntime.GetOptionalInt(parameters, "xCol") ?? 0;
    var yCol = PluginRuntime.GetOptionalInt(parameters, "yCol") ?? 1;
    var zCol = PluginRuntime.GetOptionalInt(parameters, "zCol") ?? 2;
    var skipRows = PluginRuntime.GetOptionalInt(parameters, "skipRows") ?? 0;
    // 读文件并解析（事务外，无需文档锁；AI 已判断格式，插件按参数批量解析）
    var points = ParsePointFile(filePath, delimiter, hasHeader, xCol, yCol, zCol, skipRows);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding TIN vertices is not supported for surface type '{surface.GetType().Name}'.");
      }
      tinSurface.AddVertices(points);
      RebuildSurfaceIfAvailable(surface);
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["pointsAdded"] = points.Count,
        ["source"] = filePath,
        ["parsed"] = new Dictionary<string, object?> { ["delimiter"] = delimiter, ["hasHeader"] = hasHeader, ["columns"] = new[] { xCol, yCol, zCol } },
      };
    });
  }

  private static Point3dCollection ParsePointFile(string path, string delimiter, bool hasHeader, int xCol, int yCol, int zCol, int skipRows)
  {
    var points = new Point3dCollection();
    var lines = System.IO.File.ReadAllLines(path);
    var headerSkipped = !hasHeader;
    // 2026-11-14: 解析 0 点时给「可行动诊断」——原先只说"请检查列序"，AI 只能盲猜列号（实测两次猜错→被封锁→降级）
    var blank = 0;
    var skippedByRows = 0;
    var skippedByWidth = 0;
    var skippedByParse = 0;
    var widthHistogram = new Dictionary<int, int>();
    var sample = string.Empty;
    char[] seps = delimiter switch
    {
      "comma" => new[] { ',' },
      "space" => new[] { ' ', '\t' },
      "tab" => new[] { '\t' },
      "semicolon" => new[] { ';' },
      _ => new[] { ',', ';', '\t', ' ' },   // auto: 全部尝试
    };
    foreach (var raw in lines)
    {
      var line = raw.Trim();
      if (line.Length == 0) { blank++; continue; }
      if (skipRows > 0) { skipRows--; skippedByRows++; continue; }   // 跳过前 N 行
      if (!headerSkipped) { headerSkipped = true; continue; }   // 表头行
      var parts = line.Split(seps, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length > 0)
      {
        widthHistogram.TryGetValue(parts.Length, out var wc);
        widthHistogram[parts.Length] = wc + 1;
        if (sample.Length == 0) sample = line.Length > 160 ? line.Substring(0, 160) : line;
      }
      if (parts.Length <= Math.Max(xCol, Math.Max(yCol, zCol))) { skippedByWidth++; continue; }
      if (!double.TryParse(parts[xCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)) { skippedByParse++; continue; }   // 非数字行跳过
      if (!double.TryParse(parts[yCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)) { skippedByParse++; continue; }
      if (!double.TryParse(parts[zCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)) { skippedByParse++; continue; }
      points.Add(new Point3d(x, y, z));
      if (points.Count > 100000) break;   // 防爆上限
    }
    if (points.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", BuildNoPointsDiagnostic(path, hasHeader, xCol, yCol, zCol, lines.Length, blank, skippedByRows, skippedByWidth, skippedByParse, widthHistogram, sample));
    return points;
  }

  /// <summary>
  /// 2026-11-14: 点文件 0 命中时的诊断文案。必须回答三件事：文件到底几列、你请求了哪几列、列号从几开始。
  /// 判据全部来自本次解析实测，不做猜测；建议只给"怎么改参数"，不给"帮你猜一个列序"。
  /// </summary>
  private static string BuildNoPointsDiagnostic(string path, bool hasHeader, int xCol, int yCol, int zCol, int totalLines, int blank, int skippedByRows, int skippedByWidth, int skippedByParse, Dictionary<int, int> widthHistogram, string sample)
  {
    var typical = widthHistogram.Count > 0
      ? widthHistogram.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).First()
      : 0;
    var maxReq = Math.Max(xCol, Math.Max(yCol, zCol));
    var minReq = Math.Min(xCol, Math.Min(yCol, zCol));
    string hint;
    if (typical > 0 && maxReq >= typical)
      hint = Loc.Tk("error.surfaceNoValidPoints.hintColumnRange", ("typical", typical), ("req", $"{xCol}/{yCol}/{zCol}"), ("max", typical - 1));
    else if (minReq < 0)
      hint = Loc.Tk("error.surfaceNoValidPoints.hintNegative", ("req", $"{xCol}/{yCol}/{zCol}"));
    else if (skippedByParse > 0)
      hint = Loc.Tk("error.surfaceNoValidPoints.hintNotNumber", ("parse", skippedByParse));
    else
      hint = Loc.Tk("error.surfaceNoValidPoints.hintNoDataRows", ("rows", skippedByRows), ("width", skippedByWidth));
    return Loc.Tk("error.surfaceNoValidPoints.diag",
      ("path", path),
      ("lines", totalLines),
      ("blank", blank),
      ("skipRows", skippedByRows),
      ("header", hasHeader ? "true" : "false"),
      ("typical", typical > 0 ? typical.ToString() : "?"),
      ("req", $"{xCol}/{yCol}/{zCol}"),
      ("sample", sample),
      ("hint", hint));
  }

  public static Task<object?> AddSurfaceBreaklineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var breaklineType = PluginRuntime.GetOptionalString(parameters, "breaklineType") ?? "standard";
    if (!string.Equals(breaklineType, "standard", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Breakline type '{breaklineType}' is not supported by this endpoint. No surface was modified; use breaklineType='standard'.");
    }
    var points = ParseRequiredPoint3dArray(parameters, "points", "addSurfaceBreakline requires at least two points.");
    if (points.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "addSurfaceBreakline requires at least two points.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding standard breaklines is not supported for surface type '{surface.GetType().Name}'.");
      }
      tinSurface.BreaklinesDefinition.AddStandardBreaklines(points, 1.0, 0.0, 0.0, 0.0);

      SetSurfaceDescription(surface, description);
      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["breaklineType"] = breaklineType,
        ["vertexCount"] = points.Count,
        ["description"] = surface.Description,
      };
    });
  }

  public static Task<object?> AddSurfaceBoundaryAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var boundaryType = PluginRuntime.GetOptionalString(parameters, "boundaryType") ?? "outer";
    var points = ParseRequiredPoint2dArray(parameters, "points", "addSurfaceBoundary requires at least three points.");
    if (points.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "addSurfaceBoundary requires at least three points.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      surface.BoundariesDefinition.AddBoundaries(points, 1.0, ResolveBoundaryType(boundaryType), true);

      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["boundaryType"] = boundaryType,
        ["vertexCount"] = points.Count,
      };
    });
  }

  // ─── addSurfaceContours / elevatePolylines (2026-11-12, v1.16.0) ──────────
  //
  // 为什么加这两个：本项目 443 条等高线是 AcDb2dPolyline —— 高程存在**对象属性**里、
  // 顶点 Z 全是 0（18 万顶点里 17.8 万 Z=0）。addSurfacePoints / addSurfaceBreakline 都要
  // 显式 z，于是这批等高线根本进不了曲面，EG 只能拿 1,481 个标高注记点建（点距 ~40 m，
  // 土方量 ±10~20%）。而 Civil 3D 原生就有「按多段线对象高程建等高线」：AddContours。
  //
  //   addSurfaceContours {name, handles?, layer?, skipZeroElevation?, description?, maxCount?}
  //   elevatePolylines   {handles?, layer?, deleteSource?, targetLayer?}  ← 降级路径 / 横断面复用
  //
  // 两个都只走事务、不碰命令上下文：AddContours 是纯 API，不需要 MERGE 那种交互命令。

  public static Task<object?> AddSurfaceContoursAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var handles = PluginRuntime.GetOptionalStringArray(parameters, "handles");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    if ((handles == null || handles.Length == 0) && string.IsNullOrWhiteSpace(layer))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "addSurfaceContours requires either handles[] or layer (nothing was changed).");
    }
    var skipZeroElevation = PluginRuntime.GetOptionalBool(parameters, "skipZeroElevation") ?? true;
    var midOrdinate = PluginRuntime.GetOptionalDouble(parameters, "midOrdinateDistance") ?? 1.0;
    var maxDistance = PluginRuntime.GetOptionalDouble(parameters, "maximumDistance") ?? 10.0;
    var minDistance = PluginRuntime.GetOptionalDouble(parameters, "minimumDistance") ?? 0.5;
    var weeding = PluginRuntime.GetOptionalDouble(parameters, "weedingFactor") ?? 0.0;
    if (maxDistance <= weeding || maxDistance <= 0 || midOrdinate <= 0)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"addSurfaceContours needs midOrdinateDistance > 0 and maximumDistance > weedingFactor (Civil 3D rejects maximumDistance <= weedingDistance); got midOrdinate={midOrdinate}, max={maxDistance}, min={minDistance}, weeding={weeding} - nothing was changed.");
    }
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 5000;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Contour definitions are only supported on TIN surfaces; '{surface.Name}' is a {surface.GetType().Name}.");
      }

      var skipped = new List<Dictionary<string, object?>>();
      var candidateIds = new List<ObjectId>();

      if (handles != null && handles.Length > 0)
      {
        foreach (var handle in handles)
        {
          if (candidateIds.Count >= maxCount) { skipped.Add(new Dictionary<string, object?> { ["handle"] = handle, ["reason"] = "maxCount-reached" }); continue; }
          if (!TryResolveHandle(database, handle, out var id))
          {
            skipped.Add(new Dictionary<string, object?> { ["handle"] = handle, ["reason"] = "handle-not-found" });
            continue;
          }
          candidateIds.Add(id);
        }
      }
      else
      {
        var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
          transaction, CivilObjectUtils.GetModelSpaceBlockId(database, transaction), OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
          Autodesk.AutoCAD.DatabaseServices.Entity? ent;
          try { ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity; } catch { continue; }
          if (ent == null || !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
          candidateIds.Add(id);
          if (candidateIds.Count >= maxCount) break;
        }
      }

      var contourIds = new ObjectIdCollection();
      var zeroElevation = 0;
      var notPolylines = 0;
      var vertexCount = 0;
      double? minElevation = null, maxElevation = null;

      foreach (var id in candidateIds)
      {
        Autodesk.AutoCAD.DatabaseServices.Entity? ent;
        try { ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (CivilObjectUtils.IsErasedException(ex)) { continue; }
        catch { continue; }
        if (ent == null) continue;

        var handleText = id.Handle.ToString();
        double? elevation;
        switch (ent)
        {
          case Polyline lw:
            elevation = lw.Elevation;
            vertexCount += lw.NumberOfVertices;
            break;
          case Polyline2d p2:
            elevation = p2.Elevation;
            break;
          case Polyline3d:
            elevation = null;   // 真 3D：Z 在顶点里，AddContours 直接按顶点走
            break;
          default:
            notPolylines++;
            skipped.Add(new Dictionary<string, object?> { ["handle"] = handleText, ["reason"] = "not-a-polyline:" + ent.GetType().Name });
            continue;
        }

        if (skipZeroElevation && elevation.HasValue && Math.Abs(elevation.Value) < 1.0e-9)
        {
          zeroElevation++;
          skipped.Add(new Dictionary<string, object?> { ["handle"] = handleText, ["reason"] = "object-elevation-is-zero" });
          continue;
        }

        if (elevation.HasValue)
        {
          minElevation = minElevation.HasValue ? Math.Min(minElevation.Value, elevation.Value) : elevation.Value;
          maxElevation = maxElevation.HasValue ? Math.Max(maxElevation.Value, elevation.Value) : elevation.Value;
        }
        contourIds.Add(id);
      }

      // 一条都没剩下就别动曲面（避免"空操作也算成功"）
      if (contourIds.Count == 0)
      {
        return new Dictionary<string, object?>
        {
          ["surfaceName"] = surface.Name,
          ["requested"] = candidateIds.Count,
          ["contoursAdded"] = 0,
          ["surfaceModified"] = false,
          ["zeroElevationSkipped"] = zeroElevation,
          ["notPolylines"] = notPolylines,
          ["skipped"] = skipped,
          ["note"] = "No usable contour was left, so the surface was not touched.",
        };
      }

      // 2026-11-12 真机实测：AddContours 会校验 maximumDistance > weedingDistance，
      // 传 (1.0, 0.0, 0.0, 0.0) 直接 ArgumentException("The maximumDistance should greater than weedingDistance.")。
      // 默认给 C3D 常规值（中点距离 1.0 / 最大 10.0 / 最小 0.5 / 不 weed），需要更细可传参覆盖。
      tinSurface.ContoursDefinition.AddContours(contourIds, midOrdinate, maxDistance, minDistance, weeding);
      SetSurfaceDescription(surface, description);
      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["requested"] = candidateIds.Count,
        ["contoursAdded"] = contourIds.Count,
        ["surfaceModified"] = true,
        ["zeroElevationSkipped"] = zeroElevation,
        ["notPolylines"] = notPolylines,
        ["vertexCount"] = vertexCount,
        ["elevationMin"] = minElevation,
        ["elevationMax"] = maxElevation,
        ["skipped"] = skipped,
      };
    });
  }

  public static Task<object?> ElevatePolylinesAsync(JsonObject? parameters)
  {
    var handles = PluginRuntime.GetOptionalStringArray(parameters, "handles");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    if ((handles == null || handles.Length == 0) && string.IsNullOrWhiteSpace(layer))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "elevatePolylines requires either handles[] or layer (nothing was changed).");
    }
    var deleteSource = PluginRuntime.GetOptionalBool(parameters, "deleteSource") ?? false;
    var targetLayer = PluginRuntime.GetOptionalString(parameters, "targetLayer");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 2000;

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      var space = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
        transaction, CivilObjectUtils.GetModelSpaceBlockId(database, transaction), OpenMode.ForWrite);

      var sourceIds = new List<ObjectId>();
      if (handles != null && handles.Length > 0)
      {
        foreach (var handle in handles)
          if (TryResolveHandle(database, handle, out var id)) sourceIds.Add(id);
      }
      else
      {
        foreach (ObjectId id in space)
        {
          Autodesk.AutoCAD.DatabaseServices.Entity? ent;
          try { ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity; } catch { continue; }
          if (ent == null || !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
          sourceIds.Add(id);
          if (sourceIds.Count >= maxCount) break;
        }
      }

      var created = new List<Dictionary<string, object?>>();
      var skipped = new List<Dictionary<string, object?>>();

      foreach (var id in sourceIds)
      {
        Autodesk.AutoCAD.DatabaseServices.Entity? ent;
        try { ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (CivilObjectUtils.IsErasedException(ex)) { continue; }
        catch { continue; }
        if (ent == null) continue;
        var handleText = id.Handle.ToString();

        var points = new Point3dCollection();
        bool closed;
        switch (ent)
        {
          case Polyline lw:
            closed = lw.Closed;
            for (var i = 0; i < lw.NumberOfVertices; i++)
            {
              var p = lw.GetPoint2dAt(i);
              points.Add(new Point3d(p.X, p.Y, lw.Elevation));
            }
            break;
          case Polyline2d p2:
            closed = p2.Closed;
            foreach (var child in (System.Collections.IEnumerable)p2)
            {
              if (child is not ObjectId vid || vid.IsNull) continue;
              if (transaction.GetObject(vid, OpenMode.ForRead) is Vertex2d v && v.Position is Point3d pv)
                points.Add(pv);   // Vertex2d.Position 的 Z 已经是多段线高程
            }
            break;
          case Polyline3d:
            skipped.Add(new Dictionary<string, object?> { ["handle"] = handleText, ["reason"] = "already-3d" });
            continue;
          default:
            skipped.Add(new Dictionary<string, object?> { ["handle"] = handleText, ["reason"] = "not-a-polyline:" + ent.GetType().Name });
            continue;
        }

        if (points.Count < 2)
        {
          skipped.Add(new Dictionary<string, object?> { ["handle"] = handleText, ["reason"] = "fewer-than-two-vertices" });
          continue;
        }

        var elevated = new Polyline3d(Poly3dType.SimplePoly, points, closed);
        elevated.Layer = targetLayer ?? ent.Layer;
        space.AppendEntity(elevated);
        transaction.AddNewlyCreatedDBObject(elevated, true);
        created.Add(new Dictionary<string, object?>
        {
          ["sourceHandle"] = handleText,
          ["handle"] = elevated.Handle.ToString(),
          ["vertexCount"] = points.Count,
          ["z"] = points[0].Z,
        });

        if (deleteSource)
        {
          ent.UpgradeOpen();   // ??????????? OpenMode ?????????????
          ent.Erase();
        }
      }

      return new Dictionary<string, object?>
      {
        ["created"] = created,
        ["createdCount"] = created.Count,
        ["skipped"] = skipped,
        ["deleteSource"] = deleteSource,
      };
    });
  }

  private static bool TryResolveHandle(Database database, string handle, out ObjectId id)
  {
    id = ObjectId.Null;
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber,
          System.Globalization.CultureInfo.InvariantCulture, out var value))
      return false;
    try { id = database.GetObjectId(false, new Handle(value), 0); }
    catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
    return !id.IsNull;
  }

  public static Task<object?> ExtractSurfaceContoursAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minorInterval = PluginRuntime.GetRequiredDouble(parameters, "minorInterval");
    var majorInterval = PluginRuntime.GetRequiredDouble(parameters, "majorInterval");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var extracted = ExtractContourEntities(surface, minorInterval, majorInterval);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["minorInterval"] = minorInterval,
        ["majorInterval"] = majorInterval,
        ["contourCount"] = extracted.Count,
        ["handles"] = extracted
          .Select(objectId => CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Autodesk.AutoCAD.DatabaseServices.Entity>(transaction, objectId, OpenMode.ForRead)))
          .ToList(),
      };
    });
  }

  // ── 2026-09-28 A3 修复 ──────────────────────────────────────────────
  // 实测根因：只要输入里含"廊道曲面"，TinVolumeSurface.Create 立即抛
  //   ArgumentException: Fail to create a surface（纯 TIN 之间正常）。同时廊道曲面的
  //   GeometricExtents 不可靠（网格采样会得到 0 个点）。
  // 对策：① 网格采样法兜底（不依赖体积曲面对象，还能给出挖填面积）；② 明确可行动的错误。
  private static bool TryGetExtents(CivilSurface surface, out double minX, out double minY, out double maxX, out double maxY)
  {
    minX = minY = maxX = maxY = 0;
    try
    {
      var extents = surface.GeometricExtents;
      minX = extents.MinPoint.X; minY = extents.MinPoint.Y;
      maxX = extents.MaxPoint.X; maxY = extents.MaxPoint.Y;
      return maxX > minX && maxY > minY;
    }
    catch { return false; }
  }

  private static Dictionary<string, object?> ComputeVolumeByGrid(CivilSurface baseSurface, CivilSurface comparisonSurface, double requestedStep)
  {
    var haveA = TryGetExtents(baseSurface, out var ax0, out var ay0, out var ax1, out var ay1);
    var haveB = TryGetExtents(comparisonSurface, out var bx0, out var by0, out var bx1, out var by1);
    if (!haveA && !haveB)
    {
      throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED", Loc.Tk("error.surfaceGridNoExtents"));
    }

    double minX, minY, maxX, maxY;
    string extentsFrom;
    if (haveA && haveB)
    {
      minX = Math.Max(ax0, bx0); minY = Math.Max(ay0, by0);
      maxX = Math.Min(ax1, bx1); maxY = Math.Min(ay1, by1);
      extentsFrom = "intersection";
    }
    else if (haveA) { minX = ax0; minY = ay0; maxX = ax1; maxY = ay1; extentsFrom = "base"; }
    else { minX = bx0; minY = by0; maxX = bx1; maxY = by1; extentsFrom = "comparison"; }

    if (maxX <= minX || maxY <= minY)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", Loc.Tk("error.surfaceNoOverlap"));
    }

    var step = requestedStep > 0 ? requestedStep : Math.Max(maxX - minX, maxY - minY) / 100.0;
    if (step < 0.01) step = 0.01;
    var cellArea = step * step;
    double cut = 0, fill = 0, cutArea = 0, fillArea = 0;
    int used = 0, skipped = 0;

    for (var x = minX + step / 2; x < maxX; x += step)
    {
      for (var y = minY + step / 2; y < maxY; y += step)
      {
        double za, zb;
        try
        {
          za = InvokeSurfaceElevation(baseSurface, x, y);
          zb = InvokeSurfaceElevation(comparisonSurface, x, y);
        }
        catch { skipped++; continue; }
        var d = za - zb;
        used++;
        if (d > 0) { cut += d * cellArea; cutArea += cellArea; }
        else if (d < 0) { fill += -d * cellArea; fillArea += cellArea; }
      }
    }

    return new Dictionary<string, object?>
    {
      ["method"] = "grid",
      ["gridStep"] = step,
      ["cellsUsed"] = used,
      ["cellsSkipped"] = skipped,
      ["cutVolume"] = cut,
      ["fillVolume"] = fill,
      ["netVolume"] = fill - cut,   // 口径对齐 C3D（UnadjustedNetVolume = fill - cut）
      ["cutArea"] = cutArea,
      ["fillArea"] = fillArea,
      ["extentsFrom"] = extentsFrom,
    };
  }

  // ── 2026-09-28 A3 二刀：廊道曲面烘焙 ────────────────────────────────
  // C3D 内置：TinSurface.CreateFromCorridorSurface(name, corridorSurface) 可把廊道曲面转成普通 TIN。
  // 用途：廊道曲面①不能作 TinVolumeSurface 输入；②GeometricExtents/采样域退化。烘焙成普通 TIN 后全部可用。
  private static bool TryFindCorridorSurface(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string surfaceName, out Autodesk.Civil.DatabaseServices.CorridorSurface corridorSurface)
  {
    corridorSurface = null!;
    foreach (ObjectId corridorId in civilDoc.CorridorCollection)
    {
      var corridor = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Corridor>(transaction, corridorId, OpenMode.ForRead);
      foreach (Autodesk.Civil.DatabaseServices.CorridorSurface cs in corridor.CorridorSurfaces)
      {
        if (string.Equals(cs.Name, surfaceName, StringComparison.OrdinalIgnoreCase)) { corridorSurface = cs; return true; }
      }
    }
    return false;
  }

  /// <summary>若该曲面是廊道曲面，烘焙成临时普通 TIN（加入 tempIds 待清理）；否则返回 null</summary>
  private static TinSurface? TryBakeCorridorSurface(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface surface, List<ObjectId> tempIds)
  {
    if (!TryFindCorridorSurface(civilDoc, transaction, surface.Name, out var corridorSurface)) return null;
    var bakedName = surface.Name + "__baked_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    var bakedId = TinSurface.CreateFromCorridorSurface(bakedName, corridorSurface);
    tempIds.Add(bakedId);
    return CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, bakedId, OpenMode.ForRead);
  }

  private static void EraseTemps(Transaction transaction, List<ObjectId> tempIds)
  {
    foreach (var id in tempIds)
    {
      try
      {
        if (id.IsNull || id.IsErased) continue;
        var obj = transaction.GetObject(id, OpenMode.ForWrite, false);
        obj?.Erase();
      }
      catch {
        /* best-effort: a temp-entity cleanup failure is not fatal */
      }
    }
    tempIds.Clear();
  }
  private static Dictionary<string, object?> ComputeVolumeDict(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface, Database database, string method, double gridStep)
  {
    var units = new Dictionary<string, object?>
    {
      ["volume"] = $"{CivilObjectUtils.LinearUnits(database)}^3",
      ["area"] = $"{CivilObjectUtils.LinearUnits(database)}^2",
    };

    if (method == "grid")
    {
      var grid = ComputeVolumeByGrid(baseSurface, comparisonSurface, gridStep);
      grid["units"] = units;
      return grid;
    }

    var tempIds = new List<ObjectId>();
    try
    {
      // 2026-09-28：廊道曲面先烘焙成普通 TIN（否则 TinVolumeSurface 必失败）
      var baseForVolume = TryBakeCorridorSurface(civilDoc, transaction, baseSurface, tempIds) ?? baseSurface;
      var compForVolume = TryBakeCorridorSurface(civilDoc, transaction, comparisonSurface, tempIds) ?? comparisonSurface;
      var baked = tempIds.Count > 0;

      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseForVolume, compForVolume);
      return new Dictionary<string, object?>
      {
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["cutArea"] = null,
        ["fillArea"] = null,
        ["method"] = "tin_volume",
        ["bakedCorridorInputs"] = baked,
        ["units"] = units,
      };
    }
    catch (Exception ex) when (method != "tin_volume")
    {
      var grid = ComputeVolumeByGrid(baseSurface, comparisonSurface, gridStep);
      grid["tinError"] = ex.Message;
      grid["units"] = units;
      return grid;
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED",
        Loc.Tk("error.tinVolumeUnsupported", ("msg", ex.Message)));
    }
    finally
    {
      EraseTemps(transaction, tempIds);
    }
  }

  /// <summary>bakeCorridorSurface —— 把廊道曲面烘焙成普通 TIN 曲面（永久保留）
  /// 用途：廊道曲面不能算体积、无可用 extents（采样/网格法都不行）；烘焙后一切可用。</summary>
  public static Task<object?> BakeCorridorSurfaceAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var newName = PluginRuntime.GetOptionalString(parameters, "newSurfaceName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      if (!TryFindCorridorSurface(civilDoc, transaction, surfaceName, out var corridorSurface))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", Loc.Tk("error.surfaceNotCorridor", ("name", surfaceName)));
      }

      var targetName = string.IsNullOrWhiteSpace(newName) ? surfaceName + "_baked" : newName;
      var bakedId = TinSurface.CreateFromCorridorSurface(targetName, corridorSurface);
      var baked = CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, bakedId, OpenMode.ForRead);

      var haveExtents = TryGetExtents(baked, out var minX, out var minY, out var maxX, out var maxY);
      var stats = baked.GetGeneralProperties();
      return new Dictionary<string, object?>
      {
        ["sourceSurface"] = surfaceName,
        ["bakedSurface"] = baked.Name,
        ["handle"] = CivilObjectUtils.GetHandle(baked),
        ["numberOfPoints"] = stats.NumberOfPoints,
        ["numberOfTriangles"] = GetTriangleCount(baked),
        ["extentsAvailable"] = haveExtents,
        ["boundingBox"] = haveExtents
          ? new Dictionary<string, object?> { ["minX"] = minX, ["minY"] = minY, ["maxX"] = maxX, ["maxY"] = maxY }
          : null,
        ["note"] = "After baking, the surface behaves like a normal TIN: volume (computeSurfaceVolume), sampling and grid methods all work",
      };
    });
  }
  public static Task<object?> ComputeSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "auto";
    var gridStep = PluginRuntime.GetOptionalDouble(parameters, "gridStep") ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      return ComputeVolumeDict(civilDoc, transaction, baseSurface, comparisonSurface, database, method, gridStep);
    });
  }

  // ─── New analysis methods ───────────────────────────────────────────────

  public static Task<object?> CalculateSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "auto";
    var gridStep = PluginRuntime.GetOptionalDouble(parameters, "gridStep") ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeDict = ComputeVolumeDict(civilDoc, transaction, baseSurface, comparisonSurface, database, method, gridStep);
      var units = CivilObjectUtils.LinearUnits(database);

      volumeDict["baseSurface"] = baseSurfaceName;
      volumeDict["comparisonSurface"] = comparisonSurfaceName;
      volumeDict["requestedMethod"] = method;
      return volumeDict;
    });
  }

  public static Task<object?> GetSurfaceVolumeReportAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var format = PluginRuntime.GetOptionalString(parameters, "format") ?? "summary";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);
      var units = CivilObjectUtils.LinearUnits(database);

      var cut = volumeProperties.UnadjustedCutVolume;
      var fill = volumeProperties.UnadjustedFillVolume;
      var net = volumeProperties.UnadjustedNetVolume;

      var lines = new List<string>
      {
        $"Surface Volume Report",
        $"====================",
        $"Base Surface:       {baseSurfaceName}",
        $"Comparison Surface: {comparisonSurfaceName}",
        $"",
        $"Cut Volume:  {cut:F3} {units}^3",
        $"Fill Volume: {fill:F3} {units}^3",
        $"Net Volume:  {net:F3} {units}^3",
        $"",
        "Cut and fill areas are not exposed by VolumeSurfaceProperties.",
      };

      if (format == "detailed")
      {
        lines.Add($"");
        lines.Add($"Net Balance: {(net >= 0 ? "Cut exceeds fill" : "Fill exceeds cut")} by {Math.Abs(net):F3} {units}^3");
        lines.Add($"Cut/Fill Ratio: {(fill > 0 ? (cut / fill).ToString("F3") : "N/A")}");
      }

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["format"] = format,
        ["report"] = string.Join("\n", lines),
        ["volumes"] = new Dictionary<string, object?>
        {
          ["cut"] = cut,
          ["fill"] = fill,
          ["net"] = net,
        },
        ["areas"] = new Dictionary<string, object?>
        {
          ["cut"] = null,
          ["fill"] = null,
        },
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  public static Task<object?> CalculateSurfaceVolumeByRegionAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var boundary = ParseRequiredPoint2dArray(parameters, "boundary", "calculateSurfaceVolumeByRegion requires at least 3 boundary points.");
    if (boundary.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "calculateSurfaceVolumeByRegion requires at least 3 boundary points.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var units = CivilObjectUtils.LinearUnits(database);

      // Sample elevations on a grid within the boundary and compute volume manually
      var minX = boundary.OfType<Point2d>().Min(p => p.X);
      var maxX = boundary.OfType<Point2d>().Max(p => p.X);
      var minY = boundary.OfType<Point2d>().Min(p => p.Y);
      var maxY = boundary.OfType<Point2d>().Max(p => p.Y);
      var gridSpacing = Math.Max((maxX - minX), (maxY - minY)) / 50.0;
      if (gridSpacing < 0.01) gridSpacing = 0.01;

      double cutVolume = 0;
      double fillVolume = 0;
      double cutArea = 0;
      double fillArea = 0;
      var cellArea = gridSpacing * gridSpacing;

      for (var x = minX + gridSpacing / 2; x < maxX; x += gridSpacing)
      {
        for (var y = minY + gridSpacing / 2; y < maxY; y += gridSpacing)
        {
          if (!IsPointInPolygon(x, y, boundary))
          {
            continue;
          }

          double baseZ;
          double compZ;
          try
          {
            baseZ = InvokeSurfaceElevation(baseSurface, x, y);
            compZ = InvokeSurfaceElevation(comparisonSurface, x, y);
          }
          catch
          {
            continue;
          }

          var diff = compZ - baseZ;
          if (diff > 0)
          {
            fillVolume += diff * cellArea;
            fillArea += cellArea;
          }
          else if (diff < 0)
          {
            cutVolume += Math.Abs(diff) * cellArea;
            cutArea += cellArea;
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["cutVolume"] = cutVolume,
        ["fillVolume"] = fillVolume,
        ["netVolume"] = fillVolume - cutVolume,
        ["cutArea"] = cutArea,
        ["fillArea"] = fillArea,
        ["regionBoundaryPointCount"] = boundary.Count,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  public static Task<object?> AnalyzeSurfaceSlopeAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var requestedRanges = PluginRuntime.GetOptionalInt(parameters, "numRanges");
    var numRanges = requestedRanges is > 0 ? requestedRanges.Value : 5;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var analysisData = surface.Analysis.GetSlopeData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored slope analysis. Generate slope ranges on the Surface Properties Analysis tab, then retry.");
      }

      var slopeBands = analysisData.Select((range, index) => new Dictionary<string, object?>
      {
        ["rangeIndex"] = index,
        ["minPercent"] = range.MinimumSlope * 100.0,
        ["maxPercent"] = range.MaximumSlope * 100.0,
        ["color"] = range.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "slope",
        ["numRanges"] = slopeBands.Count,
        ["requestedRanges"] = requestedRanges,
        ["requestedCustomRanges"] = rangesNode?.Count,
        ["slopeBands"] = slopeBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["slope"] = "percent",
        },
        ["note"] = "These are the exact slope ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  public static Task<object?> AnalyzeSurfaceElevationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var numRanges = PluginRuntime.GetOptionalInt(parameters, "numRanges") ?? 5;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      // 2026-09-28: 廊道曲面的 GeometricExtents 可能不可用 → 容错，不让整个 getSurface 挂掉
      var units = CivilObjectUtils.LinearUnits(database);
      var analysisData = surface.Analysis.GetElevationData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored elevation analysis. Generate elevation ranges on the Surface Properties Analysis tab, then retry.");
      }

      var elevBands = analysisData.Select((range, index) => new Dictionary<string, object?>
      {
        ["rangeIndex"] = index,
        ["minElevation"] = range.MinimumElevation,
        ["maxElevation"] = range.MaximumElevation,
        ["color"] = range.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "elevation",
        ["numRanges"] = elevBands.Count,
        ["requestedRanges"] = numRanges,
        ["requestedCustomRanges"] = rangesNode?.Count,
        ["overallMin"] = generalProperties.MinimumElevation,
        ["overallMax"] = generalProperties.MaximumElevation,
        ["elevationBands"] = elevBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["elevation"] = units,
        },
        ["note"] = "These are the exact elevation ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  public static Task<object?> AnalyzeSurfaceDirectionsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var requestedRanges = PluginRuntime.GetOptionalInt(parameters, "numRanges");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var analysisData = surface.Analysis.GetDirectionData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored direction analysis. Generate direction ranges on the Surface Properties Analysis tab, then retry.");
      }

      var directionBands = analysisData.Select((band, index) => new Dictionary<string, object?>
      {
        ["sectorIndex"] = index,
        ["startAngle"] = band.MinimumDirection * 180.0 / Math.PI,
        ["endAngle"] = band.MaximumDirection * 180.0 / Math.PI,
        ["color"] = band.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "directions",
        ["numSectors"] = directionBands.Count,
        ["requestedSectors"] = requestedRanges,
        ["directionBands"] = directionBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["angle"] = "degrees",
        },
        ["note"] = "These are the exact direction ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  public static Task<object?> AddSurfaceWatershedsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var depthThreshold = PluginRuntime.GetOptionalDouble(parameters, "depthThreshold") ?? 0.1;
    var mergeAdjacent = PluginRuntime.GetOptionalBool(parameters, "mergeAdjacentWatersheds") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);

      // Try to add watersheds via reflection
      var watershedsAdded = false;
      var watershedCount = 0;

      foreach (var methodName in new[] { "AddWatersheds", "CreateWatersheds", "ComputeWatersheds" })
      {
        var result = CivilObjectUtils.InvokeMethod(surface, methodName, depthThreshold);
        if (result != null)
        {
          watershedsAdded = true;
          watershedCount = result is int count ? count : 1;
          break;
        }
      }

      if (!watershedsAdded)
      {
        // Try accessing the Watersheds property
        var watershedsProperty = CivilObjectUtils.GetPropertyValue<object>(surface, "Watersheds");
        if (watershedsProperty != null)
        {
          CivilObjectUtils.InvokeMethod(watershedsProperty, "Add", depthThreshold);
          watershedsAdded = true;
        }
      }

      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["depthThreshold"] = depthThreshold,
        ["mergeAdjacentWatersheds"] = mergeAdjacent,
        ["watershedsAdded"] = watershedsAdded,
        ["watershedCount"] = watershedCount,
        ["status"] = watershedsAdded ? "Watershed analysis added successfully" : "Watershed analysis may require manual configuration in Civil 3D",
      };
    });
  }

  public static Task<object?> SetSurfaceContourIntervalAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minorInterval = PluginRuntime.GetRequiredDouble(parameters, "minorInterval");
    var majorInterval = PluginRuntime.GetRequiredDouble(parameters, "majorInterval");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);

      var styleId = surface.StyleId;
      var style = CivilObjectUtils.GetRequiredObject<SurfaceStyle>(transaction, styleId, OpenMode.ForWrite);
      style.ContourStyle.MinorContourInterval = minorInterval;
      style.ContourStyle.MajorContourInterval = majorInterval;

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["minorInterval"] = minorInterval,
        ["majorInterval"] = majorInterval,
        ["applied"] = true,
        ["status"] = $"Contour intervals set: minor={minorInterval}, major={majorInterval}",
      };
    });
  }

  public static Task<object?> GetSurfaceStatisticsDetailedAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      // 2026-09-28: 廊道曲面的 GeometricExtents 可能不可用 → 容错，不让整个 getSurface 挂掉
      var terrainProperties = GetTerrainProperties(surface);
      var units = CivilObjectUtils.LinearUnits(database);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["minimumElevation"] = generalProperties.MinimumElevation,
        ["maximumElevation"] = generalProperties.MaximumElevation,
        ["meanElevation"] = generalProperties.MeanElevation,
        ["area2d"] = terrainProperties?.SurfaceArea2D,
        ["area3d"] = terrainProperties?.SurfaceArea3D,
        ["numberOfPoints"] = generalProperties.NumberOfPoints,
        ["numberOfTriangles"] = GetTriangleCount(surface),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = units,
          ["vertical"] = units,
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  public static Task<object?> SampleSurfaceElevationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var method = PluginRuntime.GetRequiredString(parameters, "method");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var units = CivilObjectUtils.LinearUnits(database);
      var samples = new List<Dictionary<string, object?>>();

      if (method == "grid")
      {
        var gridSpacing = PluginRuntime.GetRequiredDouble(parameters, "gridSpacing");
        var extents = CivilObjectUtils.GetPropertyValue<Extents3d?>(surface, "GeometricExtents");
        if (extents == null)
        {
          throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to get extents for surface '{name}'.");
        }

        var minX = extents.Value.MinPoint.X;
        var maxX = extents.Value.MaxPoint.X;
        var minY = extents.Value.MinPoint.Y;
        var maxY = extents.Value.MaxPoint.Y;

        var boundaryNode = PluginRuntime.GetParameter(parameters, "boundary") as JsonArray;
        var boundaryPoints = boundaryNode != null
          ? boundaryNode.OfType<JsonObject>()
            .Select(p => new Point2d(
              PluginRuntime.GetRequiredDoubleFromNode(p["x"], "boundary[].x"),
              PluginRuntime.GetRequiredDoubleFromNode(p["y"], "boundary[].y")))
            .ToList()
          : (List<Point2d>?)null;

        for (var x = minX; x <= maxX; x += gridSpacing)
        {
          for (var y = minY; y <= maxY; y += gridSpacing)
          {
            if (boundaryPoints != null && !IsPointInPolygon(x, y, new Point2dCollection(boundaryPoints.ToArray())))
            {
              continue;
            }

            double elevation;
            var hasData = TrySampleElevation(surface, x, y, out elevation);
            samples.Add(new Dictionary<string, object?>
            {
              ["x"] = x,
              ["y"] = y,
              ["elevation"] = hasData ? elevation : (double?)null,
              ["inside"] = hasData,
            });
          }
        }
      }
      else if (method == "points")
      {
        var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=points requires 'points' array.");

        foreach (var pointNode in pointsNode.OfType<JsonObject>())
        {
          var x = PluginRuntime.GetRequiredDoubleFromNode(pointNode["x"], "points[].x");
          var y = PluginRuntime.GetRequiredDoubleFromNode(pointNode["y"], "points[].y");
          double elevation;
          var hasData = TrySampleElevation(surface, x, y, out elevation);
          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["elevation"] = hasData ? elevation : (double?)null,
            ["inside"] = hasData,
          });
        }
      }
      else if (method == "transect")
      {
        var startNode = PluginRuntime.GetParameter(parameters, "startPoint") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=transect requires 'startPoint'.");
        var endNode = PluginRuntime.GetParameter(parameters, "endPoint") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=transect requires 'endPoint'.");
        var numSamples = PluginRuntime.GetOptionalInt(parameters, "numSamples") ?? 50;
        if (numSamples < 2) numSamples = 2;

        var x0 = PluginRuntime.GetRequiredDoubleFromNode(startNode["x"], "startPoint.x");
        var y0 = PluginRuntime.GetRequiredDoubleFromNode(startNode["y"], "startPoint.y");
        var x1 = PluginRuntime.GetRequiredDoubleFromNode(endNode["x"], "endPoint.x");
        var y1 = PluginRuntime.GetRequiredDoubleFromNode(endNode["y"], "endPoint.y");
        var totalLength = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

        for (var i = 0; i < numSamples; i++)
        {
          var t = (double)i / (numSamples - 1);
          var x = x0 + t * (x1 - x0);
          var y = y0 + t * (y1 - y0);
          double elevation;
          var hasData = TrySampleElevation(surface, x, y, out elevation);
          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["station"] = t * totalLength,
            ["elevation"] = hasData ? elevation : (double?)null,
            ["inside"] = hasData,
          });
        }
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown sampling method '{method}'. Use 'grid', 'points', or 'transect'.");
      }

      var outsideCount = samples.Count(s => s.TryGetValue("inside", out var v) && v is bool b && !b);
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["method"] = method,
        ["sampleCount"] = samples.Count,
        ["outsideCount"] = outsideCount,
        ["samples"] = samples,
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = units,
          ["vertical"] = units,
        },
      };
    });
  }

  public static Task<object?> CreateSurfaceFromDemAsync(JsonObject? parameters)
  {
    var filePath = FileBoundary.ResolveImportPath(
      PluginRuntime.GetRequiredString(parameters, "filePath"),
      ".dem", ".tif", ".tiff", ".asc", ".adf");
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var coordinateSystem = PluginRuntime.GetOptionalString(parameters, "coordinateSystem");

    if (!string.IsNullOrWhiteSpace(coordinateSystem))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "createSurfaceFromDem cannot assign a coordinate system through SurfaceDefinitionDEMFiles.AddDEMFile. " +
        "Assign the drawing coordinate system explicitly before importing the DEM.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);

      var surfaceId = CreateTinSurface(name, styleId);
      var surface = CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, surfaceId, OpenMode.ForWrite);
      try
      {
        surface.DEMFilesDefinition.AddDEMFile(filePath);
      }
      catch (Exception exception)
      {
        surface.Erase();
        throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to import DEM file '{filePath}': {exception.Message}");
      }
      if (!string.IsNullOrWhiteSpace(layer)) surface.Layer = layer;
      if (!string.IsNullOrWhiteSpace(description)) surface.Description = description;
      surface.Rebuild();

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["filePath"] = filePath,
        ["created"] = true,
        ["coordinateSystem"] = null,
      };
    });
  }

  // ─── Private helpers for new methods ────────────────────────────────────

  private static bool IsPointInPolygon(double x, double y, Point2dCollection polygon)
  {
    var count = polygon.Count;
    var inside = false;
    for (int i = 0, j = count - 1; i < count; j = i++)
    {
      var xi = polygon[i].X;
      var yi = polygon[i].Y;
      var xj = polygon[j].X;
      var yj = polygon[j].Y;
      if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
      {
        inside = !inside;
      }
    }

    return inside;
  }

  private static string MapSurfaceType(CivilSurface surface)
  {
    return surface switch
    {
      TinVolumeSurface => "TINVolume",
      GridSurface => "Grid",
      _ => "TIN",
    };
  }

  // 2026-11-16 P1: elevation 0 means "no data"
  //   C3D 的 FindElevationAtXY 对曲面范围外的点**不抛异常，直接返回 0.0** —— 以前这里把 0 当真实高程返回，
  //   调用方无法区分"高程 0"和"没数据"（真实项目里 GROUND-EXIST 曲面采 446 个桩号，276 个静默返回 0）。
  //   现在统一约定：0 视为无数据 → elevation=null + inside=false，并计入 outsideCount。
  //   注意：真实高程恰为 0 的地形（海平面）会被判为无数据，这种数据在本工具范围内不予支持。
  private static bool TrySampleElevation(CivilSurface surface, double x, double y, out double elevation)
  {
    // C3D 的两种失败都要归到「无数据」：
    //   - 完全在曲面范围外 -> 抛异常 "Point Outside Surface"（以前这会让整批采样中断）
    //   - 在范围内但缺三角/边界退化 -> 返回 0.0（以前被当成真实高程返回）
    try
    {
      elevation = surface.FindElevationAtXY(x, y);
    }
    catch (System.Exception)
    {
      elevation = 0;
      return false;
    }
    return elevation != 0.0;
  }

  private static double InvokeSurfaceElevation(CivilSurface surface, double x, double y)
  {
    try
    {
      return surface.FindElevationAtXY(x, y);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not sample surface '{surface.Name}' at ({x}, {y}): {exception.Message}");
    }
  }

  private static TerrainSurfaceProperties? GetTerrainProperties(CivilSurface surface) => surface switch
  {
    TinSurface tinSurface => tinSurface.GetTerrainProperties(),
    GridSurface gridSurface => gridSurface.GetTerrainProperties(),
    _ => null,
  };

  private static int? GetTriangleCount(CivilSurface surface) =>
    surface is TinSurface tinSurface ? tinSurface.GetTinProperties().NumberOfTriangles : null;

  private static ObjectId CreateTinSurface(string name, ObjectId styleId) => TinSurface.Create(name, styleId);

  private static Point3dCollection ParseRequiredPoint3dArray(JsonObject? parameters, string name, string errorMessage)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, name) as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    var points = new Point3dCollection();
    foreach (var pointNode in pointsNode)
    {
      if (pointNode is not JsonObject point)
      {
        continue;
      }

      var x = PluginRuntime.GetRequiredDoubleFromNode(point["x"], $"{name}[].x");
      var y = PluginRuntime.GetRequiredDoubleFromNode(point["y"], $"{name}[].y");
      var z = PluginRuntime.GetRequiredDoubleFromNode(point["z"], $"{name}[].z");
      points.Add(new Point3d(x, y, z));
    }

    if (points.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    return points;
  }

  private static Point2dCollection ParseRequiredPoint2dArray(JsonObject? parameters, string name, string errorMessage)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, name) as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    var points = new Point2dCollection();
    foreach (var pointNode in pointsNode)
    {
      if (pointNode is not JsonObject point)
      {
        continue;
      }

      var x = PluginRuntime.GetRequiredDoubleFromNode(point["x"], $"{name}[].x");
      var y = PluginRuntime.GetRequiredDoubleFromNode(point["y"], $"{name}[].y");
      points.Add(new Point2d(x, y));
    }

    if (points.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    return points;
  }

  private static SurfaceBoundaryType ResolveBoundaryType(string boundaryType) => boundaryType.Trim().ToLowerInvariant() switch
  {
    "show" => SurfaceBoundaryType.Show,
    "hide" => SurfaceBoundaryType.Hide,
    "outer" => SurfaceBoundaryType.Outer,
    "dataclip" or "data_clip" or "data-clip" => SurfaceBoundaryType.DataClip,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported surface boundary type '{boundaryType}'."),
  };

  private static void SetSurfaceDescription(CivilSurface surface, string? description)
  {
    if (!string.IsNullOrWhiteSpace(description))
    {
      surface.Description = description;
    }
  }

  private static void RebuildSurfaceIfAvailable(CivilSurface surface)
  {
    surface.Rebuild();
  }

  private static List<ObjectId> ExtractContourEntities(CivilSurface surface, double minorInterval, double majorInterval)
  {
    if (minorInterval <= 0 || majorInterval <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Contour intervals must be greater than zero.");
    }
    var minorContours = surface switch
    {
      TinSurface tinSurface => tinSurface.ExtractContours(minorInterval),
      GridSurface gridSurface => gridSurface.ExtractContours(minorInterval),
      _ => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Contour extraction is not supported for surface type '{surface.GetType().Name}'."),
    };
    var extracted = minorContours.Cast<ObjectId>().ToList();
    if (Math.Abs(majorInterval - minorInterval) > 1.0e-9)
    {
      var majorContours = surface switch
      {
        TinSurface tinSurface => tinSurface.ExtractContours(majorInterval),
        GridSurface gridSurface => gridSurface.ExtractContours(majorInterval),
        _ => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Contour extraction is not supported for surface type '{surface.GetType().Name}'."),
      };
      extracted.AddRange(majorContours.Cast<ObjectId>());
    }
    return extracted;
  }

  private static VolumeSurfaceProperties GetVolumeProperties(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface)
  {
    // 2026-08-12 M7: 临时体积曲面用完即删(调用方需 WriteAsync 提交, 防残留幽灵曲面)
    var volumeSurface = CreateTinVolumeSurface(civilDoc, transaction, baseSurface, comparisonSurface);
    var props = volumeSurface.GetVolumeProperties();
    try
    {
      var w = transaction.GetObject(volumeSurface.ObjectId, OpenMode.ForWrite);
      w?.Erase();
    }
    catch {
      /* best-effort: erasing the temporary volume surface failed - non-fatal */
    }
    return props;
  }

  private static TinVolumeSurface CreateTinVolumeSurface(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface)
  {
    var name = $"{baseSurface.Name}_{comparisonSurface.Name}_Volume";
    var surfaceId = TinVolumeSurface.Create(name, baseSurface.ObjectId, comparisonSurface.ObjectId);
    return CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, surfaceId, OpenMode.ForRead);
  }
}
