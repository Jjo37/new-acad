using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Slope geometry and stability analysis commands.
/// Calculates daylight line catch-points and flags unstable slope conditions.
/// </summary>
public static class SlopeAnalysisCommands
{
  // ─── calculateSlopeGeometry ──────────────────────────────────────────────────

  public static Task<object?> CalculateSlopeGeometryAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetOptionalString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var cutSlopeRatio = PluginRuntime.GetOptionalDouble(parameters, "cutSlopeRatio") ?? 2.0;
    var fillSlopeRatio = PluginRuntime.GetOptionalDouble(parameters, "fillSlopeRatio") ?? 3.0;
    var benchWidth = PluginRuntime.GetOptionalDouble(parameters, "benchWidth") ?? 0.0;
    var benchHeightInterval = PluginRuntime.GetOptionalDouble(parameters, "benchHeightInterval") ?? 20.0;
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var stationInterval = PluginRuntime.GetOptionalDouble(parameters, "stationInterval") ?? 10.0;
    var roadwayHalfWidth = PluginRuntime.GetOptionalDouble(parameters, "roadwayWidth") ?? 0.0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Resolve alignment
      Alignment? alignment = null;
      if (!string.IsNullOrWhiteSpace(alignmentName))
        alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);

      if (alignment == null)
        alignment = GetFirstAlignment(civilDoc, transaction);

      if (alignment == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No alignment found in drawing.");

      // Resolve profile
      Profile? profile = null;
      if (!string.IsNullOrWhiteSpace(profileName))
        profile = FindProfileByName(alignment, transaction, profileName!);

      if (profile == null)
        profile = GetFirstProfile(alignment, transaction, preferFinishedGrade: true);

      // Resolve surface
      CivilSurface? surface = null;
      if (!string.IsNullOrWhiteSpace(surfaceName))
        surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName!, OpenMode.ForRead);

      if (surface == null)
        surface = GetFirstSurface(civilDoc, transaction);

      if (profile == null || surface == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Could not resolve profile or surface.");

      var alignLen = alignment.Length;
      var startSta = stationStart ?? alignment.StartingStation;
      var endSta = stationEnd ?? (startSta + alignLen);

      var rows = new List<Dictionary<string, object?>>();
      double maxCutHeight = 0, maxFillHeight = 0;

      for (double sta = startSta; sta <= endSta + 0.01; sta += stationInterval)
      {
        var clampedSta = Math.Min(sta, endSta);

        // Get design elevation at station
        double designElev = GetProfileElevationAtStation(profile, clampedSta);

        // Get XY of station on alignment
        var (ptX, ptY) = GetAlignmentPointAtStation(alignment, clampedSta);

        // Get ground elevation at that XY
        double groundElev = GetSurfaceElevation(surface, ptX, ptY);

        double cutFill = designElev - groundElev; // positive = fill, negative = cut
        bool isCut = cutFill < 0;
        double height = Math.Abs(cutFill);

        double slopeRatio = isCut ? cutSlopeRatio : fillSlopeRatio;

        // Calculate catch-point offset including benching
        double catchOffset = roadwayHalfWidth + CalculateCatchOffset(height, slopeRatio, benchWidth, benchHeightInterval);

        if (isCut) maxCutHeight = Math.Max(maxCutHeight, height);
        else maxFillHeight = Math.Max(maxFillHeight, height);

        rows.Add(new Dictionary<string, object?>
        {
          ["station"] = Math.Round(clampedSta, 2),
          ["designElevation"] = Math.Round(designElev, 3),
          ["groundElevation"] = Math.Round(groundElev, 3),
          ["cutFillDepth"] = Math.Round(cutFill, 3),
          ["condition"] = isCut ? "CUT" : (Math.Abs(cutFill) < 0.05 ? "MATCH" : "FILL"),
          ["slopeRatio"] = $"{slopeRatio}:1",
          ["slopeHeight"] = Math.Round(height, 3),
          ["catchPointOffset"] = Math.Round(catchOffset, 2),
          ["benchCount"] = benchWidth > 0 && height > 0 ? (int)(height / benchHeightInterval) : 0,
        });

        if (clampedSta >= endSta) break;
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["surfaceName"] = surface.Name,
        ["cutSlopeRatio"] = $"{cutSlopeRatio}:1",
        ["fillSlopeRatio"] = $"{fillSlopeRatio}:1",
        ["benchWidth"] = benchWidth,
        ["stationCount"] = rows.Count,
        ["maximumCutHeight"] = Math.Round(maxCutHeight, 2),
        ["maximumFillHeight"] = Math.Round(maxFillHeight, 2),
        ["stations"] = rows,
      };
    });
  }

  // ─── checkSlopeStability ─────────────────────────────────────────────────────

  public static Task<object?> CheckSlopeStabilityAsync(JsonObject? parameters)
  {
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Slope stability cannot be determined from surface slope and a soil-type label. A valid check requires explicit cut/fill geometry plus engineer-approved geotechnical parameters and a factor-of-safety method. No result was generated.");
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  private static double CalculateCatchOffset(double height, double slopeRatio, double benchWidth, double benchInterval)
  {
    if (benchWidth <= 0 || benchInterval <= 0 || height <= 0)
      return height * slopeRatio;

    int benches = (int)(height / benchInterval);
    double remaining = height - benches * benchInterval;
    return (benches * benchInterval * slopeRatio) + (benches * benchWidth) + (remaining * slopeRatio);
  }

  private static double GetProfileElevationAtStation(Profile profile, double station)
  {
    try
    {
      return profile.ElevationAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static (double X, double Y) GetAlignmentPointAtStation(Alignment alignment, double station)
  {
    try
    {
      double x = 0;
      double y = 0;
      alignment.PointLocation(station, 0, ref x, ref y);
      return (x, y);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not locate alignment '{alignment.Name}' at station {station}: {exception.Message}");
    }
  }

  private static double GetSurfaceElevation(CivilSurface surface, double x, double y)
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

  private static Alignment? GetFirstAlignment(CivilDocument civilDoc, Transaction transaction)
  {
    foreach (ObjectId id in civilDoc.GetAlignmentIds())
    {
      return CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
    }

    return null;
  }

  private static CivilSurface? GetFirstSurface(CivilDocument civilDoc, Transaction transaction)
  {
    foreach (ObjectId id in civilDoc.GetSurfaceIds())
    {
      return CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
    }

    return null;
  }

  private static Profile? FindProfileByName(Alignment alignment, Transaction transaction, string name)
  {
    foreach (ObjectId id in alignment.GetProfileIds())
    {
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead);
      if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return profile;
      }
    }

    return null;
  }

  private static Profile? GetFirstProfile(Alignment alignment, Transaction transaction, bool preferFinishedGrade)
  {
    Profile? first = null;
    foreach (ObjectId id in alignment.GetProfileIds())
    {
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead);
      first ??= profile;
      if (preferFinishedGrade && (profile.Name.Contains("FG", StringComparison.OrdinalIgnoreCase)
        || profile.Name.Contains("Design", StringComparison.OrdinalIgnoreCase)))
      {
        return profile;
      }
    }

    return first;
  }
}
