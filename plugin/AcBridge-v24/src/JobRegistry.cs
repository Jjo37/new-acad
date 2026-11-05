using System.Collections.Concurrent;

namespace Civil3DMcpPlugin;

public sealed class JobRecord
{
  public required string JobId { get; init; }
  public required string State { get; set; }
  public required string Operation { get; init; }
  public int? ProgressPercent { get; set; }
  public string? CurrentPhase { get; set; }
  public int? EstimatedRemainingSeconds { get; set; }
  public object? Result { get; set; }
  public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
  public DateTimeOffset? CompletedAt { get; set; }
  public long? DurationMs { get; set; }
  public string? RequestId { get; init; }
  public string? DrawingIdentity { get; init; }
  public string? FailureCategory { get; set; }
  public List<string> Warnings { get; } = [];
  public CancellationTokenSource? CancellationSource { get; set; }
  public bool CancellationRequested { get; set; }
  internal object SyncRoot { get; } = new();
}

public sealed record JobRegistryStats(
  int Total,
  int Running,
  int Completed,
  int Failed,
  int Cancelled,
  int Capacity,
  int TerminalRetentionMinutes);

public static class JobRegistry
{
  private static readonly ConcurrentDictionary<string, JobRecord> Jobs = new();
  private static readonly object RetentionSync = new();
  private const int MaxJobs = 256;
  private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);

  public static JobRecord Create(
    string currentPhase,
    string? operation = null,
    string? requestId = null,
    string? drawingIdentity = null)
  {
    lock (RetentionSync)
    {
      ApplyRetention(DateTimeOffset.UtcNow);
      if (Jobs.Count >= MaxJobs)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Job registry is at capacity ({MaxJobs}). Wait for running work to finish or for terminal jobs to expire.");
      }

      var record = new JobRecord
      {
        JobId = Guid.NewGuid().ToString("N"),
        State = "running",
        Operation = string.IsNullOrWhiteSpace(operation) ? currentPhase : operation,
        ProgressPercent = 0,
        CurrentPhase = currentPhase,
        EstimatedRemainingSeconds = null,
        RequestId = requestId,
        DrawingIdentity = drawingIdentity,
      };

      Jobs[record.JobId] = record;
      return record;
    }
  }

  public static JobRecord Complete(string jobId, object? result, IEnumerable<string>? warnings = null)
  {
    var record = GetRequired(jobId, "completing");
    lock (record.SyncRoot)
    {
      if (record.State != "running") return record;
      record.State = "completed";
      record.ProgressPercent = 100;
      record.CurrentPhase = null;
      record.EstimatedRemainingSeconds = 0;
      record.Result = result;
      if (warnings != null)
      {
        record.Warnings.AddRange(warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)));
      }
      MarkTerminal(record);
    }
    ApplyRetentionSafely();
    return record;
  }

  public static JobRecord Fail(string jobId, string errorMessage, string? failureCategory = null)
  {
    var record = GetRequired(jobId, "failing");
    lock (record.SyncRoot)
    {
      if (record.State != "running") return record;
      record.State = "failed";
      record.CurrentPhase = null;
      record.EstimatedRemainingSeconds = 0;
      record.FailureCategory = failureCategory ?? "CIVIL3D.INTERNAL_ERROR";
      record.Result = new Dictionary<string, object?>
      {
        ["error"] = errorMessage,
        ["category"] = record.FailureCategory,
      };
      MarkTerminal(record);
    }
    ApplyRetentionSafely();
    return record;
  }

  public static int PurgeTerminalJobsOlderThan(DateTimeOffset cutoff)
  {
    lock (RetentionSync)
    {
      return PurgeTerminalJobsOlderThanCore(cutoff);
    }
  }

  public static JobRecord Get(string jobId)
  {
    ApplyRetentionSafely();
    if (!Jobs.TryGetValue(jobId, out var record))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Job '{jobId}' was not found.");
    }

    return record;
  }

  public static JobRecord Cancel(string jobId)
  {
    var record = Get(jobId);
    CancellationTokenSource? cancellationSource = null;
    lock (record.SyncRoot)
    {
      if (record.State != "running") return record;
      record.CancellationRequested = true;
      record.CurrentPhase = "Cancellation requested";
      record.EstimatedRemainingSeconds = null;
      cancellationSource = record.CancellationSource;
    }

    try
    {
      cancellationSource?.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // The worker completed while cancellation was being requested.
    }

    return record;
  }

  public static JobRecord AcknowledgeCancellation(string jobId)
  {
    var record = Get(jobId);
    lock (record.SyncRoot)
    {
      if (record.State != "running") return record;
      record.CancellationRequested = true;
      record.State = "cancelled";
      record.CurrentPhase = null;
      record.EstimatedRemainingSeconds = null;
      record.FailureCategory = "CIVIL3D.CANCELLED";
      MarkTerminal(record);
    }
    ApplyRetentionSafely();
    return record;
  }

  public static void Progress(string jobId, int? progressPercent, string? currentPhase, int? estimatedRemainingSeconds)
  {
    if (!Jobs.TryGetValue(jobId, out var record))
    {
      return;
    }

    lock (record.SyncRoot)
    {
      if (record.State != "running") return;
      if (progressPercent.HasValue)
      {
        record.ProgressPercent = Math.Clamp(progressPercent.Value, 0, 100);
      }

      if (!string.IsNullOrWhiteSpace(currentPhase))
      {
        record.CurrentPhase = currentPhase;
      }

      if (estimatedRemainingSeconds.HasValue)
      {
        record.EstimatedRemainingSeconds = Math.Max(0, estimatedRemainingSeconds.Value);
      }
    }
  }

  public static JobRegistryStats GetStats()
  {
    ApplyRetentionSafely();
    var snapshot = Jobs.Values.ToArray();
    return new JobRegistryStats(
      snapshot.Length,
      snapshot.Count(job => job.State == "running"),
      snapshot.Count(job => job.State == "completed"),
      snapshot.Count(job => job.State == "failed"),
      snapshot.Count(job => job.State == "cancelled"),
      MaxJobs,
      (int)TerminalRetention.TotalMinutes);
  }

  private static JobRecord GetRequired(string jobId, string operation)
  {
    if (!Jobs.TryGetValue(jobId, out var record))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.OBJECT_NOT_FOUND",
        $"Job '{jobId}' was not found when {operation}.");
    }
    return record;
  }

  private static void MarkTerminal(JobRecord record)
  {
    var completedAt = DateTimeOffset.UtcNow;
    record.CompletedAt = completedAt;
    record.DurationMs = Math.Max(0, (long)(completedAt - record.CreatedAt).TotalMilliseconds);
  }

  private static void ApplyRetentionSafely()
  {
    lock (RetentionSync)
    {
      ApplyRetention(DateTimeOffset.UtcNow);
    }
  }

  private static void ApplyRetention(DateTimeOffset now)
  {
    PurgeTerminalJobsOlderThanCore(now - TerminalRetention);
    if (Jobs.Count < MaxJobs)
    {
      return;
    }

    foreach (var terminal in Jobs.Values
      .Where(job => job.State is "completed" or "failed" or "cancelled")
      .OrderBy(job => job.CompletedAt ?? job.CreatedAt)
      .ToArray())
    {
      if (Jobs.Count < MaxJobs)
      {
        break;
      }
      Jobs.TryRemove(terminal.JobId, out _);
    }
  }

  private static int PurgeTerminalJobsOlderThanCore(DateTimeOffset cutoff)
  {
    var removed = 0;
    foreach (var entry in Jobs.ToArray())
    {
      var isTerminal = entry.Value.State is "completed" or "failed" or "cancelled";
      if (isTerminal
        && entry.Value.CompletedAt is { } completedAt
        && completedAt < cutoff
        && Jobs.TryRemove(entry.Key, out _))
      {
        removed++;
      }
    }
    return removed;
  }
}
