using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for pressure network MCP tools.
///
/// Civil 3D API notes (reflection-based late binding):
///   AeccPressureNetwork   -- top-level network object
///   AeccPressurePipe      -- pipe segment (InnerDiameter, Length3D, Material, CoverDepth)
///   AeccPressureFitting   -- fitting (elbow, tee, reducer, cap)
///   AeccPressureAppurtenance -- valve, hydrant, meter, ARV, etc.
///
/// The API surface lives in AeccDbMgd.dll. We access it via reflection so the
/// plugin builds without a direct assembly reference and tolerates minor
/// version differences between Civil 3D releases.
/// </summary>
public static class PressureNetworkCommands
{
  // -------------------------------------------------------------------------
  // listPressureNetworks
  // -------------------------------------------------------------------------

  public static Task<object?> ListPressureNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networks = EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(net => ToPressureNetworkSummary(net, transaction))
        .ToList();

      // 2026-11-17 P28b: 空集合要说清"这张图里就没有压力管网"—— 压力管网是独立子系统，
      //   和重力管网（listPipeNetworks）不是一回事，客户很容易把两者搞混后以为数据丢了。
      var payload = new Dictionary<string, object?>
      {
        ["networks"] = networks,
        ["count"] = networks.Count,
      };

      // 2026-11-18 P34: 光说"没有压力管网"不够用 —— 建网必须给 partsList 名和部件名，
      // 而压力部件列表（Styles.GetPressurePartLists）原来没有任何方法能列出来，客户只能猜。
      var partsLists = EnumeratePressurePartsLists(civilDoc, transaction);
      payload["availablePartsLists"] = partsLists;
      payload["partsListCount"] = partsLists.Count;

      if (networks.Count == 0)
      {
        payload["hint"] = partsLists.Count == 0
          ? "No pressure network in the current drawing, and no pressure parts list either (parts lists come from the drawing template). Start from a Civil 3D template that includes pressure networks, or import a parts list, then createPressureNetwork."
          : "No pressure network in the current drawing. Pressure networks are a separate subsystem from gravity pipe networks - a drawing can have pipe networks and still have zero pressure networks. Use availablePartsLists[].name for createPressureNetwork and availablePartsLists[].pipeParts/fittingParts/appurtenanceParts for addPressurePipe / addPressureFitting / addPressureAppurtenance.";
      }
      return payload;
    });
  }

  // -------------------------------------------------------------------------
  // getPressureNetworkInfo
  // -------------------------------------------------------------------------

  public static Task<object?> GetPressureNetworkInfoAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);

      var pipes = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes")
        .Select(id => ToPressurePipeData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      var fittings = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings")
        .Select(id => ToPressureFittingData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      var appurtenances = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances")
        .Select(id => ToPressureAppurtenanceData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
        ["pipes"] = pipes,
        ["fittings"] = fittings,
        ["appurtenances"] = appurtenances,
      };
    });
  }

  // -------------------------------------------------------------------------
  // createPressureNetwork
  // -------------------------------------------------------------------------

  public static Task<object?> CreatePressureNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 2026-11-18 P34: 旧顺序是「先建网 → 再查 partsList」，partsList 不存在时抛错但网已经建出来了，
      // 客户下次 listPressureNetworks 会看到一个没有部件列表的空网。现在先确认 partsList 存在再动手。
      var partsListId = FindPressurePartsListId(civilDoc, transaction, partsListName);

      // 同名网先查重（C3D 可能静默改名）
      foreach (var existing in EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead))
      {
        if (string.Equals(CivilObjectUtils.GetName(existing), name, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"Pressure network '{name}' already exists (handle {CivilObjectUtils.GetHandle(existing)}). Pick another name or use getPressureNetworkInfo to inspect it.");
      }

      var networkId = CreatePressureNetwork(database, name);
      var network = CivilObjectUtils.GetRequiredObject<PressurePipeNetwork>(transaction, networkId, OpenMode.ForWrite);

      // Layer
      var layer = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layer))
      {
        network.Layer = layer;
      }

      network.PartsListId = partsListId;

      // Reference surface
      var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
      if (!string.IsNullOrWhiteSpace(referenceSurface))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurface!, OpenMode.ForRead);
        network.ReferenceSurfaceId = surface.ObjectId;
      }

      // Reference alignment
      var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, referenceAlignment!);
        network.ReferenceAlignmentId = alignment.ObjectId;
      }

      // 回读校验：名字和 partsList 必须真的落地（P29 教训：写操作自证）
      var actualName = CivilObjectUtils.GetName(network) ?? "";
      if (!string.Equals(actualName, name, StringComparison.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Pressure network was created as '{actualName}' instead of '{name}' (Civil 3D renamed it). Re-query with listPressureNetworks.");
      if (network.PartsListId.IsNull || network.PartsListId != partsListId)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Pressure network '{actualName}' was created but its parts list did not stick (requested '{partsListName}').");

      return new Dictionary<string, object?>
      {
        ["name"] = actualName,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = partsListName,
        ["created"] = true,
        ["verified"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // deletePressureNetwork
  // -------------------------------------------------------------------------

  public static Task<object?> DeletePressureNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var handle = CivilObjectUtils.GetHandle(network);
      network.Erase();

      // 2026-11-18 P34: 删完回读确认（P29 教训：写操作不许只说"我删了"）
      var stillThere = EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Any(n => string.Equals(CivilObjectUtils.GetName(n), name, StringComparison.OrdinalIgnoreCase));
      if (stillThere)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Pressure network '{name}' (handle {handle}) was erased but is still listed. Verify the drawing in Civil 3D before continuing.");

      return new Dictionary<string, object?>
      {
        ["deleted"] = true,
        ["name"] = name,
        ["handle"] = handle,
        ["remainingNetworkCount"] = EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead).Count(),
        ["verified"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // assignPressurePartsList
  // -------------------------------------------------------------------------

  public static Task<object?> AssignPressurePartsListAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var partsListId = FindPressurePartsListId(civilDoc, transaction, partsListName);
      ((PressurePipeNetwork)network).PartsListId = partsListId;

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["partsList"] = partsListName,
        ["assigned"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setPressureNetworkCover
  // -------------------------------------------------------------------------

  public static Task<object?> SetPressureNetworkCoverAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var minCover = PluginRuntime.GetRequiredDouble(parameters, "minCoverDepth");
    var maxCover = PluginRuntime.GetOptionalDouble(parameters, "maxCoverDepth");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 PressurePipeNetwork does not expose network-level minimum or maximum cover setters. No pipe elevations were changed; apply reviewed cover criteria through a pipe-run workflow.");
  }

  // -------------------------------------------------------------------------
  // validatePressureNetwork
  // -------------------------------------------------------------------------

  public static Task<object?> ValidatePressureNetworkAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "networkName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Pressure-network validation requires explicit cover criteria and typed connection checks. The previous implementation treated missing criteria as zero and could return a false valid result; no validation result was produced.");
  }

  // -------------------------------------------------------------------------
  // exportPressureNetwork
  // -------------------------------------------------------------------------

  public static Task<object?> ExportPressureNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var includeCoordinates = parameters?["includeCoordinates"]?.GetValue<bool>() ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);

      var pipes = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes")
        .Select(id => ExportPressurePipe(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      var fittings = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings")
        .Select(id => ExportPressureFitting(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      var appurtenances = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances")
        .Select(id => ExportPressureAppurtenance(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
        ["pipes"] = pipes,
        ["fittings"] = fittings,
        ["appurtenances"] = appurtenances,
        ["exportedAt"] = DateTime.UtcNow.ToString("O"),
      };
    });
  }

  // -------------------------------------------------------------------------
  // connectPressureNetworks
  // -------------------------------------------------------------------------

  public static Task<object?> ConnectPressureNetworksAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "targetNetwork");
    _ = PluginRuntime.GetRequiredString(parameters, "sourceNetwork");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 does not expose a managed pressure-network merge operation. Component NetworkId values are read-only and were not reassigned; neither network was changed.");
  }

  // -------------------------------------------------------------------------
  // addPressurePipe
  // -------------------------------------------------------------------------

  public static Task<object?> AddPressurePipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var startPoint = ReadPoint(parameters, "startPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startPoint is required; an origin point will not be assumed.");
    var endPoint = ReadPoint(parameters, "endPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endPoint is required; a unit-length pipe will not be fabricated.");
    var diameter = PluginRuntime.GetOptionalDouble(parameters, "diameter");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var createdId = AddPressurePipeToNetwork(network, transaction, partName, startPoint, endPoint, diameter);
      var pipe = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipe"] = ToPressurePipeData(pipe),
        ["added"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getPressurePipeProperties
  // -------------------------------------------------------------------------

  public static Task<object?> GetPressurePipePropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPressureComponentByName(network, transaction, pipeName, "GetPipeIds", "PipeIds", "Pipes");
      return ToPressurePipeData(pipe);
    });
  }

  // -------------------------------------------------------------------------
  // resizePressurePipe
  // -------------------------------------------------------------------------

  public static Task<object?> ResizePressurePipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetRequiredString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 PressurePipe does not expose a managed resize or part-swap method. No diameter or catalog part was changed.");
  }

  // -------------------------------------------------------------------------
  // addPressureFitting
  // -------------------------------------------------------------------------

  public static Task<object?> AddPressureFittingAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var position = ReadPoint(parameters, "position")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "position is required; drawing origin will not be assumed.");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    if (Math.Abs(rotation) > 1.0e-12)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D 2026 AddFitting does not accept a rotation. No fitting was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var createdId = AddPressureComponentToNetwork(network, transaction, partName, position, rotation, isFitting: true);
      var fitting = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["fitting"] = ToPressureFittingData(fitting),
        ["added"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getPressureFittingProperties
  // -------------------------------------------------------------------------

  public static Task<object?> GetPressureFittingPropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var fittingName = PluginRuntime.GetRequiredString(parameters, "fittingName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var fitting = FindPressureComponentByName(network, transaction, fittingName, "GetFittingIds", "FittingIds", "Fittings");
      return ToPressureFittingData(fitting);
    });
  }

  // -------------------------------------------------------------------------
  // addPressureAppurtenance
  // -------------------------------------------------------------------------

  public static Task<object?> AddPressureAppurtenanceAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var position = ReadPoint(parameters, "position")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "position is required unless a future pipe-insertion workflow explicitly derives it.");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    if (Math.Abs(rotation) > 1.0e-12)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D 2026 AddAppurtenance does not accept a rotation. No appurtenance was created.");
    var onPipeName = PluginRuntime.GetOptionalString(parameters, "onPipeName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);

      // If onPipeName specified, find the pipe and snap position to its midpoint
      if (!string.IsNullOrWhiteSpace(onPipeName))
      {
        try
        {
          var pipe = FindPressureComponentByName(network, transaction, onPipeName!, "GetPipeIds", "PipeIds", "Pipes");
          var sp = GetPointProperty(pipe, "StartPoint", "PointAtStart");
          var ep = GetPointProperty(pipe, "EndPoint", "PointAtEnd");
          if (sp.HasValue && ep.HasValue)
          {
            position = new Point3d(
              (sp.Value.X + ep.Value.X) / 2,
              (sp.Value.Y + ep.Value.Y) / 2,
              (sp.Value.Z + ep.Value.Z) / 2
            );
          }
        }
        catch
        {
          // Non-fatal - use provided position
        }
      }

      var createdId = AddPressureComponentToNetwork(network, transaction, partName, position, rotation, isFitting: false);
      var app = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["appurtenance"] = ToPressureAppurtenanceData(app),
        ["added"] = true,
      };
    });
  }

  // =========================================================================
  // Private helpers
  // =========================================================================

  private static IEnumerable<AcDbObject> EnumeratePressureNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (var objectId in GetPressureNetworkIds(civilDoc))
    {
      yield return CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, openMode);
    }
  }

  private static IEnumerable<ObjectId> GetPressureNetworkIds(object civilDoc)
  {
    if (civilDoc is CivilDocument typedDocument)
    {
      foreach (ObjectId objectId in typedDocument.GetPressurePipeNetworkIds())
      {
        if (objectId != ObjectId.Null)
          yield return objectId;
      }
      yield break;
    }

    var candidates = new object?[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPressureNetworkIds"),
      GetNamedMember(civilDoc, "PressureNetworkCollection"),
      GetNamedMember(civilDoc, "PressureNetworks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIdsFlexible(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static AcDbObject FindPressureNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (var network in EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead))
    {
      if (!string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (openMode == OpenMode.ForWrite)
      {
        return CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, network.ObjectId, OpenMode.ForWrite);
      }

      return network;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure network '{name}' was not found.");
  }

  private static AcDbObject FindPressureComponentByName(
    AcDbObject network,
    Transaction transaction,
    string componentName,
    params string[] idMethods)
  {
    foreach (var objectId in GetChildObjectIds(network, idMethods))
    {
      var component = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(component), componentName, StringComparison.OrdinalIgnoreCase))
      {
        return component;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"Component '{componentName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Dictionary<string, object?> ToPressureNetworkSummary(AcDbObject network, Transaction transaction)
  {
    var pipeIds = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes").ToList();
    var fittingIds = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings").ToList();
    var appIds = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances").ToList();

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(network) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["pipeCount"] = pipeIds.Count,
      ["fittingCount"] = fittingIds.Count,
      ["appurtenanceCount"] = appIds.Count,
      ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
    };
  }

  private static Dictionary<string, object?> ToPressurePipeData(AcDbObject pipe)
  {
    var sp = GetPointProperty(pipe, "StartPoint", "PointAtStart");
    var ep = GetPointProperty(pipe, "EndPoint", "PointAtEnd");

    // 2026-11-18 P34: Civil 3D 2025 的压力管不暴露 Length3D/Length2D/Length（真机全 null，
    // qtyPressureNetworkLengths 因此报"读不到长度"）。有端点时用三维距离补上，并标明来源。
    var length = GetAnyDouble(pipe, "Length3D", "Length2D", "Length");
    string? lengthSource = length.HasValue ? "api" : null;
    if (!length.HasValue && sp.HasValue && ep.HasValue)
    {
      var dx = sp.Value.X - ep.Value.X;
      var dy = sp.Value.Y - ep.Value.Y;
      var dz = sp.Value.Z - ep.Value.Z;
      var computed = Math.Sqrt(dx * dx + dy * dy + dz * dz);
      if (computed > 0)
      {
        length = computed;
        lengthSource = "computed-from-endpoints";
      }
    }

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(pipe) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      ["diameter"] = GetAnyDouble(pipe, "InnerDiameter", "InnerDiameterOrWidth", "Diameter"),
      ["length"] = length,
      ["lengthSource"] = lengthSource,
      ["material"] = GetAnyString(pipe, "Material", "PartFamilyName", "PartDescription") ?? string.Empty,
      ["startPoint"] = sp.HasValue ? new Dictionary<string, object?> { ["x"] = sp.Value.X, ["y"] = sp.Value.Y, ["z"] = sp.Value.Z } : null,
      ["endPoint"] = ep.HasValue ? new Dictionary<string, object?> { ["x"] = ep.Value.X, ["y"] = ep.Value.Y, ["z"] = ep.Value.Z } : null,
      ["coverDepth"] = GetAnyDouble(pipe, "CoverDepth", "Cover", "MinCover"),
    };
  }

  private static Dictionary<string, object?> ToPressureFittingData(AcDbObject fitting)
  {
    var pos = GetPointProperty(fitting, "Position", "Location", "CenterPoint");

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(fitting) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(fitting),
      ["type"] = GetAnyString(fitting, "FittingType", "PartType", "PartDescription") ?? fitting.GetType().Name,
      ["position"] = pos.HasValue ? new Dictionary<string, object?> { ["x"] = pos.Value.X, ["y"] = pos.Value.Y, ["z"] = pos.Value.Z } : null,
      ["partSize"] = GetAnyString(fitting, "PartSize", "SizeName", "PartFamilyName"),
    };
  }

  private static Dictionary<string, object?> ToPressureAppurtenanceData(AcDbObject app)
  {
    var pos = GetPointProperty(app, "Position", "Location", "CenterPoint");

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(app) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(app),
      ["type"] = GetAnyString(app, "AppurtenanceType", "PartType", "PartDescription") ?? app.GetType().Name,
      ["position"] = pos.HasValue ? new Dictionary<string, object?> { ["x"] = pos.Value.X, ["y"] = pos.Value.Y, ["z"] = pos.Value.Z } : null,
      ["partSize"] = GetAnyString(app, "PartSize", "SizeName", "PartFamilyName"),
    };
  }

  private static Dictionary<string, object?> ExportPressurePipe(AcDbObject pipe, bool includeCoords)
  {
    var data = ToPressurePipeData(pipe);
    if (!includeCoords)
    {
      data.Remove("startPoint");
      data.Remove("endPoint");
    }

    return data;
  }

  private static Dictionary<string, object?> ExportPressureFitting(AcDbObject fitting, bool includeCoords)
  {
    var data = ToPressureFittingData(fitting);
    if (!includeCoords)
    {
      data.Remove("position");
    }

    return data;
  }

  private static Dictionary<string, object?> ExportPressureAppurtenance(AcDbObject app, bool includeCoords)
  {
    var data = ToPressureAppurtenanceData(app);
    if (!includeCoords)
    {
      data.Remove("position");
    }

    return data;
  }

  private static ObjectId CreatePressureNetwork(Database database, string name)
    => PressurePipeNetwork.Create(database, name);

  private static ObjectId AddPressurePipeToNetwork(AcDbObject network, Transaction transaction, string partName, Point3d startPoint, Point3d endPoint, double? diameter)
  {
    var pressureNetwork = (PressurePipeNetwork)network;
    var part = FindPressurePart(transaction, pressureNetwork.PartsListId, partName, PressurePartType.PressurePipe);
    var id = pressureNetwork.AddLinePipe(new LineSegment3d(startPoint, endPoint), part);
    if (diameter.HasValue)
    {
      var created = CivilObjectUtils.GetRequiredObject<PressurePipe>(transaction, id, OpenMode.ForRead);
      var actualDiameter = GetAnyDouble(created, "InnerDiameter", "InnerDiameterOrWidth");
      if (!actualDiameter.HasValue || Math.Abs(actualDiameter.Value - diameter.Value) > 1.0e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Catalog part '{partName}' does not match requested diameter {diameter.Value}. The pipe was not committed.");
    }
    return id;
  }

  private static ObjectId AddPressureComponentToNetwork(AcDbObject network, Transaction transaction, string partName, Point3d position, double rotation, bool isFitting)
  {
    _ = rotation;
    var pressureNetwork = (PressurePipeNetwork)network;
    var candidates = isFitting
      ? new[] { PressurePartType.Elbow, PressurePartType.Tee, PressurePartType.Wye, PressurePartType.Cross, PressurePartType.Cap, PressurePartType.Coupling, PressurePartType.Plug, PressurePartType.Reducer }
      : new[] { PressurePartType.Valve, PressurePartType.Pump, PressurePartType.Hydrant };
    var part = FindPressurePart(transaction, pressureNetwork.PartsListId, partName, candidates);
    return isFitting
      ? pressureNetwork.AddFitting(position, part)
      : pressureNetwork.AddAppurtenance(position, part);
  }

  /// <summary>
  /// 本图的压力部件列表 + 各类部件名（各取前 12 个，避免返回体膨胀）。
  /// 建压力管网必须给 partsList 名和部件名，没有这个清单客户只能猜。
  /// </summary>
  private static List<Dictionary<string, object?>> EnumeratePressurePartsLists(object civilDoc, Transaction transaction)
  {
    var result = new List<Dictionary<string, object?>>();
    try
    {
      foreach (ObjectId listId in ((CivilDocument)civilDoc).Styles.GetPressurePartLists())
      {
        var list = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, listId, OpenMode.ForRead);
        result.Add(new Dictionary<string, object?>
        {
          ["name"] = list.Name,
          ["handle"] = CivilObjectUtils.GetHandle(list),
          ["pipeParts"] = PartNames(list, transaction, PressurePartType.PressurePipe),
          ["fittingParts"] = PartNames(list, transaction, PressurePartType.Elbow, PressurePartType.Tee, PressurePartType.Wye, PressurePartType.Cross, PressurePartType.Cap, PressurePartType.Coupling, PressurePartType.Plug, PressurePartType.Reducer),
          ["appurtenanceParts"] = PartNames(list, transaction, PressurePartType.Valve, PressurePartType.Pump, PressurePartType.Hydrant),
        });
      }
    }
    catch (Exception ex)
    {
      result.Add(new Dictionary<string, object?> { ["error"] = ex.GetType().Name + ": " + ex.Message });
    }
    return result;
  }

  private static List<string> PartNames(PressurePartList list, Transaction transaction, params PressurePartType[] types)
  {
    var names = new List<string>();
    foreach (var type in types)
    {
      try
      {
        foreach (var part in list.GetParts(type))
        {
          if (!part.IsValid || string.IsNullOrWhiteSpace(part.Description)) continue;
          if (names.Contains(part.Description)) continue;
          names.Add(part.Description);
          if (names.Count >= 12) return names;
        }
      }
      catch { }
    }
    return names;
  }

  private static ObjectId FindPressurePartsListId(object civilDoc, Transaction transaction, string partsListName)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).Styles.GetPressurePartLists())
    {
      var list = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(list.Name, partsListName, StringComparison.OrdinalIgnoreCase))
        return objectId;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure parts list '{partsListName}' was not found.");
  }

  private static PressurePartSize FindPressurePart(
    Transaction transaction,
    ObjectId partsListId,
    string partName,
    params PressurePartType[] partTypes)
  {
    if (partsListId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The pressure network does not have a pressure parts list assigned.");
    var partsList = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, partsListId, OpenMode.ForRead);
    foreach (var partType in partTypes)
    {
      foreach (var part in partsList.GetParts(partType))
      {
        if (part.IsValid && string.Equals(part.Description, partName, StringComparison.OrdinalIgnoreCase))
          return part;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Exact pressure part '{partName}' was not found in parts list '{partsList.Name}'.");
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(object owner, params string[] memberNames)
  {
    // 2026-11-18 P34: 多个成员名常常指向同一个集合（真机 getPressureNetworkInfo 对 1 根管报了 2 根），
    // 必须按 ObjectId 去重，否则 pipes/fittings/appurtenances 都会翻倍。
    var seen = new HashSet<ObjectId>();
    foreach (var memberName in memberNames)
    {
      var value = CivilObjectUtils.InvokeMethod(owner, memberName) ?? GetNamedMember(owner, memberName);
      foreach (var id in ToObjectIdsFlexible(value))
      {
        if (id.IsNull) continue;
        if (seen.Add(id)) yield return id;
      }
    }
  }

  private static IEnumerable<ObjectId> ToObjectIdsFlexible(object? value)
  {
    foreach (var id in CivilObjectUtils.ToObjectIds(value))
    {
      yield return id;
    }

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId oid && oid != ObjectId.Null) { yield return oid; continue; }
        var itemId = GetAnyObjectId(item, "ObjectId", "Id");
        if (itemId != ObjectId.Null) yield return itemId;
      }
    }
  }

  private static IEnumerable<object> EnumerateObjects(object? collection)
  {
    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item != null) yield return item;
      }
    }
  }

  private static object? GetNamedMember(object? value, string memberName)
  {
    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null) return null;
    try { return CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead)); }
    catch { return null; }
  }

  private static ObjectId GetAnyObjectId(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var id = CivilObjectUtils.GetPropertyValue<ObjectId>(value, name);
      if (id != ObjectId.Null) return id;
    }

    return ObjectId.Null;
  }

  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<double?>(value, name);
      if (v.HasValue) return v.Value;
    }

    return null;
  }

  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetStringProperty(value, name);
      if (!string.IsNullOrWhiteSpace(v)) return v;
    }

    return null;
  }

  private static Point3d? GetPointProperty(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<Point3d?>(value, name);
      if (v.HasValue) return v.Value;
    }

    return null;
  }



  private static ObjectId ExtractObjectId(object? result, object?[] args)
  {
    if (result is ObjectId id && id != ObjectId.Null) return id;
    var rid = GetAnyObjectId(result, "ObjectId", "Id");
    if (rid != ObjectId.Null) return rid;
    foreach (var arg in args)
    {
      var aid = GetAnyObjectId(arg, "ObjectId", "Id");
      if (aid != ObjectId.Null) return aid;
    }

    return ObjectId.Null;
  }

  /// <summary>
  /// 2026-11-18 P34: 只认 {x,y,z} 时，客户传 [x,y,z] 会被当成"没给点"（真机实测：
  /// addPressurePipe 传数组 → "startPoint is required"）。现在两种形状都收，畸形值直接报错。
  /// 高程仍然必须给（不做"默认 0"的假设）。
  /// </summary>
  private static Point3d? ReadPoint(JsonObject? parameters, string paramName)
  {
    var raw = PluginRuntime.GetParameter(parameters, paramName);
    if (raw is JsonObject node)
    {
      return new Point3d(
        node["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}.x is required."),
        node["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}.y is required."),
        node["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}.z is required; elevation 0 will not be assumed.")
      );
    }
    if (raw is JsonArray arr && arr.Count >= 3)
    {
      return new Point3d(
        arr[0]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}[0] is not a number."),
        arr[1]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}[1] is not a number."),
        arr[2]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{paramName}[2] is required; elevation 0 will not be assumed.")
      );
    }
    if (raw is JsonArray shortArr)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{paramName} must be {{x,y,z}} or [x,y,z]; got {shortArr.Count} element(s).");
    return null;
  }
}
