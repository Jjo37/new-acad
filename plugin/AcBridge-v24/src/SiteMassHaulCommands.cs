using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// A1×A2 联动（2026-09-28）：**从两张曲面直接出逐桩土方 + 调配方案**。
///
/// 链路：optimizeGrading 生成设计面 → 本方法按桩号切分挖填 → MassHaulCore 出调配（就近/免费运距/超运/弃借）。
/// 桩号来源：给 alignmentName 沿路线投影；否则沿 X/Y 轴（场地轴线）计量。
/// 纯只读：不写图面对象。
/// </summary>
public static class SiteMassHaulCommands
{
  public static Task<object?> ComputeSiteMassHaulAsync(JsonObject? parameters)
  {
    var baseName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var designName = PluginRuntime.GetRequiredString(parameters, "designSurface");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var axis = (PluginRuntime.GetOptionalString(parameters, "axis") ?? "x").ToLowerInvariant();
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval") ?? 20.0;
    var gridStep = PluginRuntime.GetOptionalDouble(parameters, "gridStep") ?? 4.0;
    var freeHaul = PluginRuntime.GetOptionalDouble(parameters, "freeHaul") ?? 0.0;
    var topPairs = PluginRuntime.GetOptionalInt(parameters, "topPairs") ?? 12;
    var regionNode = parameters?["region"] as JsonObject;

    if (interval <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "interval must be > 0");
    if (gridStep <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "gridStep must be > 0");
    if (axis != "x" && axis != "y") throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "axis must be 'x' or 'y'");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = FindSurface(civilDoc, transaction, baseName);
      var designSurface = FindSurface(civilDoc, transaction, designName);

      Alignment? alignment = null;
      if (!string.IsNullOrWhiteSpace(alignmentName))
      {
        alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
      }

      var be = GetExtents(baseSurface, baseName);
      var de = GetExtents(designSurface, designName);
      double minX = Math.Max(be.MinPoint.X, de.MinPoint.X), maxX = Math.Min(be.MaxPoint.X, de.MaxPoint.X);
      double minY = Math.Max(be.MinPoint.Y, de.MinPoint.Y), maxY = Math.Min(be.MaxPoint.Y, de.MaxPoint.Y);
      if (regionNode != null)
      {
        minX = Math.Max(minX, PluginRuntime.GetRequiredDoubleFromNode(regionNode["minX"], "region.minX"));
        minY = Math.Max(minY, PluginRuntime.GetRequiredDoubleFromNode(regionNode["minY"], "region.minY"));
        maxX = Math.Min(maxX, PluginRuntime.GetRequiredDoubleFromNode(regionNode["maxX"], "region.maxX"));
        maxY = Math.Min(maxY, PluginRuntime.GetRequiredDoubleFromNode(regionNode["maxY"], "region.maxY"));
      }
      if (maxX <= minX || maxY <= minY)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"base/design surfaces do not overlap (x[{minX:0.##},{maxX:0.##}] y[{minY:0.##},{maxY:0.##}])");

      var cellArea = gridStep * gridStep;
      var bands = new SortedDictionary<double, double[]>();     // key=bandStart → [cut, fill]
      double totalCut = 0, totalFill = 0;
      long cells = 0, skipped = 0;
      var minStation = double.MaxValue; var maxStation = double.MinValue;

      for (var x = minX; x <= maxX; x += gridStep)
      {
        for (var y = minY; y <= maxY; y += gridStep)
        {
          double zb, zd;
          try { zb = baseSurface.FindElevationAtXY(x, y); } catch { skipped++; continue; }
          try { zd = designSurface.FindElevationAtXY(x, y); } catch { skipped++; continue; }
          if (double.IsNaN(zb) || double.IsNaN(zd)) { skipped++; continue; }

          double station;
          if (alignment != null)
          {
            try
            {
              var p = new Point3d(x, y, 0.0);
              station = alignment.GetDistAtPoint(alignment.GetClosestPointTo(p, false));
            }
            catch { skipped++; continue; }
          }
          else
          {
            station = (axis == "y" ? y : x) - (axis == "y" ? minY : minX);
          }

          var cut = zb - zd;                     // >0 → 挖
          var cutVol = cut > 0 ? cut * cellArea : 0.0;
          var fillVol = cut < 0 ? -cut * cellArea : 0.0;
          totalCut += cutVol; totalFill += fillVol;
          cells++;
          if (station < minStation) minStation = station;
          if (station > maxStation) maxStation = station;

          var bandKey = Math.Floor(station / interval) * interval;
          if (!bands.TryGetValue(bandKey, out var acc)) { acc = new double[2]; bands[bandKey] = acc; }
          acc[0] += cutVol; acc[1] += fillVol;
        }
      }

      if (bands.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"too few station bands ({bands.Count}); lower interval or check surface overlap");

      // 补齐连续桩号（空档填 0），保证 stations(n+1) 与 increments(n) 对应
      var bandKeys = bands.Keys.ToList();
      var stations = new List<double>();
      var increments = new List<double>();
      var bandRows = new List<Dictionary<string, object?>>();
      double bandCutSum = 0, bandFillSum = 0;
      var cur = bandKeys[0];
      while (cur <= bandKeys[^1] + 1e-9)
      {
        bands.TryGetValue(cur, out var acc);
        var cut = acc?[0] ?? 0.0;
        var fill = acc?[1] ?? 0.0;
        bandCutSum += cut; bandFillSum += fill;
        stations.Add(cur);
        increments.Add(cut - fill);
        bandRows.Add(new Dictionary<string, object?>
        {
          ["stationStart"] = cur,
          ["stationEnd"] = cur + interval,
          ["cutVolume"] = Math.Round(cut, 4),
          ["fillVolume"] = Math.Round(fill, 4),
          ["netVolume"] = Math.Round(cut - fill, 4),
        });
        cur += interval;
      }
      stations.Add(cur);                       // 末边界

      // 场地剖面常为开区间（净挖/净填）→ 开启开区间配对，让就近调配与弃/借方同时出
      var plan = MassHaulCore.Solve(stations, increments, freeHaul, pairOpenSections: true);

      var sections = new List<Dictionary<string, object?>>();
      foreach (var s in plan.Sections)
      {
        var pairs = s.Pairs
          .OrderByDescending(p => p.Volume)
          .Take(Math.Max(1, topPairs))
          .Select(p => (object)new Dictionary<string, object?>
          {
            ["volume"] = Math.Round(p.Volume, 3),
            ["distance"] = Math.Round(p.Distance, 2),
            ["cutStation"] = Math.Round(p.CutStation, 2),
            ["fillStation"] = Math.Round(p.FillStation, 2),
            ["overhaulMoment"] = Math.Round(p.OverhaulMoment, 3),
          }).ToList();
        sections.Add(new Dictionary<string, object?>
        {
          ["startStation"] = s.StartStation,
          ["endStation"] = s.EndStation,
          ["cutVolume"] = Math.Round(s.CutVolume, 3),
          ["fillVolume"] = Math.Round(s.FillVolume, 3),
          ["pairCount"] = s.Pairs.Count,
          ["planMoment"] = Math.Round(s.PlanMoment, 3),
          ["curveMoment"] = Math.Round(s.CurveMoment, 3),
          ["consistent"] = s.Consistent,
          ["isOpen"] = s.IsOpen,
          ["avgHaulDistance"] = Math.Round(s.AvgHaulDistance, 2),
          ["maxHaulDistance"] = Math.Round(s.MaxHaulDistance, 2),
          ["overhaulMoment"] = Math.Round(s.OverhaulMoment, 3),
          ["topPairs"] = pairs,
        });
      }

      var netFromBands = totalFill - totalCut;
      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseName,
        ["designSurface"] = designName,
        ["stationSource"] = alignment != null ? ("alignment:" + alignment.Name) : ("axis:" + axis),
        ["interval"] = interval,
        ["gridStep"] = gridStep,
        ["cellsUsed"] = cells,
        ["cellsSkipped"] = skipped,
        ["stationRange"] = new List<object?> { Math.Round(minStation, 2), Math.Round(maxStation, 2) },
        ["bandCount"] = bandRows.Count,
        ["bands"] = bandRows,
        ["surfaceVolumes"] = new Dictionary<string, object?>
        {
          ["cutVolume"] = Math.Round(totalCut, 4),
          ["fillVolume"] = Math.Round(totalFill, 4),
          ["netVolume"] = Math.Round(netFromBands, 4),
        },
        ["plan"] = new Dictionary<string, object?>
        {
          ["sectionCount"] = sections.Count,
          ["sections"] = sections,
          ["totals"] = new Dictionary<string, object?>
          {
            ["cutVolume"] = Math.Round(plan.CutVolume, 4),
            ["fillVolume"] = Math.Round(plan.FillVolume, 4),
            ["netVolume"] = Math.Round(plan.NetVolume, 4),
            ["hauledCutVolume"] = Math.Round(plan.HauledCutVolume, 4),
            ["hauledFillVolume"] = Math.Round(plan.HauledFillVolume, 4),
            ["haulMoment"] = Math.Round(plan.HaulMoment, 4),
            ["freeHaulMoment"] = Math.Round(plan.FreeHaulMoment, 4),
            ["overhaulMoment"] = Math.Round(plan.OverhaulMoment, 4),
            ["avgHaulDistance"] = Math.Round(plan.AvgHaulDistance, 3),
            ["pairCount"] = plan.PairCount,
          },
          ["unbalanced"] = plan.Unbalanced == null ? null : new Dictionary<string, object?>
          {
            ["fromStation"] = Math.Round(plan.Unbalanced.FromStation, 2),
            ["volume"] = Math.Round(plan.Unbalanced.Volume, 4),
            ["type"] = plan.Unbalanced.Type,
          },
          ["freeHaulDistance"] = plan.FreeHaulDistance,
        },
        ["consistency"] = new Dictionary<string, object?>
        {
          ["bandsSumEqualsSurfaceVolumes"] = Math.Abs(bandCutSum - totalCut) < Math.Max(0.01, totalCut * 1e-9)
                                          && Math.Abs(bandFillSum - totalFill) < Math.Max(0.01, totalFill * 1e-9),
          ["bandCutSum"] = Math.Round(bandCutSum, 4),
          ["bandFillSum"] = Math.Round(bandFillSum, 4),
          ["planVsCurveMomentOk"] = plan.Sections.Where(s => !s.IsOpen).All(s => s.Consistent),
          ["openSectionCount"] = plan.Sections.Count(s => s.IsOpen),
          ["closedSectionCount"] = plan.Sections.Count(s => !s.IsOpen),
          ["note"] = "planMoment==curveMoment 恒等式仅对闭合区间成立；开区间（净挖/净填）已用等面积平衡线配对，其一致性不参与该校验",
          ["coreOffsetWithinBands"] = new Dictionary<string, object?>
          {
            ["cut"] = Math.Round(totalCut - plan.CutVolume, 4),
            ["fill"] = Math.Round(totalFill - plan.FillVolume, 4),
            ["note"] = "调配核按‘段内先挖填抵消’报净量；差值 = 同一桩号段内被就地消纳的挖填量（毛量见 bandCutSum/bandFillSum）",
          },
        },
        ["method"] = "surface_grid_to_station_bands_then_masshaul",
        ["note"] = "桩号来源可为路线投影或 X/Y 轴；planMoment 与 curveMoment 两套独立算法互证；运距矩单位=体积×距离",
      };
    });
  }

  private static CivilSurface FindSurface(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string name)
  {
    try
    {
      return CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"Surface '{name}' not usable for mass haul ({ex.Message}). 廊道曲面请先用 bakeCorridorSurface 烘焙，或用 computeCorridorEarthwork。");
    }
  }

  private static Extents3d GetExtents(CivilSurface surface, string name)
  {
    var extents = CivilObjectUtils.GetPropertyValue<Extents3d?>(surface, "GeometricExtents");
    if (extents == null)
      throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to get extents for surface '{name}'.");
    return extents.Value;
  }
}
