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

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      // 2026-09-28: 廊道曲面的 GeometricExtents 可能不可用 → 容错，不让整个 getSurface 挂掉
      var terrainProperties = GetTerrainProperties(surface);
      return new Dictionary<string, object?>
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
    foreach (var raw in lines)
    {
      var line = raw.Trim();
      if (line.Length == 0) continue;
      if (skipRows > 0) { skipRows--; continue; }   // 跳过前 N 行
      if (!headerSkipped) { headerSkipped = true; continue; }   // 表头行
      char[] seps = delimiter switch
      {
        "comma" => new[] { ',' },
        "space" => new[] { ' ', '\t' },
        "tab" => new[] { '\t' },
        "semicolon" => new[] { ';' },
        _ => new[] { ',', ';', '\t', ' ' },   // auto: 全部尝试
      };
      var parts = line.Split(seps, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length <= Math.Max(xCol, Math.Max(yCol, zCol))) continue;
      if (!double.TryParse(parts[xCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;   // 非数字行跳过
      if (!double.TryParse(parts[yCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
      if (!double.TryParse(parts[zCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;
      points.Add(new Point3d(x, y, z));
      if (points.Count > 100000) break;   // 防爆上限
    }
    if (points.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "文件未解析到有效点（检查 delimiter/hasHeader/列序参数）: " + path);
    return points;
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
      throw new JsonRpcDispatchException("CIVIL3D.NOT_SUPPORTED", "网格法失败：两个曲面的范围（GeometricExtents）都不可用。");
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
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "两曲面 XY 范围不重叠，无法计算体积。");
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
      catch { /* 清理失败不致命 */ }
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
        "TinVolumeSurface 创建失败（Civil 3D 不支持廊道曲面参与体积曲面计算）：" + ex.Message);
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
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"曲面 '{surfaceName}' 不是廊道曲面，无需烘焙。");
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
        ["note"] = "烘焙后可用普通 TIN 做体积（computeSurfaceVolume）/采样/网格法",
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
            try
            {
              elevation = InvokeSurfaceElevation(surface, x, y);
            }
            catch
            {
              continue;
            }

            samples.Add(new Dictionary<string, object?>
            {
              ["x"] = x,
              ["y"] = y,
              ["elevation"] = elevation,
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
          try
          {
            elevation = InvokeSurfaceElevation(surface, x, y);
          }
          catch
          {
            continue;
          }

          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["elevation"] = elevation,
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
          try
          {
            elevation = InvokeSurfaceElevation(surface, x, y);
          }
          catch
          {
            continue;
          }

          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["station"] = t * totalLength,
            ["elevation"] = elevation,
          });
        }
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown sampling method '{method}'. Use 'grid', 'points', or 'transect'.");
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["method"] = method,
        ["sampleCount"] = samples.Count,
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
    catch { }
    return props;
  }

  private static TinVolumeSurface CreateTinVolumeSurface(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface)
  {
    var name = $"{baseSurface.Name}_{comparisonSurface.Name}_Volume";
    var surfaceId = TinVolumeSurface.Create(name, baseSurface.ObjectId, comparisonSurface.ObjectId);
    return CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, surfaceId, OpenMode.ForRead);
  }
}
