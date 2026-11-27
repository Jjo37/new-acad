using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// B1.3：操作日志查询口（日志由 OperationTrace 落盘；注意与图像描摹的 TraceCommands 无关系）。
///   readTrace {limit?, method?, onlyFailed?} → 最近 N 条（倒序取、正序返回）
///   traceStats {}                            → 今日汇总（总数/失败数/按方法）
/// </summary>
public static class TraceQueryCommands
{
  public static Task<object?> ReadTraceAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 50;
    if (limit <= 0 || limit > 5000) limit = 50;
    var method = PluginRuntime.GetOptionalString(parameters, "method");
    var onlyFailed = PluginRuntime.GetOptionalBool(parameters, "onlyFailed") ?? false;

    var entries = OperationTrace.Read(limit, method, onlyFailed);
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["count"] = entries.Count,
      ["file"] = OperationTrace.CurrentTraceFile(),
      ["entries"] = entries,
    });
  }

  public static Task<object?> TraceStatsAsync(JsonObject? parameters)
  {
    var (total, failed, byMethod) = OperationTrace.Stats();
    var byMethodOut = new Dictionary<string, object?>();
    foreach (var kv in byMethod) byMethodOut[kv.Key] = kv.Value;

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["file"] = OperationTrace.CurrentTraceFile(),
      ["total"] = total,
      ["failed"] = failed,
      ["byMethod"] = byMethodOut,
    });
  }
}
