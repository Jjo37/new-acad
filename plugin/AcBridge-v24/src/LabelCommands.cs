using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

public static class LabelCommands
{
  public static Task<object?> ListLabelStylesAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styles = GetLabelStyleCollection(civilDoc, objectType);
      var styleItems = styles
        .Select(objectId => transaction.GetObject(objectId, OpenMode.ForRead))
        .Where(style => style != null)
        .Select(style => new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(style),
          ["handle"] = style is DBObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["description"] = CivilObjectUtils.GetStringProperty(style, "Description"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["objectType"] = objectType,
        ["styles"] = styleItems,
      };
    });
  }

  public static Task<object?> ListLabelsAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = ResolveTargetObject(civilDoc, transaction, objectType, objectName, OpenMode.ForRead);
      var labels = GetLabelIds(target)
        .Select(objectId => transaction.GetObject(objectId, OpenMode.ForRead))
        .Where(label => label != null)
        .Select(label => new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(label),
          ["handle"] = label is DBObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["type"] = label?.GetType().Name,
          ["style"] = ResolveObjectName(transaction, GetObjectId(label, "StyleId")),
          ["text"] = CivilObjectUtils.GetStringProperty(label, "TextOverride") ?? CivilObjectUtils.GetStringProperty(label, "Text"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["objectType"] = objectType,
        ["objectName"] = objectName,
        ["labels"] = labels,
      };
    });
  }

  public static Task<object?> AddLabelAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");
    var labelType = PluginRuntime.GetRequiredString(parameters, "labelType");
    var labelStyle = PluginRuntime.GetOptionalString(parameters, "labelStyle");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = ResolveTargetObject(civilDoc, transaction, objectType, objectName, OpenMode.ForWrite);

      if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "label_set", StringComparison.OrdinalIgnoreCase))
      {
        var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelStyle);
        ImportLabelSet(target, labelSetId);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, labelSetId),
          ["applied"] = true,
        };
      }

      if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "station", StringComparison.OrdinalIgnoreCase))
      {
        // station 参数作为桩号标签间距 increment（AlignmentStationLabelGroup.Create 的第三个参数）
        var increment = PluginRuntime.GetOptionalDouble(parameters, "increment")
          ?? PluginRuntime.GetOptionalDouble(parameters, "station")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Alignment station labels require 'station' (桩号间距, 如 20/50/100).");
        var styleId = FindStationLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = CreateAlignmentStationLabel(target, styleId, increment);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["increment"] = increment,
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "label_set", StringComparison.OrdinalIgnoreCase))
      {
        var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, labelStyle);
        ImportLabelSet(target, labelSetId);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, labelSetId),
          ["applied"] = true,
        };
      }

      if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "station", StringComparison.OrdinalIgnoreCase))
      {
        var increment = PluginRuntime.GetOptionalDouble(parameters, "increment")
          ?? PluginRuntime.GetOptionalDouble(parameters, "station")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Profile station labels require 'station' (桩号间距).");
        var styleId = FindStationLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = CreateProfileStationLabel(civilDoc, transaction, target, styleId, increment);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["increment"] = increment,
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "surface", StringComparison.OrdinalIgnoreCase) && (string.Equals(labelType, "spot_elevation", StringComparison.OrdinalIgnoreCase) || string.Equals(labelType, "spot", StringComparison.OrdinalIgnoreCase)))
      {
        var pointNode = PluginRuntime.GetParameter(parameters, "point") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require 'point'.");
        var point = new Point2d(
          pointNode["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require point.x."),
          pointNode["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require point.y.")
        );
        var styleId = FindLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = CreateSurfaceSpotLabel(target, styleId, point);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["point"] = new Dictionary<string, object?>
          {
            ["x"] = point.X,
            ["y"] = point.Y,
          },
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "pipe", StringComparison.OrdinalIgnoreCase))
      {
        var styleId = FindLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = CreatePipeLabel(target, styleId, labelType);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "structure", StringComparison.OrdinalIgnoreCase))
      {
        var styleId = FindLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = CreateStructureLabel(target, styleId, labelType);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"addLabel currently supports alignment/profile 'label_set', alignment/profile 'station', surface 'spot_elevation', and basic pipe/structure labels. Requested objectType='{objectType}', labelType='{labelType}'."
      );
    });
  }

  public static Task<object?> EditLabelAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var styleName = PluginRuntime.GetOptionalString(parameters, "styleName");
    var textOverride = PluginRuntime.GetOptionalString(parameters, "textOverride");
    var x = PluginRuntime.GetOptionalDouble(parameters, "x");
    var y = PluginRuntime.GetOptionalDouble(parameters, "y");
    if (styleName == null && textOverride == null && !x.HasValue && !y.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "editLabel requires at least one of: styleName/textOverride/x/y");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = database.GetObjectId(false, new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found: " + handle };
      var label = transaction.GetObject(id, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Label;
      if (label == null) return new Dictionary<string, object?> { ["error"] = "not a label entity: " + handle };
      var applied = new List<string>();
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = ObjectId.Null;
        foreach (var candidateId in GetLabelStyleCollection(civilDoc, label.LabelType.ToString()))
        {
          if (candidateId == ObjectId.Null) continue;
          var candidate = transaction.GetObject(candidateId, OpenMode.ForRead);
          if (string.Equals(CivilObjectUtils.GetName(candidate), styleName, StringComparison.OrdinalIgnoreCase))
          { styleId = candidateId; break; }
        }
        if (styleId != ObjectId.Null) { label.StyleId = styleId; applied.Add("styleName"); }
        else return new Dictionary<string, object?> { ["error"] = "label style not found: " + styleName };
      }
      if (textOverride != null)
      {
        label.SetTextComponentOverride(label.StyleId, textOverride);
        applied.Add("textOverride");
      }
      if (x.HasValue && y.HasValue)
      {
        label.LabelLocation = new Autodesk.AutoCAD.Geometry.Point3d(x.Value, y.Value, label.LabelLocation.Z);
        applied.Add("position");
      }
      return new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["applied"] = applied,
        ["styleName"] = ResolveObjectName(transaction, label.StyleId),
      };
    });
  }

  private static DBObject ResolveTargetObject(object civilDoc, Transaction transaction, string objectType, string objectName, OpenMode openMode)
  {
    if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase))
    {
      return CivilObjectUtils.FindAlignmentByName((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc, transaction, objectName);
    }

    if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase))
    {
      foreach (ObjectId alignmentId in ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Alignment>(transaction, alignmentId, OpenMode.ForRead);
        foreach (ObjectId profileId in alignment.GetProfileIds())
        {
          var profile = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, profileId, openMode);
          if (string.Equals(CivilObjectUtils.GetName(profile), objectName, StringComparison.OrdinalIgnoreCase))
          {
            return profile;
          }
        }
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{objectName}' was not found.");
    }

    if (string.Equals(objectType, "surface", StringComparison.OrdinalIgnoreCase))
    {
      return CivilObjectUtils.FindSurfaceByName((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc, transaction, objectName, openMode);
    }

    if (string.Equals(objectType, "pipe", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeObject(civilDoc, transaction, objectName, openMode, true);
    }

    if (string.Equals(objectType, "structure", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeObject(civilDoc, transaction, objectName, openMode, false);
    }

    if (string.Equals(objectType, "pipe_network", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeNetwork(civilDoc, transaction, objectName, openMode);
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported label objectType '{objectType}'.");
  }

  private static DBObject ResolvePipeNetwork(object civilDoc, Transaction transaction, string objectName, OpenMode openMode)
  {
    foreach (var objectId in GetPipeNetworkIds(civilDoc))
    {
      var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, openMode);
      if (string.Equals(CivilObjectUtils.GetName(network), objectName, StringComparison.OrdinalIgnoreCase))
      {
        return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{objectName}' was not found.");
  }

  private static DBObject ResolvePipeObject(object civilDoc, Transaction transaction, string objectName, OpenMode openMode, bool pipe)
  {
    foreach (var networkId in GetPipeNetworkIds(civilDoc))
    {
      var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, networkId, OpenMode.ForRead);
      foreach (var objectId in GetPipeRelatedObjectIds(network, pipe))
      {
        var dbObject = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, openMode);
        if (string.Equals(CivilObjectUtils.GetName(dbObject), objectName, StringComparison.OrdinalIgnoreCase))
        {
          return dbObject;
        }
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{(pipe ? "Pipe" : "Structure")} '{objectName}' was not found.");
  }

  private static IEnumerable<ObjectId> GetPipeNetworkIds(object civilDoc)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMemberValue(civilDoc, "PipeNetworkCollection"),
      GetNamedMemberValue(civilDoc, "NetworkCollection"),
      GetNamedMemberValue(civilDoc, "PipeNetworks"),
      GetNamedMemberValue(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in CivilObjectUtils.ToObjectIds(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static IEnumerable<ObjectId> GetLabelStyleCollection(object civilDoc, string objectType)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles") ?? throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", "Civil 3D styles collection is not available.");
    var labelSetStyles = GetNamedMemberValue(styles, "LabelSetStyles");

    object? collection = objectType.ToLowerInvariant() switch
    {
      "alignment" => GetNamedMemberValue(labelSetStyles, "AlignmentLabelSetStyles") ?? GetNamedMemberValue(styles, "AlignmentLabelStyles"),
      "profile" => GetNamedMemberValue(labelSetStyles, "ProfileLabelSetStyles") ?? GetNamedMemberValue(styles, "ProfileLabelStyles"),
      "surface" => GetNamedMemberValue(styles, "SurfaceLabelStyles"),
      "pipe_network" => GetNamedMemberValue(styles, "PipeNetworkLabelStyles") ?? GetNamedMemberValue(styles, "PipeLabelStyles"),
      "pipe" => GetNamedMemberValue(styles, "PipeLabelStyles") ?? GetNamedMemberValue(styles, "PipeNetworkLabelStyles"),
      "structure" => GetNamedMemberValue(styles, "StructureLabelStyles") ?? GetNamedMemberValue(styles, "PipeNetworkLabelStyles"),
      _ => null,
    };

    if (collection == null)
    {
      return Array.Empty<ObjectId>();
    }

    return CivilObjectUtils.ToObjectIds(collection).Where(objectId => objectId != ObjectId.Null).ToList();
  }

  private static ObjectId FindLabelStyleId(object civilDoc, Transaction transaction, string objectType, string? labelStyle)
  {
    var fallback = ObjectId.Null;

    foreach (var objectId in GetLabelStyleCollection(civilDoc, objectType))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(labelStyle))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(style), labelStyle, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return fallback;
  }

  private static ObjectId CreateSurfaceSpotLabel(DBObject surface, ObjectId styleId, Point2d point)
  {
    return TryCreateLabel(
      new[]
      {
        "Autodesk.Civil.DatabaseServices.SurfaceElevationLabel, AeccDbMgd",
      },
      "surface spot elevation",
      parameters => BuildSurfaceSpotLabelArguments(parameters, surface.ObjectId, styleId, point)
    );
  }

  private static ObjectId CreateProfileStationLabel(object civilDoc, Transaction transaction, DBObject profile, ObjectId styleId, double increment)
  {
    var profileViewId = FindProfileViewIdForProfile(civilDoc, transaction, profile.ObjectId);
    return TryCreateLabel(
      new[]
      {
        "Autodesk.Civil.DatabaseServices.ProfileStationLabelGroup, AeccDbMgd",
      },
      "profile station",
      parameters => BuildProfileStationLabelArguments(parameters, profileViewId, profile.ObjectId, styleId, increment)
    );
  }

  private static ObjectId CreatePipeLabel(DBObject pipe, ObjectId styleId, string labelType)
  {
    var candidateTypes = string.Equals(labelType, "plan", StringComparison.OrdinalIgnoreCase)
      ? new[]
      {
        "Autodesk.Civil.DatabaseServices.Labels.PipePlanLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.PipePlanLabel, AeccDbMgd",
      }
      : new[]
      {
        "Autodesk.Civil.DatabaseServices.Labels.PipeLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.PipeLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.Labels.PipePlanLabel, AeccDbMgd",
      };

    return TryCreateLabel(
      candidateTypes,
      "pipe",
      parameters => BuildSingleObjectLabelArguments(parameters, pipe.ObjectId, styleId)
    );
  }

  private static ObjectId CreateStructureLabel(DBObject structure, ObjectId styleId, string labelType)
  {
    var candidateTypes = string.Equals(labelType, "plan", StringComparison.OrdinalIgnoreCase)
      ? new[]
      {
        "Autodesk.Civil.DatabaseServices.Labels.StructurePlanLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.StructurePlanLabel, AeccDbMgd",
      }
      : new[]
      {
        "Autodesk.Civil.DatabaseServices.Labels.StructureLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.StructureLabel, AeccDbMgd",
        "Autodesk.Civil.DatabaseServices.Labels.StructurePlanLabel, AeccDbMgd",
      };

    return TryCreateLabel(
      candidateTypes,
      "structure",
      parameters => BuildSingleObjectLabelArguments(parameters, structure.ObjectId, styleId)
    );
  }

  private static ObjectId CreateAlignmentStationLabel(DBObject alignment, ObjectId styleId, double increment)
  {
    return TryCreateLabel(
      new[]
      {
        "Autodesk.Civil.DatabaseServices.AlignmentStationLabelGroup, AeccDbMgd",
      },
      "alignment station",
      parameters => BuildAlignmentStationLabelArguments(parameters, alignment.ObjectId, styleId, increment)
    );
  }

  private static ObjectId TryCreateLabel(
    IEnumerable<string> candidateTypeNames,
    string labelDescription,
    Func<Civil3DCompatibility.ParameterShape[], object?[]?> argumentBuilder)
  {
    foreach (var typeName in candidateTypeNames)
    {
      var type = Civil3DCompatibility.FindLoadedType(typeName);
      if (type == null)
      {
        continue;
      }

      if (Civil3DCompatibility.TryInvokeStaticOverloads(
        type,
        "Create",
        argumentBuilder,
        out var result,
        out var args,
        allowRetry: false)) // 2026-08-12 M6: 写操作不重试(防双重创建)
      {
        var objectId = ExtractCreatedObjectId(result, args ?? Array.Empty<object?>());
        if (objectId != ObjectId.Null)
        {
          return objectId;
        }
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to create a {labelDescription} label with the available Civil 3D API overloads.");
  }

  private static object?[]? BuildSurfaceSpotLabelArguments(Civil3DCompatibility.ParameterShape[] parameters, ObjectId surfaceId, ObjectId styleId, Point2d point)
  {
    // 25.0.58: SurfaceElevationLabel.Create(surfaceId, location, labelStyleId, markerStyleId)
    // 或简化版 Create(surfaceId, location)。objectIds 顺序: surfaceId → labelStyleId → markerStyleId
    var args = new object?[parameters.Length];
    var objectIds = new Queue<ObjectId>(new[] { surfaceId, styleId, ObjectId.Null });
    var points2d = new Queue<Point2d>(new[] { point });
    var doubles = new Queue<double>(new[] { point.X, point.Y, 0.0, 0.0 });

    for (var i = 0; i < parameters.Length; i++)
    {
      var parameterType = parameters[i].Type;

      if (parameterType == typeof(ObjectId))
      {
        args[i] = objectIds.Count > 0 ? objectIds.Dequeue() : ObjectId.Null;
        continue;
      }

      if (parameterType == typeof(Point2d))
      {
        args[i] = points2d.Count > 0 ? points2d.Dequeue() : point;
        continue;
      }

      if (parameterType == typeof(Point3d))
      {
        args[i] = new Point3d(point.X, point.Y, 0.0);
        continue;
      }

      if (parameterType == typeof(double))
      {
        args[i] = doubles.Count > 0 ? doubles.Dequeue() : 0.0;
        continue;
      }

      if (parameterType == typeof(bool))
      {
        args[i] = false;
        continue;
      }

      if (parameterType == typeof(int))
      {
        args[i] = 0;
        continue;
      }

      if (parameterType == typeof(string))
      {
        args[i] = string.Empty;
        continue;
      }

      return null;
    }

    return args;
  }

  private static object?[]? BuildProfileStationLabelArguments(Civil3DCompatibility.ParameterShape[] parameters, ObjectId profileViewId, ObjectId profileId, ObjectId styleId, double increment)
  {
    // 25.0.58: ProfileStationLabelGroup.Create(profileViewId, profileId, styleId, increment) — profileViewId 在前
    var args = new object?[parameters.Length];
    var objectIds = new Queue<ObjectId>(new[] { profileViewId, profileId, styleId });
    var doubles = new Queue<double>(new[] { increment, 0.0, 0.0 });

    for (var i = 0; i < parameters.Length; i++)
    {
      var parameterType = parameters[i].Type;

      if (parameterType == typeof(ObjectId))
      {
        args[i] = objectIds.Count > 0 ? objectIds.Dequeue() : ObjectId.Null;
        continue;
      }

      if (parameterType == typeof(double))
      {
        args[i] = doubles.Count > 0 ? doubles.Dequeue() : 0.0;
        continue;
      }

      if (parameterType == typeof(Point2d))
      {
        args[i] = new Point2d(0.0, 0.0);
        continue;
      }

      if (parameterType == typeof(Point3d))
      {
        args[i] = Point3d.Origin;
        continue;
      }

      if (parameterType == typeof(bool))
      {
        args[i] = false;
        continue;
      }

      if (parameterType == typeof(int))
      {
        args[i] = 0;
        continue;
      }

      if (parameterType == typeof(string))
      {
        args[i] = string.Empty;
        continue;
      }

      return null;
    }

    return args;
  }

  private static object?[]? BuildSingleObjectLabelArguments(Civil3DCompatibility.ParameterShape[] parameters, ObjectId objectId, ObjectId styleId)
  {
    var args = new object?[parameters.Length];
    var objectIds = new Queue<ObjectId>(new[] { objectId, styleId });

    for (var i = 0; i < parameters.Length; i++)
    {
      var parameterType = parameters[i].Type;

      if (parameterType == typeof(ObjectId))
      {
        args[i] = objectIds.Count > 0 ? objectIds.Dequeue() : ObjectId.Null;
        continue;
      }

      if (parameterType == typeof(Point2d))
      {
        args[i] = new Point2d(0.0, 0.0);
        continue;
      }

      if (parameterType == typeof(Point3d))
      {
        args[i] = Point3d.Origin;
        continue;
      }

      if (parameterType == typeof(double))
      {
        args[i] = 0.0;
        continue;
      }

      if (parameterType == typeof(bool))
      {
        args[i] = false;
        continue;
      }

      if (parameterType == typeof(int))
      {
        args[i] = 0;
        continue;
      }

      if (parameterType == typeof(string))
      {
        args[i] = string.Empty;
        continue;
      }

      return null;
    }

    return args;
  }

  private static object?[]? BuildAlignmentStationLabelArguments(Civil3DCompatibility.ParameterShape[] parameters, ObjectId alignmentId, ObjectId styleId, double increment)
  {
    // 25.0.58: AlignmentStationLabelGroup.Create(styleId, alignmentId, increment) — styleId 在前
    var args = new object?[parameters.Length];
    var objectIds = new Queue<ObjectId>(new[] { styleId, alignmentId });
    var doubles = new Queue<double>(new[] { increment, 0.0, 0.0 });

    for (var i = 0; i < parameters.Length; i++)
    {
      var parameterType = parameters[i].Type;

      if (parameterType == typeof(ObjectId))
      {
        args[i] = objectIds.Count > 0 ? objectIds.Dequeue() : ObjectId.Null;
        continue;
      }

      if (parameterType == typeof(double))
      {
        args[i] = doubles.Count > 0 ? doubles.Dequeue() : 0.0;
        continue;
      }

      if (parameterType == typeof(Point2d))
      {
        args[i] = new Point2d(0.0, 0.0);
        continue;
      }

      if (parameterType == typeof(Point3d))
      {
        args[i] = Point3d.Origin;
        continue;
      }

      if (parameterType == typeof(bool))
      {
        args[i] = false;
        continue;
      }

      if (parameterType == typeof(int))
      {
        args[i] = 0;
        continue;
      }

      if (parameterType == typeof(string))
      {
        args[i] = string.Empty;
        continue;
      }

      return null;
    }

    return args;
  }

  private static ObjectId ExtractCreatedObjectId(object? result, object?[] args)
  {
    if (result is ObjectId objectId && objectId != ObjectId.Null)
    {
      return objectId;
    }

    var resultId = CivilObjectUtils.GetPropertyValue<ObjectId>(result, "ObjectId");
    if (resultId != ObjectId.Null)
    {
      return resultId;
    }

    foreach (var arg in args)
    {
      var argId = CivilObjectUtils.GetPropertyValue<ObjectId>(arg, "ObjectId");
      if (argId != ObjectId.Null)
      {
        return argId;
      }
    }

    return ObjectId.Null;
  }

  private static string? ResolveHandle(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead) as DBObject;
      return dbObject == null ? null : CivilObjectUtils.GetHandle(dbObject);
    }
    catch
    {
      return null;
    }
  }

  private static IEnumerable<ObjectId> GetPipeRelatedObjectIds(DBObject network, bool pipe)
  {
    var members = pipe
      ? new[] { "GetPipeIds", "PipeIds", "Pipes", "PipeCollection" }
      : new[] { "GetStructureIds", "StructureIds", "Structures", "StructureCollection" };

    foreach (var name in members)
    {
      var value = CivilObjectUtils.InvokeMethod(network, name) ?? GetNamedMemberValue(network, name);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static IEnumerable<ObjectId> GetLabelIds(object target)
  {
    foreach (var name in new[] { "GetLabelIds", "LabelIds", "Labels" })
    {
      var value = CivilObjectUtils.InvokeMethod(target, name) ?? GetNamedMemberValue(target, name);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static object? GetNamedMemberValue(object? value, string memberName)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static ObjectId GetObjectId(object? value, string propertyName)
  {
    return CivilObjectUtils.GetPropertyValue<ObjectId>(value, propertyName);
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      return CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead));
    }
    catch
    {
      return null;
    }
  }

  private static void ImportLabelSet(object target, ObjectId labelSetId)
  {
    if (labelSetId == ObjectId.Null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Requested label set was not found.");
    }

    // 25.0.58: Alignment/Profile 没有 LabelSetId 属性，正确 API 是 ImportLabelSet(ObjectId) 方法
    var result = CivilObjectUtils.InvokeMethod(target, "ImportLabelSet", labelSetId);
    _ = result; // void 方法
  }

  /// <summary>
  /// 25.0.58: station 标签样式在 LabelStyles 树的 Major/MinorStationLabelStyles 集合里，
  /// 不在 LabelSetStyles（那是 label_set 用的）。
  /// </summary>
  private static ObjectId FindStationLabelStyleId(object civilDoc, Transaction transaction, string objectType, string? labelStyle)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    var labelStyles = GetNamedMemberValue(styles, "LabelStyles");
    object? root = objectType.ToLowerInvariant() switch
    {
      "alignment" => GetNamedMemberValue(labelStyles, "AlignmentLabelStyles"),
      "profile" => GetNamedMemberValue(labelStyles, "ProfileLabelStyles"),
      _ => null,
    };

    if (root == null)
    {
      return ObjectId.Null;
    }

    // 优先 MajorStation，其次 MinorStation，最后任意集合兜底
    var candidates = new[]
    {
      GetNamedMemberValue(root, "MajorStationLabelStyles"),
      GetNamedMemberValue(root, "MinorStationLabelStyles"),
      GetNamedMemberValue(root, "StationLabelStyles"),
      GetNamedMemberValue(root, "LineLabelStyles"),
      GetNamedMemberValue(root, "CurveLabelStyles"),
    };

    var fallback = ObjectId.Null;
    foreach (var collection in candidates)
    {
      if (collection == null)
      {
        continue;
      }

      foreach (var objectId in CivilObjectUtils.ToObjectIds(collection))
      {
        if (objectId == ObjectId.Null)
        {
          continue;
        }

        if (fallback == ObjectId.Null)
        {
          fallback = objectId;
        }

        if (string.IsNullOrWhiteSpace(labelStyle))
        {
          continue;
        }

        var style = transaction.GetObject(objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(style), labelStyle, StringComparison.OrdinalIgnoreCase))
        {
          return objectId;
        }
      }
    }

    return fallback;
  }

  /// <summary>
  /// 25.0.58: ProfileStationLabelGroup.Create 需要 profileViewId 参数。
  /// Profile 没有 ProfileViewId 属性，只能从所属 Alignment 的 GetProfileViewIds() 里匹配。
  /// </summary>
  private static ObjectId FindProfileViewIdForProfile(object civilDoc, Transaction transaction, ObjectId profileId)
  {
    // 遍历所有 alignment，找包含该 profile 的 view
    foreach (var alignmentObjectId in CivilObjectUtils.ToObjectIds(CivilObjectUtils.InvokeMethod(civilDoc, "GetAlignmentIds")))
    {
      var candidate = CivilObjectUtils.GetRequiredObject<DBObject>(
        transaction, alignmentObjectId, OpenMode.ForRead);
      var profileIds = CivilObjectUtils.ToObjectIds(CivilObjectUtils.InvokeMethod(candidate, "GetProfileIds"));
      if (profileIds.Any(id => id == profileId))
      {
        var viewIds = CivilObjectUtils.ToObjectIds(CivilObjectUtils.InvokeMethod(candidate, "GetProfileViewIds"));
        var firstView = viewIds.FirstOrDefault(id => id != ObjectId.Null);
        if (firstView != ObjectId.Null)
        {
          return firstView;
        }
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Profile has no profile view to host station labels.");
  }
}
