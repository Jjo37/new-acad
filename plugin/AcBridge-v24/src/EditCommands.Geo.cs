using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-10-10 批次 6.2：编辑类几何自研（第二批）—— fillet / chamfer / stretch / dimAngular / sectionPlane。
/// 一律走真 API、**同步返回结果**（可断言），不再 SendStringToExecute 投递。
/// 数学：FILLET/CHAMFER 目前支持**直线-直线**（最常见的用法）；其他组合给出明确错误而不是静默。
/// </summary>
public static partial class EditCommands
{
  private const double Eps = 1e-9;

  private static Point3d LineIntersection(Line a, Line b)
  {
    var p = a.StartPoint; var r = a.EndPoint - a.StartPoint;
    var q = b.StartPoint; var s = b.EndPoint - b.StartPoint;
    var rxs = r.X * s.Y - r.Y * s.X;
    if (System.Math.Abs(rxs) < 1e-12)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "两条直线平行/共线，无法倒角或圆角。");
    var qp = q - p;
    var t = (qp.X * s.Y - qp.Y * s.X) / rxs;
    return p + (r * t);
  }

  private static Vector3d DirFromCorner(Point3d corner, Point3d s, Point3d e)
  {
    var v = corner.DistanceTo(s) > corner.DistanceTo(e) ? s - corner : e - corner;
    return v.GetNormal();
  }

  private static Dictionary<string, object?> CornerPrep(Transaction transaction, Database database, string h1, string h2,
      out Line l1, out Line l2, out Point3d corner, out Vector3d dir1, out Vector3d dir2, out double theta)
  {
    var e1 = CivilObjectUtils.GetRequiredObject<Entity>(transaction, ToObjectId(database, h1), OpenMode.ForWrite);
    var e2 = CivilObjectUtils.GetRequiredObject<Entity>(transaction, ToObjectId(database, h2), OpenMode.ForWrite);
    if (e1 is not Line a || e2 is not Line b)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "当前仅支持直线-直线（Line-Line）的圆角/倒角。");
    l1 = a; l2 = b;
    corner = LineIntersection(l1, l2);
    dir1 = DirFromCorner(corner, l1.StartPoint, l1.EndPoint);
    dir2 = DirFromCorner(corner, l2.StartPoint, l2.EndPoint);
    var cos = System.Math.Max(-1, System.Math.Min(1, dir1.DotProduct(dir2)));
    theta = System.Math.Acos(cos);
    if (theta < 1e-6 || theta > System.Math.PI - 1e-6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "两直线夹角退化为 0°/180°，无法圆角/倒角。");
    return new Dictionary<string, object?>();
  }

  private static void TrimLineTo(Line line, Point3d corner, Point3d target)
  {
    if (corner.DistanceTo(line.StartPoint) < corner.DistanceTo(line.EndPoint)) line.StartPoint = target;
    else line.EndPoint = target;
  }

  // ── FILLET：直线-直线，半径 r → 修剪两线 + 生成相切圆弧 ────────────────
  public static Task<object?> FilletLinesGeoAsync(JsonObject? parameters)
  {
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var h1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var h2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      CornerPrep(transaction, database, h1, h2, out var l1, out var l2, out var corner, out var d1, out var d2, out var theta);
      var half = theta / 2.0;
      var tangent = radius / System.Math.Tan(half);
      var t1 = corner + d1 * tangent;
      var t2 = corner + d2 * tangent;
      var center = corner + (d1 + d2).GetNormal() * (radius / System.Math.Sin(half));

      var a1 = System.Math.Atan2(t1.Y - center.Y, t1.X - center.X);
      var a2 = System.Math.Atan2(t2.Y - center.Y, t2.X - center.X);
      var ccw = (a2 - a1) % (2 * System.Math.PI); if (ccw < 0) ccw += 2 * System.Math.PI;
      var want = System.Math.PI - theta;
      using var arc = System.Math.Abs(ccw - want) <= System.Math.Abs(ccw - (2 * System.Math.PI - want))
        ? new Arc(center, Vector3d.ZAxis, radius, a1, a2)
        : new Arc(center, Vector3d.ZAxis, radius, a2, a1);
      modelSpace.AppendEntity(arc);
      transaction.AddNewlyCreatedDBObject(arc, true);
      TrimLineTo(l1, corner, t1);
      TrimLineTo(l2, corner, t2);

      return new Dictionary<string, object?>
      {
        ["method"] = "geometry",
        ["corner"] = new[] { corner.X, corner.Y, corner.Z },
        ["radius"] = radius,
        ["tangentPoints"] = new[] { new[] { t1.X, t1.Y, t1.Z }, new[] { t2.X, t2.Y, t2.Z } },
        ["arcHandle"] = CivilObjectUtils.GetHandle(arc),
      };
    });
  }

  // ── CHAMFER：直线-直线，距离 d1/d2 → 修剪两线 + 连接直线 ─────────────────
  public static Task<object?> ChamferLinesGeoAsync(JsonObject? parameters)
  {
    var dist1 = PluginRuntime.GetRequiredDouble(parameters, "distance1");
    var dist2 = PluginRuntime.GetRequiredDouble(parameters, "distance2");
    var h1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var h2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      CornerPrep(transaction, database, h1, h2, out var l1, out var l2, out var corner, out var d1, out var d2, out _);
      var t1 = corner + d1 * dist1;
      var t2 = corner + d2 * dist2;
      using var line = new Line(t1, t2);
      modelSpace.AppendEntity(line);
      transaction.AddNewlyCreatedDBObject(line, true);
      TrimLineTo(l1, corner, t1);
      TrimLineTo(l2, corner, t2);
      return new Dictionary<string, object?>
      {
        ["method"] = "geometry",
        ["corner"] = new[] { corner.X, corner.Y, corner.Z },
        ["chamferLineHandle"] = CivilObjectUtils.GetHandle(line),
        ["points"] = new[] { new[] { t1.X, t1.Y, t1.Z }, new[] { t2.X, t2.Y, t2.Z } },
      };
    });
  }

  // ── STRETCH：把"窗口内顶点"位移 (dx,dy)；无 window 则整体位移 ──────────
  public static Task<object?> StretchGeoAsync(JsonObject? parameters)
  {
    var handles = new List<string>();
    if (PluginRuntime.GetParameter(parameters, "handles") is JsonArray arr)
      foreach (var n in arr) if (n is JsonValue jv) handles.Add(jv.ToString());
    var single = PluginRuntime.GetOptionalString(parameters, "handle");
    if (!string.IsNullOrWhiteSpace(single)) handles.Add(single!);
    if (handles.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "需要 handle 或 handles[]。");
    var dx = PluginRuntime.GetRequiredDouble(parameters, "dx");
    var dy = PluginRuntime.GetRequiredDouble(parameters, "dy");
    var win = PluginRuntime.GetParameter(parameters, "window") as JsonObject;
    bool HasWin = win != null;
    double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
    if (HasWin)
    {
      x1 = win!["x1"]!.GetValue<double>(); y1 = win!["y1"]!.GetValue<double>();
      x2 = win!["x2"]!.GetValue<double>(); y2 = win!["y2"]!.GetValue<double>();
      if (x1 > x2) (x1, x2) = (x2, x1);
      if (y1 > y2) (y1, y2) = (y2, y1);
    }
    bool InWin(double x, double y) => !HasWin || (x >= x1 && x <= x2 && y >= y1 && y <= y2);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var movedVertices = 0; var movedEntities = 0; var skipped = new List<string>();
      foreach (var h in handles)
      {
        var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, ToObjectId(database, h), OpenMode.ForWrite);
        switch (ent)
        {
          case Line line:
            var sp = line.StartPoint; var ep = line.EndPoint; var cnt = 0;
            if (InWin(sp.X, sp.Y)) { line.StartPoint = new Point3d(sp.X + dx, sp.Y + dy, sp.Z); cnt++; }
            if (InWin(ep.X, ep.Y)) { line.EndPoint = new Point3d(ep.X + dx, ep.Y + dy, ep.Z); cnt++; }
            movedVertices += cnt; if (cnt > 0) movedEntities++; break;
          case Polyline pl:
            var pc = 0;
            for (var i = 0; i < pl.NumberOfVertices; i++)
            {
              var p = pl.GetPoint2dAt(i);
              if (InWin(p.X, p.Y)) { pl.SetPointAt(i, new Point2d(p.X + dx, p.Y + dy)); pc++; }
            }
            movedVertices += pc; if (pc > 0) movedEntities++; break;
          case DBPoint dp:
            if (InWin(dp.Position.X, dp.Position.Y)) { dp.Position = new Point3d(dp.Position.X + dx, dp.Position.Y + dy, dp.Position.Z); movedVertices++; movedEntities++; }
            break;
          default:
            if (!HasWin) { ent.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0))); movedEntities++; }
            else skipped.Add(h + "(" + ent.GetType().Name + ")");
            break;
        }
      }
      return new Dictionary<string, object?>
      {
        ["method"] = "geometry",
        ["movedEntities"] = movedEntities,
        ["movedVertices"] = movedVertices,
        ["window"] = HasWin ? new[] { x1, y1, x2, y2 } : null,
        ["skipped"] = skipped,
      };
    });
  }

  // ── DIMANGULAR：自研（无 AngularDimension 类）→ 圆弧 + 角度文字 ──────────
  public static Task<object?> DimAngularGeoAsync(JsonObject? parameters)
  {
    var h1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var h2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    var dimX = PluginRuntime.GetRequiredDouble(parameters, "dimX");
    var dimY = PluginRuntime.GetRequiredDouble(parameters, "dimY");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      CornerPrep(transaction, database, h1, h2, out _, out _, out var corner, out var d1, out var d2, out var theta);
      var deg = theta * 180.0 / System.Math.PI;
      var r = System.Math.Max(corner.DistanceTo(new Point3d(dimX, dimY, corner.Z)), 1e-6);
      var a1 = System.Math.Atan2(d1.Y, d1.X);
      var a2 = System.Math.Atan2(d2.Y, d2.X);
      var ccw = (a2 - a1) % (2 * System.Math.PI); if (ccw < 0) ccw += 2 * System.Math.PI;
      using var arc = ccw <= System.Math.PI ? new Arc(corner, Vector3d.ZAxis, r, a1, a2) : new Arc(corner, Vector3d.ZAxis, r, a2, a1);
      modelSpace.AppendEntity(arc); transaction.AddNewlyCreatedDBObject(arc, true);

      var mid = ccw <= System.Math.PI ? a1 + ccw / 2 : a2 + (2 * System.Math.PI - ccw) / 2;
      var tp = new Point3d(corner.X + (r + 0.5) * System.Math.Cos(mid), corner.Y + (r + 0.5) * System.Math.Sin(mid), corner.Z);
      using var txt = new DBText
      {
        Position = tp,
        Height = System.Math.Max(r * 0.08, 0.05),
        TextString = deg.ToString("0.##") + "\u00B0",
      };
      modelSpace.AppendEntity(txt); transaction.AddNewlyCreatedDBObject(txt, true);

      return new Dictionary<string, object?>
      {
        ["method"] = "geometry",
        ["angleDeg"] = System.Math.Round(deg, 4),
        ["angleRad"] = System.Math.Round(theta, 6),
        ["radius"] = r,
        ["vertex"] = new[] { corner.X, corner.Y, corner.Z },
        ["arcHandle"] = CivilObjectUtils.GetHandle(arc),
        ["textHandle"] = CivilObjectUtils.GetHandle(txt),
      };
    });
  }

  // ── SECTIONPLANE：真 API Section（原 _.SECTIONPLANE 命令壳）────────────
  public static Task<object?> SectionPlaneGeoAsync(JsonObject? parameters)
  {
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = ModelSpace(transaction, database);
      var pts = new Point3dCollection();
      if (PluginRuntime.GetParameter(parameters, "points") is JsonArray arr)
      {
        foreach (var n in arr)
        {
          if (n is JsonArray pa && pa.Count >= 2)
            pts.Add(new Point3d(pa[0]!.GetValue<double>(), pa[1]!.GetValue<double>(), pa.Count > 2 ? pa[2]!.GetValue<double>() : 0));
          else if (n is JsonObject po && po["x"] != null && po["y"] != null)
            pts.Add(new Point3d(po["x"]!.GetValue<double>(), po["y"]!.GetValue<double>(), po["z"]?.GetValue<double>() ?? 0));
        }
      }
      if (pts.Count < 3)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sectionPlane 需要 points[[x,y],...] ≥3 个点（定义剖切线）。");
      var vertical = new Vector3d(0, 1, 0);
      var view = new Vector3d(0, 0, 1);
      if (PluginRuntime.GetParameter(parameters, "verticalDir") is JsonArray vd && vd.Count >= 3)
        vertical = new Vector3d(vd[0]!.GetValue<double>(), vd[1]!.GetValue<double>(), vd[2]!.GetValue<double>());
      if (PluginRuntime.GetParameter(parameters, "viewDir") is JsonArray wd && wd.Count >= 3)
        view = new Vector3d(wd[0]!.GetValue<double>(), wd[1]!.GetValue<double>(), wd[2]!.GetValue<double>());

      using var section = new Section(pts, vertical, view);
      modelSpace.AppendEntity(section);
      transaction.AddNewlyCreatedDBObject(section, true);
      return new Dictionary<string, object?>
      {
        ["method"] = "geometry",
        ["handle"] = CivilObjectUtils.GetHandle(section),
        ["vertices"] = pts.Count,
      };
    });
  }
}
