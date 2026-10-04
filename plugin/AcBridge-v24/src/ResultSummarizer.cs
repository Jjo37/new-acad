using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// C2（2026-09-28）：**大结果自动降级** —— list*/get* 返回过大时不硬塞上下文。
///
/// 两种策略（按优先级）：
///   ① **形状保持式裁剪**（主）：保留原结果的键结构，把最占体积的数组截断成前缀
///      （`samples` 还在，只是变短），并追加 `_summarized` 说明裁了什么、怎么取全量。
///      → 调用方（AI/脚本）不会因为"键不见了"而崩。
///   ② **结构化画像信封**（兜底）：连截断都压不下去时（如单个巨型字符串），换成
///      profile（体量分布 + 集合画像 + 数值区间 + 低基数字段取值）+ 小样本。
///
/// 挂点：PluginRuntime.HandleRawRequestAsync 成功出口（与 B1 trace 同一咽喉）。
/// 逃生门：`fullResult:true` / `maxResultBytes:N`（0=不限）。摘要失败一律回退原结果。
/// </summary>
public static class ResultSummarizer
{
  public const int DefaultThresholdBytes = 65536;

  public readonly struct Outcome
  {
    public Outcome(object? value, string serialized, bool summarized)
    {
      Value = value;
      Serialized = serialized;
      Summarized = summarized;
    }
    public object? Value { get; }
    public string Serialized { get; }
    public bool Summarized { get; }
  }

  public static Outcome ApplyIfOversized(object? result, string serialized, string method, JsonObject? parameters)
  {
    var original = new Outcome(result, serialized, false);
    try
    {
      if (result == null) return original;
      if (PluginRuntime.GetOptionalBool(parameters, "fullResult") ?? false) return original;

      var threshold = PluginRuntime.GetOptionalInt(parameters, "maxResultBytes") ?? DefaultThresholdBytes;
      if (threshold <= 0) return original;

      var bytes = Encoding.UTF8.GetByteCount(serialized);
      if (bytes <= threshold) return original;

      var node = JsonNode.Parse(serialized);
      if (node == null) return original;

      var shapeOutcome = TryShapePreserving(node, bytes, threshold, method, parameters);
      if (shapeOutcome.HasValue) return shapeOutcome.Value;

      var envelope = BuildEnvelope(node, bytes, threshold, method, parameters);
      Shrink(envelope, envelope, threshold);
      return new Outcome(envelope, OperationTrace.SerializeResult(envelope), true);
    }
    catch
    {
      return original;   // 摘要失败绝不影响主流程
    }
  }

  // ---------- 策略①：形状保持式裁剪 ----------
  private static Outcome? TryShapePreserving(JsonNode node, long bytes, int threshold, string method, JsonObject? parameters)
  {
    if (node is not JsonObject root) return null;   // 顶层不是对象 → 交给画像

    var truncations = new List<object?>();
    var summary = new Dictionary<string, object?>
    {
      ["method"] = method,
      ["originalBytes"] = bytes,
      ["thresholdBytes"] = threshold,
      ["truncations"] = truncations,
      ["note"] = "结果过大，已按原结构截断（前缀保留）；需要全量请用 fullAvailableVia",
      ["fullAvailableVia"] = FullVia(method, parameters),
    };
    root["_summarized"] = JsonSerializer.SerializeToNode(summary);

    long Size() => Encoding.UTF8.GetByteCount(root.ToJsonString());

    var ok = false;
    for (var attempt = 0; attempt < 60; attempt++)
    {
      if (Size() <= threshold) { ok = true; break; }
      if (ShrinkLargestArray(root, truncations)) continue;
      if (ShrinkLongestString(root, truncations)) continue;
      break;
    }
    if (!ok) return null;   // 压不下去 → 兜底信封

    // 更新说明（truncations 是引用型 List，需要重算 note 里的统计）
    root["_summarized"] = JsonSerializer.SerializeToNode(summary);
    return new Outcome(root, root.ToJsonString(), true);
  }

  /// <summary>找当前最占体积的数组（深度 ≤ 4），保留前缀一半（至少 1 个元素）</summary>
  private static bool ShrinkLargestArray(JsonNode node, List<object?> truncations)
  {
    JsonArray? biggest = null;
    long biggestBytes = 0;
    string biggestPath = string.Empty;

    void Walk(JsonNode? current, string path, int depth)
    {
      if (current == null || depth > 4) return;
      if (current is JsonObject obj)
      {
        foreach (var kv in obj)
        {
          if (kv.Value is JsonArray arr)
          {
            var b = Encoding.UTF8.GetByteCount(arr.ToJsonString());
            if (arr.Count > 1 && b > biggestBytes) { biggest = arr; biggestBytes = b; biggestPath = path + "/" + kv.Key; }
            Walk(arr, path + "/" + kv.Key, depth + 1);
          }
          else Walk(kv.Value, path + "/" + kv.Key, depth + 1);
        }
      }
    }
    Walk(node, "", 0);

    if (biggest == null || biggestBytes < 256) return false;
    var original = biggest.Count;
    var keep = Math.Max(1, original / 2);
    while (biggest.Count > keep) biggest.RemoveAt(biggest.Count - 1);
    truncations.Add(new Dictionary<string, object?>
    {
      ["path"] = biggestPath,
      ["originalCount"] = original,
      ["keptCount"] = keep,
    });
    return true;
  }

  /// <summary>找最长的字符串，砍一半（至少留 200 字符）</summary>
  private static bool ShrinkLongestString(JsonNode node, List<object?> truncations)
  {
    string? longest = null;
    string longestPath = string.Empty;
    var longestLen = 0;

    void Walk(JsonNode? current, string path, int depth)
    {
      if (current == null || depth > 4) return;
      if (current is JsonObject obj)
      {
        foreach (var kv in obj) Walk(kv.Value, path + "/" + kv.Key, depth + 1);
      }
      else if (current is JsonArray arr)
      {
        for (var i = 0; i < arr.Count; i++) Walk(arr[i], path + "/" + i, depth + 1);
      }
      else if (current is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > longestLen)
      {
        longest = s; longestLen = s.Length; longestPath = path;
      }
    }
    Walk(node, "", 0);

    if (longest == null || longestLen < 2048) return false;
    var keep = Math.Max(200, longestLen / 2);
    ReplaceString(node, longestPath, longest.Substring(0, keep) + "…");
    truncations.Add(new Dictionary<string, object?>
    {
      ["path"] = longestPath,
      ["kind"] = "string",
      ["originalChars"] = longestLen,
      ["keptChars"] = keep,
    });
    return true;
  }

  private static void ReplaceString(JsonNode root, string path, string value)
  {
    if (string.IsNullOrEmpty(path)) { if (root is JsonValue) { /* 顶层标量：忽略 */ } return; }
    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    JsonNode? current = root;
    for (var i = 0; i < parts.Length - 1; i++)
    {
      current = current switch
      {
        JsonObject o => o[parts[i]],
        JsonArray a => int.TryParse(parts[i], out var idx) && idx < a.Count ? a[idx] : null,
        _ => null,
      };
      if (current == null) return;
    }
    var last = parts[parts.Length - 1];
    if (current is JsonObject po) po[last] = value;
    else if (current is JsonArray pa && int.TryParse(last, out var li) && li < pa.Count) pa[li] = value;
  }

  // ---------- 策略②：兜底画像信封 ----------
  private static Dictionary<string, object?> BuildEnvelope(
    JsonNode node, long bytes, int threshold, string method, JsonObject? parameters)
  {
    var envelope = new Dictionary<string, object?>
    {
      ["summarized"] = true,
      ["reason"] = $"结果过大且无法安全截断：{bytes} 字节 > 阈值 {threshold} 字节",
      ["method"] = method,
      ["originalBytes"] = bytes,
      ["thresholdBytes"] = threshold,
      ["profile"] = Profile(node, 0),
      ["sample"] = Sample(node, 0),
      ["fullAvailableVia"] = FullVia(method, parameters),
    };
    return envelope;
  }

  private static Dictionary<string, object?> FullVia(string method, JsonObject? parameters) => new()
  {
    ["method"] = method,
    ["params"] = FullParams(parameters),
    ["hint"] = "加 fullResult:true 取全量；若支持 limit/offset 请分页；或先 summarizeDrawing / analyzeLayer 看画像再下钻",
  };

  private static JsonObject? FullParams(JsonObject? parameters)
  {
    if (parameters == null) return new JsonObject { ["fullResult"] = true };
    var clone = (JsonObject)parameters.DeepClone();
    clone.Remove("fullResult");
    clone.Remove("maxResultBytes");
    clone["fullResult"] = true;
    return clone;
  }

  private static object? Profile(JsonNode? node, int depth)
  {
    if (node == null) return null;

    if (node is JsonArray arr)
    {
      var d = new Dictionary<string, object?> { ["kind"] = "array", ["count"] = arr.Count };
      var firstObj = arr.OfType<JsonObject>().FirstOrDefault();
      if (firstObj != null)
      {
        d["itemKeys"] = firstObj.Select(kv => kv.Key).Take(30).ToArray();
        d["numericFields"] = NumericStats(arr);
        d["categoricalFields"] = CategoricalStats(arr);
      }
      else
      {
        d["itemKind"] = arr.Count > 0 ? KindOf(arr[0]) : "empty";
        var nums = arr.Select(AsDouble).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (nums.Count > 0)
          d["numeric"] = new Dictionary<string, object?>
          {
            ["min"] = nums.Min(), ["max"] = nums.Max(), ["mean"] = Math.Round(nums.Average(), 4),
          };
      }
      if (depth < 1) d["nested"] = NestedProfiles(arr);
      return d;
    }

    if (node is JsonObject obj)
    {
      var d = new Dictionary<string, object?> { ["kind"] = "object" };
      var fields = new List<object?>();
      foreach (var kv in obj)
      {
        fields.Add(new Dictionary<string, object?>
        {
          ["key"] = kv.Key,
          ["kind"] = KindOf(kv.Value),
          ["bytes"] = Bytes(kv.Value),
          ["count"] = kv.Value is JsonArray ja ? ja.Count : (int?)null,
        });
      }
      d["fields"] = fields.OrderByDescending(f => Convert.ToInt64(((Dictionary<string, object?>)f!)["bytes"])).Take(40).ToList();
      var arrayProfiles = new Dictionary<string, object?>();
      foreach (var kv in obj)
      {
        if (kv.Value is JsonArray inner && inner.Count > 0 && kv.Key != "_summarized")
          arrayProfiles[kv.Key] = Profile(inner, depth + 1);
      }
      if (arrayProfiles.Count > 0) d["arrayProfiles"] = arrayProfiles;
      return d;
    }

    return new Dictionary<string, object?>
    {
      ["kind"] = KindOf(node),
      ["bytes"] = Bytes(node),
      ["sample"] = Truncate(node.ToJsonString(), 120),
    };
  }

  private static Dictionary<string, object?>? NumericStats(JsonArray arr)
  {
    var keys = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    foreach (var item in arr.OfType<JsonObject>())
    {
      foreach (var kv in item)
      {
        var v = AsDouble(kv.Value);
        if (v == null) continue;
        if (!keys.TryGetValue(kv.Key, out var list)) { list = new List<double>(); keys[kv.Key] = list; }
        list.Add(v.Value);
      }
    }
    if (keys.Count == 0) return null;
    var outDict = new Dictionary<string, object?>();
    foreach (var kv in keys.OrderBy(kv => kv.Key).Take(12))
    {
      outDict[kv.Key] = new Dictionary<string, object?>
      {
        ["min"] = Math.Round(kv.Value.Min(), 3),
        ["max"] = Math.Round(kv.Value.Max(), 3),
        ["mean"] = Math.Round(kv.Value.Average(), 3),
      };
    }
    return outDict;
  }

  private static Dictionary<string, object?>? CategoricalStats(JsonArray arr)
  {
    var buckets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var item in arr.OfType<JsonObject>())
    {
      foreach (var kv in item)
      {
        if (kv.Value is not JsonValue jv || !jv.TryGetValue<string>(out var s)) continue;
        if (!buckets.TryGetValue(kv.Key, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); buckets[kv.Key] = set; }
        if (set.Count < 50) set.Add(s);
      }
    }
    var outDict = new Dictionary<string, object?>();
    foreach (var kv in buckets)
    {
      if (kv.Value.Count == 0 || kv.Value.Count > 12) continue;
      outDict[kv.Key] = new Dictionary<string, object?>
      {
        ["distinct"] = kv.Value.Count,
        ["values"] = kv.Value.OrderBy(v => v, StringComparer.Ordinal).Take(12).Select(v => (object?)Truncate(v, 40)).ToArray(),
      };
    }
    return outDict.Count > 0 ? outDict : null;
  }

  private static Dictionary<string, object?>? NestedProfiles(JsonArray arr)
  {
    var first = arr.OfType<JsonObject>().FirstOrDefault();
    if (first == null) return null;
    var d = new Dictionary<string, object?>();
    foreach (var kv in first)
    {
      if (kv.Value is JsonArray inner)
        d[kv.Key] = new Dictionary<string, object?>
        {
          ["kind"] = "array",
          ["countInFirstItem"] = inner.Count,
          ["itemKind"] = inner.Count > 0 ? KindOf(inner[0]) : "empty",
          ["itemKeys"] = inner.OfType<JsonObject>().FirstOrDefault()?.Select(k => k.Key).Take(15).ToArray(),
        };
    }
    return d.Count > 0 ? d : null;
  }

  private static object? Sample(JsonNode? node, int depth)
  {
    if (node == null) return null;
    if (node is JsonArray arr) return arr.Take(3).Select(v => TruncateNode(v, 120)).ToList();
    if (node is JsonObject obj)
    {
      var d = new Dictionary<string, object?>();
      foreach (var kv in obj.Take(8)) d[kv.Key] = TruncateNode(kv.Value, 120);
      return d;
    }
    return TruncateNode(node, 120);
  }

  private static object? TruncateNode(JsonNode? node, int max)
  {
    if (node == null) return null;
    if (node is JsonValue) return node.DeepClone();
    return Truncate(node.ToJsonString(), max);
  }

  private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

  private static string KindOf(JsonNode? node) => node switch
  {
    null => "null",
    JsonArray => "array",
    JsonObject => "object",
    JsonValue v when v.TryGetValue<bool>(out _) => "bool",
    JsonValue v when v.TryGetValue<double>(out _) => "number",
    JsonValue => "string",
    _ => "unknown",
  };

  private static long Bytes(JsonNode? node)
  {
    if (node == null) return 0;
    try { return Encoding.UTF8.GetByteCount(node.ToJsonString()); } catch { return 0; }
  }

  private static double? AsDouble(JsonNode? node)
  {
    if (node is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
    return null;
  }

  private static void Shrink(Dictionary<string, object?> envelope, object root, int threshold)
  {
    long Size() => Encoding.UTF8.GetByteCount(OperationTrace.SerializeResult(root));
    if (Size() <= threshold) return;
    envelope.Remove("sample");
    if (Size() <= threshold) return;
    if (envelope.TryGetValue("profile", out var p) && p is Dictionary<string, object?> pd)
    {
      pd.Remove("nested");
      if (pd.TryGetValue("fields", out var f) && f is List<object?> fl && fl.Count > 10) pd["fields"] = fl.Take(10).ToList();
      pd.Remove("categoricalFields");
      if (pd.TryGetValue("numericFields", out var nf) && nf is Dictionary<string, object?> nfd && nfd.Count > 6)
        pd["numericFields"] = nfd.Take(6).ToDictionary(kv => kv.Key, kv => kv.Value);
      if (pd.TryGetValue("arrayProfiles", out var ap) && ap is Dictionary<string, object?> apd && apd.Count > 3)
        pd["arrayProfiles"] = apd.Take(3).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
    if (Size() <= threshold) return;
    envelope["profile"] = new Dictionary<string, object?> { ["kind"] = "truncated", ["note"] = "结果过大，画像已进一步压缩" };
  }
}
