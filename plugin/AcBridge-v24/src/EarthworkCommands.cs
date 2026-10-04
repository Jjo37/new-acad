using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DMcpPlugin;

/// <summary>
/// A1 收口（2026-09-28）：**廊道土方（网格法 + 桩号归属）**。
///
/// 为什么不用"逐桩号偏移射线积分"（经典平均断面法）：
///   ① C3D 的 SampleLine/Section 对廊道曲面在远处会**外插**（SectionPoints 给出 ±30 的假设计标高）；
///   ② 弯道处一条偏移射线会**反复穿过廊道曲面**（实测 station 247 的射线一直打到 +55），
///      按桩号逐条积分 → 同一块面积被重复计入（实测填方虚高 33%）；
///   ③ 烘焙 TIN 的真实足迹是 ~13000 m²（≈29m 平均带宽），不是要素线窗口的 12.2m。
///
/// 正解：**在网格上采样两个曲面，按单元格中心"沿廊道桩号"归类到桩号区间**。
///   - 总量与 C3D 曲面体积法（TinVolumeSurface / 网格法）必然一致（同口径、无重复计数）
///   - 同时给出逐桩号挖填方，供土方调配（运距/分区）使用
///
/// 实测（First Street）：tin_volume 挖 1235.89 / 填 18394.45；grid 2m 挖 1227.63 / 填 18391.65。
/// </summary>
public static class EarthworkCommands
{
  public static Task<object?> ComputeCorridorEarthworkAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baseName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var designName = PluginRuntime.GetRequiredString(parameters, "designSurface");
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval") ?? 5.0;          // 桩号区间
    var gridStep = PluginRuntime.GetOptionalDouble(parameters, "gridStep") ?? 2.0;          // 网格步长
    var stationStartParam = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEndParam = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var useRegionRange = PluginRuntime.GetOptionalBool(parameters, "useRegionRange") ?? true;
    var maxOffset = PluginRuntime.GetOptionalDouble(parameters, "maxOffset");               // 可选：限制横向范围（过滤离廊道过远的单元）

    if (interval <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "interval must be > 0");
    if (gridStep <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "gridStep must be > 0");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = corridor.Baselines.Cast<Baseline>().FirstOrDefault()
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor '{corridorName}' has no baseline.");
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, baseline.AlignmentId, OpenMode.ForRead);

      var baseRaw = ResolveSurface(civilDoc, transaction, corridor, baseName);
      var designRaw = ResolveSurface(civilDoc, transaction, corridor, designName);

      var tempIds = new List<ObjectId>();
      try
      {
        var baseSurface = IsCorridorSurface(corridor, baseRaw) ? BakeCorridorSurface(transaction, corridor, baseRaw, tempIds) : baseRaw;
        var designSurface = IsCorridorSurface(corridor, designRaw) ? BakeCorridorSurface(transaction, corridor, designRaw, tempIds) : designRaw;

        var baseExtents = GetExtents(baseSurface, baseRaw.Name);
        var designExtents = GetExtents(designSurface, designRaw.Name);
        var minX = Math.Max(baseExtents.MinPoint.X, designExtents.MinPoint.X);
        var maxX = Math.Min(baseExtents.MaxPoint.X, designExtents.MaxPoint.X);
        var minY = Math.Max(baseExtents.MinPoint.Y, designExtents.MinPoint.Y);
        var maxY = Math.Min(baseExtents.MaxPoint.Y, designExtents.MaxPoint.Y);

        var (regionStart, regionEnd) = GetRegionRange(baseline);
        var stationStart = stationStartParam ?? (useRegionRange ? regionStart : baseline.StartStation);
        var stationEnd = stationEndParam ?? (useRegionRange ? regionEnd : baseline.EndStation);

        var bands = new SortedDictionary<double, Band>();
        double cutVolume = 0, fillVolume = 0, cutArea = 0, fillArea = 0;
        long cellsUsed = 0, cellsSkipped = 0;
        var cellArea = gridStep * gridStep;
        var minOffsetSeen = double.MaxValue;
        var maxOffsetSeen = double.MinValue;

        for (var x = minX; x <= maxX; x += gridStep)
        {
          for (var y = minY; y <= maxY; y += gridStep)
          {
            double baseElev, designElev;
            try { baseElev = baseSurface.FindElevationAtXY(x, y); } catch { cellsSkipped++; continue; }
            try { designElev = designSurface.FindElevationAtXY(x, y); } catch { cellsSkipped++; continue; }
            if (double.IsNaN(baseElev) || double.IsNaN(designElev)) { cellsSkipped++; continue; }

            double station, offset;
            try
            {
              var point = new Point3d(x, y, 0.0);
              var closest = alignment.GetClosestPointTo(point, false);
              station = alignment.GetDistAtPoint(closest);
              offset = OffsetOf(alignment, station, point, closest);
            }
            catch { cellsSkipped++; continue; }

            if (station < stationStart || station > stationEnd) { cellsSkipped++; continue; }
            if (maxOffset.HasValue && Math.Abs(offset) > maxOffset.Value) { cellsSkipped++; continue; }

            if (offset < minOffsetSeen) minOffsetSeen = offset;
            if (offset > maxOffsetSeen) maxOffsetSeen = offset;

            var bandKey = Math.Floor(station / interval) * interval;
            if (!bands.TryGetValue(bandKey, out var band))
            {
              band = new Band { StationStart = bandKey, StationEnd = bandKey + interval };
              bands[bandKey] = band;
            }

            var diff = baseElev - designElev;   // >0 → 挖
            cellsUsed++;
            if (diff > 0) { cutVolume += diff * cellArea; cutArea += cellArea; band.Cut += diff * cellArea; band.CutArea += cellArea; }
            else { fillVolume += -diff * cellArea; fillArea += cellArea; band.Fill += -diff * cellArea; band.FillArea += cellArea; }
            band.MinOffset = Math.Min(band.MinOffset, offset);
            band.MaxOffset = Math.Max(band.MaxOffset, offset);
            band.Cells++;
          }
        }

        var series = bands.Values.Select(b => (object)new Dictionary<string, object?>
        {
          ["stationStart"] = b.StationStart,
          ["stationEnd"] = b.StationEnd,
          ["stationMid"] = (b.StationStart + b.StationEnd) / 2.0,
          ["cutVolume"] = b.Cut,
          ["fillVolume"] = b.Fill,
          ["netVolume"] = b.Fill - b.Cut,
          ["cutArea"] = b.CutArea,
          ["fillArea"] = b.FillArea,
          ["minOffset"] = b.MinOffset == double.MaxValue ? (double?)null : b.MinOffset,
          ["maxOffset"] = b.MaxOffset == double.MinValue ? (double?)null : b.MaxOffset,
          ["cells"] = b.Cells,
        }).ToList();

        return new Dictionary<string, object?>
        {
          ["corridorName"] = corridor.Name,
          ["baseSurface"] = baseRaw.Name,
          ["designSurface"] = designRaw.Name,
          ["alignmentName"] = alignment.Name,
          ["stationStart"] = stationStart,
          ["stationEnd"] = stationEnd,
          ["interval"] = interval,
          ["gridStep"] = gridStep,
          ["cutVolume"] = cutVolume,
          ["fillVolume"] = fillVolume,
          ["netVolume"] = fillVolume - cutVolume,
          ["cutArea"] = cutArea,
          ["fillArea"] = fillArea,
          ["totalArea"] = cutArea + fillArea,
          ["cellsUsed"] = cellsUsed,
          ["cellsSkipped"] = cellsSkipped,
          ["minOffset"] = minOffsetSeen == double.MaxValue ? (double?)null : minOffsetSeen,
          ["maxOffsetSeen"] = maxOffsetSeen == double.MinValue ? (double?)null : maxOffsetSeen,
          ["method"] = "grid_station_banded",
          ["note"] = "网格单元按'单元格中心沿廊道桩号'归入桩号区间；总量与曲面体积法同口径（无重复计数）。廊道曲面自动烘焙成临时 TIN",
          ["stations"] = series,
        };
      }
      finally
      {
        EraseTemps(transaction, tempIds);
      }
    });
  }

  private sealed class Band
  {
    public double StationStart;
    public double StationEnd;
    public double Cut;
    public double Fill;
    public double CutArea;
    public double FillArea;
    public double MinOffset = double.MaxValue;
    public double MaxOffset = double.MinValue;
    public long Cells;
  }

  /// <summary>带符号横断面偏距（左负右正，Civil 3D 约定）</summary>
  private static double OffsetOf(Alignment alignment, double station, Point3d point, Point3d closest)
  {
    Point3d a, b;
    try
    {
      double ax = 0, ay = 0, bx = 0, by = 0;
      alignment.PointLocation(station, 0.0, ref ax, ref ay);
      alignment.PointLocation(station + 1.0, 0.0, ref bx, ref by);
      a = new Point3d(ax, ay, 0.0);
      b = new Point3d(bx, by, 0.0);
    }
    catch { return point.DistanceTo(closest); }

    var dx = b.X - a.X; var dy = b.Y - a.Y;
    var len = Math.Sqrt(dx * dx + dy * dy);
    if (len < 1e-9) return point.DistanceTo(closest);
    dx /= len; dy /= len;
    var vx = point.X - closest.X; var vy = point.Y - closest.Y;
    var cross = dx * vy - dy * vx;          // >0 → 左侧
    var dist = Math.Sqrt(vx * vx + vy * vy);
    return cross > 0 ? -dist : dist;        // 左负右正
  }

  private static Extents3d GetExtents(CivilSurface surface, string name)
  {
    var extents = CivilObjectUtils.GetPropertyValue<Extents3d?>(surface, "GeometricExtents");
    if (extents == null)
      throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to get extents for surface '{name}'.");
    return extents.Value;
  }

  private static CivilSurface ResolveSurface(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction,
    Corridor corridor, string name)
  {
    foreach (CorridorSurface corridorSurface in corridor.CorridorSurfaces)
    {
      if (!string.Equals(corridorSurface.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
      if (corridorSurface.SurfaceId == ObjectId.Null)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor surface '{name}' has no built surface ObjectId.");
      return CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, corridorSurface.SurfaceId, OpenMode.ForRead);
    }
    return CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
  }

  private static bool IsCorridorSurface(Corridor corridor, CivilSurface surface)
  {
    foreach (CorridorSurface corridorSurface in corridor.CorridorSurfaces)
    {
      if (corridorSurface.SurfaceId == surface.ObjectId) return true;
    }
    return false;
  }

  /// <summary>廊道曲面必须烘焙成普通 TIN 才能按高程采样</summary>
  private static CivilSurface BakeCorridorSurface(Transaction transaction, Corridor corridor, CivilSurface surface, List<ObjectId> tempIds)
  {
    CorridorSurface? definition = null;
    foreach (CorridorSurface candidate in corridor.CorridorSurfaces)
    {
      if (candidate.SurfaceId == surface.ObjectId) { definition = candidate; break; }
    }
    if (definition == null)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface matching '{surface.Name}' was not found on corridor '{corridor.Name}'.");

    var bakedName = surface.Name + "__baked_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    var bakedId = TinSurface.CreateFromCorridorSurface(bakedName, definition);
    tempIds.Add(bakedId);
    return CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, bakedId, OpenMode.ForRead);
  }

  private static (double? start, double? end) GetRegionRange(Baseline baseline)
  {
    try
    {
      var regions = baseline.BaselineRegions.Cast<BaselineRegion>().ToList();
      if (regions.Count == 0) return (baseline.StartStation, baseline.EndStation);
      return (regions.Min(r => r.StartStation), regions.Max(r => r.EndStation));
    }
    catch { return (baseline.StartStation, baseline.EndStation); }
  }

  private static void EraseTemps(Transaction transaction, List<ObjectId> tempIds)
  {
    foreach (var id in tempIds)
    {
      try
      {
        if (id.IsNull || id.IsErased) continue;
        transaction.GetObject(id, OpenMode.ForWrite, false)?.Erase();
      }
      catch { /* 清理失败不致命 */ }
    }
    tempIds.Clear();
  }
}
