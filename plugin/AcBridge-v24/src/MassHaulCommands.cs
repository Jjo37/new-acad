using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// computeMassHaul —— 土方调配计算（A1）。纯计算，不访问文档。
/// 输入二选一：
///   stations(n+1) + increments(n)            —— 直接给每段净土方（挖+ / 填−）
///   stations(n+1) + cut[](n) + fill[](n)     —— 给每段挖/填体积，内部换算 increments = cut − fill
/// 输出：平衡区间（含每对就近调配）/ 挖填总量 / 运距矩 / 免费与超运距分解 / 不平衡（弃方·借方）
/// 依据：MassHaulCore（移植自 A1.2 spike，11/11 手算对账）+ 双算法互证（planMoment == curveMoment）
/// </summary>
public static class MassHaulCommands
{
  public static Task<object?> ComputeMassHaulAsync(JsonObject? parameters)
  {
    var stations = ReadDoubles(parameters, "stations");
    var increments = ReadDoubles(parameters, "increments");
    var cuts = ReadDoubles(parameters, "cut");
    var fills = ReadDoubles(parameters, "fill");
    var freeHaul = PluginRuntime.GetOptionalDouble(parameters, "freeHaul") ?? 0;

    if (increments.Count == 0 && (cuts.Count > 0 || fills.Count > 0))
    {
      var n = Math.Max(cuts.Count, fills.Count);
      for (var i = 0; i < n; i++)
      {
        var c = i < cuts.Count ? cuts[i] : 0;
        var f = i < fills.Count ? fills[i] : 0;
        increments.Add(c - f);
      }
    }

    if (stations.Count < 2 || increments.Count < 1 || stations.Count != increments.Count + 1)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"computeMassHaul 需要 stations(n+1) 与 increments(n) 一一对应；或 stations(n+1) + cut[](n) + fill[](n)。当前 stations={stations.Count}, increments={increments.Count}, cut={cuts.Count}, fill={fills.Count}");
    }

    var r = MassHaulCore.Solve(stations, increments, freeHaul);

    var sections = new List<Dictionary<string, object?>>();
    foreach (var s in r.Sections)
    {
      var pairs = new List<Dictionary<string, object?>>();
      foreach (var p in s.Pairs)
      {
        pairs.Add(new Dictionary<string, object?>
        {
          ["volume"] = p.Volume,
          ["distance"] = p.Distance,
          ["cutStation"] = p.CutStation,
          ["fillStation"] = p.FillStation,
          ["overhaulMoment"] = p.OverhaulMoment,
        });
      }

      sections.Add(new Dictionary<string, object?>
      {
        ["startStation"] = s.StartStation,
        ["endStation"] = s.EndStation,
        ["cutVolume"] = s.CutVolume,
        ["fillVolume"] = s.FillVolume,
        ["pairCount"] = s.Pairs.Count,
        ["planMoment"] = s.PlanMoment,
        ["curveMoment"] = s.CurveMoment,
        ["consistent"] = s.Consistent,
        ["avgHaulDistance"] = s.AvgHaulDistance,
        ["maxHaulDistance"] = s.MaxHaulDistance,
        ["overhaulMoment"] = s.OverhaulMoment,
        ["pairs"] = pairs,
      });
    }

    object? unbalanced = r.Unbalanced == null ? null : new Dictionary<string, object?>
    {
      ["fromStation"] = r.Unbalanced.FromStation,
      ["volume"] = r.Unbalanced.Volume,
      ["type"] = r.Unbalanced.Type,
    };

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["sectionCount"] = sections.Count,
      ["sections"] = sections,
      ["totals"] = new Dictionary<string, object?>
      {
        ["cutVolume"] = r.CutVolume,
        ["fillVolume"] = r.FillVolume,
        ["netVolume"] = r.NetVolume,
        ["hauledCutVolume"] = r.HauledCutVolume,
        ["hauledFillVolume"] = r.HauledFillVolume,
        ["haulMoment"] = r.HaulMoment,
        ["freeHaulMoment"] = r.FreeHaulMoment,
        ["overhaulMoment"] = r.OverhaulMoment,
        ["avgHaulDistance"] = r.AvgHaulDistance,
        ["pairCount"] = r.PairCount,
      },
      ["unbalanced"] = unbalanced,
      ["freeHaulDistance"] = r.FreeHaulDistance,
      ["note"] = "planMoment(FIFO 调配) 与 curveMoment(曲线积分) 为两套独立算法，一致=自洽；运距矩单位=体积×距离",
    });
  }

  private static List<double> ReadDoubles(JsonObject? parameters, string name)
  {
    var list = new List<double>();
    if (PluginRuntime.GetParameter(parameters, name) is JsonArray arr)
    {
      foreach (var node in arr)
      {
        if (node == null) continue;
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) list.Add(d);
      }
    }
    return list;
  }
}
