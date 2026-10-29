namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-10-27 (P2.6 S1): 工具页视图状态的唯一归属。
///
/// 病根：面板里原来用 5 个 bool 表示视图（toolsCatalogMode / toolsWorkflowMode / toolsJournalMode /
/// toolsAssistedActive / toolsBindingActive）—— 3^5 种组合里只有少数是合法的，2026-10-23 当天因此出了
/// "首点调用流先画成工作流再切模式"、"从日志视图返回仍显示日志列"两个 bug。
///
/// 这里把"当前视图"收敛成一个枚举，切换走一张显式的转换表：
///   - 任一时刻只有一个 Current（枚举天然保证）；
///   - TrySwitch 是原子的：要么切换成功并清掉旧视图的临时状态，要么完全不动；
///   - assisted / binding 是 workflows 视图的子状态，离开任何视图都必须被清掉。
///
/// 纯类，无 UI / 无 Autodesk 依赖，可直接进 UnitSystemTests。
/// </summary>
public enum ToolsView
{
  Stream = 0,
  Catalog = 1,
  Workflows = 2,
  Journal = 3
}

public sealed class PaletteViewState
{
  public ToolsView Current { get; private set; } = ToolsView.Stream;

  /// <summary>回放会话是否进行中（只可能属于 Workflows 视图）。</summary>
  public bool AssistedActive { get; private set; }

  /// <summary>槽位绑定表是否打开（依赖回放会话）。</summary>
  public bool BindingActive { get; private set; }

  // 转换表是唯一真相：谁能切到谁。任何"切换"都必须过这张表。
  private static readonly Dictionary<ToolsView, ToolsView[]> Allowed = new()
  {
    [ToolsView.Stream] = new[] { ToolsView.Catalog, ToolsView.Workflows, ToolsView.Journal },
    [ToolsView.Catalog] = new[] { ToolsView.Stream, ToolsView.Workflows, ToolsView.Journal },
    [ToolsView.Workflows] = new[] { ToolsView.Stream, ToolsView.Catalog, ToolsView.Journal },
    [ToolsView.Journal] = new[] { ToolsView.Stream, ToolsView.Catalog, ToolsView.Workflows }
  };

  public static bool IsAllowed(ToolsView from, ToolsView to) =>
    from == to || Array.IndexOf(Allowed[from], to) >= 0;

  public bool CanSwitch(ToolsView target) => IsAllowed(Current, target);

  /// <summary>
  /// 原子切换：成功则返回 true 并清掉旧视图的临时状态；不允许或原地不动时状态不变。
  /// </summary>
  public bool TrySwitch(ToolsView target)
  {
    if (!CanSwitch(target)) return false;
    if (target == Current) return true;

    Current = target;
    // 离开任何视图都必须清干净（S1：不留在别的视图里显示上一个视图的列/会话）。
    AssistedActive = false;
    BindingActive = false;
    return true;
  }

  public ToolsView SwitchOrThrow(ToolsView target) =>
    TrySwitch(target) ? Current : throw new InvalidOperationException($"view switch not allowed: {Current} -> {target}");

  public void BeginAssisted()
  {
    if (Current != ToolsView.Workflows)
      throw new InvalidOperationException("assisted replay only exists in the Workflows view");
    // 2026-10-28 接线时发现：参数表是"回放前的编辑态"，开始回放就把它收掉 —— 两者互斥。
    BindingActive = false;
    AssistedActive = true;
  }

  public void EndAssisted()
  {
    AssistedActive = false;
    BindingActive = false;   // 回放结束，绑定表也必须收起
  }

  public void BeginBinding()
  {
    // 2026-10-28 接线修正：参数表是回放**之前**的编辑态（用户在表里改参数，再点"开始回放"），
    // 所以它不依赖 assisted 会话，只要求当前在 Workflows 视图；反过来回放进行中不能再改表。
    if (Current != ToolsView.Workflows)
      throw new InvalidOperationException("binding table only exists in the Workflows view");
    if (AssistedActive)
      throw new InvalidOperationException("cannot edit the binding table while a replay is running");
    BindingActive = true;
  }

  public void EndBinding() => BindingActive = false;
}
