using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// C1（2026-09-28）：**预算化图纸画像** —— 大图纸下让 AI 不必把几万条实体读进上下文。
///
/// 与 analyzeLayer 的区别：analyzeLayer 是"单图层画像"（按需下钻），
/// summarizeDrawing 是"整张图的固定预算概览"：图层维度 + 空间维度 + 全局维度，
/// **输出字节数被 budgetBytes 硬约束**（逐级裁剪：类型→块名→图层尾部→空间网格→仅计数）。
///
/// 裁剪过程会写入 result.trimmed[]，AI 据此知道"看到的是摘要"，需要细节时用
/// getEntitiesByLayer / analyzeLayer 定向下钻。
/// </summary>
public static class SummarizeCommands
{
  public static Task<object?> SummarizeDrawingAsync(JsonObject? parameters)
  {
    var budgetBytes = PluginRuntime.GetOptionalInt(parameters, "budgetBytes") ?? 8000;
    var gridCells = PluginRuntime.GetOptionalInt(parameters, "gridCells") ?? 12;
    var topLayers = PluginRuntime.GetOptionalInt(parameters, "topLayers") ?? 40;
    var topTypes = PluginRuntime.GetOptionalInt(parameters, "topTypes") ?? 6;
    var topBlocks = PluginRuntime.GetOptionalInt(parameters, "topBlocks") ?? 10;
    var includeSpatial = PluginRuntime.GetOptionalBool(parameters, "includeSpatial") ?? true;

    if (budgetBytes < 512) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "budgetBytes must be >= 512");
    if (gridCells < 2 || gridCells > 64) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "gridCells must be in [2, 64]");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
        transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

      var layers = new Dictionary<string, LayerAgg>(StringComparer.OrdinalIgnoreCase);
      var globalTypes = new Dictionary<string, int>(StringComparer.Ordinal);
      double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
      var cellCounts = new Dictionary<(int, int), int>();
      long total = 0, skipped = 0;
      var extentsSeen = 0;

      // 第一遍：聚合（同时用世界坐标范围做空间网格；先收集包围盒再分格）
      var bounds = new List<(double x0, double y0, double x1, double y1, string layer, string type)>(1024);

      foreach (ObjectId id in modelSpace)
      {
        if (id.IsNull || id.IsErased) { skipped++; continue; }
        Entity? ent;
        try { ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { skipped++; continue; }
        if (ent == null || ent.IsErased) { skipped++; continue; }
        total++;

        string type;
        try { type = ent.GetRXClass().Name; } catch { type = ent.GetType().Name; }
        globalTypes.TryGetValue(type, out var gt);
        globalTypes[type] = gt + 1;

        if (!layers.TryGetValue(ent.Layer, out var agg))
        {
          agg = new LayerAgg { Name = ent.Layer };
          layers[ent.Layer] = agg;
        }
        agg.Count++;
        agg.Types.TryGetValue(type, out var tc);
        agg.Types[type] = tc + 1;
        if (ent is BlockReference br)
        {
          agg.Blocks.TryGetValue(br.Name, out var bc);
          agg.Blocks[br.Name] = bc + 1;
        }

        // 包围盒（用于 z 范围 + 空间网格）
        double x0, y0, x1, y1;
        try
        {
          var ext = ent.GeometricExtents;
          x0 = ext.MinPoint.X; y0 = ext.MinPoint.Y; x1 = ext.MaxPoint.X; y1 = ext.MaxPoint.Y;
          if (ext.MinPoint.Z < agg.ZMin) agg.ZMin = ext.MinPoint.Z;
          if (ext.MaxPoint.Z > agg.ZMax) agg.ZMax = ext.MaxPoint.Z;
          agg.HasZ = true;
        }
        catch
        {
          var p = TryGetPoint(ent);
          if (p == null) { skipped++; continue; }
          x0 = x1 = p.Value.X; y0 = y1 = p.Value.Y;
          if (p.Value.Z < agg.ZMin) agg.ZMin = p.Value.Z;
          if (p.Value.Z > agg.ZMax) agg.ZMax = p.Value.Z;
          agg.HasZ = true;
        }

        extentsSeen++;
        if (x0 < minX) minX = x0;
        if (y0 < minY) minY = y0;
        if (x1 > maxX) maxX = x1;
        if (y1 > maxY) maxY = y1;
        bounds.Add((x0, y0, x1, y1, ent.Layer, type));
      }

      // 空间网格（按包围盒中心落格）
      var spatial = new Dictionary<string, object?>();
      if (includeSpatial && extentsSeen > 0 && maxX > minX && maxY > minY)
      {
        var w = (double)(maxX - minX);
        var h = (double)(maxY - minY);
        foreach (var b in bounds)
        {
          var cx = (b.x0 + b.x1) / 2.0;
          var cy = (b.y0 + b.y1) / 2.0;
          var col = (int)Math.Min(gridCells - 1, Math.Max(0, (cx - minX) / w * gridCells));
          var row = (int)Math.Min(gridCells - 1, Math.Max(0, (cy - minY) / h * gridCells));
          var key = (row, col);
          cellCounts.TryGetValue(key, out var c);
          cellCounts[key] = c + 1;
        }

        var rows = new List<object?>();
        for (var r = gridCells - 1; r >= 0; r--)   // 上北下南：行序自上而下
        {
          var rowArr = new List<object?>();
          for (var c = 0; c < gridCells; c++)
          {
            cellCounts.TryGetValue((r, c), out var v);
            rowArr.Add(v);
          }
          rows.Add(rowArr);
        }

        spatial["grid"] = new Dictionary<string, object?>
        {
          ["rows"] = gridCells,
          ["cols"] = gridCells,
          ["order"] = "row0=最北，col0=最西；数值=落在该格的实体数",
          ["counts"] = rows,
        };
        spatial["extents"] = new Dictionary<string, object?>
        {
          ["minX"] = Math.Round(minX, 3), ["minY"] = Math.Round(minY, 3),
          ["maxX"] = Math.Round(maxX, 3), ["maxY"] = Math.Round(maxY, 3),
        };
      }

      // 图层属性（开关/冻结/颜色）
      var layerProps = ReadLayerProps(database, transaction);

      // 组装（按实体数降序，先给足细节，交给裁剪器压缩）
      var ordered = layers.Values.OrderByDescending(l => l.Count).ToList();
      var layerList = new List<object?>();
      foreach (var agg in ordered.Take(Math.Max(1, topLayers)))
      {
        var d = new Dictionary<string, object?>
        {
          ["name"] = agg.Name,
          ["count"] = agg.Count,
          ["types"] = agg.Types.OrderByDescending(kv => kv.Value).Take(Math.Max(1, topTypes))
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };
        if (agg.Blocks.Count > 0)
        {
          d["blocks"] = agg.Blocks.OrderByDescending(kv => kv.Value).Take(Math.Max(1, topBlocks))
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        if (agg.HasZ) d["zRange"] = new List<object?> { Math.Round(agg.ZMin, 2), Math.Round(agg.ZMax, 2) };
        if (layerProps.TryGetValue(agg.Name, out var lp)) d["state"] = lp;
        layerList.Add(d);
      }

      var result = new Dictionary<string, object?>
      {
        ["total"] = total,
        ["entityTypes"] = globalTypes.OrderByDescending(kv => kv.Value).Take(12)
          .ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["layerCount"] = layers.Count,
        ["layers"] = layerList,
        ["layersOmitted"] = Math.Max(0, layers.Count - layerList.Count),
        ["spatial"] = spatial,
        ["skipped"] = skipped,
        ["budgetBytes"] = budgetBytes,
        ["trimmed"] = new List<object?>(),
        ["note"] = "这是**预算化摘要**（非全量）。需要细节：analyzeLayer{layer} / getEntitiesByLayer{layer,limit,offset}",
      };

      var trimmed = (List<object?>)result["trimmed"]!;
      var used = Measure(result);
      if (used > budgetBytes)
      {
        // 裁剪第 1 级：每层类型/块名只留 Top3
        foreach (var l in layerList)
        {
          if (l is not Dictionary<string, object?> d) continue;
          if (d.TryGetValue("types", out var tNode) && tNode is Dictionary<string, object?> t && t.Count > 3)
            d["types"] = t.OrderByDescending(kv => Convert.ToInt32(kv.Value)).Take(3).ToDictionary(kv => kv.Key, kv => kv.Value);
          if (d.TryGetValue("blocks", out var bNode) && bNode is Dictionary<string, object?> b && b.Count > 3)
            d["blocks"] = b.OrderByDescending(kv => Convert.ToInt32(kv.Value)).Take(3).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        used = Measure(result);
        if (used > budgetBytes) trimmed.Add("types/blocks → Top3");
      }
      if (used > budgetBytes)
      {
        // 裁剪第 2 级：空间网格降档
        while (used > budgetBytes && gridCells > 4 && spatial.Count > 0)
        {
          gridCells = Math.Max(4, gridCells / 2);
          spatial["grid"] = RebuildCoarseGrid(bounds, minX, minY, maxX, maxY, gridCells);
          used = Measure(result);
        }
        if (used > budgetBytes) trimmed.Add("spatial grid → " + gridCells + "x" + gridCells);
      }
      if (used > budgetBytes)
      {
        // 裁剪第 3 级：砍图层尾部（保留实体数 >= 阈值）
        while (used > budgetBytes && layerList.Count > 6)
        {
          var cut = Math.Max(1, layerList.Count / 4);
          layerList.RemoveRange(layerList.Count - cut, cut);
          result["layersOmitted"] = layers.Count - layerList.Count;
          used = Measure(result);
        }
        if (used > budgetBytes) trimmed.Add("layers → Top" + layerList.Count);
      }
      if (used > budgetBytes)
      {
        // 裁剪第 4 级：图层只留 name+count
        foreach (var l in layerList)
        {
          if (l is not Dictionary<string, object?> d) continue;
          d.Remove("types"); d.Remove("blocks"); d.Remove("zRange"); d.Remove("state");
        }
        used = Measure(result);
        if (used > budgetBytes) trimmed.Add("layers → name+count");
      }
      if (used > budgetBytes)
      {
        // 裁剪第 5 级：去掉空间网格，仅保留全局计数 + Top10 图层计数
        result.Remove("spatial");
        used = Measure(result);
        if (used > budgetBytes) trimmed.Add("spatial dropped");
      }
      if (used > budgetBytes && layerList.Count > 10)
      {
        layerList.RemoveRange(10, layerList.Count - 10);
        result["layersOmitted"] = layers.Count - layerList.Count;
        used = Measure(result);
        trimmed.Add("layers → Top10");
      }
      if (used > budgetBytes)
      {
        result.Remove("layers");
        used = Measure(result);
        trimmed.Add("layers dropped (only totals)");
      }
      if (used > budgetBytes && result["entityTypes"] is Dictionary<string, object?> et && et.Count > 5)
      {
        result["entityTypes"] = et.OrderByDescending(kv => Convert.ToInt32(kv.Value)).Take(5)
          .ToDictionary(kv => kv.Key, kv => kv.Value);
        used = Measure(result);
        trimmed.Add("entityTypes → Top5");
      }

      result["usedBytes"] = used;
      result["withinBudget"] = used <= budgetBytes;
      return (object?)result;
    });
  }

  private static List<object?> RebuildCoarseGrid(
    List<(double x0, double y0, double x1, double y1, string layer, string type)> bounds,
    double minX, double minY, double maxX, double maxY, int cells)
  {
    var counts = new Dictionary<(int, int), int>();
    var w = maxX - minX;
    var h = maxY - minY;
    if (w <= 0 || h <= 0) return new List<object?>();
    foreach (var b in bounds)
    {
      var col = (int)Math.Min(cells - 1, Math.Max(0, ((b.x0 + b.x1) / 2 - minX) / w * cells));
      var row = (int)Math.Min(cells - 1, Math.Max(0, ((b.y0 + b.y1) / 2 - minY) / h * cells));
      counts.TryGetValue((row, col), out var c);
      counts[(row, col)] = c + 1;
    }
    var rows = new List<object?>();
    for (var r = cells - 1; r >= 0; r--)
    {
      var rowArr = new List<object?>();
      for (var c = 0; c < cells; c++) { counts.TryGetValue((r, c), out var v); rowArr.Add(v); }
      rows.Add(rowArr);
    }
    return rows;
  }

  private static Dictionary<string, object?> ReadLayerProps(Database database, Transaction transaction)
  {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    try
    {
      var table = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
      foreach (ObjectId id in table)
      {
        try
        {
          var rec = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
          var flags = new List<string>();
          if (rec.IsOff) flags.Add("off");
          if (rec.IsFrozen) flags.Add("frozen");
          if (rec.IsLocked) flags.Add("locked");
          if (!rec.IsPlottable) flags.Add("noplot");
          if (flags.Count > 0) result[rec.Name] = string.Join("+", flags);
        }
        catch { }
      }
    }
    catch { }
    return result;
  }

  private static Point3d? TryGetPoint(Entity ent)
  {
    try
    {
      switch (ent)
      {
        case DBPoint p: return p.Position;
        case DBText t: return t.Position;
        case MText m: return m.Location;
        case BlockReference b: return b.Position;
        case Curve c: return c.StartPoint;
        default: return null;
      }
    }
    catch { return null; }
  }

  private static readonly JsonSerializerOptions SizeOptions = new()
  {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  private static long Measure(object value)
  {
    var json = JsonSerializer.Serialize(value, SizeOptions);
    return Encoding.UTF8.GetByteCount(json);
  }

  private sealed class LayerAgg
  {
    public string Name = string.Empty;
    public int Count;
    public double ZMin = double.MaxValue;
    public double ZMax = double.MinValue;
    public bool HasZ;
    public readonly Dictionary<string, int> Types = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Blocks = new(StringComparer.Ordinal);
  }
}
