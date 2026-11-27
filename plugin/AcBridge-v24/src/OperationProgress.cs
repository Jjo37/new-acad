using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-11-18 P36：长操作的进度登记表。
///
/// 为什么要有它：6 万条导出要跑约 1 分钟，面板 AI 在这段时间里**完全瞎着** ——
/// 不知道是在跑、卡住了、还是已经死了。插件没有主动推送通道（TCP 是请求/响应），
/// 所以做法是：长操作把进度写进这张表，调用方从 getCivil3DHealth 的 operations 字段读。
///
/// 硬约束：
///   · 注册/更新必须线程安全（导出在 UI 线程跑，读在 TCP 处理线程）；
///   · processed 单调不减（客户端可以拿它算速率，不会被回退搞乱）；
///   · 只保留最近 8 条，结束 10 分钟后清掉，避免表无限增长；
///   · finished 的操作仍然可读（done + summary），客户端可据此确认"真跑完了"。
/// </summary>
internal static class OperationProgress
{
  private sealed class Entry
  {
    internal string Id = "";
    internal string Kind = "";
    internal long Total;
    internal long Processed;
    internal string Phase = "running";
    internal string StartedAt = "";
    internal string? FinishedAt;
    internal bool Done;
    internal string? Error;
    internal Dictionary<string, object?>? Summary;
  }

  private const int MaxEntries = 8;
  private static readonly object Gate = new();
  private static readonly Dictionary<string, Entry> Items = new(StringComparer.OrdinalIgnoreCase);

  internal static void Begin(string operationId, string kind, long total = -1, string phase = "running")
  {
    if (string.IsNullOrWhiteSpace(operationId)) return;
    lock (Gate)
    {
      Prune();
      Items[operationId] = new Entry
      {
        Id = operationId,
        Kind = kind,
        Total = total,
        Processed = 0,
        Phase = phase,
        StartedAt = PluginTime.StoredNow(),
      };
    }
  }

  /// <summary>更新已处理条数（传绝对值；比当前值小则忽略，保证单调）。</summary>
  internal static void Step(string operationId, long processed, string? phase = null)
  {
    if (string.IsNullOrWhiteSpace(operationId)) return;
    lock (Gate)
    {
      if (!Items.TryGetValue(operationId, out var entry)) return;
      if (processed > entry.Processed) entry.Processed = processed;
      if (!string.IsNullOrWhiteSpace(phase)) entry.Phase = phase!;
    }
  }

  internal static void Finish(string operationId, Dictionary<string, object?>? summary = null)
  {
    if (string.IsNullOrWhiteSpace(operationId)) return;
    lock (Gate)
    {
      if (!Items.TryGetValue(operationId, out var entry)) return;
      entry.Done = true;
      entry.Phase = "done";
      entry.FinishedAt = PluginTime.StoredNow();
      if (entry.Total > 0 && entry.Processed < entry.Total) entry.Processed = entry.Total;
      entry.Summary = summary;
    }
  }

  internal static void Fail(string operationId, string error)
  {
    if (string.IsNullOrWhiteSpace(operationId)) return;
    lock (Gate)
    {
      if (!Items.TryGetValue(operationId, out var entry)) return;
      entry.Done = true;
      entry.Phase = "failed";
      entry.FinishedAt = PluginTime.StoredNow();
      entry.Error = error;
    }
  }

  internal static List<Dictionary<string, object?>> Snapshot()
  {
    lock (Gate)
    {
      Prune();
      return Items.Values
        .OrderByDescending(e => e.StartedAt, StringComparer.Ordinal)
        .Take(MaxEntries)
        .Select(e => new Dictionary<string, object?>
        {
          ["operationId"] = e.Id,
          ["kind"] = e.Kind,
          ["phase"] = e.Phase,
          ["processed"] = e.Processed,
          ["total"] = e.Total,
          ["percent"] = e.Total > 0 ? Math.Round(100.0 * e.Processed / e.Total, 1) : (double?)null,
          ["done"] = e.Done,
          ["error"] = e.Error,
          ["startedAt"] = e.StartedAt,
          ["finishedAt"] = e.FinishedAt,
          ["summary"] = e.Summary,
        })
        .ToList();
    }
  }

  private static void Prune()
  {
    if (Items.Count <= MaxEntries) return;
    foreach (var stale in Items.Values
               .Where(e => e.Done)
               .OrderBy(e => e.StartedAt, StringComparer.Ordinal)
               .Take(Math.Max(0, Items.Count - MaxEntries))
               .Select(e => e.Id)
               .ToList())
    {
      Items.Remove(stale);
    }
  }
}
