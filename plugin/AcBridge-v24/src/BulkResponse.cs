using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// P24（2026-11-17）：批量读方法自己的**响应字节预算**。
///
/// 为什么要有它：超过 <see cref="ResultSummarizer.DefaultThresholdBytes"/>（64KB）时，
/// ResultSummarizer 会做"形状保持式裁剪"（数组砍半再砍半）。P23 虽然在裁剪处同步了计数，
/// 但分页语义还是坏的 —— 调用方明明传了 maxCount=2000、offset=0，实际只拿到 250 条，
/// 也拿不到"下一页从哪开始"。批量读方法自己按字节裁可以：
///   ① 只保留前 N 条（分页顺序不变，不切半）；
///   ② 明确回 nextOffset / hasMore / byteLimited，翻页是机械动作；
///   ③ 不触发 summarizer，返回体形状稳定。
///
/// 预算默认 = 阈值 × 0.7；未显式 fullResult:true 时硬顶到阈值 × 0.9
/// （抬到阈值以上只会被 summarizer 再剪一次，得不偿失）。
/// </summary>
internal static class BulkResponse
{
  private const double DefaultFraction = 0.7;
  private const double ClampFraction = 0.9;

  public static int DefaultBudget => (int)(ResultSummarizer.DefaultThresholdBytes * DefaultFraction);
  public static int MaxBudgetWithoutFullResult => (int)(ResultSummarizer.DefaultThresholdBytes * ClampFraction);

  public static int ResolveBudget(JsonObject? parameters) => ResolveBudget(parameters, out _);

  public static int ResolveBudget(JsonObject? parameters, out bool clampedToThreshold)
  {
    var wantsFullResult = PluginRuntime.GetOptionalBool(parameters, "fullResult") ?? false;
    var requested = PluginRuntime.GetOptionalInt(parameters, "maxResponseBytes") ?? DefaultBudget;
    if (requested <= 0) requested = DefaultBudget;
    var budget = wantsFullResult ? requested : Math.Min(requested, MaxBudgetWithoutFullResult);
    clampedToThreshold = !wantsFullResult && requested > budget;
    return budget;
  }

  /// <summary>
  /// 按字节预算保留**前 N 条**（顺序不变）。返回可保留条数；items 不被修改。
  /// overheadBytes 用于给外层键名/统计字段留余量。
  /// </summary>
  public static int Fit(IReadOnlyList<Dictionary<string, object?>> items, int byteBudget, int overheadBytes = 1024)
  {
    if (items.Count == 0 || byteBudget <= 0) return items.Count;
    long total = overheadBytes;
    var kept = 0;
    for (var i = 0; i < items.Count; i++)
    {
      var size = JsonSerializer.SerializeToUtf8Bytes(items[i]).Length + 1;
      if (total + size > byteBudget) break;
      total += size;
      kept++;
    }
    // 单条就超预算时至少留 1 条：空数组会让调用方以为"没有数据"，比超大更危险
    if (kept == 0) kept = 1;
    return kept;
  }

  /// <summary>
  /// 字符串数组（handle 列表）的字节预算：只保留前 N 个，顺序不变。
  /// 2026-11-17 P25：selectByCriteria 的 handles 会被 5000 上限和体积双重限制，
  ///   但 selected 是"CAD 里真的选中了多少" —— 两个数必须分开报，不能互相覆盖。
  /// </summary>
  public static int FitStrings(IReadOnlyList<string> items, int byteBudget, int overheadBytes = 1024)
  {
    if (items.Count == 0 || byteBudget <= 0) return items.Count;
    long total = overheadBytes;
    var kept = 0;
    for (var i = 0; i < items.Count; i++)
    {
      var size = System.Text.Encoding.UTF8.GetByteCount(items[i]) + 4; // 引号 + 逗号
      if (total + size > byteBudget) break;
      total += size;
      kept++;
    }
    if (kept == 0) kept = 1;
    return kept;
  }

  public static (int Kept, bool ByteLimited) TrimStrings(List<string> items, int byteBudget, int overheadBytes = 1024)
  {
    var kept = FitStrings(items, byteBudget, overheadBytes);
    var limited = kept < items.Count;
    if (limited) items.RemoveRange(kept, items.Count - kept);
    return (items.Count, limited);
  }

  /// <summary>按预算裁掉尾巴，返回 (保留条数, 是否因字节受限而少给了)。</summary>
  public static (int Kept, bool ByteLimited) Trim(List<Dictionary<string, object?>> items, int byteBudget, int overheadBytes = 1024)
  {
    var kept = Fit(items, byteBudget, overheadBytes);
    var limited = kept < items.Count;
    if (limited) items.RemoveRange(kept, items.Count - kept);
    return (items.Count, limited);
  }
}
