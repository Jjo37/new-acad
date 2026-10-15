using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_superelevation_* tools.
///
/// Civil 3D API notes:
///   Alignment.SuperelevationCurves exposes calculated curve groups and their
///   typed critical stations. Wizard calculation settings are not exposed by
///   the managed 2026 API and therefore are never inferred.
/// </summary>
public static class SuperelevationCommands
{
  // -------------------------------------------------------------------------
  // getSuperelevation
  // -------------------------------------------------------------------------

  public static Task<object?> GetSuperelevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var includeRawData = PluginRuntime.GetOptionalBool(parameters, "includeRawData") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      var curves = alignment.SuperelevationCurves;
      var designSpeeds = alignment.DesignSpeeds
        .Select(speed => new Dictionary<string, object?>
        {
          ["station"] = speed.Station,
          ["value"] = speed.Value,
          ["comment"] = speed.Comment,
        })
        .ToList();

      var result = new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeed"] = designSpeeds.FirstOrDefault()?.GetValueOrDefault("value"),
        ["designSpeeds"] = designSpeeds,
        ["attainmentMethod"] = null,
        ["normalCrownSlope"] = null,
        ["pivotPoint"] = null,
        ["superelevationType"] = alignment.SuperelevationType.ToString(),
        ["curveCount"] = curves.Count,
        ["hasSuperelevation"] = curves.Count > 0,
        ["managedApiLimit"] = "Civil 3D 2026 does not expose wizard attainment, crown, or pivot settings through the managed API.",
      };

      if (includeRawData)
      {
        result["rawTable"] = ReadSuperelevationTable(alignment);
      }

      return result;
    });
  }

  // -------------------------------------------------------------------------
  // setSuperelevation
  // -------------------------------------------------------------------------

  public static Task<object?> SetSuperelevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var normalCrownSlope = PluginRuntime.GetRequiredDouble(parameters, "normalCrownSlope");
    var attainmentMethod = PluginRuntime.GetOptionalString(parameters, "attainmentMethod") ?? "AASHTO_2011";
    var pivotPoint = PluginRuntime.GetOptionalString(parameters, "pivotPoint") ?? "centerline";

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 does not expose the superelevation wizard calculation settings through its managed API. Use the Civil 3D wizard or import reviewed superelevation data; no alignment data was modified.");
  }

  // -------------------------------------------------------------------------
  // checkSuperelevationDesign
  // -------------------------------------------------------------------------

  public static Task<object?> CheckSuperelevationDesignAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var maxSuperelevation = PluginRuntime.GetOptionalDouble(parameters, "maxSuperelevation")
      ?? throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "checkSuperelevationDesign requires an engineer-approved 'maxSuperelevation'; the plugin does not assume a design standard.");
    var checkAttainmentLength = PluginRuntime.GetOptionalBool(parameters, "checkAttainmentLength") ?? true;
    var checkRunoffLength = PluginRuntime.GetOptionalBool(parameters, "checkRunoffLength") ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var violations = new List<Dictionary<string, object?>>();
      var passed = true;

      if (checkAttainmentLength || checkRunoffLength)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          "The managed Civil 3D 2026 API does not expose enough criteria data to verify attainment or runoff length. Set both checks to false to evaluate only the explicit maximum cross-slope limit.");
      }

      var rawTable = ReadSuperelevationTable(alignment);
      if (rawTable.Count == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Alignment '{alignmentName}' has no calculated superelevation critical stations to check.");
      }

      // Check each station for violations
      foreach (var row in rawTable)
      {
        var leftSlope = row["leftSlope"] as double?;
        var rightSlope = row["rightSlope"] as double?;
        var station = Convert.ToDouble(row["station"]);

        if (!leftSlope.HasValue && !rightSlope.HasValue)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.API_ERROR",
            $"No lane cross-slope is defined at station {station} on alignment '{alignmentName}'.");
        }

        if ((leftSlope.HasValue && Math.Abs(leftSlope.Value) > maxSuperelevation)
          || (rightSlope.HasValue && Math.Abs(rightSlope.Value) > maxSuperelevation))
        {
          passed = false;
          violations.Add(new Dictionary<string, object?>
          {
            ["station"] = station,
            ["violationType"] = "MAX_SUPERELEVATION_EXCEEDED",
            ["leftSlope"] = leftSlope,
            ["rightSlope"] = rightSlope,
            ["maxAllowed"] = maxSuperelevation,
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeed"] = designSpeed,
        ["maxSuperelevation"] = maxSuperelevation,
        ["passed"] = passed,
        ["violationCount"] = violations.Count,
        ["violations"] = violations,
      };
    });
  }

  // -------------------------------------------------------------------------
  // generateSuperelevationReport
  // -------------------------------------------------------------------------

  public static Task<object?> GenerateSuperelevationReportAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var includeRunoffTable = PluginRuntime.GetOptionalBool(parameters, "includeRunoffTable") ?? true;
    var includeViolations = PluginRuntime.GetOptionalBool(parameters, "includeViolations") ?? true;
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      if (includeViolations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          "A report cannot claim design violations without an explicit reviewed criteria set. Set includeViolations=false to export the Civil 3D data table only.");
      }

      var designSpeeds = alignment.DesignSpeeds.Select(speed => speed.Value).ToList();
      var rawTable = includeRunoffTable ? ReadSuperelevationTable(alignment) : new List<Dictionary<string, object?>>();
      var sb = new StringBuilder();

      sb.AppendLine($"SUPERELEVATION REPORT — {alignmentName}");
      sb.AppendLine($"Design Speeds:       {(designSpeeds.Count == 0 ? "not assigned" : string.Join(", ", designSpeeds))}");
      sb.AppendLine($"Superelevation Type: {alignment.SuperelevationType}");
      sb.AppendLine($"Report Date:         {DateTime.Now:yyyy-MM-dd HH:mm}");
      sb.AppendLine(new string('-', 70));

      if (includeRunoffTable && rawTable.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("SUPERELEVATION TABLE");
        sb.AppendLine($"{"Station",-14} {"Left Slope %",-14} {"Right Slope %",-14} {"Notes",-20}");
        sb.AppendLine(new string('-', 62));
        foreach (var row in rawTable)
        {
          var station = row.TryGetValue("station", out var st) ? $"{Convert.ToDouble(st):F3}" : "—";
          var left = row.TryGetValue("leftSlope", out var ls) ? $"{Convert.ToDouble(ls):F2}" : "—";
          var right = row.TryGetValue("rightSlope", out var rs) ? $"{Convert.ToDouble(rs):F2}" : "—";
          var notes = row.TryGetValue("notes", out var n) ? n?.ToString() ?? "" : "";
          sb.AppendLine($"{station,-14} {left,-14} {right,-14} {notes,-20}");
        }
      }

      var reportText = sb.ToString();

      if (!string.IsNullOrWhiteSpace(outputPath))
      {
        outputPath = FileBoundary.WriteAllTextAtomic(
          outputPath, reportText, Encoding.UTF8, overwrite, ".txt");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeeds"] = designSpeeds,
        ["superelevationType"] = alignment.SuperelevationType.ToString(),
        ["rowCount"] = rawTable.Count,
        ["report"] = string.IsNullOrWhiteSpace(outputPath) ? reportText : null,
        ["outputPath"] = outputPath,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static List<Dictionary<string, object?>> ReadSuperelevationTable(Alignment alignment)
  {
    var table = new List<Dictionary<string, object?>>();

    foreach (SuperelevationCurve curve in alignment.SuperelevationCurves)
    {
      foreach (SuperelevationCriticalStation station in curve.CriticalStations)
      {
        var leftOut = TryGetSlopePercent(station, SuperelevationCrossSegmentType.LeftOutLaneCrossSlope);
        var leftIn = TryGetSlopePercent(station, SuperelevationCrossSegmentType.LeftInLaneCrossSlope);
        var rightOut = TryGetSlopePercent(station, SuperelevationCrossSegmentType.RightOutLaneCrossSlope);
        var rightIn = TryGetSlopePercent(station, SuperelevationCrossSegmentType.RightInLaneCrossSlope);
        table.Add(new Dictionary<string, object?>
        {
          ["curveName"] = curve.Name,
          ["station"] = station.Station,
          ["stationType"] = station.StationType.ToString(),
          ["transitionRegionType"] = station.TransitionRegionType.ToString(),
          ["leftOutLaneSlope"] = leftOut,
          ["leftInLaneSlope"] = leftIn,
          ["rightOutLaneSlope"] = rightOut,
          ["rightInLaneSlope"] = rightIn,
          ["leftSlope"] = MaximumMagnitude(leftOut, leftIn),
          ["rightSlope"] = MaximumMagnitude(rightOut, rightIn),
        });
      }
    }

    return table;
  }

  private static double? TryGetSlopePercent(
    SuperelevationCriticalStation station,
    SuperelevationCrossSegmentType segmentType)
  {
    try
    {
      return station.GetSlope(segmentType) * 100.0;
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }

  private static double? MaximumMagnitude(params double?[] values)
  {
    return values
      .Where(value => value.HasValue)
      .OrderByDescending(value => Math.Abs(value!.Value))
      .FirstOrDefault();
  }
}
