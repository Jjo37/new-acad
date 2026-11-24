using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Quantity takeoff commands — aggregate Civil 3D object data for cost estimation.
/// Uses typed Civil 3D APIs wherever the 2026 API exposes the required data.
/// </summary>
public static class QuantityCommands
{
  // -------------------------------------------------------------------------
  // qtyCorridorVolumes
  // -------------------------------------------------------------------------

  public static Task<object?> QtyCorridorVolumesAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "name");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Corridor material quantities are exposed through SampleLineGroup QTO material lists, not Corridor baseline properties. A sample-line group and material-list identifier are required; no zero quantities were returned.");
  }

  // -------------------------------------------------------------------------
  // qtySurfaceVolume
  // -------------------------------------------------------------------------

  public static Task<object?> QtySurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var compSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);

      var temporaryName = $"MCP_TMP_VOLUME_{Guid.NewGuid():N}";
      var volumeSurfaceId = TinVolumeSurface.Create(temporaryName, baseSurface.ObjectId, compSurface.ObjectId);
      var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeSurfaceId, OpenMode.ForRead);
      var volumeProperties = volumeSurface.GetVolumeProperties();

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["corridorName"] = corridorName,
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["net2dArea"] = null,
        ["units"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = "Volumes are exact Civil 3D TinVolumeSurface results. The managed volume-properties API does not expose a net 2D area.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyPipeNetworkLengths
  // -------------------------------------------------------------------------

  public static Task<object?> QtyPipeNetworkLengthsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var groupBySize = PluginRuntime.GetOptionalBool(parameters, "groupBySize") ?? false;
    var groupByMaterial = PluginRuntime.GetOptionalBool(parameters, "groupByMaterial") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkObj = FindPipeNetworkByName(civilDoc, transaction, name)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");

      // 2026-11-17 P28b: 老代码把直径键硬编码成 "Ø{d:F0}mm" —— 英制图里 1.5 ft 的管会显示成 "Ø2mm"。
      //   现在按图纸单位换算：米制 → mm（d*1000），英制 → in（d*12），并回 diameterUnit。
      var linearUnits = CivilObjectUtils.LinearUnits(database);
      var isMetricDrawing = linearUnits.StartsWith("meter", StringComparison.OrdinalIgnoreCase);
      var diameterUnit = isMetricDrawing ? "mm" : "in";
      string DiameterKey(double d) => isMetricDrawing
        ? "Ø" + (d * 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "mm"
        : "Ø" + (d * 12.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "in";
      var totalLength = 0.0;
      var pipeCount = 0;
      var breakdown = new Dictionary<string, (double Length, int Count)>(StringComparer.OrdinalIgnoreCase);

      foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
      {
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pipeId, OpenMode.ForRead);
        var length = pipe.Length3D;
        var diameter = pipe.InnerDiameterOrWidth;
        var material = pipe.Material;

        totalLength += length;
        pipeCount++;

        // Build breakdown key
        var key = "All";
        if (groupBySize && groupByMaterial)
          key = $"{material} {DiameterKey(diameter)}";
        else if (groupBySize)
          key = DiameterKey(diameter);
        else if (groupByMaterial)
          key = material;

        if (breakdown.TryGetValue(key, out var existing))
          breakdown[key] = (existing.Length + length, existing.Count + 1);
        else
          breakdown[key] = (length, 1);
      }

      var breakdownList = breakdown
        .Select(kvp => new Dictionary<string, object?>
        {
          ["key"] = kvp.Key,
          ["length"] = kvp.Value.Length,
          ["count"] = kvp.Value.Count,
        })
        .OrderByDescending(d => d["length"])
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = name,
        ["totalLength"] = totalLength,
        ["pipeCount"] = pipeCount,
        ["breakdown"] = breakdownList,
        ["units"] = linearUnits,
        ["diameterUnit"] = diameterUnit,
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyPressureNetworkLengths
  // -------------------------------------------------------------------------

  public static Task<object?> QtyPressureNetworkLengthsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var groupBySize = PluginRuntime.GetOptionalBool(parameters, "groupBySize") ?? false;
    var groupByMaterial = PluginRuntime.GetOptionalBool(parameters, "groupByMaterial") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkObj = FindPressureNetworkByName(civilDoc, transaction, name)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure network '{name}' was not found.");

      // 2026-11-17 P28b: 老代码把直径键硬编码成 "Ø{d:F0}mm" —— 英制图里 1.5 ft 的管会显示成 "Ø2mm"。
      //   现在按图纸单位换算：米制 → mm（d*1000），英制 → in（d*12），并回 diameterUnit。
      var linearUnits = CivilObjectUtils.LinearUnits(database);
      var isMetricDrawing = linearUnits.StartsWith("meter", StringComparison.OrdinalIgnoreCase);
      var diameterUnit = isMetricDrawing ? "mm" : "in";
      string DiameterKey(double d) => isMetricDrawing
        ? "Ø" + (d * 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "mm"
        : "Ø" + (d * 12.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "in";
      var totalLength = 0.0;
      var pipeCount = 0;
      var computedLengthCount = 0;   // 长度不是 API 给的，而是按端点距离算出来的（如实回传）
      var breakdown = new Dictionary<string, (double Length, int Count)>(StringComparer.OrdinalIgnoreCase);

      foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes"))
      {
        var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
        // 2026-11-18 P34: Civil 3D 2025 的压力管不暴露 Length3D/Length2D/Length（真机全 null），
        // 旧实现直接抛"读不到长度"。有端点时按三维距离补，并计入 computedLengthCount。
        var lengthValue = TryEngineeringDouble(pipe, "Length3D", "Length2D", "Length");
        if (!lengthValue.HasValue)
        {
          var sp = TryPipePoint(pipe, "StartPoint", "PointAtStart");
          var ep = TryPipePoint(pipe, "EndPoint", "PointAtEnd");
          if (sp.HasValue && ep.HasValue)
          {
            var dx = sp.Value.X - ep.Value.X;
            var dy = sp.Value.Y - ep.Value.Y;
            var dz = sp.Value.Z - ep.Value.Z;
            var computed = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (computed > 0) { lengthValue = computed; computedLengthCount++; }
          }
        }
        if (!lengthValue.HasValue)
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            $"Could not read pressure pipe '{CivilObjectUtils.GetName(pipe)}' length: the API exposes no length property and both endpoints are unavailable. Zero was not substituted.");
        var length = lengthValue.Value;
        var diameter = GetRequiredEngineeringDouble(pipe, $"pressure pipe '{CivilObjectUtils.GetName(pipe)}' diameter", "InnerDiameter", "OuterDiameter", "Diameter", "InnerDiameterOrWidth");
        var material = GetAnyString(pipe, "Material", "PipeFamily", "PartFamilyName", "PartDescription") ?? "Unknown";

        totalLength += length;
        pipeCount++;

        var key = "All";
        if (groupBySize && groupByMaterial)
          key = $"{material} {DiameterKey(diameter)}";
        else if (groupBySize)
          key = DiameterKey(diameter);
        else if (groupByMaterial)
          key = material;

        if (breakdown.TryGetValue(key, out var existing))
          breakdown[key] = (existing.Length + length, existing.Count + 1);
        else
          breakdown[key] = (length, 1);
      }

      var breakdownList = breakdown
        .Select(kvp => new Dictionary<string, object?>
        {
          ["key"] = kvp.Key,
          ["length"] = kvp.Value.Length,
          ["count"] = kvp.Value.Count,
        })
        .OrderByDescending(d => d["length"])
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = name,
        ["totalLength"] = totalLength,
        ["pipeCount"] = pipeCount,
        ["breakdown"] = breakdownList,
        ["units"] = linearUnits,
        ["computedLengthCount"] = computedLengthCount,
        ["diameterUnit"] = diameterUnit,
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyParcelAreas
  // -------------------------------------------------------------------------

  public static Task<object?> QtyParcelAreasAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var parcelNamesNode = PluginRuntime.GetParameter(parameters, "parcelNames") as JsonArray;
    var requestedParcelNames = parcelNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var parcelResults = new List<Dictionary<string, object?>>();
      var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var totalArea = 0.0;
      var sitesScanned = 0;
      var siteNameMatched = false;

      foreach (ObjectId siteId in civilDoc.GetSiteIds())
      {
        var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
        var currentSiteName = site.Name;

        if (!string.IsNullOrWhiteSpace(siteName) &&
            !currentSiteName.Equals(siteName, StringComparison.OrdinalIgnoreCase))
          continue;
        siteNameMatched = true;
        sitesScanned++;

        foreach (ObjectId parcelId in site.GetParcelIds())
        {
          if (parcelId == ObjectId.Null) continue;
          var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, parcelId, OpenMode.ForRead);
          var parcelName = parcel.Name;

          if (requestedParcelNames != null && !requestedParcelNames.Contains(parcelName)) continue;

          var area = parcel.Area;
          // 2026-11-18 P32: Parcel 没有 Perimeter 属性（Curve 派生，只有 Area），
          // 旧实现 GetDoubleProperty(parcel,"Perimeter") 恒 null。周长走曲线弧长。
          double? perimeter = null;
          try
          {
            var length = parcel.GetDistanceAtParameter(parcel.EndParam)
                       - parcel.GetDistanceAtParameter(parcel.StartParam);
            if (length > 0) perimeter = length;
          }
          catch { }

          matched.Add(parcelName);
          totalArea += area;
          parcelResults.Add(new Dictionary<string, object?>
          {
            ["name"] = parcelName,
            ["area"] = area,
            ["perimeter"] = perimeter,
            ["siteName"] = currentSiteName,
          });
        }
      }

      // 2026-11-18 P32: 以前传错名字只回 parcels:[] / totalArea:0 —— 看起来像"这些地块没面积"。
      // 现在把「要了几个 / 命中几个 / 哪几个没找到」分开报（同 P25 的 matchedCount 教训）。
      var missingNames = requestedParcelNames == null
        ? new List<string>()
        : requestedParcelNames.Where(n => !matched.Contains(n)).OrderBy(n => n).ToList();

      var payload = new Dictionary<string, object?>
      {
        ["parcels"] = parcelResults,
        ["totalArea"] = totalArea,
        ["parcelCount"] = parcelResults.Count,
        ["units"] = string.Equals(CivilObjectUtils.LinearUnits(database), "meters", StringComparison.OrdinalIgnoreCase) ? "sqm" : "sqft",
        ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
        ["siteName"] = string.IsNullOrWhiteSpace(siteName) ? null : siteName,
        ["sitesScanned"] = sitesScanned,
      };

      if (requestedParcelNames != null)
      {
        payload["requestedCount"] = requestedParcelNames.Count;
        payload["matchedCount"] = parcelResults.Count;
        payload["missingNames"] = missingNames;
        if (parcelResults.Count == 0)
        {
          payload["hint"] = siteNameMatched
            ? "None of the requested parcel names exist in the scanned site(s) - check missingNames (names are case-insensitive but must match exactly otherwise)."
            : $"Site '{siteName}' was not found - run listParcelSites to see the available sites.";
        }
      }

      return payload;
    });
  }

  // -------------------------------------------------------------------------
  // qtyAlignmentLengths
  // -------------------------------------------------------------------------

  public static Task<object?> QtyAlignmentLengthsAsync(JsonObject? parameters)
  {
    var namesNode = PluginRuntime.GetParameter(parameters, "names") as JsonArray;
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    var requestedNames = namesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignmentResults = new List<Dictionary<string, object?>>();
      var totalLength = 0.0;

      foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);

        if (requestedNames != null && !requestedNames.Contains(alignment.Name)) continue;

        // Clamp station range
        var start = startStation.HasValue ? Math.Max(startStation.Value, alignment.StartingStation) : alignment.StartingStation;
        var end = endStation.HasValue ? Math.Min(endStation.Value, alignment.EndingStation) : alignment.EndingStation;
        var segmentLength = Math.Max(0, end - start);

        totalLength += segmentLength;
        alignmentResults.Add(new Dictionary<string, object?>
        {
          ["name"] = alignment.Name,
          ["length"] = segmentLength,
          ["totalLength"] = alignment.Length,
          ["startStation"] = start,
          ["endStation"] = end,
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignments"] = alignmentResults,
        ["totalLength"] = totalLength,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyPointCountByGroup
  // -------------------------------------------------------------------------

  public static Task<object?> QtyPointCountByGroupAsync(JsonObject? parameters)
  {
    var groupNamesNode = PluginRuntime.GetParameter(parameters, "groupNames") as JsonArray;
    var requestedGroupNames = groupNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groupResults = new List<Dictionary<string, object?>>();
      var sumOfGroupCounts = 0;
      // 2026-11-18 P32: 一个点可以属于多个组（教程图里 2405 个点分属 2 组）。
      // 旧实现把各组条数相加当 totalPoints 返回（2405+2405=4810），客户会以为图里有 4810 个点。
      // 现在 totalPoints = 去重后的真实点数，组内相加另报 sumOfGroupCounts。
      var distinctNumbers = new HashSet<uint>();
      var matchedGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (ObjectId groupId in civilDoc.PointGroups)
      {
        if (groupId == ObjectId.Null) continue;
        var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, groupId, OpenMode.ForRead);
        var groupName = group.Name;

        if (requestedGroupNames != null && !requestedGroupNames.Contains(groupName)) continue;

        var numbers = group.GetPointNumbers();
        foreach (var n in numbers) distinctNumbers.Add(n);

        matchedGroupNames.Add(groupName);
        sumOfGroupCounts += numbers.Length;
        groupResults.Add(new Dictionary<string, object?>
        {
          ["name"] = groupName,
          ["count"] = numbers.Length,
        });
      }

      var payload = new Dictionary<string, object?>
      {
        ["groups"] = groupResults,
        ["groupCount"] = groupResults.Count,
        ["totalPoints"] = distinctNumbers.Count,
        ["distinctPointCount"] = distinctNumbers.Count,
        ["sumOfGroupCounts"] = sumOfGroupCounts,
        ["note"] = "totalPoints counts distinct COGO points; a point can belong to several groups, so sumOfGroupCounts can be larger.",
      };

      if (requestedGroupNames != null)
      {
        var missing = requestedGroupNames.Where(n => !matchedGroupNames.Contains(n)).OrderBy(n => n).ToList();
        payload["requestedGroupCount"] = requestedGroupNames.Count;
        payload["missingGroupNames"] = missing;
      }

      return payload;
    });
  }

  // -------------------------------------------------------------------------
  // qtyExportToCsv
  // -------------------------------------------------------------------------

  public static Task<object?> QtyExportToCsvAsync(JsonObject? parameters)
  {
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetAiOverwrite(parameters);
    var includeAlignments = PluginRuntime.GetOptionalBool(parameters, "includeAlignments") ?? true;
    var includeSurfaces = PluginRuntime.GetOptionalBool(parameters, "includeSurfaces") ?? false;
    var includePipeNetworks = PluginRuntime.GetOptionalBool(parameters, "includePipeNetworks") ?? false;
    var includeParcelAreas = PluginRuntime.GetOptionalBool(parameters, "includeParcelAreas") ?? false;
    var includePointGroups = PluginRuntime.GetOptionalBool(parameters, "includePointGroups") ?? false;
    var filterNamesNode = PluginRuntime.GetParameter(parameters, "filterNames") as JsonArray;
    var filterNames = filterNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sb = new StringBuilder();
      var rowsWritten = 0;
      var sectionsIncluded = new List<string>();

      if (includeAlignments)
      {
        sectionsIncluded.Add("alignments");
        sb.AppendLine("Section,Name,Length,StartStation,EndStation,Units");
        foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);
          if (filterNames != null && !filterNames.Contains(alignment.Name)) continue;
          sb.AppendLine($"Alignment,{EscapeCsv(alignment.Name)},{alignment.Length:F3},{alignment.StartingStation:F3},{alignment.EndingStation:F3},{CivilObjectUtils.LinearUnits(database)}");
          rowsWritten++;
        }
        sb.AppendLine();
      }

      if (includeSurfaces)
      {
        sectionsIncluded.Add("surfaces");
        sb.AppendLine("Section,Name,Type,MinElevation,MaxElevation,NumberOfPoints,Units");
        foreach (ObjectId surfId in civilDoc.GetSurfaceIds())
        {
          var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfId, OpenMode.ForRead);
          if (filterNames != null && !filterNames.Contains(surface.Name)) continue;
          var gp = surface.GetGeneralProperties();
          sb.AppendLine($"Surface,{EscapeCsv(surface.Name)},{surface.GetType().Name},{gp.MinimumElevation:F3},{gp.MaximumElevation:F3},{gp.NumberOfPoints},{CivilObjectUtils.LinearUnits(database)}");
          rowsWritten++;
        }
        sb.AppendLine();
      }

      if (includePipeNetworks)
      {
        sectionsIncluded.Add("pipeNetworks");
        sb.AppendLine("Section,NetworkName,PipeName,Length,Diameter,Slope,Material,Units");
        foreach (var networkObj in EnumerateAllPipeNetworks(civilDoc, transaction))
        {
          var networkName = CivilObjectUtils.GetName(networkObj) ?? "unknown";
          if (filterNames != null && !filterNames.Contains(networkName)) continue;
          foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
          {
            var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
            var pipeName = CivilObjectUtils.GetName(pipe) ?? pipeId.Handle.ToString();
            var length = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' length", "Length3D", "Length2D", "Length");
            var diameter = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' diameter", "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
            var slope = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' slope", "Slope", "FlowSlope");
            var material = GetAnyString(pipe, "Material", "PartDescription", "PartFamilyName") ?? string.Empty;
            sb.AppendLine($"PipeNetwork,{EscapeCsv(networkName)},{EscapeCsv(pipeName)},{length:F3},{diameter:F3},{slope:F6},{EscapeCsv(material)},{CivilObjectUtils.LinearUnits(database)}");
            rowsWritten++;
          }
        }
        sb.AppendLine();
      }

      if (includeParcelAreas)
      {
        sectionsIncluded.Add("parcelAreas");
        sb.AppendLine("Section,SiteName,ParcelName,Area,Perimeter,Units");
        foreach (ObjectId siteId in civilDoc.GetSiteIds())
        {
          var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
          var currentSiteName = site.Name;
          foreach (ObjectId parcelId in site.GetParcelIds())
          {
            if (parcelId == ObjectId.Null) continue;
            var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, parcelId, OpenMode.ForRead);
            var parcelName = parcel.Name;
            if (filterNames != null && !filterNames.Contains(parcelName)) continue;
            var area = parcel.Area;
            // 2026-11-18 P32: Parcel 没有 Perimeter 属性，周长必须走曲线弧长（同 ParcelEditingCommands.GetParcelPerimeter）。
            double? perimeter = null;
            try
            {
              var length = parcel.GetDistanceAtParameter(parcel.EndParam)
                         - parcel.GetDistanceAtParameter(parcel.StartParam);
              if (length > 0) perimeter = length;
            }
            catch { }
            var perimeterText = perimeter.HasValue ? perimeter.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            sb.AppendLine($"Parcel,{EscapeCsv(currentSiteName)},{EscapeCsv(parcelName)},{area:F3},{perimeterText},{CivilObjectUtils.LinearUnits(database)}");
            rowsWritten++;
          }
        }
        sb.AppendLine();
      }

      if (includePointGroups)
      {
        sectionsIncluded.Add("pointGroups");
        sb.AppendLine("Section,GroupName,PointCount");
        foreach (ObjectId groupId in civilDoc.PointGroups)
        {
          if (groupId == ObjectId.Null) continue;
          var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, groupId, OpenMode.ForRead);
          var groupName = group.Name;
          if (filterNames != null && !filterNames.Contains(groupName)) continue;
          var count = group.GetPointNumbers().Length;
          sb.AppendLine($"PointGroup,{EscapeCsv(groupName)},{count}");
          rowsWritten++;
        }
        sb.AppendLine();
      }

      var canonicalOutputPath = FileBoundary.WriteAllTextAtomic(
        outputPath, sb.ToString(), Encoding.UTF8, overwrite, ".csv");

      return new Dictionary<string, object?>
      {
        ["outputPath"] = canonicalOutputPath,
        ["rowsWritten"] = rowsWritten,
        ["sectionsIncluded"] = sectionsIncluded,
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyMaterialListGet
  // -------------------------------------------------------------------------

  public static Task<object?> QtyMaterialListGetAsync(JsonObject? parameters)
  {
    _ = CorridorEditingCommands.GetCorridorName(parameters);
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 QTO material lists belong to SampleLineGroup objects. Corridor baselines do not expose GetMaterials/GetMaterialQuantities; no empty or zero-valued material list was fabricated.");
  }

  // -------------------------------------------------------------------------
  // qtyEarthworkSummary
  // -------------------------------------------------------------------------

  public static Task<object?> QtyEarthworkSummaryAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var designSurfaceName = PluginRuntime.GetRequiredString(parameters, "designSurface");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    if (startStation.HasValue || endStation.HasValue || !string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(parameters, "alignmentName")))
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Station-clipped earthwork requires sample lines/QTO. The managed TinVolumeSurface API computes whole-surface volume only; no station-volume approximation was used.");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var designSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, designSurfaceName, OpenMode.ForRead);
      var volumeId = TinVolumeSurface.Create($"MCP_TMP_EARTHWORK_{Guid.NewGuid():N}", baseSurface.ObjectId, designSurface.ObjectId);
      var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeId, OpenMode.ForRead);
      var volumes = volumeSurface.GetVolumeProperties();

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["designSurface"] = designSurfaceName,
        ["cumulativeCut"] = volumes.UnadjustedCutVolume,
        ["cumulativeFill"] = volumes.UnadjustedFillVolume,
        ["netEarthwork"] = volumes.UnadjustedNetVolume,
        ["stationCount"] = 0,
        ["stations"] = Array.Empty<object>(),
        ["volumeUnits"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = "Exact whole-surface TinVolumeSurface quantities; station breakdown is not exposed by this API path.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private static AcDbObject? FindPipeNetworkByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase))
        return obj;
    }
    return null;
  }

  private static AcDbObject? FindPressureNetworkByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    foreach (ObjectId objectId in civilDoc.GetPressurePipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase))
        return obj;
    }
    return null;
  }

  private static IEnumerable<AcDbObject> EnumerateAllPipeNetworks(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      yield return transaction.GetObject(objectId, OpenMode.ForRead);
    }
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(AcDbObject owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      var methodResult = CivilObjectUtils.InvokeMethod(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(methodResult))
      {
        if (objectId != ObjectId.Null) yield return objectId;
      }
      var propVal = CivilObjectUtils.GetPropertyValue<object>(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(propVal))
      {
        if (objectId != ObjectId.Null) yield return objectId;
      }
    }
  }

  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (v.HasValue) return v.Value;
    }
    return null;
  }

  /// <summary>非抛出的数值读取（属性名逐个试，读不到返回 null）。</summary>
  private static double? TryEngineeringDouble(object target, params string[] names)
  {
    foreach (var name in names)
    {
      var value = Civil3DCompatibility.GetPropertyValue(target, name);
      if (value == null) continue;
      try
      {
        var d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsNaN(d) && !double.IsInfinity(d)) return d;
      }
      catch { }
    }
    return null;
  }

  /// <summary>读 3D 点（属性或方法名都试）。</summary>
  private static Autodesk.AutoCAD.Geometry.Point3d? TryPipePoint(object pipe, params string[] names)
  {
    foreach (var name in names)
    {
      var value = Civil3DCompatibility.GetPropertyValue(pipe, name) ?? CivilObjectUtils.InvokeMethod(pipe, name);
      if (value is Autodesk.AutoCAD.Geometry.Point3d p) return p;
    }
    return null;
  }

  private static double GetRequiredEngineeringDouble(object? value, string valueDescription, params string[] propertyNames)
  {
    var result = GetAnyDouble(value, propertyNames);
    if (result.HasValue) return result.Value;
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Could not read {valueDescription} from the managed API. Zero was not substituted.");
  }

  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var v = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(v)) return v;
    }
    return null;
  }


  private static string EscapeCsv(string value)
  {
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
      return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
  }
}
