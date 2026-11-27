namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-10-15: 单位系统 —— **零 Autodesk 依赖**的纯函数（可脱离 CAD 单测）。
/// 默认值按 SI（米）定义，按图纸单位取换算系数；公式常数（重力 / Manning）按单位取。
/// 由 <see cref="CivilObjectUtils"/> 包装 Database 调用；测试见 plugin/AcBridge-v24/tests/UnitSystemTests。
/// </summary>
public static class UnitSystem
{
  /// <summary>1 米 = 英尺。</summary>
  public const double MetersToFeet = 3.280839895013123;

  /// <summary>
  /// 请求级单位覆盖解析（"metric"/"imperial"/中英文词/缩写）。
  /// 命中 → true 并给出 isMetric；"auto"/空/未知 → false（调用方回退图纸探测）。
  /// </summary>
  public static bool TryParseOverride(string? units, out bool isMetric)
  {
    isMetric = true;
    if (string.IsNullOrWhiteSpace(units)) return false;
    var u = units.Trim().ToLowerInvariant();
    if (u.StartsWith("met") || u == "m" || u == "si" || u == "meters" || u == "metric" || u.Contains("公制")) { isMetric = true; return true; }
    if (u.StartsWith("imp") || u == "ft" || u == "feet" || u == "us" || u == "imperial" || u.Contains("英制")) { isMetric = false; return true; }
    return false;
  }

  /// <summary>长度换算系数：默认值按 SI 定义，× 系数得图纸单位下的值。</summary>
  public static double LengthFactor(bool isMetric) => isMetric ? 1.0 : MetersToFeet;

  /// <summary>重力加速度：公制 9.81 m/s²，英制 32.2 ft/s²。</summary>
  public static double Gravity(bool isMetric) => isMetric ? 9.81 : 32.2;

  /// <summary>Manning 公式常数：SI=1.0，US customary（英尺）=1.486。</summary>
  public static double ManningConstant(bool isMetric) => isMetric ? 1.0 : 1.486;

  public static string VelocityUnits(bool isMetric) => isMetric ? "m/s" : "ft/s";
  public static string FlowUnits(bool isMetric) => isMetric ? "m³/s" : "cfs";

  /// <summary>AutoCAD Insunits 名（"Meters"/"Feet"/"US Survey Feet"…）→ 线性单位标签。</summary>
  public static string LinearUnits(string? insunitsName)
  {
    var n = (insunitsName ?? "").ToLowerInvariant();
    if (n == "meters") return "meters";
    if (n == "feet") return "feet";
    if (n.Contains("survey")) return "feet";
    return "other";
  }

  /// <summary>AutoCAD Insunits 名 → 体积单位标签。</summary>
  public static string VolumeUnits(string? insunitsName)
  {
    var n = (insunitsName ?? "").ToLowerInvariant();
    if (n == "meters") return "cubic meters";
    if (n == "feet") return "cubic feet";
    if (n.Contains("survey")) return "cubic feet";
    return "cubic units";
  }
}
