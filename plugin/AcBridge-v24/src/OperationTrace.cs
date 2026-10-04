using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// B1.1（2026-09-28）：**结构化操作日志** —— 决策链可验证性的地基。
///
/// 每个 JSON-RPC 请求写一行 JSONL：
///   {seq, ts, ms, method, outcome, errorCode?, errorMessage?, docName?, params?}
/// 落盘：&lt;ProjectRoot&gt;\exchange\trace\trace-yyyyMMdd.jsonl
/// 用途：① 复盘（AI 到底调了什么/慢在哪/哪一步失败）② 回放复现（tools/replay.js 逐条重放到出错那一步）
///
/// 设计约束：日志失败**绝不影响**主流程（全程 try/catch 吞掉）；参数只存"摘要"（长串截断、数组存计数+样本）。
/// </summary>
public static class OperationTrace
{
  private static readonly object Sync = new();
  private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
  private static long _seq;
  // 序列化口径：与 JS JSON.stringify 对齐（不转义非 ASCII / HTML 字符），否则中文会导致 digest 假不一致
  private static readonly System.Text.Json.JsonSerializerOptions ResultJsonOptions = new System.Text.Json.JsonSerializerOptions
  {
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  public static string TraceDirectory() => Path.Combine(PluginRuntime.ProjectRoot(), "exchange", "trace");

  public static string CurrentTraceFile() => Path.Combine(TraceDirectory(), "trace-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");

  public static void Record(string method, JsonObject? parameters, string outcome, string? errorCode, string? errorMessage, long durationMs, string? resultJson = null)
  {
    try
    {
      var entry = new JsonObject
      {
        ["seq"] = System.Threading.Interlocked.Increment(ref _seq),
        ["ts"] = DateTime.UtcNow.ToString("o"),
        ["ms"] = durationMs,
        ["method"] = method,
        ["outcome"] = outcome,
      };
      if (!string.IsNullOrEmpty(errorCode)) entry["errorCode"] = errorCode;
      if (!string.IsNullOrEmpty(errorMessage)) entry["errorMessage"] = Truncate(errorMessage!, 300);
      if (!string.IsNullOrEmpty(resultJson))
      {
        entry["digest"] = DigestOf(resultJson!);
        entry["sample"] = Truncate(resultJson!, 2000);   // 回放时逐字对比
      }
      try
      {
        var doc = PluginRuntime.GetActiveDrawingIdentity();
        if (!string.IsNullOrEmpty(doc)) entry["docName"] = Path.GetFileName(doc!);
      }
      catch { }
      if (parameters != null && parameters.Count > 0)
      {
        entry["params"] = Summarize(parameters, 0);          // 人读摘要
        var raw = parameters.ToJsonString();
        if (raw.Length <= 8000) entry["paramsRaw"] = parameters.DeepClone();   // 回放用原始参数
        else entry["paramsTruncated"] = true;
      }

      Directory.CreateDirectory(TraceDirectory());
      lock (Sync)
      {
        File.AppendAllText(CurrentTraceFile(), entry.ToJsonString() + "\n", Utf8NoBom);
      }
    }
    catch { /* 记录失败不影响主流程 */ }
  }

  /// <summary>把结果对象序列化成 JSON（回放对比用）；失败返回 null。</summary>
  public static string? SerializeResult(object? result)
  {
    try { return System.Text.Json.JsonSerializer.Serialize(result, ResultJsonOptions); } catch { return null; }
  }

  /// <summary>结果指纹：SHA1 前 12 位 + JSON 长度（判定"结果是否可复现"）。</summary>
  public static string DigestOf(string json)
  {
    try
    {
      using var sha = System.Security.Cryptography.SHA1.Create();
      var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
      var hex = new StringBuilder();
      for (var i = 0; i < 6; i++) hex.Append(hash[i].ToString("x2"));
      return hex + ":" + json.Length;
    }
    catch { return "unknown"; }
  }

  public static List<JsonObject> Read(int limit, string? method, bool onlyFailed)
  {
    var list = new List<JsonObject>();
    var file = CurrentTraceFile();
    if (!File.Exists(file)) return list;
    string[] lines;
    lock (Sync) { lines = File.ReadAllLines(file); }
    for (var i = lines.Length - 1; i >= 0 && list.Count < limit; i--)
    {
      if (string.IsNullOrWhiteSpace(lines[i])) continue;
      JsonObject? obj = null;
      try { obj = JsonNode.Parse(lines[i]) as JsonObject; } catch { }
      if (obj == null) continue;
      if (!string.IsNullOrEmpty(method))
      {
        var m = obj["method"]?.GetValue<string>();
        if (!string.Equals(m, method, StringComparison.OrdinalIgnoreCase)) continue;
      }
      if (onlyFailed)
      {
        var o = obj["outcome"]?.GetValue<string>();
        if (string.Equals(o, "ok", StringComparison.OrdinalIgnoreCase)) continue;
      }
      list.Add(obj);
    }
    list.Reverse();
    return list;
  }

  public static (int total, int failed, Dictionary<string, int> byMethod) Stats()
  {
    var byMethod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var total = 0; var failed = 0;
    var file = CurrentTraceFile();
    if (!File.Exists(file)) return (0, 0, byMethod);
    string[] lines;
    lock (Sync) { lines = File.ReadAllLines(file); }
    foreach (var line in lines)
    {
      if (string.IsNullOrWhiteSpace(line)) continue;
      JsonObject? obj = null;
      try { obj = JsonNode.Parse(line) as JsonObject; } catch { }
      if (obj == null) continue;
      total++;
      var m = obj["method"]?.GetValue<string>() ?? "?";
      byMethod[m] = byMethod.TryGetValue(m, out var c) ? c + 1 : 1;
      if (!string.Equals(obj["outcome"]?.GetValue<string>(), "ok", StringComparison.OrdinalIgnoreCase)) failed++;
    }
    return (total, failed, byMethod);
  }

  private static JsonNode Summarize(JsonObject obj, int depth)
  {
    var outObj = new JsonObject();
    foreach (var kv in obj) outObj[kv.Key] = SummarizeValue(kv.Value, depth + 1);
    return outObj;
  }

  private static JsonNode? SummarizeValue(JsonNode? node, int depth)
  {
    switch (node)
    {
      case null:
        return null;
      case JsonArray arr:
        {
          var sample = new JsonArray();
          var i = 0;
          foreach (var item in arr)
          {
            if (i++ >= 3) break;
            sample.Add(SummarizeValue(item, depth + 1));
          }
          return new JsonObject { ["__count"] = arr.Count, ["sample"] = sample };
        }
      case JsonObject obj:
        return depth > 3
          ? (JsonNode)new JsonObject { ["__keys"] = string.Join(",", Keys(obj)) }
          : Summarize(obj, depth);
      case JsonValue value:
        {
          var text = value.ToJsonString();
          return text.Length > 120 ? JsonValue.Create(Truncate(text, 120)) : value.DeepClone();
        }
      default:
        return null;
    }
  }

  private static IEnumerable<string> Keys(JsonObject obj)
  {
    foreach (var kv in obj) yield return kv.Key;
  }

  private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "…";
}
