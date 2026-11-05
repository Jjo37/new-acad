using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Civil 3D Point Group management and point export/transform.
///
/// Uses the documented PointGroup and StandardPointGroupQuery managed APIs.
/// </summary>
public static class PointGroupCommands
{
  // -------------------------------------------------------------------------
  // createPointGroup
  // -------------------------------------------------------------------------

  public static Task<object?> CreatePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var includeNumbers = PluginRuntime.GetOptionalString(parameters, "includeNumbers");
    var excludeNumbers = PluginRuntime.GetOptionalString(parameters, "excludeNumbers");
    var includeDescriptions = PluginRuntime.GetOptionalString(parameters, "includeDescriptions");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var newGroupId = civilDoc.PointGroups.Add(name);
      var newGroup = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, newGroupId, OpenMode.ForWrite);
      newGroup.Description = description;
      ApplyStandardQuery(newGroup, includeNumbers, excludeNumbers, includeDescriptions);

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["handle"] = CivilObjectUtils.GetHandle(newGroup),
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // updatePointGroup
  // -------------------------------------------------------------------------

  public static Task<object?> UpdatePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var includeNumbers = PluginRuntime.GetOptionalString(parameters, "includeNumbers");
    var excludeNumbers = PluginRuntime.GetOptionalString(parameters, "excludeNumbers");
    var includeDescriptions = PluginRuntime.GetOptionalString(parameters, "includeDescriptions");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindPointGroupByName(civilDoc, transaction, name, OpenMode.ForWrite);

      if (description != null) group.Description = description;
      ApplyStandardQuery(group, includeNumbers, excludeNumbers, includeDescriptions);

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["updated"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // deletePointGroup
  // -------------------------------------------------------------------------

  public static Task<object?> DeletePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindPointGroupByName(civilDoc, transaction, name, OpenMode.ForWrite);
      group.Erase();

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // exportCogoPoints
  // -------------------------------------------------------------------------

  public static Task<object?> ExportCogoPointsAsync(JsonObject? parameters)
  {
    var format = PluginRuntime.GetOptionalString(parameters, "format") ?? "pnezd";
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");
    var numbersNode = PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray;
    var delimiter = PluginRuntime.GetOptionalString(parameters, "delimiter") ?? ",";

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? allowedNumbers = null;

      if (!string.IsNullOrWhiteSpace(groupName))
      {
        var group = FindPointGroupByName(civilDoc, transaction, groupName!, OpenMode.ForRead);
        allowedNumbers = new HashSet<uint>(group.GetPointNumbers());
      }

      if (numbersNode != null && numbersNode.Count > 0)
      {
        var explicit_ = new HashSet<uint>(numbersNode.Select(n => (uint)PluginRuntime.GetRequiredIntFromNode(n, "pointNumbers[]")).Where(n => n > 0));
        allowedNumbers = allowedNumbers == null ? explicit_ : new HashSet<uint>(allowedNumbers.Intersect(explicit_));
      }

      var sb = new StringBuilder();
      var exportedCount = 0;

      foreach (ObjectId objectId in civilDoc.CogoPoints)
      {
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, OpenMode.ForRead);
        if (allowedNumbers != null && !allowedNumbers.Contains(point.PointNumber)) continue;

        var line = format switch
        {
          "pnezd" => $"{point.PointNumber}{delimiter}{point.Location.X}{delimiter}{point.Location.Y}{delimiter}{point.Location.Z}{delimiter}{point.RawDescription}",
          "penz" => $"{point.PointNumber}{delimiter}{point.Location.X}{delimiter}{point.Location.Z}{delimiter}{point.Location.Y}{delimiter}{point.RawDescription}",
          "xyzd" => $"{point.Location.X}{delimiter}{point.Location.Y}{delimiter}{point.Location.Z}{delimiter}{point.RawDescription}",
          "xyz" => $"{point.Location.X}{delimiter}{point.Location.Y}{delimiter}{point.Location.Z}",
          "csv" => $"{point.PointNumber}{delimiter}{point.PointName}{delimiter}{point.Location.X}{delimiter}{point.Location.Y}{delimiter}{point.Location.Z}{delimiter}{point.RawDescription}",
          _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported export format: {format}"),
        };

        sb.AppendLine(line);
        exportedCount++;
      }

      return new Dictionary<string, object?>
      {
        ["format"] = format,
        ["exportedCount"] = exportedCount,
        ["data"] = sb.ToString(),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // transformCogoPoints
  // -------------------------------------------------------------------------

  public static Task<object?> TransformCogoPointsAsync(JsonObject? parameters)
  {
    var numbersNode = PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray;
    var translateX = PluginRuntime.GetOptionalDouble(parameters, "translateX") ?? 0;
    var translateY = PluginRuntime.GetOptionalDouble(parameters, "translateY") ?? 0;
    var translateZ = PluginRuntime.GetOptionalDouble(parameters, "translateZ") ?? 0;
    var rotateRadians = PluginRuntime.GetOptionalDouble(parameters, "rotateRadians") ?? 0;
    var scaleFactor = PluginRuntime.GetOptionalDouble(parameters, "scaleFactor") ?? 1.0;
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? targetNumbers = null;

      if (numbersNode != null && numbersNode.Count > 0)
      {
        targetNumbers = new HashSet<uint>(numbersNode.Select(n => (uint)PluginRuntime.GetRequiredIntFromNode(n, "pointNumbers[]")).Where(n => n > 0));
      }
      else if (!string.IsNullOrWhiteSpace(groupName))
      {
        var group = FindPointGroupByName(civilDoc, transaction, groupName!, OpenMode.ForRead);
        targetNumbers = new HashSet<uint>(group.GetPointNumbers());
      }

      var transformedCount = 0;
      var sinRot = Math.Sin(rotateRadians);
      var cosRot = Math.Cos(rotateRadians);

      foreach (ObjectId objectId in civilDoc.CogoPoints)
      {
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, OpenMode.ForRead);
        if (targetNumbers != null && !targetNumbers.Contains(point.PointNumber)) continue;

        var writablePoint = transaction.GetObject(objectId, OpenMode.ForWrite) as CogoPoint;
        if (writablePoint == null) continue;

        var x = writablePoint.Location.X;
        var y = writablePoint.Location.Y;
        var z = writablePoint.Location.Z;

        // Scale around origin
        x *= scaleFactor;
        y *= scaleFactor;

        // Rotate around origin
        if (rotateRadians != 0)
        {
          var rx = x * cosRot - y * sinRot;
          var ry = x * sinRot + y * cosRot;
          x = rx;
          y = ry;
        }

        // Translate
        x += translateX;
        y += translateY;
        z += translateZ;

        SetPointCoordinate(writablePoint, x, y, z);
        transformedCount++;
      }

      return new Dictionary<string, object?>
      {
        ["transformedCount"] = transformedCount,
        ["translateX"] = translateX,
        ["translateY"] = translateY,
        ["translateZ"] = translateZ,
        ["rotateRadians"] = rotateRadians,
        ["scaleFactor"] = scaleFactor,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private static PointGroup FindPointGroupByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name,
    OpenMode mode)
  {
    foreach (ObjectId objectId in civilDoc.PointGroups)
    {
      var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, mode);
      if (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return group;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{name}' was not found.");
  }

  private static void ApplyStandardQuery(
    PointGroup group,
    string? includeNumbers,
    string? excludeNumbers,
    string? includeDescriptions)
  {
    if (includeNumbers == null && excludeNumbers == null && includeDescriptions == null)
    {
      return;
    }

    var existingQuery = group.GetQuery();
    if (existingQuery is not null && existingQuery is not StandardPointGroupQuery)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Point group '{group.Name}' uses a custom query. Standard filters were not applied because doing so would discard its existing QueryString criteria.");
    }

    var query = existingQuery as StandardPointGroupQuery ?? new StandardPointGroupQuery();
    if (includeNumbers != null) query.IncludeNumbers = includeNumbers;
    if (excludeNumbers != null) query.ExcludeNumbers = excludeNumbers;
    if (includeDescriptions != null) query.IncludeRawDescriptions = includeDescriptions;
    group.SetQuery(query);
    group.Update();
  }

  private static void SetPointCoordinate(CogoPoint point, double x, double y, double z)
  {
    try
    {
      point.Easting = x;
      point.Northing = y;
      point.Elevation = z;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"COGO point {point.PointNumber} could not be moved: {exception.Message}");
    }
  }
}
