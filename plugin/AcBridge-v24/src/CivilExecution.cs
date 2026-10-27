using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using System.Runtime.InteropServices;
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

  public static async Task<T> ExecuteCoreAsync<T>(Func<Document, Database, Transaction, T> action, bool write)
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
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

          result = action(doc, database, transaction);

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

  // ---------------------------------------------------------------------------------------
  // 2026-10-27 v1.11.0-fix: dual host support (Civil 3D + plain AutoCAD).
  // Plain AutoCAD has no AeccDbMgd, so any method whose signature mentions CivilDocument
  // would fault at JIT time. Therefore the AutoCAD-only paths below never reference it;
  // the Civil 3D overloads are thin wrappers that resolve the document lazily, and only
  // Civil 3D command handlers are allowed to call them.
  // ---------------------------------------------------------------------------------------
  public static Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
    => ExecuteCoreAsync((doc, database, transaction) => action(doc, CivilSupport.RequireDocument(), database, transaction), write);

  public static Task<T> ExecuteAsync<T>(Func<Document, Database, Transaction, T> action, bool write)
    => ExecuteCoreAsync(action, write);

  public static Task<T> ReadAsync<T>(Func<Document, Database, Transaction, T> action)
    => ExecuteCoreAsync(action, false);

  public static Task<T> WriteAsync<T>(Func<Document, Database, Transaction, T> action)
    => ExecuteCoreAsync(action, true);

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
    var gateAcquired = false;
    var opStarted = false;
    const int OPERATION_TIMEOUT_SECONDS = 25;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      gateAcquired = true;
      // 2026-09-11: 文档级操作（openDrawing/newDrawing）走 Idle、不占命令上下文，**有意不受 HostBusy 门限制**——
      // 这样即使 CAD 停在“开始”页/命令上下文被占（面板其它操作全返回 HOST_BUSY），openDrawing 仍能破局：
      // 打开一张真实 dwg → 命令上下文恢复（见下方成功分支立即 ClearHostBusy）。这也是开始页陷阱的正解。
      PluginRuntime.StartHostOperation();
      opStarted = true;
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
        // 2026-10-10 (批次6.1): 超时后统一交由看门狗处置——投递 ESC 打断卡住的交互命令 +
        // 周期探测命令上下文，一旦真正空闲立即解锁（不再盲等 60s）。
        linkedCts.Cancel();
        var diagOp = HandleAbandonedOperation("documentOp", execTask);
        throw new JsonRpcDispatchException(
          "CIVIL3D.OPERATION_TIMEOUT",
          Loc.Tk("error.timeoutDocumentOp", ("sec", OPERATION_TIMEOUT_SECONDS), ("max", HOST_BUSY_MAX_MS / 1000), ("diag", diagOp)));
      }
    }
    finally
    {
      // 2026-10-20 审计：两笔账分开结 —— 操作没开始就退回队列计数，门拿到就一定释放。
      if (opStarted) PluginRuntime.CompleteHostOperation();
      else PluginRuntime.CancelQueuedHostOperation();
      if (gateAcquired) HostExecutionGate.Release();
    }
  }

  // ── 2026-09-28 D3：会触发 C3D 内部再生的写操作（统一护栏）──────────────────
  // 背景（2026-09-21 实测）：廊道曲面首次构建若在命令上下文提交，会被 C3D 节流/推迟
  //   （215~360s，CPU≈0%，约一半撞破 300s job 上限 → 假失败；曲面其实已建好）。
  //   改走 App.Idle（文档线程 + 文档锁 + 事务，非命令上下文）后 commit 降到 ~56ms。
  // 规则：凡"改数据会引发 C3D 后台再生"的写操作，一律走 WriteViaIdleAsync，
  //   并在 commit 后用 WaitForRegenerationAsync 给它一个静默窗口等生成完成。
  //   ⚠️ Idle 回调在 UI 线程执行且阻塞消息泵 —— 只放"必须离开命令上下文"的操作，别滥用。
  public static readonly IReadOnlyList<string> RegeneratingOperations = new[]
  {
    "createCorridor",
    "addCorridorRegion",
    "deleteCorridorRegion",
    "rebuildCorridor",
    "addCorridorSurface",
    "setCorridorTargetMappings",
  };

  public static bool IsRegeneratingOperation(string? operation)
  {
    if (string.IsNullOrWhiteSpace(operation)) return false;
    foreach (var op in RegeneratingOperations)
    {
      if (string.Equals(op, operation, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }

  // 等 C3D 后台再生完成：先静默 settleMs 给一个不被打扰的窗口（实测一碰文档/Idle 就会推迟再生，
  // 高频轮询会让它永远建不完），再做低频探测。probe 返回 true 即完成。
  public static async Task<(bool ready, long waitedMs)> WaitForRegenerationAsync(
    Func<Task<bool>> probe,
    int settleMs = 30000,
    int pollMs = 30000,
    int maxMs = 600000)
  {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var ready = false;
    while (sw.ElapsedMilliseconds < maxMs)
    {
      await Task.Delay(sw.ElapsedMilliseconds == 0 ? settleMs : pollMs);
      try { ready = await probe(); }
      catch (Exception ex)
      {
        try { PluginLog.Info("Exec", "WaitForRegeneration probe: " + ex.GetType().Name + " " + ex.Message); }
        catch
        {
          /* logging must never break the regeneration wait loop */
        }
        ready = false;
      }
      if (ready) break;
    }
    return (ready, sw.ElapsedMilliseconds);
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
      // 2026-10-15: 内层等 Idle 与 job 超时对齐（再生类写操作是 job=300s；原来只给 20s，重操作期间会误判超时）
      var idleWaitSeconds = PluginRuntime.GetCurrentRequestIsJob() ? 300 : 20;
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(idleWaitSeconds)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT", "ViaIdle timeout: CAD not idle (no App.Idle within " + idleWaitSeconds + "s).");
      }
      return await tcs.Task;
    });
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    // 2026-10-20 审计：原实现只用一个 started 标志，导致 HOST_BUSY 分支泄漏 _queueDepth。
    // 拿到门(Release 的责任) 与 已开始操作(Complete 的责任) 必须分开记账：
    //   HostBusy 检查在 StartHostOperation 之前抛出 —— 那时 queue 已 +1 但从未 Start，
    //   旧代码走 started==true 分支只减 active、不减 queue → 每次 HOST_BUSY 永久泄漏 1，
    //   累积到 MaxQueuedHostOperations(64) 后整个工具面对所有请求回 "queue is full"（不可恢复，须重启 CAD）。
    var gateAcquired = false;
    var opStarted = false;
    // 2026-08-19 方案3: job 上下文用长超时（重操作不误杀），普通请求保持 25s
    var isJob = PluginRuntime.GetCurrentRequestIsJob();
    var timeoutSeconds = isJob ? 300 : 25;
    const int OPERATION_TIMEOUT_SECONDS_JOB = 300;

    try
    {
      // 2026-10-16: 零文档快速失败 —— 无活动文档时命令上下文永远建立不起来
      //   （ExecuteInCommandContextAsync 回调不触发），会硬卡满 25s 超时 → 污染 HostBusy 级联。
      //   这里提前明确报错。文档级操作走 ExecuteDocumentOpAsync（openDrawing/newDrawing）不受此限，仍可破局。
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.NO_DRAWING",
          Loc.Tk("error.noDrawing"));
      }
      // 2026-08-19 方案3: job 运行期间（后台重操作占用命令上下文），普通请求快速失败不排队——
      // 避免普通请求在门内等 job 完成而烧 25s 超时 → 误触发 HOST_BUSY。relay 侧自动等待重试。
      if (!isJob && JobRegistry.GetStats().Running > 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.JOB_RUNNING",
          Loc.Tk("error.jobRunning"));
      }
      await HostExecutionGate.WaitAsync(cancellationToken);
      gateAcquired = true;
      // 2026-08-12 C1: 上次操作超时后命令上下文仍被占用——快速失败, 不再各烧 25s
      // 2026-08-12 v2: busy 超过 60s 自动恢复(被放弃操作可能永不结束, 不能永久瘫痪面板)
      if (HostBusy && Environment.TickCount64 - HostBusySinceTicks > HOST_BUSY_MAX_MS)
      {
        HostBusy = false;
        try { PluginLog.Debug("Exec", "HostBusy auto-recovered after timeout (" + HOST_BUSY_MAX_MS + "ms)"); } catch { }
      }
      if (HostBusy)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          Loc.Tk("error.hostBusyStale"));
      }
      PluginRuntime.StartHostOperation();
      opStarted = true;
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
        // 2026-10-10 (批次6.1): 超时后统一交由看门狗处置——投递 ESC 打断卡住的交互命令 +
        // 周期探测命令上下文，一旦真正空闲立即解锁（不再盲等 60s）。
        linkedCts.Cancel(); // 停止延迟任务
        var diagCmd = HandleAbandonedOperation("command", execTask);
        throw new JsonRpcDispatchException(
          "CIVIL3D.OPERATION_TIMEOUT",
          Loc.Tk("error.timeoutCommand", ("sec", timeoutSeconds), ("max", HOST_BUSY_MAX_MS / 1000), ("diag", diagCmd)));
      }
    }
    finally
    {
      // 2026-10-20 审计：两笔账分开结 —— 操作没开始就退回队列计数，门拿到就一定释放。
      if (opStarted) PluginRuntime.CompleteHostOperation();
      else PluginRuntime.CancelQueuedHostOperation();
      if (gateAcquired) HostExecutionGate.Release();
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
        + " activeDoc=" + (d == null ? "(none)" : System.IO.Path.GetFileName(d.Name))
        + " dlg=" + DescribeWindow(FindBlockingDialog());
    }
    catch (Exception ex) { return "diag failed: " + ex.Message; }
  }

  // ── 2026-10-17 批次6.3：模态对话框解锁（ESC 到不了模态对话框）────────────────────────
  // 背景：命令上下文被**模态对话框**占用时，SendStringToExecute(ESC) 只进编辑器命令队列，
  //   到不了对话框 → 看门狗投多少次都无效，只能重启 C3D。这里补一条**进程内**解锁通道：
  //   找到本进程的阻塞对话框窗口，直接 PostMessage 投 ESC；达阈值仍无效再 WM_CLOSE（=用户点 X）。
  // 安全性：只用 PostMessage（异步入队，不阻塞、不跨线程死锁）；只看/只动**本进程**的窗口
  //   （校验 GetWindowThreadProcessId），不碰别的程序；仅在超时看门狗已判定卡住的窗口期触发。
  [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
  [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111;
  private const uint GA_ROOT = 2;
  private const uint GW_OWNER = 4;
  private const int VK_ESCAPE = 0x1B;
  private const int IDCANCEL = 2;
  private const int BN_CLICKED = 0;
  // ESC 直投无效多久后升级为关闭对话框（保守：先给 ESC 足够机会，再动手关）
  private const long DIALOG_CLOSE_AFTER_MS = 20_000;

  /// <summary>CAD 主框架窗口句柄（用于排除——绝不对主窗口投递/关闭）。</summary>
  private static IntPtr MainWindowHandle()
  {
    try { return App.MainWindow?.Handle ?? IntPtr.Zero; }
    catch { return IntPtr.Zero; }
  }

  /// <summary>
  /// 应用外壳/面板类窗口——不是“阻塞对话框”，一律不投递、不关闭。
  /// （WinForms 面板类是 HwndWrapper…，MFC 面板是 Afx:…，Qt 用 Qt5…，浏览器内核用 Chrome_WidgetWin…）
  /// </summary>
  private static bool IsShellWindowClass(string cls)
  {
    if (string.IsNullOrEmpty(cls)) return false;
    return cls.StartsWith("Afx", StringComparison.OrdinalIgnoreCase)
        || cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase)
        || cls.IndexOf("HwndWrapper", StringComparison.OrdinalIgnoreCase) >= 0
        || cls.IndexOf("WindowsForms", StringComparison.OrdinalIgnoreCase) >= 0
        || cls.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  /// <summary>
  /// 本进程内“可被视为阻塞对话框”的窗口：可见、启用、**有属主**、非主框架、非面板类。
  /// 2026-10-19 修复：旧实现只看 GetForegroundWindow()——用户切到别的程序（如微信）时前台不是 CAD，
  ///   于是返回 Zero、dlgEsc 恒为 0、模态对话框永远解不开（真机实测 _OPTIONS 卡死 60s 未解）。
  ///   现在先走前台快路径，再枚举本进程全部顶层窗口兜底。
  /// </summary>
  private static IntPtr FindBlockingDialog()
  {
    try
    {
      var main = MainWindowHandle();

      // ① 快路径：前台窗口就是本进程的弹出窗口
      var fg = GetForegroundWindow();
      if (fg != IntPtr.Zero)
      {
        GetWindowThreadProcessId(fg, out var fgPid);
        if (fgPid == (uint)Environment.ProcessId && IsWindowVisible(fg))
        {
          var fgRoot = GetAncestor(fg, GA_ROOT);
          if (fgRoot == IntPtr.Zero) fgRoot = fg;
          if (fgRoot != main && GetWindow(fgRoot, GW_OWNER) != IntPtr.Zero) return fgRoot;
        }
      }

      // ② 枚举本进程顶层窗口：优先原生对话框类 #32770，其次其它有属主的弹出窗口
      IntPtr native = IntPtr.Zero, other = IntPtr.Zero;
      EnumWindows((h, _) =>
      {
        GetWindowThreadProcessId(h, out var pid);
        if (pid != (uint)Environment.ProcessId) return true;
        if (h == main || !IsWindowVisible(h) || !IsWindowEnabled(h)) return true;
        if (GetWindow(h, GW_OWNER) == IntPtr.Zero) return true;      // 无属主 = 主框架/工具窗，绝不碰
        var cls = new System.Text.StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        var clsText = cls.ToString();
        if (IsShellWindowClass(clsText)) return true;                 // 面板/外壳，绝不碰
        if (clsText == "#32770")
        {
          if (native == IntPtr.Zero) native = h;                      // EnumWindows 自顶向下，先到先得
        }
        else if (other == IntPtr.Zero) other = h;
        return true;
      }, IntPtr.Zero);
      return native != IntPtr.Zero ? native : other;
    }
    catch { return IntPtr.Zero; }
  }

  private static string DescribeWindow(IntPtr h)
  {
    try
    {
      if (h == IntPtr.Zero) return "(none)";
      var cls = new System.Text.StringBuilder(256); GetClassName(h, cls, cls.Capacity);
      var txt = new System.Text.StringBuilder(256); GetWindowText(h, txt, txt.Capacity);
      return "cls='" + cls + "' title='" + txt + "'";
    }
    catch { return "(err)"; }
  }

  /// <summary>向本进程的阻塞对话框窗口直投 ESC 键（PostMessage，异步安全）。返回是否投出。</summary>
  private static bool TryEscapeBlockingDialog()
  {
    try
    {
      var h = FindBlockingDialog();
      if (h == IntPtr.Zero) return false;
      // lParam: 重复次数=1 | 扫描码(ESC=0x01)<<16；keyup 高位带 0xC0000000 标志
      var lDown = (IntPtr)((1 << 16) | 0x01);
      var lUp = (IntPtr)unchecked((int)0xC0000001);
      PostMessage(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, lDown);
      PostMessage(h, WM_KEYUP, (IntPtr)VK_ESCAPE, lUp);
      return true;
    }
    catch { return false; }
  }

  /// <summary>
  /// 兜底：关闭本进程的阻塞对话框窗口（等价用户点“取消”/“X”），仅在 ESC 长期无效后升级调用。
  /// 只对**原生对话框类 #32770** 动手（面板类已被 IsShellWindowClass 排除）+ 必须有属主。
  /// </summary>
  private static bool TryCloseBlockingDialog(IntPtr h)
  {
    try
    {
      if (h == IntPtr.Zero) return false;
      // 双保险：只关**有属主**的弹出窗口（对话框），主框架无属主 → 绝不会被关掉
      if (GetWindow(h, GW_OWNER) == IntPtr.Zero) return false;
      var cls = new System.Text.StringBuilder(256); GetClassName(h, cls, cls.Capacity);
      var clsText = cls.ToString();
      if (IsShellWindowClass(clsText)) return false;                  // 面板类绝不关
      // 先按“取消”按钮语义（MFC 对话框对 WM_COMMAND/IDCANCEL 有确定响应），再补一次 WM_CLOSE
      if (clsText == "#32770")
      {
        var wparam = (IntPtr)((BN_CLICKED << 16) | (IDCANCEL & 0xFFFF));
        PostMessage(h, WM_COMMAND, wparam, h);
      }
      PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
      return true;
    }
    catch { return false; }
  }

  /// 超时兑底：打断卡住的交互命令/模态对话框，尝试恢复命令上下文。
  /// ① 向活动文档命令队列投递 ESC ESC（"\x1b\x1b"）——编辑器里等输入的交互命令；
  /// ② 向本进程的阻塞对话框窗口直投 ESC——模态对话框（①②都 best-effort，可从后台线程调用，不持文档锁）。
  /// 2026-10-16: 旧实现投递的是单个反斜杠，实测无效（看门狗连投 20 次 diag 仍 cmdActive=1）；改为 ESC ESC。
  /// 2026-10-17: 补②模态对话框通道（ESC 到不了对话框），并在看门狗里达阈值后升级 WM_CLOSE。
  /// </summary>
  private static void TryInterruptActiveCommand()
  {
    try
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc != null)
      {
        // Autodesk 开发者建议的取消投递方式（Chr(27)+Chr(27)）。
        doc.SendStringToExecute("\x1b\x1b", true, false, false);   // ESC ESC
      }
    }
    catch {
      /* best-effort: send ESC to break a stuck command; failure is non-fatal (the watchdog retries) */
    }
    // 模态对话框下上面那条到不了对话框 → 直接向本进程阻塞窗口投 ESC。
    try { TryEscapeBlockingDialog(); } catch { }
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
        try { PluginLog.Debug("Exec", "abandoned operation finally faulted: " + t.Exception?.GetBaseException()?.Message); } catch { }
      }
      else
      {
        try { PluginLog.Debug("Exec", "abandoned operation finished (completed after timeout); the command context should be free now"); } catch { }
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
            try { PluginLog.Debug("Exec", "HostBusy timer expired — auto-recovered (" + HOST_BUSY_MAX_MS + "ms)"); } catch { }
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

  // ── 2026-10-10 批次6.1：命令上下文超时看门狗 ────────────────────────────────
  // 背景（旧实现痛点）：25s 超时后只是“标记 busy + 投 3 次 ESC + 盲等 60s”，其间无法判断
  //   命令上下文是否真的恢复；极端情况（模态对话框 / 卡死的交互命令）会一直占用文档锁，
  //   逼用户重启 C3D。批次 6.2 已把绝大多数方法移出命令上下文（不再用阻塞式 (command ...)），
  //   这里补上安全网：
  //   ① 统一跟踪被放弃的操作 + 置 busy；
  //   ② 启动单例看门狗线程，每 3s 探测宿主状态；
  //   ③ 一旦命令上下文真正空闲（CMDACTIVE=0 且无未结束的放弃任务且有活动文档）→ 立即解锁；
  //   ④ 未恢复则补投 ESC（限速，每轮最多 1 次）继续尝试打断；
  //   ⑤ 到 60s 上限仍未恢复 → 放行重试（与既有自动恢复语义一致）并保留诊断供健康查询。
  private static readonly object WatchdogLock = new();
  private static CancellationTokenSource? WatchdogCts;
  private const int WATCHDOG_POLL_MS = 3000;
  private static HostRecoverySnapshot? LastHostRecovery;

  private sealed class HostRecoverySnapshot
  {
    public string Kind = "";
    public long StartedAtUnixMs;
    public string InitialDiag = "";
    public int Probes;
    public int EscSent;
    public bool Recovered;
    public long RecoveredAfterMs = -1;
    public int DialogEscapes;      // 向模态对话框直投 ESC 的次数
    public int DialogCloseSent;    // 升级关闭对话框（WM_CLOSE）的次数
    public string FinalDiag = "";
  }

  // 统一处理“超时被放弃的操作”。返回超时瞬间的宿主诊断快照（供异常消息）。
  private static string HandleAbandonedOperation(string kind, Task execTask)
  {
    TrackAbandonedTask(execTask);
    SetHostBusy();
    // 立即连投 3 次 ESC 提高打断成功率（模态对话框下 ESC 仍可能无效，但尽力而为）
    TryInterruptActiveCommand();
    TryInterruptActiveCommand();
    TryInterruptActiveCommand();
    var diag = CaptureHostState();
    StartHostRecoveryWatchdog(kind, diag);
    try { PluginLog.Error("Exec", "[TIMEOUT-diag] op=" + kind + " " + diag); } catch { }
    return diag;
  }

  private static void StartHostRecoveryWatchdog(string kind, string initialDiag)
  {
    lock (WatchdogLock)
    {
      WatchdogCts?.Cancel(); // 新超时接管旧看门狗
      var cts = new CancellationTokenSource();
      WatchdogCts = cts;
      var snap = new HostRecoverySnapshot
      {
        Kind = kind,
        StartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        InitialDiag = initialDiag,
      };
      LastHostRecovery = snap;
      _ = Task.Run(() => RunHostRecoveryWatchdogAsync(snap, cts.Token));
    }
  }

  private static async Task RunHostRecoveryWatchdogAsync(HostRecoverySnapshot snap, CancellationToken token)
  {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < HOST_BUSY_MAX_MS)
      {
        try { await Task.Delay(WATCHDOG_POLL_MS, token); }
        catch (OperationCanceledException) { return; }
        snap.Probes++;
        if (IsHostContextFree())
        {
          snap.Recovered = true;
          snap.RecoveredAfterMs = sw.ElapsedMilliseconds;
          snap.FinalDiag = CaptureHostState();
          ClearHostBusy(); // 真正恢复 → 立即解锁（不等 60s）
          try { PluginLog.Info("Exec", $"[WATCHDOG] command context recovered kind={snap.Kind} after={sw.ElapsedMilliseconds}ms probes={snap.Probes} esc={snap.EscSent}"); } catch { }
          return;
        }
        TryInterruptActiveCommand();
        snap.EscSent++;
        // 2026-10-19: 每轮只解析一次句柄——重复枚举会让“计数/升级关闭”读到不一致的结果（真机 dlgEsc 抖动）
        var dlg = FindBlockingDialog();
        if (dlg != IntPtr.Zero) snap.DialogEscapes++;
        try { PluginLog.Debug("Exec", $"[WATCHDOG] context still busy, sent another ESC #{snap.EscSent} kind={snap.Kind} dlgEsc={snap.DialogEscapes} win={DescribeWindow(dlg)}"); } catch { }
        // 2026-10-17: ESC 到不了模态对话框 → 达阈值后升级为关闭对话框（=用户点取消/X），打破死局
        if (sw.ElapsedMilliseconds >= DIALOG_CLOSE_AFTER_MS && dlg != IntPtr.Zero)
        {
          if (TryCloseBlockingDialog(dlg))
          {
            snap.DialogCloseSent++;
            try { PluginLog.Warn("Exec", $"[WATCHDOG] ESC ineffective for {DIALOG_CLOSE_AFTER_MS}ms — escalating to close the blocking dialog #{snap.DialogCloseSent} kind={snap.Kind} win={DescribeWindow(dlg)}"); } catch { }
          }
          else
          {
            try { PluginLog.Warn("Exec", $"[WATCHDOG] closing the blocking dialog was blocked by guardrails (not a native dialog / palette class / no owner) win={DescribeWindow(dlg)}"); } catch { }
          }
        }
      }
      // 到上限仍未确认恢复：放行重试（与旧 60s 自动恢复一致）并保留诊断
      snap.FinalDiag = CaptureHostState();
      ClearHostBusy();
      try { PluginLog.Warn("Exec", $"[WATCHDOG] command context not confirmed recovered within {HOST_BUSY_MAX_MS / 1000}s kind={snap.Kind} esc={snap.EscSent} diag={snap.FinalDiag}; if CAD is stuck in a modal dialog/command, press ESC manually or restart Civil 3D"); } catch { }
    }
    catch (Exception ex)
    {
      try { PluginLog.Error("Exec", "[WATCHDOG] exception", ex); } catch { }
    }
  }

  // 命令上下文是否真正空闲（可安全执行插件操作）：有活动文档 + 无活动命令 + 无未结束的放弃任务。
  private static bool IsHostContextFree()
  {
    try
    {
      if (App.DocumentManager.MdiActiveDocument == null) return false;
      var cmdActive = Convert.ToInt32(App.GetSystemVariable("CMDACTIVE"));
      if (cmdActive != 0) return false;
      // 2026-10-19 真机纠错：原实现还要求“无未结束的放弃任务”，但被放弃的操作**可能永不结束**
      //   （模态对话框期间发起的 ExecuteInCommandContextAsync 回调再也没回来）——宿主明明已经可用，
      //   看门狗却一直判“未恢复”，直到 60s 上限。判断能不能干活只看：命令上下文空闲 + 没有阻塞对话框。
      if (FindBlockingDialog() != IntPtr.Zero) return false;   // 对话框还开着 → 还没恢复
      return true;
    }
    catch { return false; }
  }

  // 供健康查询/诊断：报告 busy 状态 + 最近一次超时恢复快照。
  public static object GetHostRecoveryState()
  {
    HostRecoverySnapshot? snap;
    lock (WatchdogLock) { snap = LastHostRecovery; }
    var busySinceTicks = HostBusySinceTicks;
    return new Dictionary<string, object?>
    {
      ["hostBusy"] = HostBusy,
      ["hostBusySinceUnixMs"] = busySinceTicks == 0
        ? (long?)null
        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (Environment.TickCount64 - busySinceTicks),
      ["hostBusyMaxMs"] = HOST_BUSY_MAX_MS,
      ["watchdogPollMs"] = WATCHDOG_POLL_MS,
      ["lastTimeout"] = snap == null ? null : (object)new Dictionary<string, object?>
      {
        ["kind"] = snap.Kind,
        ["startedAtUnixMs"] = snap.StartedAtUnixMs,
        ["initialDiag"] = snap.InitialDiag,
        ["probes"] = snap.Probes,
        ["escSent"] = snap.EscSent,
        ["recovered"] = snap.Recovered,
        ["recoveredAfterMs"] = snap.RecoveredAfterMs,
        ["dialogEscapes"] = snap.DialogEscapes,
        ["dialogCloseSent"] = snap.DialogCloseSent,
        ["finalDiag"] = snap.FinalDiag,
      },
    };
  }
}


