using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

public static class CivilObjectUtils
{
  public static string GetHandle(AcDbObject dbObject)
  {
    return dbObject.Handle.ToString();
  }

  public static string? GetName(object? value)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, "Name")?.ToString();
  }

  public static string? GetStringProperty(object? value, string propertyName)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, propertyName)?.ToString();
  }

  public static T? GetPropertyValue<T>(object? value, string propertyName)
  {
    if (value == null)
    {
      return default;
    }

    return Civil3DCompatibility.GetPropertyValue<T>(value, propertyName);
  }

  public static object? InvokeMethod(object? value, string methodName, params object?[] arguments)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.InvokeMethod(value, methodName, arguments);
  }

  public static IEnumerable<ObjectId> ToObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }
      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }

  public static T GetRequiredObject<T>(Transaction transaction, ObjectId objectId, OpenMode openMode) where T : AcDbObject
  {
    return (T)(transaction.GetObject(objectId, openMode) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Object {objectId} not found."));
  }

  public static string LinearUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "meters";
    if (database.Insunits == UnitsValue.Feet) return "feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "feet";
    return "other";
  }

  // ── 2026-10-15: 单位感知（默认值按 SI 定义，按图纸单位换算；公式常数按单位取）──

  /// <summary>图纸是否公制（米）。</summary>
  public static bool IsMetric(Database database) => database.Insunits == UnitsValue.Meters;

  /// <summary>
  /// 长度换算系数：默认值统一按 SI（米）定义，乘以该系数得到图纸单位下的值。
  /// 公制=1.0；英尺（含 Survey）=3.28084；未知/未设 → 1.0（按公制处理）。
  /// </summary>
  public static double LengthFactor(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return 1.0;
    if (database.Insunits == UnitsValue.Feet) return 3.280839895013123;
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return 3.280839895013123;
    return 1.0;
  }

  /// <summary>重力加速度（与图纸长度单位一致）：公制 9.81 m/s²，英制 32.2 ft/s²。</summary>
  public static double Gravity(Database database) => IsMetric(database) ? 9.81 : 32.2;

  /// <summary>Manning 公式常数：SI=1.0，US customary（英尺）=1.486。</summary>
  public static double ManningConstant(Database database) => IsMetric(database) ? 1.0 : 1.486;

  /// <summary>流速单位标签。</summary>
  public static string VelocityUnits(Database database) => IsMetric(database) ? "m/s" : "ft/s";

  /// <summary>流量单位标签。</summary>
  public static string FlowUnits(Database database) => IsMetric(database) ? "m³/s" : "cfs";

  // ── 活动图纸版（命令上下文拿不到 database 参数时用；取不到按公制）──
  /// <summary>2026-10-15: 解析请求级单位覆盖（"metric"/"imperial"…）。命中返回 true 并给出 isMetric。</summary>
  private static bool TryParseUnitOverride(string? units, out bool isMetric)
  {
    isMetric = true;
    if (string.IsNullOrWhiteSpace(units)) return false;
    var u = units.Trim().ToLowerInvariant();
    if (u.StartsWith("met") || u == "m" || u == "si" || u == "meters" || u == "metric" || u.Contains("公制")) { isMetric = true; return true; }
    if (u.StartsWith("imp") || u == "ft" || u == "feet" || u == "us" || u == "imperial" || u.Contains("英制")) { isMetric = false; return true; }
    return false;   // "auto"/未知 → 回退图纸探测
  }

  /// <summary>长度换算系数：优先请求级覆盖，否则按活动图纸（取不到 → 1.0）。</summary>
  public static double ActiveLengthFactor()
  {
    if (TryParseUnitOverride(PluginRuntime.GetCurrentUnitOverride(), out var m)) return m ? 1.0 : 3.280839895013123;
    try { return LengthFactor(HostApplicationServices.WorkingDatabase); } catch { return 1.0; }
  }

  /// <summary>重力加速度：优先请求级覆盖，否则按活动图纸。</summary>
  public static double ActiveGravity()
  {
    if (TryParseUnitOverride(PluginRuntime.GetCurrentUnitOverride(), out var m)) return m ? 9.81 : 32.2;
    try { return Gravity(HostApplicationServices.WorkingDatabase); } catch { return 9.81; }
  }

  /// <summary>是否公制：优先请求级覆盖，否则按活动图纸（取不到 → true）。</summary>
  public static bool ActiveIsMetric()
  {
    if (TryParseUnitOverride(PluginRuntime.GetCurrentUnitOverride(), out var m)) return m;
    try { return IsMetric(HostApplicationServices.WorkingDatabase); } catch { return true; }
  }

  public static string AngularUnits(short aunits)
  {
    return aunits switch
    {
      0 => "degrees",
      1 => "degrees",
      2 => "grads",
      3 => "radians",
      _ => "degrees",
    };
  }

  public static Alignment FindAlignmentByName(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return alignment;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment '{name}' was not found.");
  }

  public static Profile FindProfileByName(Alignment alignment, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in alignment.GetProfileIds())
    {
      var profile = GetRequiredObject<Profile>(transaction, objectId, openMode);
      if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return profile;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{name}' was not found on alignment '{alignment.Name}'.");
  }

  public static CivilSurface FindSurfaceByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.GetSurfaceIds())
    {
      var surface = GetRequiredObject<CivilSurface>(transaction, objectId, openMode);
      if (string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return surface;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Surface '{name}' was not found.");
  }

  public static Corridor FindCorridorByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.CorridorCollection)
    {
      var corridor = GetRequiredObject<Corridor>(transaction, objectId, openMode);
      if (string.Equals(corridor.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return corridor;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor '{name}' was not found.");
  }

  public static Database GetDatabase(object? civilDoc)
  {
    if (civilDoc != null)
    {
      var db = GetPropertyValue<Database>(civilDoc, "Database");
      if (db != null)
      {
        return db;
      }
    }

    return HostApplicationServices.WorkingDatabase
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "No active AutoCAD database is available.");
  }

  public static ObjectId GetModelSpaceBlockId(Database database, Transaction transaction)
  {
    var blockTable = GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    return blockTable[BlockTableRecord.ModelSpace];
  }

  public static double? GetDoubleProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToDouble(raw); } catch { return null; }
  }

  public static bool? GetBoolProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToBoolean(raw); } catch { return null; }
  }

  public static string VolumeUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "cubic meters";
    if (database.Insunits == UnitsValue.Feet) return "cubic feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "cubic feet";
    return "cubic units";
  }

  public static void TrySetName(AcDbObject obj, string name)
  {
    Civil3DCompatibility.TrySetProperty(obj, "Name", name);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    return Civil3DCompatibility.InvokeStaticMethod(type, methodName, arguments);
  }

  public static bool TrySetLayer(AcDbObject obj, string layer, Database database, Transaction transaction)
  {
    try
    {
      var layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
      if (layerTable == null) return false;
      if (!layerTable.Has(layer))
      {
        // 2026-08-12 M10: 同事务已 ForRead, 用 UpgradeOpen 避免双开
        layerTable.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = layer };
        layerTable.Add(ltr);
        transaction.AddNewlyCreatedDBObject(ltr, true);
        layerTable.DowngradeOpen();
      }
      return Civil3DCompatibility.TrySetProperty(obj, "Layer", layer);
    }
    catch { return false; /* 失败返回 false, 调用方可感知 */ }
  }

  public static Dictionary<string, object?> ToPointData(CogoPoint point)
  {
    return new Dictionary<string, object?>
    {
      ["number"] = point.PointNumber,
      ["name"] = point.PointName,
      ["x"] = point.Location.X,
      ["y"] = point.Location.Y,
      ["z"] = point.Location.Z,
      ["rawDescription"] = point.RawDescription,
      ["fullDescription"] = point.FullDescription,
    };
  }
}
