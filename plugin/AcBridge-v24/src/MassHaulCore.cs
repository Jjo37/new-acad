using System;
using System.Collections.Generic;

namespace Civil3DMcpPlugin;

/// <summary>
/// 土方调配核心算法（A1）—— **纯计算，无任何 CAD/插件依赖**，可独立单元测试。
///
/// 移植自 A1.2 spike（tools/_spike-masshaul.js，Node 原型经 11/11 手算对账）。
/// 约定：曲线节点 (station, 累计净土方) 中"挖为正"。increments[i] 为 [stations[i], stations[i+1]] 段净土方（挖+ / 填−）。
///
/// 两套独立算法互证：
///   planMoment —— 保序 FIFO 就近调配（一维等运价最优）的 体积×运距 之和
///   curveMoment —— 曲线积分 ∫|C − C_区间起点| dx
///   两者应一致（一致性即算法自洽的证据）。
/// </summary>
public static class MassHaulCore
{
  private const double Eps = 1e-9;

  public static MassHaulResult Solve(IReadOnlyList<double> stations, IReadOnlyList<double> increments, double freeHaulDistance, bool pairOpenSections = false)
  {
    if (stations == null) throw new ArgumentNullException(nameof(stations));
    if (increments == null) throw new ArgumentNullException(nameof(increments));
    if (stations.Count < 2) throw new ArgumentException("stations 至少 2 个（n+1）");
    if (stations.Count != increments.Count + 1)
      throw new ArgumentException($"stations({stations.Count}) 必须 = increments({increments.Count}) + 1");

    // 累计曲线
    var xs = new double[stations.Count];
    var cs = new double[stations.Count];
    double acc = 0;
    for (var i = 0; i < stations.Count; i++)
    {
      xs[i] = stations[i];
      cs[i] = acc;
      if (i < increments.Count) acc += increments[i];
    }

    // 平衡区间：累计曲线回到区间起点值的那些点
    var sections = new List<(int I0, int I1)>();
    var start = 0;
    var level = cs[0];
    for (var i = 1; i < cs.Length; i++)
    {
      if (Math.Abs(cs[i] - level) < Eps)
      {
        sections.Add((start, i));
        start = i;
        level = cs[i];
      }
    }
    (int I0, int I1)? leftover = start < cs.Length - 1 ? (start, cs.Length - 1) : null;

    var result = new MassHaulResult { FreeHaulDistance = freeHaulDistance };

    MassHaulSection BuildSection(int i0, int i1, double? balanceLevel = null, bool isOpen = false)
    {
      var cutBlocks = new List<(double Xc, double Vol)>();
      var fillBlocks = new List<(double Xc, double Vol)>();
      for (var i = i0; i < i1; i++)
      {
        var v = increments[i];
        if (Math.Abs(v) < Eps) continue;
        var xc = (stations[i] + stations[i + 1]) / 2.0;
        if (v > 0) cutBlocks.Add((xc, v)); else fillBlocks.Add((xc, -v));
      }

      var cutVolume = 0.0;
      foreach (var b in cutBlocks) cutVolume += b.Vol;
      var fillVolume = 0.0;
      foreach (var b in fillBlocks) fillVolume += b.Vol;

      // 保序 FIFO 就近调配
      var pairs = new List<MassHaulPair>();
      double moment = 0, overhaul = 0, maxDist = 0;
      int ci = 0, fi = 0;
      var cRem = cutBlocks.Count > 0 ? cutBlocks[0].Vol : 0.0;
      var fRem = fillBlocks.Count > 0 ? fillBlocks[0].Vol : 0.0;
      while (ci < cutBlocks.Count && fi < fillBlocks.Count)
      {
        var v = Math.Min(cRem, fRem);
        var d = Math.Abs(fillBlocks[fi].Xc - cutBlocks[ci].Xc);
        var ov = d > freeHaulDistance ? v * (d - freeHaulDistance) : 0.0;
        moment += v * d;
        overhaul += ov;
        if (d > maxDist) maxDist = d;
        pairs.Add(new MassHaulPair
        {
          Volume = v,
          Distance = d,
          CutStation = cutBlocks[ci].Xc,
          FillStation = fillBlocks[fi].Xc,
          OverhaulMoment = ov,
        });
        cRem -= v;
        fRem -= v;
        if (cRem <= Eps) { ci++; cRem = ci < cutBlocks.Count ? cutBlocks[ci].Vol : 0.0; }
        if (fRem <= Eps) { fi++; fRem = fi < fillBlocks.Count ? fillBlocks[fi].Vol : 0.0; }
      }

      return new MassHaulSection
      {
        StartStation = xs[i0],
        EndStation = xs[i1],
        CutVolume = cutVolume,
        FillVolume = fillVolume,
        PlanMoment = moment,
        CurveMoment = CurveMoment(xs, cs, i0, i1, balanceLevel ?? cs[i0]),
        AvgHaulDistance = cutVolume > Eps ? moment / cutVolume : 0.0,
        MaxHaulDistance = pairs.Count > 0 ? maxDist : 0.0,
        OverhaulMoment = overhaul,
        Pairs = pairs,
        IsOpen = isOpen,
      };
    }

    void Accumulate(MassHaulSection section)
    {
      // 经典恒等式 planMoment==curveMoment 只对“闭合区间”（累计曲线回到起点值）成立；开区间只做如实标注
      section.Consistent = !section.IsOpen && Math.Abs(section.PlanMoment - section.CurveMoment) < 1e-6;
      result.Sections.Add(section);
      result.HauledCutVolume += section.CutVolume;
      result.HauledFillVolume += section.FillVolume;
      result.HaulMoment += section.PlanMoment;
      result.OverhaulMoment += section.OverhaulMoment;
      result.PairCount += section.Pairs.Count;
    }

    foreach (var (i0, i1) in sections) Accumulate(BuildSection(i0, i1));

    // 未闭合尾部（净挖/净填）：opt-in 时整段作为一个“挖填互配”区间，
    // 平衡线取等面积线 L*（∫(C−L*)+ = ∫(L*−C)+，等价于挖填互配）；余量仍记弃/借方。
    if (leftover.HasValue && pairOpenSections)
    {
      var (l0, l1) = leftover.Value;
      Accumulate(BuildSection(l0, l1, BalanceLevel(xs, cs, l0, l1), isOpen: true));
    }

    double totalCut = 0, totalFill = 0;
    foreach (var v in increments) { if (v > 0) totalCut += v; else totalFill += -v; }
    result.CutVolume = totalCut;
    result.FillVolume = totalFill;
    result.NetVolume = totalCut - totalFill;
    result.FreeHaulMoment = result.HaulMoment - result.OverhaulMoment;
    result.AvgHaulDistance = result.HauledCutVolume > Eps ? result.HaulMoment / result.HauledCutVolume : 0.0;

    if (leftover.HasValue)
    {
      var (l0, l1) = leftover.Value;
      var v = cs[l1] - cs[l0];
      result.Unbalanced = new MassHaulImbalance
      {
        FromStation = xs[l0],
        Volume = Math.Abs(v),
        Type = v > 0 ? "waste" : "borrow",
      };
    }

    return result;
  }

  /// <summary>∫|C − level| dx（按零点切开后线性精确积分）</summary>
  /// <summary>等面积平衡线：使 ∫(C−L)+ = ∫(L−C)+ 的 L（二分）</summary>
  private static double BalanceLevel(double[] xs, double[] cs, int i0, int i1)
  {
    var lo = double.MaxValue; var hi = double.MinValue;
    for (var i = i0; i <= i1; i++) { if (cs[i] < lo) lo = cs[i]; if (cs[i] > hi) hi = cs[i]; }
    for (var it = 0; it < 80; it++)
    {
      var mid = (lo + hi) / 2.0;
      var (above, below) = CurveAreas(xs, cs, i0, i1, mid);
      if (above > below) lo = mid; else hi = mid;
    }
    return (lo + hi) / 2.0;
  }

  /// <summary>分段线性曲线相对水平线的上/下方面积（含过零切分）</summary>
  private static (double above, double below) CurveAreas(double[] xs, double[] cs, int i0, int i1, double level)
  {
    double above = 0, below = 0;
    for (var i = i0; i < i1; i++)
    {
      var w = xs[i + 1] - xs[i];
      var c0 = cs[i] - level;
      var c1 = cs[i + 1] - level;
      if (c0 >= 0 && c1 >= 0) { above += 0.5 * (c0 + c1) * w; continue; }
      if (c0 <= 0 && c1 <= 0) { below += -0.5 * (c0 + c1) * w; continue; }
      var t = c0 / (c0 - c1);
      var a1 = 0.5 * c0 * (t * w);
      var a2 = -0.5 * c1 * ((1 - t) * w);
      if (c0 > 0) { above += Math.Abs(a1); below += Math.Abs(a2); }
      else { below += Math.Abs(a1); above += Math.Abs(a2); }
    }
    return (above, below);
  }

  private static double CurveMoment(double[] xs, double[] cs, int i0, int i1, double level)
  {
    double m = 0;
    for (var i = i0; i < i1; i++)
    {
      var a = cs[i] - level;
      var b = cs[i + 1] - level;
      var dx = xs[i + 1] - xs[i];
      if (dx <= 0) continue;
      if (a * b < 0)
      {
        var t = a / (a - b);
        m += Math.Abs(a / 2 * (dx * t)) + Math.Abs(b / 2 * (dx * (1 - t)));
      }
      else
      {
        m += Math.Abs((a + b) / 2 * dx);
      }
    }
    return m;
  }
}

public sealed class MassHaulResult
{
  public List<MassHaulSection> Sections { get; } = new List<MassHaulSection>();
  public double CutVolume { get; set; }
  public double FillVolume { get; set; }
  public double NetVolume { get; set; }
  public double HaulMoment { get; set; }
  public double FreeHaulMoment { get; set; }
  public double OverhaulMoment { get; set; }
  public double AvgHaulDistance { get; set; }
  public double HauledCutVolume { get; set; }
  public double HauledFillVolume { get; set; }
  public double FreeHaulDistance { get; set; }
  public int PairCount { get; set; }
  public MassHaulImbalance? Unbalanced { get; set; }
}

public sealed class MassHaulSection
{
  public double StartStation { get; set; }
  public double EndStation { get; set; }
  public double CutVolume { get; set; }
  public double FillVolume { get; set; }
  public double PlanMoment { get; set; }
  public double CurveMoment { get; set; }
  public bool Consistent { get; set; }

  /// <summary>开区间（净挖/净填，累计曲线不闭合）：FIFO 配对矩与曲线积分不要求相等</summary>
  public bool IsOpen { get; set; }
  public double AvgHaulDistance { get; set; }
  public double MaxHaulDistance { get; set; }
  public double OverhaulMoment { get; set; }
  public List<MassHaulPair> Pairs { get; set; } = new List<MassHaulPair>();
}

public sealed class MassHaulPair
{
  public double Volume { get; set; }
  public double Distance { get; set; }
  public double CutStation { get; set; }
  public double FillStation { get; set; }
  public double OverhaulMoment { get; set; }
}

public sealed class MassHaulImbalance
{
  public double FromStation { get; set; }
  public double Volume { get; set; }
  public string Type { get; set; } = "";
}
