using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D horizontal alignments:
/// add_tangent, add_curve, add_spiral, delete_entity,
/// set_station_equation, get_station_offset,
/// offset_create, widen_transition.
/// </summary>
public static class AlignmentEditCommands
{
  // ─── alignmentAddTangent ──────────────────────────────────────────────────

  public static Task<object?> AddTangentAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      entities.AddFixedLine(new Point3d(startX, startY, 0), new Point3d(endX, endY, 0));

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "add_tangent",
        ["entityIndex"] = entities.Count - 1,
        ["startX"] = startX,
        ["startY"] = startY,
        ["endX"] = endX,
        ["endY"] = endY,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentAddCurve ────────────────────────────────────────────────────

  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var passThroughX = PluginRuntime.GetRequiredDouble(parameters, "passThroughX");
    var passThroughY = PluginRuntime.GetRequiredDouble(parameters, "passThroughY");
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add the requested radius-{radius} curve at ({passThroughX}, {passThroughY}): Civil 3D 2026 requires an explicit clockwise direction or additional geometry. " +
      "The current tool schema does not provide enough information, so no curve was created.");
  }

  // ─── alignmentAddSpiral ───────────────────────────────────────────────────

  public static Task<object?> AddSpiralAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var spiralType = PluginRuntime.GetOptionalString(parameters, "spiralType") ?? "clothoid";
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startRadius = PluginRuntime.GetRequiredDouble(parameters, "startRadius");
    var endRadius = PluginRuntime.GetRequiredDouble(parameters, "endRadius");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add {spiralType} spiral '{alignmentName}' from ({startX}, {startY}) with radii {startRadius}/{endRadius} and length {length}: " +
      "the Civil 3D 2026 AddFixedSpiral overloads require a previous entity id and additional geometric constraints not present in this tool schema. No spiral was created.");
  }

  // ─── alignmentDeleteEntity ────────────────────────────────────────────────

  public static Task<object?> DeleteEntityAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      var count = entities.Count;
      if (entityIndex < 0 || entityIndex >= count)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Entity index {entityIndex} is out of range (alignment has {count} entities).");
      }

      entities.RemoveAt(entityIndex);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "delete_entity",
        ["deletedEntityIndex"] = entityIndex,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentSetReferencePoint ─────────────────────────────────────────
  // 把路线参考点设为指定坐标，参考点桩号设为指定值（实现“圆心=K0+000，北负南正”

  public static Task<object?> SetReferencePointAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var station = PluginRuntime.GetOptionalDouble(parameters, "station") ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      writeAlignment.ReferencePoint = new Point2d(x, y);
      writeAlignment.ReferencePointStation = station;

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["referenceX"] = x,
        ["referenceY"] = y,
        ["referencePointStation"] = station,
        ["startingStation"] = writeAlignment.StartingStation,
        ["endingStation"] = writeAlignment.EndingStation,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentSetStationEquation ─────────────────────────────────────────

  public static Task<object?> SetStationEquationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var rawStation = PluginRuntime.GetRequiredDouble(parameters, "rawStation");
    var nominalStation = PluginRuntime.GetRequiredDouble(parameters, "nominalStation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var equationType = nominalStation >= rawStation
        ? StationEquationType.Increasing
        : StationEquationType.Decreasing;
      writeAlignment.StationEquations.Add(rawStation, nominalStation, equationType);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["rawStation"] = rawStation,
        ["nominalStation"] = nominalStation,
        ["equationType"] = equationType.ToString(),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentGetStationOffset ────────────────────────────────────────────

  public static Task<object?> GetStationOffsetAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      double station = 0;
      double offset = 0;
      alignment.StationOffset(x, y, ref station, ref offset);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // ─── alignmentOffsetCreate ────────────────────────────────────────────────

  public static Task<object?> OffsetCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var offsetName = PluginRuntime.GetRequiredString(parameters, "offsetName");
    var offset = PluginRuntime.GetRequiredDouble(parameters, "offset");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, null);

      // Alignment.CreateOffsetAlignment 25.0.58 真实签名（api-inspect 反射确认）:
      //   4 参: CreateOffsetAlignment(String alignmentName, ObjectId parentAlignmentId, Double offset, ObjectId styleId)
      //   6 参: CreateOffsetAlignment(String alignmentName, ObjectId parentAlignmentId, Double offset, ObjectId styleId, Double startStation, Double endStation)
      // 注：旧代码传 siteId/layerId/labelSetId 与真实签名不匹配，从未成功过（2026-08-05 排查发现）
      var offsetAlignmentId = (ObjectId)(
        CivilObjectUtils.InvokeStaticMethod(typeof(Alignment), "CreateOffsetAlignment",
          offsetName, baseAlignment.ObjectId, offset, styleId)
        ?? CivilObjectUtils.InvokeStaticMethod(typeof(Alignment), "CreateOffsetAlignment",
          offsetName, baseAlignment.ObjectId, offset, styleId, 0.0, 0.0)
        ?? throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          "Alignment.CreateOffsetAlignment is not available in this Civil 3D version."));

      var offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, offsetAlignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = offsetAlignment.Name,
        ["offset"] = offset,
        ["handle"] = CivilObjectUtils.GetHandle(offsetAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentWidenTransition ─────────────────────────────────────────────

  public static Task<object?> WidenTransitionAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var startOffset = PluginRuntime.GetRequiredDouble(parameters, "startOffset");
    var endOffset = PluginRuntime.GetRequiredDouble(parameters, "endOffset");
    var offsetName = PluginRuntime.GetOptionalString(parameters, "offsetName")
      ?? $"{alignmentName}_Widen_{side}";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      // Build a variable-offset alignment using AlignmentOffsetOptions if available,
      // or fall back to creating a simple offset and noting limitations.
      var layerId = LookupUtils.GetLayerId(database, transaction, null);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, null);
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, null);
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, null);

      // Attempt variable offset via Alignment.CreateOffsetAlignment with OffsetOptions
      var offsetAlignmentId = TryCreateWidenTransition(
        civilDoc, transaction, database,
        baseAlignment, offsetName, side,
        startStation, endStation, startOffset, endOffset,
        siteId, layerId, styleId, labelSetId);

      var resultAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, offsetAlignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = resultAlignment.Name,
        ["side"] = side,
        ["startStation"] = startStation,
        ["endStation"] = endStation,
        ["startOffset"] = startOffset,
        ["endOffset"] = endOffset,
        ["handle"] = CivilObjectUtils.GetHandle(resultAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────

  private static ObjectId TryCreateWidenTransition(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    Database database,
    Alignment baseAlignment,
    string offsetName,
    string side,
    double startStation,
    double endStation,
    double startOffset,
    double endOffset,
    ObjectId siteId,
    ObjectId layerId,
    ObjectId styleId,
    ObjectId labelSetId)
  {
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 does not expose Alignment.CreateWideningAlignment in the managed API. No constant-offset substitute was created.");
  }
}
