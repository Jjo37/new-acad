using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// D2.x（2026-09-28）：编辑类操作的**几何自研版**（不再走 LISP 命令通道）。
/// 现有 `trimEntity`/`extendEntity`/`overkillEntities` 都是 SendStringToExecute 命令壳：
/// 异步、无返回值、无法并行/断言。这里用真 API 实现，**同步返回结果**，可验证。
///   - Trim：Curve.IntersectWith + Curve.GetSplitCurves
///   - Extend：Curve.IntersectWith(ExtendThis) + Curve.Extend(bool, Point3d)
///   - Overkill：几何指纹去重（Line/Arc/Circle/Polyline）
/// </summary>
public static partial class EditCommands
{
  private static ObjectId ToObjectId(Database database, string handle)
  {
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var value))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "无效 handle：" + handle);
    var h = new Handle(value);
    if (!database.TryGetObjectId(h, out var id) || id.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "找不到实体：" + handle);
    return id;
  }

  private static BlockTableRecord ModelSpace(Transaction transaction, Database database)
  {
    var bt = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    return CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
  }

  // ── TRIM：按交点切开目标曲线，删除包含"拾取点"的那一段 ────────────────
  public static Task<object?> TrimEntityGeoAsync(JsonObject? parameters)
  {
    var targetHandle = PluginRuntime.GetRequiredString(parameters, "targetHandle");
    var cuttingHandle = PluginRuntime.GetRequiredString(parameters, "cuttingHandle");
    var pickX = PluginRuntime.GetRequiredDouble(parameters, "pickX");
    var pickY = PluginRuntime.GetRequiredDouble(parameters, "pickY");
    var pickZ = PluginRuntime.GetOptionalDouble(parameters, "pickZ") ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      var target = CivilObjectUtils.GetRequiredObject<Curve>(transaction, ToObjectId(database, targetHandle), OpenMode.ForWrite);
      var cutting = CivilObjectUtils.GetRequiredObject<Entity>(transaction, ToObjectId(database, cuttingHandle), OpenMode.ForRead);

      var points = new Point3dCollection();
      target.IntersectWith(cutting, Intersect.OnBothOperands, points, System.IntPtr.Zero, System.IntPtr.Zero);
      if (points.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "目标曲线与剪切边没有交点，无法修剪。");

      var pick = new Point3d(pickX, pickY, pickZ);
      // 用最近的交点作为切开点（多点相交时按拾取点就近取一处）
      var cut = points[0];
      var best = double.MaxValue;
      foreach (Point3d p in points)
      {
        var d = p.DistanceTo(pick);
        if (d < best) { best = d; cut = p; }
      }

      var splitInput = new Point3dCollection { cut };
      var pieces = target.GetSplitCurves(splitInput);

      // 选出"离拾取点最近"的那一段作为被删除段，其余保留
      Curve? toRemove = null;
      var minD = double.MaxValue;
      var kept = new List<Curve>();
      foreach (DBObject obj in pieces)
      {
        if (obj is not Curve piece) continue;
        var d = piece.GetClosestPointTo(pick, false).DistanceTo(pick);
        if (d < minD) { minD = d; toRemove = piece; }
        kept.Add(piece);
      }
      if (toRemove == null || kept.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INTERNAL_ERROR", "切开失败（未产生足够的分段）。");
      kept.Remove(toRemove);

      var keptHandles = new List<string>();
      foreach (var piece in kept)
      {
        var id = modelSpace.AppendEntity(piece);
        transaction.AddNewlyCreatedDBObject(piece, true);
        keptHandles.Add(CivilObjectUtils.GetHandle(piece));
      }
      target.Erase();

      return new Dictionary<string, object?>
      {
        ["target"] = targetHandle,
        ["cutting"] = cuttingHandle,
        ["cutPoint"] = new[] { cut.X, cut.Y, cut.Z },
        ["intersectionCount"] = points.Count,
        ["keptHandles"] = keptHandles,
        ["removedSegments"] = 1,
        ["method"] = "geometry",
      };
    });
  }

  // ── EXTEND：把目标曲线延伸到与边界相交（就近一端） ────────────────────
  public static Task<object?> ExtendEntityGeoAsync(JsonObject? parameters)
  {
    var targetHandle = PluginRuntime.GetRequiredString(parameters, "targetHandle");
    var boundaryHandle = PluginRuntime.GetRequiredString(parameters, "boundaryHandle");
    var extendStartParam = PluginRuntime.GetOptionalBool(parameters, "extendStart");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = CivilObjectUtils.GetRequiredObject<Curve>(transaction, ToObjectId(database, targetHandle), OpenMode.ForWrite);
      var boundary = CivilObjectUtils.GetRequiredObject<Entity>(transaction, ToObjectId(database, boundaryHandle), OpenMode.ForRead);

      var points = new Point3dCollection();
      target.IntersectWith(boundary, Intersect.ExtendThis, points, System.IntPtr.Zero, System.IntPtr.Zero);
      if (points.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "目标曲线延伸后仍与边界无交点。");

      var start = target.StartPoint;
      var end = target.EndPoint;
      // 选取离"将被延伸的那一端"最近的交点
      Point3d bestPoint = points[0];
      var bestDist = double.MaxValue;
      foreach (Point3d p in points)
      {
        var d = System.Math.Min(p.DistanceTo(start), p.DistanceTo(end));
        if (d < bestDist) { bestDist = d; bestPoint = p; }
      }

      var extendStart = extendStartParam ?? (bestPoint.DistanceTo(start) < bestPoint.DistanceTo(end));
      target.Extend(extendStart, bestPoint);

      return new Dictionary<string, object?>
      {
        ["target"] = targetHandle,
        ["boundary"] = boundaryHandle,
        ["extendedEnd"] = extendStart ? "start" : "end",
        ["toPoint"] = new[] { bestPoint.X, bestPoint.Y, bestPoint.Z },
        ["newStartPoint"] = new[] { target.StartPoint.X, target.StartPoint.Y, target.StartPoint.Z },
        ["newEndPoint"] = new[] { target.EndPoint.X, target.EndPoint.Y, target.EndPoint.Z },
        ["method"] = "geometry",
      };
    });
  }

  // ── OVERKILL：按几何指纹去重（保留第一个） ────────────────────────────
  public static Task<object?> OverkillGeoAsync(JsonObject? parameters)
  {
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      var tol = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 1e-4;
      var groups = new Dictionary<string, List<ObjectId>>();

      foreach (ObjectId id in modelSpace)
      {
        if (id.IsNull || id.IsErased) continue;
        DBObject obj;
        try { obj = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
        var key = Fingerprint(obj as Curve, tol);
        if (key == null) continue;
        if (!groups.TryGetValue(key, out var list)) { list = new List<ObjectId>(); groups[key] = list; }
        list.Add(id);
      }

      var removed = 0;
      var duplicateGroups = 0;
      foreach (var kv in groups)
      {
        if (kv.Value.Count < 2) continue;
        duplicateGroups++;
        for (var i = 1; i < kv.Value.Count; i++)
        {
          var victim = transaction.GetObject(kv.Value[i], OpenMode.ForWrite, false);
          victim?.Erase();
          removed++;
        }
      }

      return new Dictionary<string, object?>
      {
        ["scanned"] = groups.Count,
        ["duplicateGroups"] = duplicateGroups,
        ["removed"] = removed,
        ["tolerance"] = tol,
        ["method"] = "geometry",
      };
    });
  }

  /// <summary>几何指纹：同类型 + 同几何（按容差取整）→ 相同字符串即重复</summary>
  private static string? Fingerprint(Curve? curve, double tol) => curve switch
  {
    Line line => $"L:{Round(line.StartPoint, tol)}|{Round(line.EndPoint, tol)}|{line.Layer}",
    Circle circle => $"C:{Round(circle.Center, tol)}|{R(circle.Radius, tol)}|{circle.Layer}",
    Arc arc => $"A:{Round(arc.Center, tol)}|{R(arc.Radius, tol)}|{R(arc.StartAngle, tol)}|{R(arc.EndAngle, tol)}|{arc.Layer}",
    Polyline pl => FingerprintPolyline(pl, tol),
    _ => null,
  };

  private static string FingerprintPolyline(Polyline pl, double tol)
  {
    var sb = new System.Text.StringBuilder("P:");
    for (var i = 0; i < pl.NumberOfVertices; i++)
    {
      var p = pl.GetPoint2dAt(i);
      sb.Append(R(p.X, tol)).Append(',').Append(R(p.Y, tol)).Append(';');
      sb.Append(R(pl.GetBulgeAt(i), tol)).Append(';');
    }
    sb.Append(pl.Closed ? "1" : "0").Append('|').Append(pl.Layer);
    return sb.ToString();
  }

  private static double R(double v, double tol) => System.Math.Round(v / tol) * tol;
  private static string Round(Point3d p, double tol) => $"{R(p.X, tol)},{R(p.Y, tol)},{R(p.Z, tol)}";
}
