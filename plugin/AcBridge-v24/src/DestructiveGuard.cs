namespace Civil3DMcpPlugin;

/// <summary>
/// 破坏性动作的唯一确认守卫（P2.6 S4，2026-10-28）。
///
/// 病根：面板里能不可逆删数据的地方有六处，确认机制却有三套 ——
///   · 回退清单"清空"：自己攒了一对 bool + DateTime（clearJournalArmed / clearJournalArmedAt）
///   · 清项目 / 清聊天 / 清 AI 配置：各写一遍 MessageBox
///   · 清计划、清"撤不回的记录"：**一句确认都没有**
/// 于是"破坏性动作必须确认"这条规则实际上靠人记得，不靠结构 —— 加一个新入口就能绕过。
///
/// 这个类只管"两击式"确认的状态机（UI 模态框那几处保持原样，但必须登记在
/// docs/audit/destructive-actions.json 里，由门禁扫描核对）。规则：
///   D1 第一次 Request(id) → Armed（只提示，不动手）
///   D2 TTL 内第二次 Request(同 id) → Confirmed（可以动手了，同时解除）
///   D3 超过 TTL → 重新 Armed（不许"半分钟前点过一次"就永久有效）
///   D4 任一时刻只有一个动作处于 Armed —— 瞄准 A 的手感不能用来放行 B
///   D5 id 为空/空白 → Blocked（调用方 bug 时宁可不做，fail-closed）
///   D6 确认只能消费一次：Confirmed 之后状态已复位，第三次点击重新 Armed
///
/// 纯类：时钟靠注入，不碰 UI、不碰 Autodesk，可直接进 UnitSystemTests。
/// </summary>
public enum GuardDecision
{
  Armed = 0,
  Confirmed = 1,
  Blocked = 2
}

public sealed class DestructiveGuard
{
  private readonly Func<DateTime> _clock;
  private readonly int _ttlSeconds;
  private string _armedAction = "";
  private DateTime _armedAt;

  public DestructiveGuard(Func<DateTime> clock, int ttlSeconds = 8)
  {
    _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    _ttlSeconds = ttlSeconds <= 0 ? 8 : ttlSeconds;
  }

  public int TtlSeconds => _ttlSeconds;

  /// <summary>当前被"瞄准"的动作 id（空字符串 = 没有）。</summary>
  public string ArmedAction => _armedAction;

  public bool IsArmed(string actionId) =>
    !string.IsNullOrWhiteSpace(actionId) && _armedAction == actionId && !Expired();

  public GuardDecision Request(string actionId)
  {
    if (string.IsNullOrWhiteSpace(actionId)) return GuardDecision.Blocked;

    if (_armedAction == actionId && !Expired())
    {
      // D2/D6: 消费一次就复位
      Reset();
      return GuardDecision.Confirmed;
    }

    // D1/D3/D4: 重新瞄准（顺手把别的动作解除）
    _armedAction = actionId;
    _armedAt = _clock();
    return GuardDecision.Armed;
  }

  /// <summary>动作完成、被取消、或用户切走了 —— 一律解除。</summary>
  public void Reset()
  {
    _armedAction = "";
    _armedAt = default;
  }

  /// <summary>距离过期还剩多少秒；未瞄准返回 null。</summary>
  public double? SecondsLeft(string actionId)
  {
    if (!IsArmed(actionId)) return null;
    return Math.Max(0, _ttlSeconds - (_clock() - _armedAt).TotalSeconds);
  }

  private bool Expired() => _armedAction.Length > 0 && (_clock() - _armedAt).TotalSeconds > _ttlSeconds;
}
