namespace Civil3DMcpPlugin;

/// <summary>
/// 换图/关图时该清掉什么 —— 纯决策，不碰 Autodesk（可进 UnitSystemTests）。
///
/// 病根（2026-10-23 ~ 10-28）：UndoJournal 只在 MCP 的 newDrawing/openDrawing 里被清，
/// 用户在 CAD 里 Ctrl+Tab、File→Open、关图都不经过那里 —— 旧图的句柄留在日志里，
/// 于是「撤回」可能作用到另一张图上，或者跨图去读已经不存在的 handle。
///
/// 判据（每条对应一个真实后果）：
///   D1 同一个文档对象 → 什么都不清（激活事件会重复触发，重清会把 undo 历史白扔）
///   D2 换了文档 → 日志必清（句柄是 per-drawing 的）
///   D3 选择集快照来自别的图（或来源未知）→ 必须丢（拿旧图句柄去选对象是纯踩雷）
///   D4 当前文档名读不出来（未保存/启动早期）→ 按"最坏情况"处理：日志清、快照丢
///   D5 文档换了但名字一样（另存后重开）→ 日志清；快照同名可留
/// </summary>
public readonly struct DocSwitchPlan
{
  public DocSwitchPlan(bool clearJournal, bool dropSelection, string reason)
  {
    ClearJournal = clearJournal;
    DropSelection = dropSelection;
    Reason = reason;
  }

  public bool ClearJournal { get; }
  public bool DropSelection { get; }
  public string Reason { get; }

  public bool Any => ClearJournal || DropSelection;
}

public static class DocLifecyclePolicy
{
  /// <param name="sameDocument">是否是同一个文档对象（引用相等，不看名字 —— 名字是弱身份）</param>
  /// <param name="selectionDocName">选择集快照记录的图纸名（空 = 没有快照或旧格式）</param>
  /// <param name="currentDocName">当前激活文档的图纸名</param>
  public static DocSwitchPlan DecideOnActivation(bool sameDocument, string? selectionDocName, string? currentDocName)
  {
    if (sameDocument) return new DocSwitchPlan(false, false, "same-document");

    var current = (currentDocName ?? string.Empty).Trim();
    var snapshot = (selectionDocName ?? string.Empty).Trim();

    if (current.Length == 0) return new DocSwitchPlan(true, true, "switch-unknown-document");
    if (snapshot.Length == 0) return new DocSwitchPlan(true, true, "switch-no-snapshot-origin");
    if (!string.Equals(snapshot, current, System.StringComparison.OrdinalIgnoreCase))
      return new DocSwitchPlan(true, true, "switch-stale-selection");

    return new DocSwitchPlan(true, false, "switch-same-name-new-document");
  }
}
