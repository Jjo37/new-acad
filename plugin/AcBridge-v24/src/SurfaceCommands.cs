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
      var terrainProperties = GetTerrainProperties(surface);
      var extents = surface.GeometricExtents;

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
        ["boundingBox"] = new Dictionary<string, object?>
        {
          ["minX"] = extents.MinPoint.X,
          ["minY"] = extents.MinPoint.Y,
          ["maxX"] = extents.MaxPoint.X,
          ["maxY"] = extents.MaxPoint.Y,
        },
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

  public static Task<object?> ComputeSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);

      return new Dictionary<string, object?>
      {
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["cutArea"] = null,
        ["fillArea"] = null,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{CivilObjectUtils.LinearUnits(database)}^3",
          ["area"] = $"{CivilObjectUtils.LinearUnits(database)}^2",
        },
      };
    });
  }

  // ─── New analysis methods ───────────────────────────────────────────────

  public static Task<object?> CalculateSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "tin_volume";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);
      var units = CivilObjectUtils.LinearUnits(database);

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["cutArea"] = null,
        ["fillArea"] = null,
        ["method"] = method,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
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
