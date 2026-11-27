using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Hydraulic analysis commands for gravity pipe networks.
/// Implements Manning's equation, HGL backwater analysis, and structure property queries.
/// </summary>
public static class PipeHydraulicsCommands
{
  // ─── calculatePipeNetworkHgl ────────────────────────────────────────────────

  public static Task<object?> CalculatePipeNetworkHglAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var tailwaterElevation = PluginRuntime.GetOptionalDouble(parameters, "tailwaterElevation");
    var designFlow = PluginRuntime.GetOptionalDouble(parameters, "designFlow");
    var manningsN = PluginRuntime.GetOptionalDouble(parameters, "manningsN") ?? 0.013;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var pipes = GetPipeData(network, transaction);
      var structures = GetStructureData(network, transaction);

      if (pipes.Count == 0)
      {
        return new Dictionary<string, object?>
        {
          ["networkName"] = networkName,
          ["nodeCount"] = 0,
          ["nodes"] = Array.Empty<object>(),
          ["warning"] = "Network contains no pipes.",
        };
      }

      // Sort pipes upstream to downstream by invert elevation (lowest invert = downstream)
      var k = CivilObjectUtils.ManningConstant(database);   // 2026-10-15: 单位一致时 SI=1.0 / 英尺=1.486
      var sortedPipes = pipes.OrderByDescending(p => p.InvertIn).ToList();
      var outletInvert = sortedPipes.Last().InvertOut;
      var hglAtOutlet = tailwaterElevation ?? outletInvert;

      // Build HGL table — simple backwater walk
      var nodes = new List<Dictionary<string, object?>>();
      var currentHgl = hglAtOutlet;

      for (int i = sortedPipes.Count - 1; i >= 0; i--)
      {
        var pipe = sortedPipes[i];
        double diameter = pipe.Diameter;
        double length = pipe.Length;
        double slope = Math.Abs(pipe.Slope); // 比值（0.0411 = 4.11%），无需除 100
        double area = Math.PI * diameter * diameter / 4.0;
        double wetPerim = Math.PI * diameter;
        double hydraulicRadius = diameter / 4.0;
        double qFull = k * (1.0 / manningsN) * area * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Pow(slope, 0.5);

        // Head loss in pipe (Manning's based)
        // 2026-11-19 P1 修复：hL = n²·L·Q² / (k²·A²·R^(4/3)) —— 必须带 k²（英尺制 k=1.486，漏了就虚高 2.208×）。
        // 同一根管分别用 SI 与英尺表示时，换算回来必须得出同一个水头损失（下方 verify 用 WL 独立验算过）。
        var qForLoss = designFlow ?? qFull;
        var headLoss = (manningsN * manningsN * length * qForLoss * qForLoss)
                          / (k * k * Math.Pow(hydraulicRadius, 4.0 / 3.0) * area * area);
        headLoss = Math.Max(0, headLoss);

        var hglAtUpstream = currentHgl + headLoss;
        var isSurcharged = hglAtUpstream > pipe.InvertIn + diameter;

        nodes.Add(new Dictionary<string, object?>
        {
          ["pipeName"] = pipe.Name,
          ["invertIn"] = Math.Round(pipe.InvertIn, 3),
          ["invertOut"] = Math.Round(pipe.InvertOut, 3),
          ["invertSource"] = pipe.InvertSource,
          ["diameter"] = Math.Round(diameter, 3),
          ["length"] = Math.Round(length, 2),
          ["slopePercent"] = Math.Round(Math.Abs(pipe.Slope) * 100.0, 4),
          // 2026-11-17 P29: 去掉 cfs/fps 后缀 —— 米制图上值是 m3/s、m/s，名字写 Cfs/Fps 是骗人
          ["qFull"] = Math.Round(qFull, 3),
          ["flowUnits"] = CivilObjectUtils.FlowUnits(database),
          ["hglDownstream"] = Math.Round(currentHgl, 3),
          ["hglUpstream"] = Math.Round(hglAtUpstream, 3),
          ["headLoss"] = Math.Round(headLoss, 3),
          ["surcharged"] = isSurcharged,
          ["status"] = isSurcharged ? "SURCHARGED" : "FREE_SURFACE",
        });

        currentHgl = hglAtUpstream;
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["tailwaterElevation"] = Math.Round(hglAtOutlet, 3),
        ["manningsN"] = manningsN,
        ["designFlow"] = designFlow,
        ["pipeCount"] = pipes.Count,
        ["surchargedCount"] = nodes.Count(n => (bool)(n["surcharged"] ?? false)),
        ["nodes"] = nodes,
      };
    });
  }

  // ─── analyzePipeNetworkHydraulics ──────────────────────────────────────────

  public static Task<object?> AnalyzePipeNetworkHydraulicsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var designFlow = PluginRuntime.GetOptionalDouble(parameters, "designFlow");
    var manningsN = PluginRuntime.GetOptionalDouble(parameters, "manningsN") ?? 0.013;
    var minCoverDepthArg = PluginRuntime.GetOptionalDouble(parameters, "minCoverDepth");
    var minVelocityArg = PluginRuntime.GetOptionalDouble(parameters, "minVelocity");
    var maxVelocityArg = PluginRuntime.GetOptionalDouble(parameters, "maxVelocity");
    var minSlope = PluginRuntime.GetOptionalDouble(parameters, "minSlope") ?? 0.5;   // 无量纲（%）

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var pipes = GetPipeData(network, transaction);
      var structures = GetStructureData(network, transaction);

      // 2026-10-15: 默认值按 SI 定义 → 按图纸单位换算；公式常数按单位取
      var f = CivilObjectUtils.LengthFactor(database);
      var k = CivilObjectUtils.ManningConstant(database);
      var g = CivilObjectUtils.Gravity(database);
      var vUnit = CivilObjectUtils.VelocityUnits(database);
      var fUnit = CivilObjectUtils.FlowUnits(database);
      var minCoverDepth = minCoverDepthArg ?? (0.6 * f);
      var minVelocity = minVelocityArg ?? (0.6 * f);
      var maxVelocity = maxVelocityArg ?? (3.0 * f);

      var results = new List<Dictionary<string, object?>>();
      var violations = new List<Dictionary<string, object?>>();

      foreach (var pipe in pipes)
      {
        double diameter = pipe.Diameter;
        // pipe.Slope 是比值（0.0411 = 4.11%）；以前一律 /100 当百分数 → qFull 小 10 倍、判定全错
        double slopeRatio = Math.Abs(pipe.Slope);
        double slopePercent = slopeRatio * 100.0;
        double slope = slopeRatio;
        double area = Math.PI * diameter * diameter / 4.0;
        double hydraulicRadius = diameter / 4.0;
        double qFull = k * (1.0 / manningsN) * area * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Pow(slope, 0.5);
        double vFull = qFull / area;
        double froudeNumber = area > 0 ? vFull / Math.Sqrt(g * hydraulicRadius) : 0;

        double actualFlow = designFlow ?? qFull;
        double capacityRatio = qFull > 0 ? actualFlow / qFull : 0;
        double velocity = area > 0 ? actualFlow / area : 0;

        var pipeViolations = new List<string>();
        if (slopePercent < minSlope) pipeViolations.Add($"slope {slopePercent:F2}% < min {minSlope}%");
        if (velocity < minVelocity) pipeViolations.Add($"velocity {velocity:F2} < min {minVelocity} {vUnit}");
        if (velocity > maxVelocity) pipeViolations.Add($"velocity {velocity:F2} > max {maxVelocity} {vUnit}");
        if (capacityRatio > 1.0) pipeViolations.Add($"flow {actualFlow:F2} exceeds capacity {qFull:F2} {fUnit}");

        var pipeResult = new Dictionary<string, object?>
        {
          ["pipeName"] = pipe.Name,
          ["diameter"] = Math.Round(diameter, 3),
          ["length"] = Math.Round(pipe.Length, 2),
          ["slope"] = Math.Round(slopeRatio, 6),
          ["slopePercent"] = Math.Round(slopePercent, 4),
          ["invertIn"] = Math.Round(pipe.InvertIn, 3),
          ["invertOut"] = Math.Round(pipe.InvertOut, 3),
          ["invertSource"] = pipe.InvertSource,
          ["qFull"] = Math.Round(qFull, 3),
          ["designFlow"] = Math.Round(actualFlow, 3),
          ["capacityRatio"] = Math.Round(capacityRatio, 3),
          ["velocity"] = Math.Round(velocity, 3),
          ["flowUnits"] = fUnit,
          ["velocityUnits"] = vUnit,
          ["froudeNumber"] = Math.Round(froudeNumber, 3),
          ["status"] = pipeViolations.Count > 0 ? "FAIL" : "OK",
          ["violations"] = pipeViolations,
        };

        results.Add(pipeResult);
        if (pipeViolations.Count > 0)
        {
          violations.Add(new Dictionary<string, object?>
          {
            ["pipeName"] = pipe.Name,
            ["violations"] = pipeViolations,
          });
        }
      }

      var structureSummary = structures.Select(s => new Dictionary<string, object?>
      {
        ["name"] = s.Name,
        ["rimElevation"] = Math.Round(s.RimElevation, 3),
        ["sumpElevation"] = Math.Round(s.SumpElevation, 3),
        ["depth"] = Math.Round(s.RimElevation - s.SumpElevation, 3),
        ["connectedPipes"] = s.ConnectedPipes,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["manningsN"] = manningsN,
        ["pipeCount"] = pipes.Count,
        ["passCount"] = results.Count(r => (string)(r["status"] ?? "") == "OK"),
        ["failCount"] = violations.Count,
        ["pipes"] = results,
        ["violations"] = violations,
        ["structures"] = structureSummary,
      };
    });
  }

  // ─── getPipeStructureProperties ─────────────────────────────────────────────

  public static Task<object?> GetPipeStructurePropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var structure = FindStructureByName(network, transaction, structureName);

      var rimElevation = GetAnyDouble(structure, "RimElevation", "SurfaceElevation", "Elevation") ?? 0.0;
      var sumpDepth = GetAnyDouble(structure, "SumpDepth") ?? 0.0;
      var sumpElevation = GetAnyDouble(structure, "SumpElevation") ?? (rimElevation - sumpDepth);
      var depth = rimElevation - sumpElevation;

      // Get connected pipes and their inverts
      var connectedPipeIds = GetChildObjectIds(structure, "GetConnectedPipeIds", "ConnectedPipeIds", "ConnectedPipes");
      var connectedPipes = new List<Dictionary<string, object?>>();
      foreach (var pipeId in connectedPipeIds)
      {
        if (pipeId == ObjectId.Null) continue;
        var pipe = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, pipeId, OpenMode.ForRead);
        var invertIn = GetAnyDouble(pipe, "StartInvertElevation", "InvertIn", "StartInnerElevation") ?? 0.0;
        var invertOut = GetAnyDouble(pipe, "EndInvertElevation", "InvertOut", "EndInnerElevation") ?? 0.0;
        connectedPipes.Add(new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(pipe) ?? pipeId.Handle.ToString(),
          ["invertIn"] = Math.Round(invertIn, 3),
          ["invertOut"] = Math.Round(invertOut, 3),
          ["diameter"] = Math.Round(GetAnyDouble(pipe, "InnerDiameterOrWidth", "InnerDiameter", "Diameter") ?? 0.0, 3),
        });
      }

      // 2026-11-17 P28: 真机同一结构，getStructure 说连了 1 根管，这里返回 0 根（GetConnectedPipeIds 反射拿不到）——
      //   静默空比报错更坏。改用 getStructure 用的那条 API（ConnectedPipesCount + 索引器）兜底，并标来源。
      var connectedPipesSource = connectedPipes.Count > 0 ? "connected-pipe-ids" : "none";
      if (connectedPipes.Count == 0 && structure is Autodesk.Civil.DatabaseServices.Structure typedStructure)
      {
        try
        {
          var structureData = PipeNetworkCommands.ToStructureData(typedStructure, transaction);
          if (structureData["connectedPipes"] is List<string> names)
          {
            foreach (var pipeName in names)
              connectedPipes.Add(new Dictionary<string, object?> { ["name"] = pipeName });
          }
          if (connectedPipes.Count > 0) connectedPipesSource = "structure-api";
        }
        catch (Exception ex)
        {
          PluginLog.Warn("PipeHydraulics", "structure connected-pipe fallback failed: " + ex.Message);
        }
      }

      var location = GetPointProperty(structure, "Location", "Position", "CenterPoint");

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["structureName"] = structureName,
        ["connectedPipesSource"] = connectedPipesSource,
        ["type"] = GetAnyString(structure, "PartType", "StructureType", "PartDescription") ?? structure.GetType().Name,
        ["x"] = Math.Round(location?.X ?? 0.0, 3),
        ["y"] = Math.Round(location?.Y ?? 0.0, 3),
        ["rimElevation"] = Math.Round(rimElevation, 3),
        ["sumpElevation"] = Math.Round(sumpElevation, 3),
        ["sumpDepth"] = Math.Round(sumpDepth, 3),
        ["totalDepth"] = Math.Round(depth, 3),
        ["barrelCount"] = GetAnyDouble(structure, "BarrelCount", "NumberOfBarrels") ?? 1.0,
        ["connectedPipes"] = connectedPipes,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  private sealed record PipeInfo(string Name, double Diameter, double Length, double Slope, double InvertIn, double InvertOut, string InvertSource);
  private sealed record StructureInfo(string Name, double RimElevation, double SumpElevation, List<string> ConnectedPipes);

  private static DBObject FindPipeNetworkByName(object civilDoc, Transaction transaction, string name)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMember(civilDoc, "PipeNetworkCollection"),
      GetNamedMember(civilDoc, "NetworkCollection"),
      GetNamedMember(civilDoc, "PipeNetworks"),
      GetNamedMember(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIds(candidate))
      {
        var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
          return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");
  }

  private static DBObject FindStructureByName(DBObject network, Transaction transaction, string structureName)
  {
    foreach (var objectId in GetChildObjectIds(network, "GetStructureIds", "StructureIds", "Structures", "StructureCollection"))
    {
      var structure = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(structure), structureName, StringComparison.OrdinalIgnoreCase))
        return structure;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{structureName}' was not found.");
  }

  private static List<PipeInfo> GetPipeData(DBObject network, Transaction transaction)
  {
    var results = new List<PipeInfo>();
    foreach (var objectId in GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
    {
      var pipe = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      var startPt = GetPointProperty(pipe, "StartPoint", "PointAtStart");
      var endPt = GetPointProperty(pipe, "EndPoint", "PointAtEnd");
      var diameter = GetAnyDouble(pipe, "InnerDiameterOrWidth", "InnerDiameter", "Diameter") ?? 1.0;
      // 2026-11-17 P28: Civil 3D 的 Pipe API 不暴露管底高程（invert）。以前直接拿"中心线 Z"当 invert，
      //   静默差一个半径（1.5 直径就错 0.75），HGL / 满管判定全跟着偏。现在：能读到就标 api，
      //   读不到就按 中心线 − 内径/2 推导，并在返回里标 invertSource=centerline-derived。
      var invertInApi = GetAnyDouble(pipe, "StartInvertElevation", "InvertIn", "StartInnerElevation");
      var invertOutApi = GetAnyDouble(pipe, "EndInvertElevation", "InvertOut", "EndInnerElevation");
      var invertSource = invertInApi.HasValue && invertOutApi.HasValue ? "api" : "centerline-derived";
      var invertIn = invertInApi ?? ((startPt?.Z ?? 0.0) - diameter / 2.0);
      var invertOut = invertOutApi ?? ((endPt?.Z ?? 0.0) - diameter / 2.0);
      var length = GetAnyDouble(pipe, "Length3D", "Length2D", "Length") ?? 0.0;
      // 2026-11-17 P28: Slope 是**比值**（0.0411 = 4.11%），不是百分数。以前 /100 当百分数用 → 满管流量小 10 倍。
      var slope = GetAnyDouble(pipe, "Slope", "FlowSlope") ?? (length > 0 ? (invertIn - invertOut) / length : 0);
      results.Add(new PipeInfo(CivilObjectUtils.GetName(pipe) ?? objectId.Handle.ToString(), diameter, length, slope, invertIn, invertOut, invertSource));
    }
    return results;
  }

  private static List<StructureInfo> GetStructureData(DBObject network, Transaction transaction)
  {
    var results = new List<StructureInfo>();
    foreach (var objectId in GetChildObjectIds(network, "GetStructureIds", "StructureIds", "Structures", "StructureCollection"))
    {
      var structure = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      var rim = GetAnyDouble(structure, "RimElevation", "SurfaceElevation", "Elevation") ?? 0.0;
      var sumpDepth = GetAnyDouble(structure, "SumpDepth") ?? 0.0;
      var sump = GetAnyDouble(structure, "SumpElevation") ?? (rim - sumpDepth);
      var pipeIds = GetChildObjectIds(structure, "GetConnectedPipeIds", "ConnectedPipeIds", "ConnectedPipes")
        .Select(id => ResolveObjectName(transaction, id) ?? id.Handle.ToString()).ToList();
      results.Add(new StructureInfo(CivilObjectUtils.GetName(structure) ?? objectId.Handle.ToString(), rim, sump, pipeIds));
    }
    return results;
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(DBObject parent, params string[] memberNames)
  {
    foreach (var name in memberNames)
    {
      var value = GetNamedMember(parent, name);
      if (value != null)
      {
        foreach (var id in ToObjectIds(value))
          yield return id;
        yield break;
      }
    }
  }

  private static IEnumerable<ObjectId> ToObjectIds(object? value)
  {
    if (value == null) yield break;

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId id && id != ObjectId.Null)
          yield return id;
        else
        {
          var itemId = GetAnyObjectId(item, "ObjectId", "Id");
          if (itemId != ObjectId.Null) yield return itemId;
        }
      }
    }
  }

  private static object? GetNamedMember(object? obj, string name)
  {
    if (obj == null) return null;
    return Civil3DCompatibility.GetPropertyValue(obj, name)
      ?? Civil3DCompatibility.InvokeMethod(obj, name);
  }

  private static ObjectId GetAnyObjectId(object? obj, params string[] names)
  {
    if (obj == null) return ObjectId.Null;
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val is ObjectId id && id != ObjectId.Null) return id;
    }
    return ObjectId.Null;
  }

  private static double? GetAnyDouble(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val != null)
      {
        try { return Convert.ToDouble(val); }
        catch {
          /* tolerance: value not convertible -> try the next property name */
        }
      }
    }
    return null;
  }

  private static string? GetAnyString(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val != null) return val.ToString();
    }
    return null;
  }

  private static Point3d? GetPointProperty(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val is Point3d pt) return pt;
    }
    return null;
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId id)
  {
    if (id == ObjectId.Null) return null;
    try
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead, false, true);
      return CivilObjectUtils.GetName(obj);
    }
    catch { return null; }
  }
}
