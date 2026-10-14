using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class PointCommands
{
  public static Task<object?> ListCogoPointsAsync(JsonObject? parameters)
  {
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");
    // 2026-08-12 性能修复: 默认 limit 1000(旧 int.MaxValue——几万点全量物化必超时); 大图纸明确传 limit
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 1000;
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? allowedNumbers = null;
      if (!string.IsNullOrWhiteSpace(groupName))
      {
        allowedNumbers = TryGetPointNumbersForGroup(civilDoc, transaction, groupName!);
      }

      // 2026-08-12 性能修复: 轻量枚举(只读 PointNumber, 不打开全属性) → 排序分页 → 只对页内点 ToPointData
      // 旧实现: 全量打开所有 CogoPoint 对象 + ToList 物化 → 4.3万图元图纸(几千点)超 25s 超时
      var allEntries = EnumeratePointNumbers(civilDoc, transaction)
        .Where(e => allowedNumbers == null || allowedNumbers.Contains(e.Number))
        .OrderBy(e => e.Number)
        .ToList();

      var page = allEntries.Skip(offset).Take(limit)
        .Select(e => CivilObjectUtils.ToPointData(
          CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, e.Id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["totalCount"] = allEntries.Count,
        ["returnedCount"] = page.Count,
        ["points"] = page,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> GetCogoPointAsync(JsonObject? parameters)
  {
    var pointNumber = (uint)PluginRuntime.GetRequiredInt(parameters, "pointNumber");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      return CivilObjectUtils.ToPointData(point);
    });
  }

  public static Task<object?> CreateCogoPointsAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createCogoPoints requires a non-empty 'points' array.");
    }

    var startNumber = PluginRuntime.GetOptionalInt(parameters, "startNumber");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdNumbers = new List<uint>();
      var collection = civilDoc.CogoPoints;

      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject pointNode)
        {
          continue;
        }

        var x = PluginRuntime.GetRequiredDoubleFromNode(pointNode["x"], $"points[{index}].x");
        var y = PluginRuntime.GetRequiredDoubleFromNode(pointNode["y"], $"points[{index}].y");
        var z = PluginRuntime.GetRequiredDoubleFromNode(pointNode["z"], $"points[{index}].z");
        var description = PluginRuntime.JsonNodeToString(pointNode["description"]) ?? string.Empty;

        var objectId = collection.Add(new Point3d(x, y, z), description, true);
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

        if (startNumber.HasValue)
        {
          point.PointNumber = (uint)(startNumber.Value + index);
        }

        createdNumbers.Add(point.PointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["created"] = createdNumbers.Count,
        ["pointNumbers"] = createdNumbers,
      };
    });
  }

  // 2026-08-07 框架: 文件通道——从文件导入 Cogo 点（AI 只需传路径+格式，数据不走 LLM 上下文）
  public static Task<object?> ImportCogoPointsFromFileAsync(JsonObject? parameters)
  {
    var format = PluginRuntime.GetRequiredString(parameters, "format");
    var filePath = PluginRuntime.GetRequiredString(parameters, "filePath");
    // 2026-08-12: 套 FileBoundary（全盘已放开, 补防穿越/规范化护栏; 旧实现裸读无检查）
    filePath = FileBoundary.ResolveImportPath(filePath, "csv", "txt", "pnezd", "penz", "xyz", "xyzd");
    var data = string.Join("\n", System.IO.File.ReadAllLines(filePath));   // 读文件 → 复用现有解析器
    var targetSurface = PluginRuntime.GetOptionalString(parameters, "targetSurface");
    if (!string.IsNullOrWhiteSpace(targetSurface))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Importing COGO points directly into a target surface is not implemented. No points were imported.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdNumbers = new List<uint>();
      foreach (var parsed in ParsePointImport(data, format))
      {
        var objectId = civilDoc.CogoPoints.Add(new Point3d(parsed.X, parsed.Y, parsed.Z), parsed.Description, true);
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        createdNumbers.Add(point.PointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["imported"] = createdNumbers.Count,
        ["pointNumbers"] = createdNumbers,
        ["targetSurface"] = null,
        ["source"] = filePath,
      };
    });
  }

  public static Task<object?> ImportCogoPointsAsync(JsonObject? parameters)
  {
    var format = PluginRuntime.GetRequiredString(parameters, "format");
    var data = PluginRuntime.GetRequiredString(parameters, "data");
    var targetSurface = PluginRuntime.GetOptionalString(parameters, "targetSurface");
    if (!string.IsNullOrWhiteSpace(targetSurface))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Importing COGO points directly into a target surface is not implemented. No points were imported.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdNumbers = new List<uint>();
      foreach (var parsed in ParsePointImport(data, format))
      {
        var objectId = civilDoc.CogoPoints.Add(new Point3d(parsed.X, parsed.Y, parsed.Z), parsed.Description, true);
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        createdNumbers.Add(point.PointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["imported"] = createdNumbers.Count,
        ["pointNumbers"] = createdNumbers,
        ["targetSurface"] = null,
      };
    });
  }

  public static Task<object?> EditCogoPointAsync(JsonObject? parameters)
  {
    var pointNumber = (uint)PluginRuntime.GetRequiredInt(parameters, "pointNumber");
    var newNumber = PluginRuntime.GetOptionalInt(parameters, "newNumber");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var elevation = PluginRuntime.GetOptionalDouble(parameters, "elevation");
    if (!newNumber.HasValue && description == null && !elevation.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "editCogoPoint requires at least one of: newNumber/description/elevation");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
      var applied = new List<string>();
      if (newNumber.HasValue) { point.PointNumber = (uint)newNumber.Value; applied.Add("newNumber"); }
      if (description != null) { point.RawDescription = description; applied.Add("description"); }
      if (elevation.HasValue) { point.Elevation = elevation.Value; applied.Add("elevation"); }
      return new Dictionary<string, object?>
      {
        ["pointNumber"] = pointNumber,
        ["applied"] = applied,
        ["updated"] = CivilObjectUtils.ToPointData(point),
      };
    });
  }

  public static Task<object?> DeleteCogoPointsAsync(JsonObject? parameters)
  {
    var numbersNode = PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray;
    if (numbersNode == null || numbersNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "deleteCogoPoints requires 'pointNumbers'.");
    }

    var pointNumbers = numbersNode.Select(node => (uint)PluginRuntime.GetRequiredIntFromNode(node, "pointNumbers[]")).Where(n => n > 0).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var deleted = new List<uint>();
      foreach (var pointNumber in pointNumbers)
      {
        var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
        point.Erase();
        deleted.Add(pointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["deleted"] = deleted.Count,
        ["pointNumbers"] = deleted,
      };
    });
  }

  public static Task<object?> ListPointGroupsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groups = new List<Dictionary<string, object?>>();
      foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.PointGroups)
      {
        var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        var query = group.GetQuery() as StandardPointGroupQuery;
        groups.Add(new Dictionary<string, object?>
        {
          ["name"] = group.Name,
          ["description"] = group.Description,
          ["pointCount"] = group.PointsCount,
          ["includePattern"] = query?.IncludeNumbers,
          ["excludePattern"] = query?.ExcludeNumbers,
        });
      }

      return new Dictionary<string, object?>
      {
        ["groups"] = groups,
      };
    });
  }

  private static IEnumerable<CogoPoint> EnumeratePoints(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      yield return CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
    }
  }

  // 2026-08-12: 轻量枚举——只读 PointNumber(排序/过滤需要), 不打开全属性。分页前用, 避免全量物化超时
  private static IEnumerable<(Autodesk.AutoCAD.DatabaseServices.ObjectId Id, uint Number)> EnumeratePointNumbers(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      yield return (objectId, point.PointNumber);
    }
  }

  private static CogoPoint FindPointByNumber(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction, uint pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode openMode)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, openMode);
      if (point.PointNumber == pointNumber)
      {
        return point;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"COGO point '{pointNumber}' was not found.");
  }

  private static HashSet<uint>? TryGetPointNumbersForGroup(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction, string groupName)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.PointGroups)
    {
      var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      if (!string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      return new HashSet<uint>(group.GetPointNumbers());
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{groupName}' was not found.");
  }

  private static IEnumerable<(double X, double Y, double Z, string Description)> ParsePointImport(string data, string format)
  {
    foreach (var rawLine in data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
    {
      var line = rawLine.Trim();
      if (line.Length == 0)
      {
        continue;
      }

      var tokens = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
      yield return format switch
      {
        "pnezd" when tokens.Length >= 5 => (double.Parse(tokens[1]), double.Parse(tokens[2]), double.Parse(tokens[3]), tokens[4]),
        "penz" when tokens.Length >= 5 => (double.Parse(tokens[1]), double.Parse(tokens[3]), double.Parse(tokens[2]), tokens[4]),
        "xyzd" when tokens.Length >= 4 => (double.Parse(tokens[0]), double.Parse(tokens[1]), double.Parse(tokens[2]), tokens[3]),
        "xyz" when tokens.Length >= 3 => (double.Parse(tokens[0]), double.Parse(tokens[1]), double.Parse(tokens[2]), string.Empty),
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported point import line for format '{format}': {line}"),
      };
    }
  }
}
