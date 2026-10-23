using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-10-22: public face of the undo journal.
///   journalList  -- what can be reverted right now
///   revertEntry  -- undo exactly one action, leaving the rest of the drawing alone
/// Descriptions in CommandDispatcher / CommandDescsEn stay in sync with these two methods.
/// </summary>
public static class UndoJournalCommands
{
  public static Task<object?> JournalListAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 50;
    var list = new List<Dictionary<string, object?>>();
    foreach (var e in UndoJournal.List(limit))
    {
      var snaps = new List<Dictionary<string, object?>>();
      foreach (var s in e.Snapshots)
        snaps.Add(new Dictionary<string, object?> { ["handle"] = s.Handle, ["kind"] = s.Kind, ["stash"] = s.StashHandle });
      list.Add(new Dictionary<string, object?>
      {
        ["seq"] = e.Seq,
        ["method"] = e.Method,
        ["task"] = e.TaskId,
        // 2026-10-23 (Bro: the time is wrong): At is UTC; the panel shows local wall clock
        ["at"] = e.At.ToLocalTime().ToString("o", CultureInfo.InvariantCulture),
        ["atUtc"] = e.At.ToString("o", CultureInfo.InvariantCulture),
        // ADR 0008 contract: revertible + revertBlockedReason + revertBlockedText
        ["revertible"] = e.Revertible,
        ["revertBlockedReason"] = e.BlockedReason,
        ["revertBlockedText"] = e.BlockedText,
        ["summary"] = e.Summary,
        ["created"] = e.Created,
        ["snapshots"] = snaps,
      });
    }
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["count"] = list.Count,
      ["entries"] = list,
      ["note"] = "one entry = one action; revertEntry undoes only that action (created -> erased, deleted/modified -> restored from the side-database snapshot); revertible=false means it cannot be undone and revertBlockedText says why",
    });
  }

  // 2026-10-23 (Bro: stale rows pile up with no way out): prune entries that can never be reverted.
  public static Task<object?> JournalPruneAsync(JsonObject? parameters)
  {
    var scope = PluginRuntime.GetOptionalString(parameters, "scope") ?? "blocked";
    var removed = UndoJournal.Prune(scope);
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["removed"] = removed,
      ["left"] = UndoJournal.List(500).Count,
      ["scope"] = scope,
    });
  }

  public static Task<object?> RevertEntryAsync(JsonObject? parameters)
  {
    var seq = PluginRuntime.GetOptionalInt(parameters, "seq") ?? 0;
    if (seq <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "revertEntry requires seq (from journalList).");
    var report = UndoJournal.Revert(seq);
    return Task.FromResult<object?>(report);
  }
}

/// <summary>Pull handle-looking strings out of an arbitrary result object (Dictionary / List / JsonNode).</summary>
public static class UndoJournalHandles
{
  public static List<string> FromResult(object? result)
  {
    var found = new List<string>();
    Walk(result, found, 0);
    return found;
  }

  private static void Walk(object? node, List<string> found, int depth)
  {
    if (node == null || depth > 4 || found.Count >= 64) return;
    if (node is JsonNode jn) { WalkJson(jn, found, depth); return; }
    if (node is string s) { Push(s, found); return; }
    if (node is IDictionary dict)
    {
      foreach (DictionaryEntry kv in dict)
      {
        var key = kv.Key?.ToString() ?? "";
        if (IsHandleKey(key))
          Collect(kv.Value, found, depth);
        else if (IsContainer(kv.Value)) Walk(kv.Value, found, depth + 1);
      }
      return;
    }
    if (node is IEnumerable seq)
    {
      foreach (var item in seq) Walk(item, found, depth + 1);
    }
  }

  private static void WalkJson(JsonNode node, List<string> found, int depth)
  {
    if (node is JsonObject obj)
    {
      foreach (var kv in obj)
      {
        if (IsHandleKey(kv.Key))
          Collect(kv.Value, found, depth);
        else if (kv.Value is JsonObject || kv.Value is JsonArray) WalkJson(kv.Value!, found, depth + 1);
      }
      return;
    }
    if (node is JsonArray arr)
    {
      foreach (var item in arr) if (item != null) WalkJson(item, found, depth + 1);
    }
  }

  private static void Collect(object? value, List<string> found, int depth)
  {
    if (value == null) return;
    if (value is string s) { Push(s, found); return; }
    if (value is JsonNode jn) { if (jn is JsonArray ja) foreach (var x in ja) Collect(x, found, depth); else if (jn is JsonValue jv && jv.TryGetValue<string>(out var str)) Push(str, found); return; }
    if (value is IEnumerable list)
    {
      foreach (var item in list) Collect(item, found, depth);
    }
  }

  // plugins report the new object under different names: handle / handles / createdHandles /
  // lineId / entityId / objectId ... -- all of them identify the thing a create made.
  private static bool IsHandleKey(string key)
  {
    if (string.IsNullOrEmpty(key)) return false;
    if (key.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return false;
    if (key.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0) return true;
    return System.Text.RegularExpressions.Regex.IsMatch(key, "^(line|entity|object|new|created)?id[s]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
  }

  private static bool IsContainer(object? v) => v is IDictionary || v is IEnumerable && v is not string;

  private static void Push(string s, List<string> found)
  {
    if (string.IsNullOrWhiteSpace(s) || s.Length > 8) return;
    foreach (var ch in s) if (!Uri.IsHexDigit(ch)) return;
    if (!found.Contains(s)) found.Add(s);
  }
}
