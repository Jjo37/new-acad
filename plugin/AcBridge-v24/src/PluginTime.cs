namespace Civil3DMcpPlugin;

/// <summary>
/// 时间的唯一存法（P2.6 S7，2026-10-28）。
///
/// 规则和 JS 侧 server/time-policy.js 一致：
///   T1 存：UTC，固定格式 `o`（ISO 8601 带毫秒 + Z）
///   T2 派生标识（文件名里的时间戳）：也从 UTC 算
///   T3 显：只在 UI 层用本地时间，且格式固定
///
/// 病根：选择集快照的 savedAt 和 trace 文件名用的是 DateTime.Now（本地）。
/// 同一份数据在不同时区的机器上写出来的字符串不一样 —— 落盘的东西就不该带时区自由度。
/// </summary>
public static class PluginTime
{
  /// <summary>T1：落盘用的时间戳（UTC ISO，毫秒 + Z）。</summary>
  public static string StoredNow() => System.DateTime.UtcNow.ToString("o");

  /// <summary>T1：校验一条字符串是不是合法存法（必须是 UTC 且带毫秒）。</summary>
  public static bool IsStoredUtc(string? value)
  {
    if (string.IsNullOrEmpty(value)) return false;
    return System.DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
             System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
           && dt.Kind == System.DateTimeKind.Utc
           && value!.EndsWith("Z", System.StringComparison.Ordinal);
  }

  /// <summary>T2：文件名里的时间戳（UTC，`yyyyMMdd-HHmmss`）。</summary>
  public static string FileStamp(System.DateTime utc) =>
    utc.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>T2：日期戳（UTC，`yyyyMMdd`）—— 按天分文件的 trace 用它。</summary>
  public static string DateStamp(System.DateTime utc) =>
    utc.ToUniversalTime().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>T2：trace 文件名（UTC 派生，跨时区一致）。</summary>
  public static string TraceFileName(System.DateTime utc) => "trace-" + DateStamp(utc) + ".jsonl";

  /// <summary>T3：展示（本地时间，固定格式）。</summary>
  public static string LocalDisplay(System.DateTime utc, bool withSeconds = true) =>
    utc.ToLocalTime().ToString(withSeconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm",
      System.Globalization.CultureInfo.InvariantCulture);
}
