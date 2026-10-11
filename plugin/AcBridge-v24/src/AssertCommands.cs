using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace Civil3DMcpPlugin;

/// <summary>
/// B3.1（2026-09-28）：**几何/图面断言原语**（决策链可验证性第二件）。
///
/// 一条命令跑完一串检查，返回逐项结果而非"感觉做完了"：
///   assertChecks {checks:[ ... ]}
/// 支持的 check.type：
///   entityExists   { handle }                          图元存在
///   entityCount    { layer?, entityType?, expected, op? }   图元计数（op: eq|ge|le|gt|lt|ne，默认 eq）
///   layerExists    { layer }
///   value          { handle, field, expected, tolerance? }  field: length|area|radius|vertexCount|count
///   surfaceBuilt   { name, minTriangles?=1 }           曲面已构建（三角面>0）
/// 返回 {total, passed, failed, pass, results:[{index,type,pass,actual,expected,note?}]}
/// </summary>
public static class AssertCommands
{
  public static Task<object?> AssertChecksAsync(JsonObject? parameters)
  {
    var checks = PluginRuntime.GetParameter(parameters, "checks") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "checks 数组必填：{checks:[{type, ...}]}");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
        transaction,
        CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead)[BlockTableRecord.ModelSpace],
        OpenMode.ForRead);

      var results = new List<Dictionary<string, object?>>();
      var passed = 0;
      var index = 0;

      foreach (var node in checks)
      {
        index++;
        var check = node as JsonObject;
        if (check == null)
        {
          results.Add(Result(index, "invalid", false, null, null, "check must be an object"));
          continue;
        }
        var type = check["type"]?.GetValue<string>() ?? "";
        try
        {
          var (ok, actual, expected, note) = RunCheck(check, type, database, transaction, modelDocSpace: modelSpace, civilDoc: civilDoc);
          if (ok) passed++;
          results.Add(Result(index, type, ok, actual, expected, note));
        }
        catch (Exception ex)
        {
          results.Add(Result(index, type, false, null, null, ex.GetType().Name + ": " + ex.Message));
        }
      }

      var total = results.Count;
      return new Dictionary<string, object?>
      {
        ["total"] = total,
        ["passed"] = passed,
        ["failed"] = total - passed,
        ["pass"] = passed == total && total > 0,
        ["results"] = results,
      };
    });
  }

  private static Dictionary<string, object?> Result(int index, string type, bool pass, object? actual, object? expected, string? note)
  {
    var d = new Dictionary<string, object?>
    {
      ["index"] = index,
      ["type"] = type,
      ["pass"] = pass,
      ["actual"] = actual,
      ["expected"] = expected,
    };
    if (!string.IsNullOrEmpty(note)) d["note"] = note;
    return d;
  }

  private static (bool ok, object? actual, object? expected, string? note) RunCheck(
    JsonObject check, string type, Database database, Transaction transaction, BlockTableRecord modelDocSpace,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    switch (type)
    {
      case "entityExists":
        {
          var handle = PluginRuntime.GetRequiredString(check, "handle");
          var found = TryToObjectId(database, handle, out var id) && !id.IsNull && !IsErased(transaction, id);
          return (found, found ? "exists" : "missing", "exists", null);
        }

      case "layerExists":
        {
          var layer = PluginRuntime.GetRequiredString(check, "layer");
          var lt = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
          var exists = lt.Has(layer);
          return (exists, exists ? "exists" : "missing", "exists", null);
        }

      case "entityCount":
        {
          var layer = PluginRuntime.GetOptionalString(check, "layer");
          var entityType = PluginRuntime.GetOptionalString(check, "entityType");
          var expected = PluginRuntime.GetRequiredDouble(check, "expected");
          var op = PluginRuntime.GetOptionalString(check, "op") ?? "eq";
          var count = 0;
          foreach (ObjectId id in modelDocSpace)
          {
            if (id.IsNull || id.IsErased) continue;
            Entity? e;
            try { e = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
            if (e == null) continue;
            if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(entityType) && !MatchesTypeName(e, entityType!)) continue;
            count++;
          }
          var ok = Compare(count, expected, op);
          return (ok, count, $"{op} {expected}", null);
        }

      case "value":
        {
          var handle = PluginRuntime.GetRequiredString(check, "handle");
          var field = PluginRuntime.GetRequiredString(check, "field");
          var expected = PluginRuntime.GetRequiredDouble(check, "expected");
          var tol = PluginRuntime.GetOptionalDouble(check, "tolerance") ?? 1e-6;
          if (!TryToObjectId(database, handle, out var id) || id.IsNull)
            return (false, null, expected, "handle 不存在");
          var obj = transaction.GetObject(id, OpenMode.ForRead, false);
          double? actual = field.ToLowerInvariant() switch
          {
            "length" => ReadLength(obj as Curve),
            "area" => ReadArea(obj),
            "radius" => (obj as Circle)?.Radius,
            "vertexcount" => (obj as Polyline)?.NumberOfVertices,
            "count" => null,
            _ => null,
          };
          if (actual == null) return (false, null, expected, $"无法读取字段 '{field}'（类型 {obj?.GetType().Name}）");
          var ok = Math.Abs(actual.Value - expected) <= tol;
          return (ok, actual.Value, expected, $"tolerance={tol}");
        }

      case "surfaceBuilt":
        {
          var name = PluginRuntime.GetRequiredString(check, "name");
          var minTri = PluginRuntime.GetOptionalInt(check, "minTriangles") ?? 1;
          var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
          var tri = GetTriangleCountSafe(transaction, surface);
          return (tri >= minTri, tri, $">= {minTri}", null);
        }

      default:
        return (false, null, null, "未知 check.type: " + type);
    }
  }

  private static bool Compare(double actual, double expected, string op) => op.ToLowerInvariant() switch
  {
    "eq" => Math.Abs(actual - expected) < 1e-9,
    "ne" => Math.Abs(actual - expected) >= 1e-9,
    "ge" => actual >= expected,
    "le" => actual <= expected,
    "gt" => actual > expected,
    "lt" => actual < expected,
    _ => false,
  };

  private static bool MatchesTypeName(Entity entity, string typeName)
  {
    var t = entity.GetType().Name;
    var t2 = "AcDb" + t;
    return string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase)
      || string.Equals(t2, typeName, StringComparison.OrdinalIgnoreCase)
      || t.Contains(typeName, StringComparison.OrdinalIgnoreCase);
  }

  private static bool TryToObjectId(Database database, string handle, out ObjectId id)
  {
    id = ObjectId.Null;
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var value)) return false;
    return database.TryGetObjectId(new Handle(value), out id) && !id.IsNull;
  }

  private static bool IsErased(Transaction transaction, ObjectId id)
  {
    try { return transaction.GetObject(id, OpenMode.ForRead, false) is Entity e && e.IsErased; }
    catch { return true; }
  }

  private static double? ReadLength(Curve? curve)
  {
    if (curve == null) return null;
    try { return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam); }
    catch { return null; }
  }

  private static double? ReadArea(DBObject? obj) => obj switch
  {
    Hatch hatch => hatch.Area,
    Polyline pl when pl.Closed => pl.Area,
    Circle circle => Math.PI * circle.Radius * circle.Radius,
    _ => null,
  };

  private static int GetTriangleCountSafe(Transaction transaction, CivilSurface surface)
  {
    try
    {
      if (surface is TinSurface tin) return tin.GetTinProperties().NumberOfTriangles;
      return 0;
    }
    catch { return 0; }
  }
}
