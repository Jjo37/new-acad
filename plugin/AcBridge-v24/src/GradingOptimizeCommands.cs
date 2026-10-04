using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DMcpPlugin;

/// <summary>
/// A2 产品化（2026-09-28）：**放坡优化** —— 一条命令从现状面生成满足约束的设计面。
///
/// 模型（凸问题，全局最优）：min Σ A·|h−EG|  s.t. 平台/边界固定节点 + 坡率上限 |Δh| ≤ s_max·dx
/// 算法：固定节点传播可行包络 → 夹逼可行化 → 逐格朝 EG 在邻居可行区间内夹紧（严格单调）
/// 验证：spike 与闭式解 min(z, t+L·d) 逐点一致；C3D 往返体积差 2.1%（网格）/1.6%（tin_volume）
/// 局限：排水最小坡度是析取（非凸）约束 → 用平台预倾斜覆盖；台阶/挡墙需几何预处理
/// </summary>
public static class GradingOptimizeCommands
{
  public static Task<object?> OptimizeGradingAsync(JsonObject? parameters)
  {
    var baseName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var designName = PluginRuntime.GetOptionalString(parameters, "designSurface") ?? (baseName + "_GRADING");
    var gridStep = PluginRuntime.GetOptionalDouble(parameters, "gridStep") ?? 2.0;
    var maxSlope = PluginRuntime.GetOptionalDouble(parameters, "maxSlope") ?? 0.33;      // 33% ≈ 1:3
    var minSlope = PluginRuntime.GetOptionalDouble(parameters, "minSlope") ?? 0.0;        // 仅用于平台预倾斜默认值
    var holdEdges = PluginRuntime.GetOptionalBool(parameters, "holdEdges") ?? false;       // 边界一圈保持现状（衔接）
    var balance = PluginRuntime.GetOptionalBool(parameters, "balance") ?? true;            // 外层二分压净方
    var maxIterations = PluginRuntime.GetOptionalInt(parameters, "maxIterations") ?? 400;
    var balanceTolerance = PluginRuntime.GetOptionalDouble(parameters, "balanceTolerance") ?? 1.0;
    var regionNode = parameters?["region"] as JsonObject;
    var platformNodes = parameters?["platforms"] as JsonArray ?? new JsonArray();
    var wallNodes = parameters?["walls"] as JsonArray ?? new JsonArray();

    if (gridStep <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "gridStep must be > 0");
    if (maxSlope <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxSlope must be > 0");
    if (balanceTolerance < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "balanceTolerance must be >= 0");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseName, OpenMode.ForRead);
      var extents = GetExtents(baseSurface, baseName);

      // 1) 区域：显式指定或自动找"满覆盖矩形"
      double minX = extents.MinPoint.X, minY = extents.MinPoint.Y, maxX = extents.MaxPoint.X, maxY = extents.MaxPoint.Y;
      if (regionNode != null)
      {
        minX = PluginRuntime.GetRequiredDoubleFromNode(regionNode["minX"], "region.minX");
        minY = PluginRuntime.GetRequiredDoubleFromNode(regionNode["minY"], "region.minY");
        maxX = PluginRuntime.GetRequiredDoubleFromNode(regionNode["maxX"], "region.maxX");
        maxY = PluginRuntime.GetRequiredDoubleFromNode(regionNode["maxY"], "region.maxY");
      }
      if (maxX <= minX || maxY <= minY) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "region must have maxX>minX and maxY>minY");

      // 2) 采样基准面到格网
      var lattice = SampleGrid(baseSurface, extents, gridStep);
      if (lattice.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Base surface '{baseName}' yielded no samples.");

      int i0, i1, j0, j1;
      if (regionNode != null)
      {
        i0 = (int)Math.Ceiling((minX - lattice.OriginX) / gridStep);
        i1 = (int)Math.Floor((maxX - lattice.OriginX) / gridStep);
        j0 = (int)Math.Ceiling((minY - lattice.OriginY) / gridStep);
        j1 = (int)Math.Floor((maxY - lattice.OriginY) / gridStep);
      }
      else
      {
        var win = FindFilledWindow(lattice, 48, 40, 32, 24, 20, 16, 12, 10, 8);
        if (win == null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "No fully-covered rectangular window found on the base surface; pass an explicit region{minX,minY,maxX,maxY}.");
        i0 = win.Value.i0; j0 = win.Value.j0; i1 = i0 + win.Value.size - 1; j1 = j0 + win.Value.size - 1;
      }
      var nx = i1 - i0 + 1;
      var ny = j1 - j0 + 1;
      if (nx < 3 || ny < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"region too small: {nx}x{ny} cells");

      var x0 = lattice.OriginX + i0 * gridStep;
      var y0 = lattice.OriginY + j0 * gridStep;
      var z = new double[nx * ny];
      var missing = 0;
      for (var j = 0; j < ny; j++)
      {
        for (var i = 0; i < nx; i++)
        {
          if (lattice.TryGet(i0 + i, j0 + j, out var v)) z[j * nx + i] = v;
          else { z[j * nx + i] = double.NaN; missing++; }
        }
      }
      if (missing > 0)
      {
        FillHoles(z, nx, ny);
        if (missing > nx * ny * 0.2)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"region has {missing}/{nx * ny} cells without base-surface data (>20%). Choose a region fully inside '{baseName}' or increase gridStep.");
      }

      // 3) 平台 / 边界约束
      var platforms = ParsePlatforms(platformNodes, nx, ny, gridStep, x0, y0, z);
      var (relaxedEdges, wallEcho) = ParseWalls(wallNodes, nx, ny, gridStep, x0, y0);
      var result = SolveWithOptionalBalance(z, nx, ny, gridStep, maxSlope, platforms, holdEdges, balance, balanceTolerance, maxIterations, relaxedEdges);

      // 4) 写设计面
      var created = WriteDesignSurface(civilDoc, transaction, database, designName, x0, y0, gridStep, nx, ny, result.H);

      // 5) 土方量 + 约束校核
      var (cut, fill, cutArea, fillArea) = Volumes(result.H, z, gridStep);
      var maxActual = MaxSlopeActual(result.H, nx, ny, gridStep, relaxedEdges);
      var maxStepAtWall = MaxStepAcrossRelaxed(result.H, relaxedEdges);
      var platformChecks = platforms.Select(p => new Dictionary<string, object?>
      {
        ["name"] = p.Name,
        ["elevation"] = Math.Round(p.Elevation, 4),
        ["cells"] = p.Cells.Count,
        ["tiltSlope"] = p.TiltSlope,
        ["ok"] = p.Cells.All(c => Math.Abs(result.H[c] - PlatformTargetAt(p, c, nx, gridStep)) < 1e-6),
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["designSurface"] = designName,
        ["baseSurface"] = baseName,
        ["region"] = new Dictionary<string, object?>
        {
          ["minX"] = x0, ["minY"] = y0,
          ["maxX"] = x0 + (nx - 1) * gridStep, ["maxY"] = y0 + (ny - 1) * gridStep,
        },
        ["gridStep"] = gridStep,
        ["cells"] = nx * ny,
        ["maxSlopeLimit"] = maxSlope,
        ["maxSlopeActual"] = Math.Round(maxActual, 5),
        ["cutVolume"] = Math.Round(cut, 4),
        ["fillVolume"] = Math.Round(fill, 4),
        ["netVolume"] = Math.Round(fill - cut, 4),
        ["cutArea"] = Math.Round(cutArea, 2),
        ["fillArea"] = Math.Round(fillArea, 2),
        ["iterations"] = result.Iterations,
        ["repairIterations"] = result.RepairIterations,
        ["balanced"] = result.Balanced,
        ["platforms"] = platformChecks,
        ["walls"] = wallEcho,
        ["maxStepAtWall"] = Math.Round(maxStepAtWall, 4),
        ["constraints"] = new Dictionary<string, object?>
        {
          ["maxSlopeOk"] = maxActual <= maxSlope + 1e-6,
          ["feasible"] = maxActual <= maxSlope + 1e-6 && platformChecks.All(p => (bool)p["ok"]!),
          ["hint"] = maxActual <= maxSlope + 1e-6
            ? "约束满足"
            : "约束不可满足：平台高差在给定间距内超出坡率上限。请增大平台间距 / 放宽 maxSlope / 用 walls 声明挡墙（墙体边不参与坡率约束）",
          ["platformsOk"] = platformChecks.All(p => (bool)p["ok"]!),
          ["wallEdgesRelaxed"] = relaxedEdges.Count,
          ["baseDataCoverage"] = Math.Round(1.0 - (double)missing / (nx * ny), 4),
        },
        ["designSurfaceHandle"] = created,
        ["method"] = "grading_optimize_convex_lipschitz",
        ["note"] = "凸模型：min ΣA|h−EG| s.t. 固定节点 + 坡率上限；排水最小坡度请用平台 tiltSlope 预倾斜表达；台阶/挡墙需先做几何预处理",
      };
    });
  }

  // ---------------- 采样 ----------------

  private sealed class Lattice
  {
    public double OriginX, OriginY, Step;
    public int NX, NY;
    private Dictionary<long, double> _cells = new();
    private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

    public Lattice(double ox, double oy, double step, int nx, int ny)
    {
      OriginX = ox; OriginY = oy; Step = step; NX = nx; NY = ny;
    }
    public void Set(int i, int j, double v) => _cells[Key(i, j)] = v;
    public bool TryGet(int i, int j, out double v) => _cells.TryGetValue(Key(i, j), out v);
    public int Count => _cells.Count;
    public IEnumerable<(int i, int j, double v)> All()
    {
      foreach (var kv in _cells) yield return ((int)(kv.Key >> 32), (int)(uint)kv.Key, kv.Value);
    }
  }

  private static Lattice SampleGrid(CivilSurface surface, Extents3d extents, double step)
  {
    var nx = (int)Math.Floor((extents.MaxPoint.X - extents.MinPoint.X) / step) + 1;
    var ny = (int)Math.Floor((extents.MaxPoint.Y - extents.MinPoint.Y) / step) + 1;
    var lattice = new Lattice(extents.MinPoint.X, extents.MinPoint.Y, step, nx, ny);
    for (var j = 0; j < ny; j++)
    {
      for (var i = 0; i < nx; i++)
      {
        var x = extents.MinPoint.X + i * step;
        var y = extents.MinPoint.Y + j * step;
        double elevation;
        try { elevation = surface.FindElevationAtXY(x, y); } catch { continue; }
        if (double.IsNaN(elevation) || double.IsInfinity(elevation)) continue;
        lattice.Set(i, j, elevation);
      }
    }
    return lattice;
  }

  private static (int i0, int j0, int size)? FindFilledWindow(Lattice lattice, params int[] candidateSizes)
  {
    var w = (lattice.NX + 1) * (lattice.NY + 1);
    var occ = new int[w];
    for (var j = 0; j < lattice.NY; j++)
      for (var i = 0; i < lattice.NX; i++)
        occ[(j + 1) * (lattice.NX + 1) + (i + 1)] = lattice.TryGet(i, j, out _) ? 1 : 0;
    var integral = new int[w];
    for (var j = 0; j < lattice.NY; j++)
      for (var i = 0; i < lattice.NX; i++)
        integral[(j + 1) * (lattice.NX + 1) + (i + 1)] =
          occ[(j + 1) * (lattice.NX + 1) + (i + 1)]
          + integral[j * (lattice.NX + 1) + (i + 1)]
          + integral[(j + 1) * (lattice.NX + 1) + i]
          - integral[j * (lattice.NX + 1) + i];
    int Sum(int i, int j, int i2, int j2) =>
      integral[(j2 + 1) * (lattice.NX + 1) + (i2 + 1)]
      - integral[j * (lattice.NX + 1) + (i2 + 1)]
      - integral[(j2 + 1) * (lattice.NX + 1) + i]
      + integral[j * (lattice.NX + 1) + i];

    foreach (var size in candidateSizes)
    {
      if (size > lattice.NX || size > lattice.NY) continue;
      var bx = 0; var by = 0; var best = -1;
      for (var j = 0; j + size <= lattice.NY; j++)
        for (var i = 0; i + size <= lattice.NX; i++)
        {
          var v = Sum(i, j, i + size - 1, j + size - 1);
          if (v > best) { best = v; bx = i; by = j; }
        }
      if (best >= (int)(size * size * 0.95)) return (bx, by, size);
    }
    return null;
  }

  private static void FillHoles(double[] z, int nx, int ny)
  {
    for (var j = 0; j < ny; j++)
      for (var i = 0; i < nx; i++)
      {
        if (!double.IsNaN(z[j * nx + i])) continue;
        for (var r = 1; r <= 5 && double.IsNaN(z[j * nx + i]); r++)
          for (var dj = -r; dj <= r; dj++)
            for (var di = -r; di <= r; di++)
            {
              var ii = i + di; var jj = j + dj;
              if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) continue;
              var v = z[jj * nx + ii];
              if (!double.IsNaN(v)) { z[j * nx + i] = v; break; }
            }
      }
  }

  // ---------------- 平台约束 ----------------

  private sealed class Platform
  {
    public string Name = "platform";
    public int IX0, IX1, JY0, JY1;
    public double Elevation;
    public double? ElevationInput;      // 显式标高（null → 由均值/dropFromBase/平衡推导）
    public double DropFromBase;         // 无显式标高时：平台区基准面均值 − drop
    public double TiltSlope;            // 预倾斜（排水）
    public bool HasExplicitTarget;      // 调用方给了 elevation/dropFromBase → 平衡不得覆盖
    public string TiltToward = "y-";    // 低边方向
    public List<int> Cells = new();
  }

  private static List<Platform> ParsePlatforms(JsonArray nodes, int nx, int ny, double step, double x0, double y0, double[] z)
  {
    var list = new List<Platform>();
    var index = 0;
    foreach (var node in nodes)
    {
      index++;
      if (node is not JsonObject o) continue;
      var p = new Platform { Name = PluginRuntime.GetOptionalString(o, "name") ?? ("platform" + index) };
      var px0 = PluginRuntime.GetRequiredDoubleFromNode(o["minX"], "platforms[].minX");
      var py0 = PluginRuntime.GetRequiredDoubleFromNode(o["minY"], "platforms[].minY");
      var px1 = PluginRuntime.GetRequiredDoubleFromNode(o["maxX"], "platforms[].maxX");
      var py1 = PluginRuntime.GetRequiredDoubleFromNode(o["maxY"], "platforms[].maxY");
      p.IX0 = (int)Math.Ceiling((px0 - x0) / step);
      p.IX1 = (int)Math.Floor((px1 - x0) / step);
      p.JY0 = (int)Math.Ceiling((py0 - y0) / step);
      p.JY1 = (int)Math.Floor((py1 - y0) / step);
      p.IX0 = Math.Max(0, p.IX0); p.JY0 = Math.Max(0, p.JY0);
      p.IX1 = Math.Min(nx - 1, p.IX1); p.JY1 = Math.Min(ny - 1, p.JY1);
      if (p.IX1 < p.IX0 || p.JY1 < p.JY0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"platform '{p.Name}' does not intersect the grading region");

      p.ElevationInput = PluginRuntime.GetOptionalDouble(o, "elevation");
      p.DropFromBase = PluginRuntime.GetOptionalDouble(o, "dropFromBase") ?? 0.0;
      p.HasExplicitTarget = o["elevation"] != null || o["dropFromBase"] != null;
      p.TiltSlope = PluginRuntime.GetOptionalDouble(o, "tiltSlope") ?? 0.0;
      p.TiltToward = PluginRuntime.GetOptionalString(o, "tiltToward") ?? "y-";

      var sum = 0.0; var n = 0;
      for (var j = p.JY0; j <= p.JY1; j++)
        for (var i = p.IX0; i <= p.IX1; i++)
        {
          p.Cells.Add(j * nx + i);
          if (!double.IsNaN(z[j * nx + i])) { sum += z[j * nx + i]; n++; }
        }
      var baseMean = n > 0 ? sum / n : 0;
      p.Elevation = p.ElevationInput ?? (baseMean - p.DropFromBase);
      list.Add(p);
    }
    return list;
  }

  /// <summary>平台目标标高（含预倾斜）：低边方向按 TiltToward 递减</summary>
  private static double PlatformTargetAt(Platform p, int cell, int nx, double step)
  {
    var j = cell / nx;
    var i = cell % nx;
    var d = p.TiltToward switch
    {
      "x+" => (p.IX1 - i) * step,
      "x-" => (i - p.IX0) * step,
      "y+" => (p.JY1 - j) * step,
      _ => (j - p.JY0) * step,
    };
    return p.Elevation + p.TiltSlope * d;
  }

  // ---------------- 求解 ----------------

  private sealed class SolveResult
  {
    public double[] H = Array.Empty<double>();
    public int Iterations;
    public int RepairIterations;
    public bool Balanced;
  }

  private static SolveResult SolveWithOptionalBalance(
    double[] z, int nx, int ny, double step, double maxSlope,
    List<Platform> platforms, bool holdEdges, bool balance, double tolerance, int maxIterations,
    HashSet<long> relaxedEdges)
  {
    // 只有“未指定平台目标标高”时才允许用平衡反推平台标高（不覆盖设计意图）
    if (!balance || platforms.Count == 0 || platforms.Any(p => p.HasExplicitTarget))
      return RunSolver(z, nx, ny, step, maxSlope, platforms, holdEdges, maxIterations, relaxedEdges);

    // 外层：以"平台标高相对基准面均值的偏移"为控制量二分，压净方
    var p0 = platforms[0];
    var baseElevations = new List<double>();
    for (var j = p0.JY0; j <= p0.JY1; j++)
      for (var i = p0.IX0; i <= p0.IX1; i++)
        if (!double.IsNaN(z[j * nx + i])) baseElevations.Add(z[j * nx + i]);
    var mean = baseElevations.Count > 0 ? baseElevations.Average() : 0;

    var lo = -10.0; var hi = 10.0;      // 偏移范围（米）
    SolveResult? best = null; var bestAbsNet = double.MaxValue;
    for (var it = 0; it < 40; it++)
    {
      var mid = (lo + hi) / 2;
      foreach (var p in platforms) p.Elevation = mean + mid;
      var r = RunSolver(z, nx, ny, step, maxSlope, platforms, holdEdges, maxIterations, relaxedEdges);
      var (cut, fill, _, _) = Volumes(r.H, z, step);
      var net = fill - cut;
      if (Math.Abs(net) < bestAbsNet) { bestAbsNet = Math.Abs(net); best = r; }
      if (Math.Abs(net) <= tolerance) { best = r; break; }      // 够好就停
      if (net > 0) hi = mid; else lo = mid;                     // 平台抬高 → 净方(fill−cut) 单调增
    }
    if (best != null) { best.Balanced = true; return best; }
    return RunSolver(z, nx, ny, step, maxSlope, platforms, holdEdges, maxIterations, relaxedEdges);
  }

  private static SolveResult RunSolver(
    double[] z, int nx, int ny, double step, double maxSlope,
    List<Platform> platforms, bool holdEdges, int maxIterations, HashSet<long> relaxedEdges)
  {
    var n = nx * ny;
    var L = maxSlope * step;
    var h = new double[n];
    var fixedNode = new bool[n];

    // 平台固定
    foreach (var p in platforms)
      foreach (var cell in p.Cells)
      {
        h[cell] = PlatformTargetAt(p, cell, nx, step);
        fixedNode[cell] = true;
      }
    // 边界一圈保持现状（与既有地面衔接）
    if (holdEdges)
    {
      for (var i = 0; i < nx; i++) { FixEdge(i, z, h, fixedNode); FixEdge((ny - 1) * nx + i, z, h, fixedNode); }
      for (var j = 0; j < ny; j++) { FixEdge(j * nx, z, h, fixedNode); FixEdge(j * nx + nx - 1, z, h, fixedNode); }
    }

    // A. 固定节点包络
    var upper = new double[n];
    var lower = new double[n];
    for (var i = 0; i < n; i++) { upper[i] = double.PositiveInfinity; lower[i] = double.NegativeInfinity; }
    for (var i = 0; i < n; i++) if (fixedNode[i]) { upper[i] = h[i]; lower[i] = h[i]; }
    var changed = true; var passes = 0;
    while (changed && passes < 200)
    {
      changed = false; passes++;
      for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
          var c = j * nx + i;
          if (i > 0) Relax(c - 1, c, L, upper, lower, ref changed, relaxedEdges);
          if (j > 0) Relax(c - nx, c, L, upper, lower, ref changed, relaxedEdges);
        }
      for (var j = ny - 1; j >= 0; j--)
        for (var i = nx - 1; i >= 0; i--)
        {
          var c = j * nx + i;
          if (i < nx - 1) Relax(c + 1, c, L, upper, lower, ref changed, relaxedEdges);
          if (j < ny - 1) Relax(c + nx, c, L, upper, lower, ref changed, relaxedEdges);
        }
    }

    // B. 初值（夹进包络）+ 可行性修复
    for (var i = 0; i < n; i++)
      if (!fixedNode[i]) h[i] = Math.Min(upper[i], Math.Max(lower[i], z[i]));
    var repair = 0;
    for (; repair < 200; repair++)
    {
      var worst = 0.0;
      for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
          var c = j * nx + i;
          if (fixedNode[c]) continue;
          var (lo, hi) = NeighborRange(h, nx, ny, i, j, L, relaxedEdges);
          var want = h[c];
          var next = Math.Min(hi, Math.Max(lo, want));
          if (next != want) { worst = Math.Max(worst, Math.Abs(next - want)); h[c] = next; }
        }
      if (worst < 1e-12) break;
    }

    // C. 下降（严格单调）
    var energy = Energy(h, z, step);
    var sweeps = 0;
    for (; sweeps < maxIterations; sweeps++)
    {
      var moved = 0.0;
      for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
          var c = j * nx + i;
          if (fixedNode[c]) continue;
          var (lo, hi) = NeighborRange(h, nx, ny, i, j, L, relaxedEdges);
          var target = Math.Min(hi, Math.Max(lo, z[c]));
          if (target != h[c]) { moved += Math.Abs(target - h[c]); h[c] = target; }
        }
      var e = Energy(h, z, step);
      var gain = energy - e;
      energy = e;
      if (moved < 1e-9 || gain < 1e-9) { sweeps++; break; }
    }

    return new SolveResult { H = h, Iterations = sweeps, RepairIterations = repair };
  }

  private static void FixEdge(int cell, double[] z, double[] h, bool[] fixedNode)
  {
    if (double.IsNaN(z[cell])) return;
    h[cell] = z[cell];
    fixedNode[cell] = true;
  }

  /// <summary>墙体边的键（无序，取小者高位）</summary>
  private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

  private static void Relax(int from, int to, double L, double[] upper, double[] lower, ref bool changed, HashSet<long> relaxedEdges)
  {
    if (relaxedEdges.Contains(EdgeKey(from, to))) return;   // 挡墙：不传播坡率约束
    if (upper[from] + L < upper[to]) { upper[to] = upper[from] + L; changed = true; }
    if (lower[from] - L > lower[to]) { lower[to] = lower[from] - L; changed = true; }
  }

  private static (double lo, double hi) NeighborRange(double[] h, int nx, int ny, int i, int j, double L, HashSet<long> relaxedEdges)
  {
    var lo = double.NegativeInfinity; var hi = double.PositiveInfinity;
    var self = j * nx + i;
    void Acc(int ii, int jj)
    {
      if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) return;
      var c2 = jj * nx + ii;
      if (relaxedEdges.Contains(EdgeKey(self, c2))) return;   // 挡墙边不参与夹逼
      var v = h[c2];
      if (v - L > lo) lo = v - L;
      if (v + L < hi) hi = v + L;
    }
    Acc(i - 1, j); Acc(i + 1, j); Acc(i, j - 1); Acc(i, j + 1);
    return (lo, hi);
  }

  private static double Energy(double[] h, double[] z, double step)
  {
    var a = step * step;
    var e = 0.0;
    for (var i = 0; i < h.Length; i++) e += a * Math.Abs(h[i] - z[i]);
    return e;
  }

  private static (double cut, double fill, double cutArea, double fillArea) Volumes(double[] h, double[] z, double step)
  {
    var a = step * step;
    double cut = 0, fill = 0, ca = 0, fa = 0;
    for (var i = 0; i < h.Length; i++)
    {
      var d = z[i] - h[i];
      if (d > 0) { cut += d * a; ca += a; } else { fill += -d * a; fa += a; }
    }
    return (cut, fill, ca, fa);
  }

  /// <summary>实际最大坡率（**排除挡墙边**——墙体处允许垂直落差）</summary>
  private static double MaxSlopeActual(double[] h, int nx, int ny, double step, HashSet<long> relaxedEdges)
  {
    var max = 0.0;
    for (var j = 0; j < ny; j++)
      for (var i = 0; i < nx; i++)
      {
        var c = j * nx + i;
        if (i + 1 < nx && !relaxedEdges.Contains(EdgeKey(c, c + 1))) max = Math.Max(max, Math.Abs(h[c] - h[c + 1]));
        if (j + 1 < ny && !relaxedEdges.Contains(EdgeKey(c, c + nx))) max = Math.Max(max, Math.Abs(h[c] - h[c + nx]));
      }
    return max / step;
  }

  /// <summary>墙体处最大落差（米）—— 供人工决定是否需要挡墙结构</summary>
  private static double MaxStepAcrossRelaxed(double[] h, HashSet<long> relaxedEdges)
  {
    var max = 0.0;
    foreach (var key in relaxedEdges)
    {
      var a = (int)(key >> 32);
      var b = (int)(uint)key;
      if (a < 0 || b < 0 || a >= h.Length || b >= h.Length) continue;
      max = Math.Max(max, Math.Abs(h[a] - h[b]));
    }
    return max;
  }

  /// <summary>挡墙：沿给定线把跨越它的相邻格边标为“无坡率约束”（orientation=v 竖墙 / h 横墙）</summary>
  private static (HashSet<long> Edges, List<Dictionary<string, object?>> Echo) ParseWalls(
    JsonArray nodes, int nx, int ny, double step, double x0, double y0)
  {
    var edges = new HashSet<long>();
    var echo = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (var node in nodes)
    {
      index++;
      if (node is not JsonObject o) continue;
      var orientation = (PluginRuntime.GetOptionalString(o, "orientation") ?? "v").ToLowerInvariant();
      var at = PluginRuntime.GetRequiredDoubleFromNode(o["at"], "walls[].at");
      var from = PluginRuntime.GetRequiredDoubleFromNode(o["from"], "walls[].from");
      var to = PluginRuntime.GetRequiredDoubleFromNode(o["to"], "walls[].to");
      var name = PluginRuntime.GetOptionalString(o, "name") ?? ("wall" + index);
      var lo = Math.Min(from, to);
      var hi = Math.Max(from, to);
      var count = 0;
      if (orientation == "v")
      {
        var i = (int)Math.Floor((at - x0) / step);
        i = Math.Min(Math.Max(i, 0), nx - 2);
        var j0 = Math.Max(0, (int)Math.Ceiling((lo - y0) / step));
        var j1 = Math.Min(ny - 1, (int)Math.Floor((hi - y0) / step));
        for (var j = j0; j <= j1; j++) { edges.Add(EdgeKey(j * nx + i, j * nx + i + 1)); count++; }
      }
      else
      {
        var j = (int)Math.Floor((at - y0) / step);
        j = Math.Min(Math.Max(j, 0), ny - 2);
        var i0 = Math.Max(0, (int)Math.Ceiling((lo - x0) / step));
        var i1 = Math.Min(nx - 1, (int)Math.Floor((hi - x0) / step));
        for (var i = i0; i <= i1; i++) { edges.Add(EdgeKey(j * nx + i, (j + 1) * nx + i)); count++; }
      }
      echo.Add(new Dictionary<string, object?>
      {
        ["name"] = name, ["orientation"] = orientation, ["at"] = at, ["from"] = lo, ["to"] = hi, ["relaxedEdges"] = count,
      });
    }
    return (edges, echo);
  }

  private static Extents3d GetExtents(CivilSurface surface, string name)
  {
    var extents = CivilObjectUtils.GetPropertyValue<Extents3d?>(surface, "GeometricExtents");
    if (extents == null)
      throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to get extents for surface '{name}'.");
    return extents.Value;
  }

  private static string WriteDesignSurface(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, Database database,
    string designName, double x0, double y0, double step, int nx, int ny, double[] h)
  {
    // 同名曲面先删（幂等重跑）
    try
    {
      var existing = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, designName, OpenMode.ForWrite);
      if (existing != null) existing.Erase();
    }
    catch { /* 不存在则忽略 */ }

    var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(null, "style"));
    var surfaceId = TinSurface.Create(designName, styleId);
    var tin = CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, surfaceId, OpenMode.ForWrite);
    var points = new Point3dCollection();
    for (var j = 0; j < ny; j++)
      for (var i = 0; i < nx; i++)
        points.Add(new Point3d(x0 + i * step, y0 + j * step, h[j * nx + i]));
    tin.AddVertices(points);
    try { tin.Rebuild(); } catch { /* 已自动重建 */ }
    return CivilObjectUtils.GetHandle(tin);
  }
}
