using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Detention basin sizing and stage-storage commands.
/// Implements Modified Rational Method storage estimation and
/// stage-storage table generation from Civil 3D surfaces.
/// </summary>
public static class DetentionCommands
{
  // ─── calculateDetentionBasinSize ─────────────────────────────────────────────

  public static Task<object?> CalculateDetentionBasinSizeAsync(JsonObject? parameters)
  {
    var inflow = PluginRuntime.GetRequiredDouble(parameters, "inflow");
    var outflow = PluginRuntime.GetRequiredDouble(parameters, "outflow");
    var stormDuration = PluginRuntime.GetOptionalDouble(parameters, "stormDuration") ?? 60.0;
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "modified_rational";
    var sideSlope = PluginRuntime.GetOptionalDouble(parameters, "sideSlope") ?? 3.0;
    var bottomWidth = PluginRuntime.GetOptionalDouble(parameters, "bottomWidth") ?? (3.0 * CivilObjectUtils.ActiveLengthFactor());
    var freeboardDepth = PluginRuntime.GetOptionalDouble(parameters, "freeboardDepth") ?? (0.3 * CivilObjectUtils.ActiveLengthFactor());
    // 2026-10-15: 按图纸单位决定体积/直径标签（流入流出量视作与图纸单位一致的流量单位：m³/s 或 cfs）
    var isMetric = CivilObjectUtils.ActiveIsMetric();

    if (outflow >= inflow)
    {
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["error"] = "Outflow must be less than inflow for detention to be required.",
        ["inflow"] = inflow,
        ["outflow"] = outflow,
        ["storageRequired"] = false,
      });
    }

    // Modified Rational Method: Vs = (Qi - Qo) * tc * 60  [drawing-unit³（m³ 或 ft³）]
    double storageVolumeNative;
    string methodLabel;

    switch (method.ToLowerInvariant())
    {
      case "triangular_hydrograph":
        // Approximate: Vs ≈ 0.5 * (Qi - Qo) * tc * 60
        storageVolumeNative = 0.5 * (inflow - outflow) * stormDuration * 60.0;
        methodLabel = "Triangular Hydrograph Approximation";
        break;
      default: // modified_rational
        storageVolumeNative = (inflow - outflow) * stormDuration * 60.0;
        methodLabel = "Modified Rational Method";
        break;
    }

    // 体积换算（按图纸单位标注；同时给出另一单位以便参考）
    var storageVolumeCuFt = isMetric ? storageVolumeNative * 35.3147 : storageVolumeNative;
    var storageVolumeCuM = isMetric ? storageVolumeNative : storageVolumeNative / 35.3147;
    var storageVolumeAcFt = storageVolumeCuFt / 43560.0;

    // Basin geometry — trapezoidal cross-section, assuming L = 2 * W for plan view
    // V = d * (L*W + (L + 2*H*z) * (W + 2*H*z) + 4 * (L + H*z) * (W + H*z)) / 6  (frustum formula)
    // Simplified: iterate depth to find where volume matches
    double basinDepth = EstimateBasinDepth(storageVolumeNative, bottomWidth, sideSlope);
    double basinLength = 2.0 * bottomWidth;
    double topWidth = bottomWidth + 2.0 * sideSlope * basinDepth;
    double topLength = basinLength + 2.0 * sideSlope * basinDepth;
    double totalDepth = basinDepth + freeboardDepth;

    // Orifice sizing: Q = Cd * A * sqrt(2*g*h)  →  A = Q / (Cd * sqrt(2*g*h))
    double cd = 0.6;
    double g = CivilObjectUtils.ActiveGravity();   // 2026-10-15: 按图纸单位（9.81 m/s² / 32.2 ft/s²）
    double headH = basinDepth; // design head = full basin depth
    double orificeArea = outflow / (cd * Math.Sqrt(2.0 * g * headH));
    double orificeDiameterNative = Math.Sqrt(4.0 * orificeArea / Math.PI);
    double orificeDiameterSmall = isMetric ? orificeDiameterNative * 1000.0 : orificeDiameterNative * 12.0;   // mm / in
    var diaUnit = isMetric ? "mm" : "in";
    var recommendedDia = isMetric ? Math.Ceiling(orificeDiameterSmall / 10.0) * 10.0 : Math.Ceiling(orificeDiameterSmall * 2) / 2.0;

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["method"] = methodLabel,
      ["inflow"] = inflow,
      ["outflow"] = outflow,
      ["stormDurationMinutes"] = stormDuration,
      ["flowUnits"] = isMetric ? "m³/s" : "cfs",
      ["volumeUnits"] = isMetric ? "m³" : "ft³",
      ["requiredStorageNative"] = Math.Round(storageVolumeNative, 1),
      ["requiredStorageCubicFeet"] = Math.Round(storageVolumeCuFt, 0),
      ["requiredStorageAcreFeet"] = Math.Round(storageVolumeAcFt, 3),
      ["requiredStorageCubicMeters"] = Math.Round(storageVolumeCuM, 1),
      ["basinGeometry"] = new Dictionary<string, object?>
      {
        ["bottomWidth"] = Math.Round(bottomWidth, 1),
        ["bottomLength"] = Math.Round(basinLength, 1),
        ["sideSlope"] = $"{sideSlope}:1",
        ["designDepth"] = Math.Round(basinDepth, 2),
        ["freeboardDepth"] = Math.Round(freeboardDepth, 2),
        ["totalDepth"] = Math.Round(totalDepth, 2),
        ["topWidth"] = Math.Round(topWidth, 1),
        ["topLength"] = Math.Round(topLength, 1),
      },
      ["outletOrifice"] = new Dictionary<string, object?>
      {
        ["designFlow"] = outflow,
        ["dischargeCoefficient"] = cd,
        ["designHead"] = Math.Round(headH, 2),
        ["requiredArea"] = Math.Round(orificeArea, 4),
        ["areaUnits"] = isMetric ? "m²" : "ft²",
        ["requiredDiameter"] = Math.Round(orificeDiameterSmall, 2),
        ["recommendedDiameter"] = Math.Round(recommendedDia, 1),
        ["diameterUnits"] = diaUnit,
      },
      ["notes"] = new[]
      {
        "Storage volume uses simplified Modified Rational Method — confirm with routing software for permit submittals.",
        "Basin dimensions assume trapezoidal cross-section with L = 2W plan ratio.",
        "Orifice size based on full-depth head — verify with stage-discharge routing.",
        "Add freeboard to design depth before specifying construction drawings.",
      },
    });
  }

  // ─── calculateDetentionStageStorage ──────────────────────────────────────────

  public static Task<object?> CalculateDetentionStageStorageAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var bottomElevation = PluginRuntime.GetRequiredDouble(parameters, "bottomElevation");
    var topElevation = PluginRuntime.GetRequiredDouble(parameters, "topElevation");
    var elevIncrement = PluginRuntime.GetOptionalDouble(parameters, "elevationIncrement") ?? (0.15 * CivilObjectUtils.ActiveLengthFactor());
    var outletType = PluginRuntime.GetOptionalString(parameters, "outletType") ?? "orifice";
    var outletDiameter = PluginRuntime.GetOptionalDouble(parameters, "outletDiameter"); // inches
    var weirLength = PluginRuntime.GetOptionalDouble(parameters, "weirLength");
    var cd = PluginRuntime.GetOptionalDouble(parameters, "dischargeCoefficient")
            ?? (string.Equals(outletType, "weir", StringComparison.OrdinalIgnoreCase) ? 3.33 : 0.6);

    if (topElevation <= bottomElevation)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "topElevation must be greater than bottomElevation.");
    }
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Stage-storage for surface '{surfaceName}' is unavailable because the Civil 3D 2026 .NET surface API does not expose " +
      "the inundated plan area at an arbitrary elevation. Provide surveyed stage-area data or an explicit basin boundary workflow; " +
      "the server will not synthesize storage from a bounding box.");
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  private static double EstimateBasinDepth(double targetVolumeCf, double bottomWidth, double sideSlope)
  {
    double basinLength = 2.0 * bottomWidth;
    double depth = 1.0;
    for (int iter = 0; iter < 50; iter++)
    {
      double vol = TrapezoidalVolume(depth, bottomWidth, basinLength, sideSlope);
      if (Math.Abs(vol - targetVolumeCf) < 1.0) break;
      depth *= (targetVolumeCf / vol);
      depth = Math.Max(0.5, Math.Min(depth, 50.0));
    }
    return Math.Max(1.0, depth);
  }

  private static double TrapezoidalVolume(double depth, double bottomWidth, double bottomLength, double sideSlope)
  {
    double topWidth = bottomWidth + 2 * sideSlope * depth;
    double topLength = bottomLength + 2 * sideSlope * depth;
    // Frustum formula: V = d/6 * (A_bottom + A_top + 4*A_mid)
    double aMid = (bottomWidth + sideSlope * depth) * (bottomLength + sideSlope * depth);
    return depth / 6.0 * (bottomWidth * bottomLength + topWidth * topLength + 4 * aMid);
  }

}
