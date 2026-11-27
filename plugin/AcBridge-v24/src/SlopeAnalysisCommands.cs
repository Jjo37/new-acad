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
    var benchHeightInterval = PluginRuntime.GetOptionalDouble(parameters, "benchHeightInterval") ?? (6.0 * CivilObjectUtils.ActiveLengthFactor());
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var stationInterval = PluginRuntime.GetOptionalDouble(parameters, "stationInterval") ?? (3.0 * CivilObjectUtils.ActiveLengthFactor());
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
      {
        // 2026-11-18 P32: 旧文案 "Could not resolve profile or surface." 既不说是哪个缺，
        // 也不符合工程里「图纸缺对象 → No ... found」的约定（门禁按措辞分桶，会被误判成真问题）。
        var missing = (profile == null ? "profile" : "") +
                      (profile == null && surface == null ? " and " : "") +
                      (surface == null ? "surface" : "");
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No {missing} found for alignment '{alignment.Name}' - slope geometry needs a finished-grade profile and a surface. Create them (or pass profileName / surfaceName) and retry.");
      }

      var alignLen = alignment.Length;
      var startSta = stationStart ?? alignment.StartingStation;
      var endSta = stationEnd ?? (startSta + alignLen);

      // 2026-11-18 P32: 纵断面范围常常比路线短（投影只覆盖一部分），
      // 旧实现拿 startSta+alignLen 当终点 → 末段桩号越界，GetProfileElevationAtStation 抛
      // "Value does not fall within the expected range"，整批采样失败。
      // 现在把窗口夹到两者的交集，并如实回 profileStationRange。
      double profStart, profEnd;
      try { profStart = profile.StartingStation; profEnd = profile.EndingStation; }
      catch { profStart = startSta; profEnd = endSta; }
      if (profEnd > profStart)
      {
        startSta = Math.Max(startSta, profStart);
        endSta = Math.Min(endSta, profEnd);
      }
      if (endSta <= startSta)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No sampled station range found: alignment '{alignment.Name}' runs {alignment.StartingStation:F2}-{alignment.StartingStation + alignLen:F2} but profile '{profile.Name}' only covers {profStart:F2}-{profEnd:F2}. Pass stationStart/stationEnd inside the profile, or pick another profileName.");
      }

      var rows = new List<Dictionary<string, object?>>();
      // 2026-11-18 P32: 路线端点常落在曲面边界外，FindElevationAtXY 抛 "Point Outside Surface"。
      // 旧实现让整批采样炸掉（门禁里算成真问题），客户拿到的是一个错误而不是"哪些桩号没数据"。
      var skippedStations = new List<Dictionary<string, object?>>();
      double maxCutHeight = 0, maxFillHeight = 0;

      for (double sta = startSta; sta <= endSta + 0.01; sta += stationInterval)
      {
        var clampedSta = Math.Min(sta, endSta);

        // Get design elevation at station
        double designElev;
        try
        {
          designElev = GetProfileElevationAtStation(profile, clampedSta);
        }
        catch (JsonRpcDispatchException ex)
        {
          skippedStations.Add(new Dictionary<string, object?>
          {
            ["station"] = Math.Round(clampedSta, 2),
            ["reason"] = ex.Message,
          });
          if (clampedSta >= endSta) break;
          continue;
        }

        // Get XY of station on alignment
        var (ptX, ptY) = GetAlignmentPointAtStation(alignment, clampedSta);

        // Get ground elevation at that XY（越界就跳过并记账，不炸整批）
        double groundElev;
        try
        {
          groundElev = GetSurfaceElevation(surface, ptX, ptY);
        }
        catch (JsonRpcDispatchException ex)
        {
          skippedStations.Add(new Dictionary<string, object?>
          {
            ["station"] = Math.Round(clampedSta, 2),
            ["reason"] = ex.Message,
          });
          if (clampedSta >= endSta) break;
          continue;
        }

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

      if (rows.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No slope samples found for alignment '{alignment.Name}': all {skippedStations.Count} stations fell outside surface '{surface.Name}'. Pass a stationStart/stationEnd inside the surface extents, or pick another surfaceName.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["surfaceName"] = surface.Name,
        ["profileStationRange"] = new List<double> { Math.Round(profStart, 2), Math.Round(profEnd, 2) },
        ["skippedStationCount"] = skippedStations.Count,
        ["skippedStations"] = skippedStations.Take(20).ToList(),
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
