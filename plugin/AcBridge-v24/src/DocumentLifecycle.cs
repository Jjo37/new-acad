using Autodesk.AutoCAD.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// 文档生命周期的唯一归属（P2.6 S6，2026-10-28）。
///
/// 之前只有 PluginEntry 挂了 DocumentCreated（用来绑选择事件），**换图和关图没人管**：
/// UndoJournal 的清空写在 MCP 的 newDrawing/openDrawing 里，用户自己在 CAD 里切图就绕过了。
/// 那正是「撤回记错条目」「跨图读旧 handle」的来源。
///
/// 现在：DocumentActivated / DocumentToBeDestroyed 统一在这里收口，触发时
///   1) UndoJournal.Clear()（句柄是 per-drawing 的，side-db 快照一起释放）
///   2) 选择集快照来自别的图 → 删掉（并让 getSelection 拒绝返回旧图的句柄）
///   3) epoch++ 并记录原因，供门禁断言"切换真的被处理了"
///
/// 判定本身在 DocLifecyclePolicy（纯函数、可单测），这里只负责取文档身份 + 执行。
/// </summary>
internal static class DocumentLifecycle
{
  private static object? currentDoc;
  private static string currentName = "";
  private static long epoch;
  private static string lastReason = "not-started";
  private static string lastEventAt = "";
  private static bool lastClearedJournal;
  private static bool lastDroppedSelection;
  private static bool started;

  public static long Epoch => epoch;
  public static string CurrentName => currentName;

  public static void Start()
  {
    if (started) return;
    started = true;
    try
    {
      App.DocumentManager.DocumentActivated += OnDocumentActivated;
      App.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
    }
    catch (System.Exception ex)
    {
      PluginLog.Warn("Lifecycle", "subscribe failed (document switches will not be handled): " + ex.Message);
    }
    try
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      currentDoc = doc;
      currentName = SafeName(doc);
      lastReason = "initial";
      PluginLog.Info("Lifecycle", "started | initial doc=" + (currentName.Length == 0 ? "(none)" : currentName));
    }
    catch (System.Exception ex)
    {
      PluginLog.Warn("Lifecycle", "initial doc read failed: " + ex.Message);
    }
  }

  public static void Stop()
  {
    if (!started) return;
    started = false;
    try { App.DocumentManager.DocumentActivated -= OnDocumentActivated; } catch { /* best effort on shutdown */ }
    try { App.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed; } catch { /* best effort on shutdown */ }
  }

  private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
  {
    try { ApplySwitch(e.Document, "activated"); }
    catch (System.Exception ex) { PluginLog.Warn("Lifecycle", "activation handling failed: " + ex.Message); }
  }

  private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
  {
    try
    {
      // 关掉的是当前文档 → 句柄和快照都不能留；关的是别的文档，只需要看新激活的是谁。
      var closingCurrent = ReferenceEquals(e.Document, currentDoc);
      var next = SafeMdi();
      if (closingCurrent)
      {
        ApplySwitch(next, "closed");
      }
      else
      {
        ApplySwitch(next, "other-closed");
      }
    }
    catch (System.Exception ex) { PluginLog.Warn("Lifecycle", "close handling failed: " + ex.Message); }
  }

  private static void ApplySwitch(object? nextDoc, string eventName)
  {
    var same = nextDoc != null && ReferenceEquals(currentDoc, nextDoc);
    var nextName = SafeName(nextDoc);
    var snapshotDocName = AcadCommands.PeekSelectionSnapshotDocName();
    var plan = DocLifecyclePolicy.DecideOnActivation(same, snapshotDocName, nextName);

    currentDoc = nextDoc;
    currentName = nextName;
    epoch++;
    lastReason = eventName + ":" + plan.Reason;
    lastClearedJournal = plan.ClearJournal;
    lastDroppedSelection = plan.DropSelection;
    lastEventAt = PluginTime.StoredNow();

    if (plan.ClearJournal) UndoJournal.Clear();
    if (plan.DropSelection) AcadCommands.DropSelectionSnapshot(plan.Reason);

    PluginLog.Info("Lifecycle", "epoch=" + epoch + " doc=" + (nextName.Length == 0 ? "(none)" : nextName) +
      " reason=" + lastReason + " clearJournal=" + plan.ClearJournal + " dropSelection=" + plan.DropSelection);
  }

  private static object? SafeMdi()
  {
    try { return App.DocumentManager.MdiActiveDocument; } catch { return null; }
  }

  private static string SafeName(object? doc)
  {
    try { return doc is Document d ? (PluginRuntime.GetDrawingIdentity(d) ?? "") : ""; } catch { return ""; }
  }

  /// <summary>门禁/排障用：文档生命周期现在是什么状态。</summary>
  internal static System.Collections.Generic.Dictionary<string, object?> State() => new()
  {
    ["epoch"] = epoch,
    ["currentName"] = currentName,
    ["lastReason"] = lastReason,
    ["lastEventAt"] = lastEventAt,
    ["lastClearedJournal"] = lastClearedJournal,
    ["lastDroppedSelection"] = lastDroppedSelection,
    ["started"] = started,
  };
}
