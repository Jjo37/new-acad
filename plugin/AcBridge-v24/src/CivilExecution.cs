using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);
  // 2026-08-12 C1 修复: 超时被放弃的操作跟踪(防假死 + 未观察异常)
  private static readonly object AbandonedLock = new();
  private static readonly List<Task> AbandonedTasks = new();
  private static volatile bool HostBusy = false;   // 超时后标记命令上下文忙, 后续请求快速失败(volatile: ContinueWith 线程跨线程可见)
  private static long HostBusySinceTicks = 0; // 2026-08-12 v2: busy 置位时间戳(Environment.TickCount64 原子, 无需 volatile)
  private const long HOST_BUSY_MAX_MS = 60_000;        // busy 最长持续 60s(被放弃操作可能永不结束, 超时后放行重试)
  // 2026-08-18 方案A: 置位即挂 60s 后台定时器，到点无条件复位（不再依赖外部请求敲门触发懒检查）
  private static readonly object BusyLock = new();                 // busy 状态互斥（定时器/清除竞态保护）
  private static CancellationTokenSource? HostBusyTimerCts = null; // 置位时挂的恢复定时器

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;

      await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

          result = action(doc, civilDoc, database, transaction);

          if (write)
          {
            transaction.Commit();
          }
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        await Task.CompletedTask;
      }, null);

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;

      await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        try
        {
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      }, null);

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  /// <summary>
  /// 2026-08-13: 文档级操作专用通道（openDrawing/newDrawing）——Open/New 不能在命令上下文
  /// （ExecuteInCommandContextAsync）里调用：Open 等命令结束、命令上下文等 Open 完成 → 死锁 25s 超时 → C3D 崩溃。
  /// 与 ExecuteSerializedAsync 相同的串行门/超时/HostBusy 机制，但 action 直接执行（不包命令上下文）。
  /// </summary>
  public static async Task<T> ExecuteDocumentOpAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;
    const int OPERATION_TIMEOUT_SECONDS = 25;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      // 2026-09-11: 文档级操作（openDrawing/newDrawing）走 Idle、不占命令上下文，**有意不受 HostBusy 门限制**——
      // 这样即使 CAD 停在“开始”页/命令上下文被占（面板其它操作全返回 HOST_BUSY），openDrawing 仍能破局：
      // 打开一张真实 dwg → 命令上下文恢复（见下方成功分支立即 ClearHostBusy）。这也是开始页陷阱的正解。
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();

      using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(OPERATION_TIMEOUT_SECONDS));
      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

      var execTask = action();
      var completedTask = await Task.WhenAny(execTask, Task.Delay(-1, linkedCts.Token));

      if (completedTask == execTask)
      {
        // 2026-09-11: 文档操作成功（如从“开始”页打开了真实图纸）→ 命令上下文已恢复，立即解锁，不等 60s 自动恢复
        var result = await execTask;
        ClearHostBusy();
        return result;
      }
      else
      {
        TrackAbandonedTask(execTask);
        SetHostBusy();
        TryInterruptActiveCommand();
        TryInterruptActiveCommand();
        TryInterruptActiveCommand();
        linkedCts.Cancel();
        var diagOp = CaptureHostState();
        try { PluginLog.Error("Exec", "[TIMEOUT-diag] op=documentOp " + diagOp); } catch { }
        throw new JsonRpcDispatchException(
          "CIVIL3D.OPERATION_TIMEOUT",
          $"Operation timed out after {OPERATION_TIMEOUT_SECONDS} seconds. The AutoCAD command context may be blocked. 若 CAD 停在“开始”页/无活动图纸，请先调 openDrawing 打开一张 dwg。后续请求将快速返回 HOST_BUSY 直到上下文恢复。[诊断: {diagOp}]");
      }
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  // 2026-09-21 fix（廊道曲面挂载"卡死"根因）: 把写事务放到 App.Idle（文档线程、非命令上下文）执行。
  // 背景：addCorridorSurface 的首次曲面构建若在 ExecuteInCommandContextAsync（命令上下文）里提交，
  //   会被 C3D 节流/推迟——实测耗时 215~360s（约一半概率撞破 300s job 上限 → 假失败），全程 CPU≈0%，
  //   超时诊断 cmdNames='' cmdActive=0（上下文空闲，排除锁占用）；改用 App.Idle 后 commit 仅 79ms。
  // 适用：会触发 C3D 内部再生（廊道/曲面重建等）的写操作；普通 DB 编辑仍走 WriteAsync 即可。
  // 注意：Idle 回调运行在 UI 线程且会阻塞消息泵，只放"必须离开命令上下文"的重操作，不要滥用。
  public static Task<T> WriteViaIdleAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteViaIdleAsync(action, true);

  public static Task<T> ReadViaIdleAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteViaIdleAsync(action, false);

  private static async Task<T> ExecuteViaIdleAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    try { PluginLog.Info("Exec", "ViaIdle enter (write=" + write + ")"); } catch { }
    return await ExecuteSerializedAsync(async () =>
    {
      var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument
            ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var civilDoc = CivilApplication.ActiveDocument
            ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;
          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();
          var result = action(doc, civilDoc, database, transaction);
          if (write)
          {
            try { PluginLog.Info("Exec", "ViaIdle action done in " + sw.ElapsedMilliseconds + "ms, committing"); } catch { }
            transaction.Commit();
            try { PluginLog.Info("Exec", "ViaIdle commit done in " + sw.ElapsedMilliseconds + "ms"); } catch { }
          }
          tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
          try { PluginLog.Error("Exec", "ViaIdle failed after " + sw.ElapsedMilliseconds + "ms", ex); } catch { }
          tcs.TrySetException(ex);
        }
      };
      App.Idle += idleHandler;
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT", "ViaIdle timeout: CAD not idle (no App.Idle within 20s).");
      }
      return await tcs.Task;
    });
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;
    // 2026-08-19 方案3: job 上下文用长超时（重操作不误杀），普通请求保持 25s
    var isJob = PluginRuntime.GetCurrentRequestIsJob();
    var timeoutSeconds = isJob ? 300 : 25;
    const int OPERATION_TIMEOUT_SECONDS_JOB = 300;

    try
    {
      // 2026-08-19 方案3: job 运行期间（后台重操作占用命令上下文），普通请求快速失败不排队——
      // 避免普通请求在门内等 job 完成而烧 25s 超时 → 误触发 HOST_BUSY。relay 侧自动等待重试。
      if (!isJob && JobRegistry.GetStats().Running > 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.JOB_RUNNING",
          "后台任务（job）正在运行，命令上下文被占用。请稍候（可用 getJobStatus 查看进度），不要重复提交相同操作。");
      }
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      // 2026-08-12 C1: 上次操作超时后命令上下文仍被占用——快速失败, 不再各烧 25s
      // 2026-08-12 v2: busy 超过 60s 自动恢复(被放弃操作可能永不结束, 不能永久瘫痪面板)
      if (HostBusy && Environment.TickCount64 - HostBusySinceTicks > HOST_BUSY_MAX_MS)
      {
        HostBusy = false;
        try { PluginLog.Debug("Exec", "HostBusy 超时自动恢复(" + HOST_BUSY_MAX_MS + "ms)"); } catch { }
      }
      if (HostBusy)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          "上一个操作超时后 AutoCAD 命令上下文仍被占用。若 CAD 停在“开始”页/没有打开图纸，请先调 openDrawing 打开一张 dwg（能直接破局）；否则稍后重试或手动结束 CAD 中正在进行的操作。");
      }
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();

      // 带超时的执行，防止操作卡死（2026-08-19 方案3: job 用 300s 长超时）
      using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

      var execTask = action();
      var completedTask = await Task.WhenAny(execTask, Task.Delay(-1, linkedCts.Token));

      if (completedTask == execTask)
      {
        // 正常完成(注意: 不清 HostBusy——若有更早超时被放弃的操作仍在后台, busy 应保持到它真正结束)
        return await execTask;
      }
      else
      {
        // 超时了：投递 ESC 打断可能卡住的交互命令，尝试恢复命令上下文；
        // 2026-08-12 C1 修复: 跟踪 abandoned task(完成时清理+日志), 标记 HostBusy 让后续请求快速失败,
        // 连续投 3 次 ESC 提高打断成功率(模态对话框下 ESC 仍可能无效, 但尽力而为)
        TrackAbandonedTask(execTask);
        SetHostBusy();
        TryInterruptActiveCommand();
        TryInterruptActiveCommand();
        TryInterruptActiveCommand();
        linkedCts.Cancel(); // 停止延迟任务
        var diagCmd = CaptureHostState();
        try { PluginLog.Error("Exec", "[TIMEOUT-diag] op=command " + diagCmd); } catch { }
        throw new JsonRpcDispatchException(
          "CIVIL3D.OPERATION_TIMEOUT",
          $"Operation timed out after {timeoutSeconds} seconds. The AutoCAD command context may be blocked. 后续请求将快速返回 HOST_BUSY 直到上下文恢复。[诊断: {diagCmd}]");
      }
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }

  // 2026-09-11: 超时时抓宿主状态快照——定位"命令上下文为什么被占"。
  // CMDNAMES=正在执行的命令; CMDACTIVE 位 8=dialog 激活; docs/activeDoc=文档状态。
  private static string CaptureHostState()
  {
    try
    {
      var d = App.DocumentManager.MdiActiveDocument;
      return "cmdNames='" + Convert.ToString(App.GetSystemVariable("CMDNAMES")) + "'"
        + " cmdActive=" + Convert.ToString(App.GetSystemVariable("CMDACTIVE"))
        + " docs=" + App.DocumentManager.Count
        + " activeDoc=" + (d == null ? "(none)" : System.IO.Path.GetFileName(d.Name));
    }
    catch (Exception ex) { return "diag failed: " + ex.Message; }
  }

  /// <summary>
  /// 超时兑底：向活动文档命令队列投递 ESC（反斜杠在命令队列中即 ESC），
  /// 打断可能卡住的交互命令，尝试恢复命令上下文。
  /// SendStringToExecute 仅入队不阻塞、不持文档锁，可从后台线程调用，不会引入新的死锁。
  /// </summary>
  private static void TryInterruptActiveCommand()
  {
    try
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return;
      doc.SendStringToExecute("\\", true, false, false);
    }
    catch { }
  }

  // 2026-08-12 C1: 跟踪超时被放弃的操作——完成时记录日志并清理, 防未观察异常
  private static void TrackAbandonedTask(Task task)
  {
    lock (AbandonedLock)
    {
      AbandonedTasks.Add(task);
    }
    _ = task.ContinueWith(t =>
    {
      lock (AbandonedLock)
      {
        AbandonedTasks.Remove(t);
      }
      if (t.IsFaulted)
      {
        try { PluginLog.Debug("Exec", "被放弃的操作最终异常: " + t.Exception?.GetBaseException()?.Message); } catch { }
      }
      else
      {
        try { PluginLog.Debug("Exec", "被放弃的操作已结束(超时后完成), 命令上下文应已恢复"); } catch { }
      }
      // 操作真正结束后恢复 busy 标记
      ClearHostBusy();
    }, TaskScheduler.Default);
  }

  // 2026-08-18 方案A: 置位 busy 并挂 60s 自动恢复定时器
  // 背景: 原懒检查只在“新请求进来拿到串行门”时触发——AI 按提示词停止重试后,
  //       没有请求敲门, busy 标志永久挂着, 面板显示“一直繁忙”。
  //       现在置位即挂定时器, 60s 无条件复位, 保留快速失败保护, 不死锁。
  private static void SetHostBusy()
  {
    lock (BusyLock)
    {
      HostBusy = true;
      HostBusySinceTicks = Environment.TickCount64;
      HostBusyTimerCts?.Cancel(); // 取消旧定时器（防重复置位时旧定时器误清新 busy）
      var cts = HostBusyTimerCts = new CancellationTokenSource();
      _ = Task.Delay(TimeSpan.FromMilliseconds(HOST_BUSY_MAX_MS), cts.Token)
        .ContinueWith(t =>
        {
          if (t.IsCanceled) return;
          lock (BusyLock)
          {
            if (HostBusyTimerCts != cts) return; // 已被新置位/清除接管
            HostBusy = false;
            HostBusySinceTicks = 0;
            HostBusyTimerCts = null;
            try { PluginLog.Debug("Exec", "HostBusy 定时器到期自动恢复(" + HOST_BUSY_MAX_MS + "ms)"); } catch { }
          }
        }, TaskScheduler.Default);
    }
  }

  // 2026-08-18 方案A: 任务真正结束时清除 busy（顺带取消定时器）
  private static void ClearHostBusy()
  {
    lock (BusyLock)
    {
      HostBusy = false;
      HostBusySinceTicks = 0;
      HostBusyTimerCts?.Cancel();
      HostBusyTimerCts = null;
    }
  }
}


