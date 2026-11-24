using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

public static class PipeNetworkCommands
{
  public static Task<object?> ListPipeNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networks = EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(network => ToPipeNetworkSummary(network, transaction))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networks"] = networks,
      };
    });
  }

  public static Task<object?> GetPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
      return ToPipeNetworkDetail(network, transaction);
    });
  }

  public static Task<object?> GetPipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForRead);
      return ToPipeData(pipe, transaction);
    });
  }

  public static Task<object?> GetStructureAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var structure = FindStructureByName(network, transaction, structureName, OpenMode.ForRead);
      return ToStructureData(structure, transaction);
    });
  }

  public static Task<object?> CheckPipeNetworkInterferenceAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var targetType = PluginRuntime.GetRequiredString(parameters, "targetType");
    var targetName = PluginRuntime.GetRequiredString(parameters, "targetName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 2026-11-17 P28: 这个方法以前是**空壳** —— 校验完存在性就 return {interferences:[], totalConflicts:0}，
      //   真机 11 根管子压在曲面上方也报"零冲突"。现在真算：
      //     surface      → 沿管每 1/8 段采样，管顶(中心线+半径)高于地面即冲突；
      //     pipe_network → 两网管道中心线采样点最小净距 < 两半径之和即冲突。
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      // 2026-11-17 P29: 采样密度做成参数（长管/短管需要不同分辨率；9 点是经验值）
      var samplesPerPipeArg = PluginRuntime.GetOptionalInt(parameters, "samplesPerPipe") ?? 9;
      var samplesPerPipe = Math.Min(50, Math.Max(2, samplesPerPipeArg));
      var conflicts = new List<Dictionary<string, object?>>();
      var outsideSamples = 0;

      if (string.Equals(targetType, "surface", StringComparison.OrdinalIgnoreCase))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, targetName, OpenMode.ForRead);
        foreach (var pipe in EnumeratePipes(network, transaction))
        {
          var start = pipe.StartPoint;
          var end = pipe.EndPoint;
          var radius = Math.Max(pipe.InnerDiameterOrWidth, 0.0) / 2.0;
          for (var i = 0; i < samplesPerPipe; i++)
          {
            var t = samplesPerPipe == 1 ? 0.0 : (double)i / (samplesPerPipe - 1);
            var x = start.X + (end.X - start.X) * t;
            var y = start.Y + (end.Y - start.Y) * t;
            var crown = start.Z + (end.Z - start.Z) * t + radius;
            double ground;
            try { ground = surface.FindElevationAtXY(x, y); } catch { outsideSamples++; continue; }
            // Civil 的 FindElevationAtXY 对"曲面外"的点不抛异常而是回 0.0（2026-11-16 已踩过）
            //   → 0 且周围明显高于 0 的，按"曲面外"跳过并计数，别把 0 当地面算覆盖。
            if (ground == 0.0 && crown > 1.0) { outsideSamples++; continue; }
            var cover = ground - crown;
            if (cover < 0)
            {
              conflicts.Add(new Dictionary<string, object?>
              {
                ["type"] = "pipe_above_surface",
                ["pipeName"] = pipe.Name,
                ["station"] = i,
                ["x"] = Math.Round(x, 3),
                ["y"] = Math.Round(y, 3),
                ["groundElevation"] = Math.Round(ground, 3),
                ["pipeCrownElevation"] = Math.Round(crown, 3),
                ["cover"] = Math.Round(cover, 3),
              });
            }
          }
        }
      }
      else if (string.Equals(targetType, "pipe_network", StringComparison.OrdinalIgnoreCase))
      {
        var target = FindPipeNetworkByName(civilDoc, transaction, targetName, OpenMode.ForRead);
        var mine = EnumeratePipes(network, transaction).ToList();
        var theirs = EnumeratePipes(target, transaction).ToList();
        foreach (var a in mine)
        {
          foreach (var b in theirs)
          {
            var sumRadius = (Math.Max(a.InnerDiameterOrWidth, 0) + Math.Max(b.InnerDiameterOrWidth, 0)) / 2.0;
            var minDistance = double.MaxValue;
            for (var i = 0; i < samplesPerPipe; i++)
            {
              var ta = (double)i / (samplesPerPipe - 1);
              var pa = new Point3d(a.StartPoint.X + (a.EndPoint.X - a.StartPoint.X) * ta,
                                   a.StartPoint.Y + (a.EndPoint.Y - a.StartPoint.Y) * ta,
                                   a.StartPoint.Z + (a.EndPoint.Z - a.StartPoint.Z) * ta);
              for (var j = 0; j < samplesPerPipe; j++)
              {
                var tb = (double)j / (samplesPerPipe - 1);
                var pb = new Point3d(b.StartPoint.X + (b.EndPoint.X - b.StartPoint.X) * tb,
                                     b.StartPoint.Y + (b.EndPoint.Y - b.StartPoint.Y) * tb,
                                     b.StartPoint.Z + (b.EndPoint.Z - b.StartPoint.Z) * tb);
                var d = pa.DistanceTo(pb);
                if (d < minDistance) minDistance = d;
              }
            }
            if (minDistance < sumRadius)
            {
              conflicts.Add(new Dictionary<string, object?>
              {
                ["type"] = "pipe_to_pipe_clearance",
                ["pipeName"] = a.Name,
                ["otherPipeName"] = b.Name,
                ["centerlineDistance"] = Math.Round(minDistance, 3),
                ["requiredClearance"] = Math.Round(sumRadius, 3),
              });
            }
          }
        }
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported targetType '{targetType}'.");
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["targetType"] = targetType,
        ["targetName"] = targetName,
        ["samplesPerPipe"] = samplesPerPipe,
        ["interferences"] = conflicts,
        ["totalConflicts"] = conflicts.Count,
        ["outsideSurfaceSamples"] = outsideSamples,
        ["note"] = "Sampled centerline check (samplesPerPipe points per pipe). cover = ground - pipe crown; negative cover = the pipe pokes out of the surface.",
      };
    });
  }

  public static Task<object?> DeletePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var handle = CivilObjectUtils.GetHandle(network);
      network.Erase();
      // 回读：删完还找得到就是没删掉
      var stillThere = ((CivilDocument)civilDoc).GetPipeNetworkIds().Cast<ObjectId>()
        .Select(id => transaction.GetObject(id, OpenMode.ForRead) as Network)
        .Any(n => n != null && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
      if (stillThere)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Pipe network \"{name}\" still exists after Erase(); nothing was deleted.");
      return new Dictionary<string, object?> { ["deleted"] = true, ["name"] = name, ["handle"] = handle, ["verified"] = true };
    });
  }

  /// <summary>
  /// 2026-11-17 P31：删管网里的**单个**井/管。之前只能整网删（deletePipeNetwork），
  ///   误建一个井就把整张网扔了 —— 写操作不闭环。
  ///   删结构时默认连带删掉与它相连的管道（C3D 里结构被删后相连管道会悬空），并如实回报连带删了哪些。
  /// </summary>
  public static Task<object?> DeleteNetworkPartsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeNames = PluginRuntime.GetOptionalStringArray(parameters, "pipeNames");
    var structureNames = PluginRuntime.GetOptionalStringArray(parameters, "structureNames");
    var deleteConnectedPipes = PluginRuntime.GetOptionalBool(parameters, "deleteConnectedPipes") ?? true;

    if ((pipeNames == null || pipeNames.Length == 0) && (structureNames == null || structureNames.Length == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Nothing to delete: pass pipeNames[] and/or structureNames[] (use deletePipeNetwork to remove the whole network).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var deletedPipes = new List<string>();
      var deletedStructures = new List<string>();
      var skipped = new List<string>();

      foreach (var pipeName in pipeNames ?? Array.Empty<string>())
      {
        try
        {
          var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);
          var name = CivilObjectUtils.GetName(pipe) ?? pipeName;
          pipe.Erase();
          deletedPipes.Add(name!);
        }
        catch (JsonRpcDispatchException) { skipped.Add("pipe:" + pipeName); }
      }

      foreach (var structureName in structureNames ?? Array.Empty<string>())
      {
        try
        {
          var structure = FindStructureByName(network, transaction, structureName, OpenMode.ForWrite);
          var name = CivilObjectUtils.GetName(structure) ?? structureName;

          if (deleteConnectedPipes && structure is Structure typedStructure)
          {
            var connected = Enumerable.Range(0, typedStructure.ConnectedPipesCount)
              .Select(index => Civil3DCompatibility.GetIndexedPropertyValue(typedStructure, "ConnectedPipe", index))
              .OfType<ObjectId>()
              .ToList();
            foreach (var pipeId in connected)
            {
              try
              {
                var connectedPipe = transaction.GetObject(pipeId, OpenMode.ForWrite) as Pipe;
                if (connectedPipe == null || connectedPipe.IsErased) continue;
                var connectedName = CivilObjectUtils.GetName(connectedPipe) ?? pipeId.Handle.ToString();
                connectedPipe.Erase();
                deletedPipes.Add(connectedName!);
              }
              catch (Exception ex) { PluginLog.Warn("PipeNetwork", "connected pipe delete failed: " + pipeId.Handle + ": " + ex.Message); }
            }
          }

          structure.Erase();
          deletedStructures.Add(name!);
        }
        catch (JsonRpcDispatchException) { skipped.Add("structure:" + structureName); }
      }

      // 回读校验：删掉的东西不能再出现
      var stillThere = new List<string>();
      var pipesLeft = network.GetPipeIds().Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead))).Where(n => n != null).ToList();
      var structsLeft = network.GetStructureIds().Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead))).Where(n => n != null).ToList();
      foreach (var name in deletedPipes) if (pipesLeft.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) stillThere.Add("pipe:" + name);
      foreach (var name in deletedStructures) if (structsLeft.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) stillThere.Add("structure:" + name);

      var payload = new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["deletedPipes"] = deletedPipes,
        ["deletedStructures"] = deletedStructures,
        ["deletedPartCount"] = deletedPipes.Count + deletedStructures.Count,
        ["skipped"] = skipped,
        ["remainingPipeCount"] = pipesLeft.Count,
        ["remainingStructureCount"] = structsLeft.Count,
        ["verified"] = stillThere.Count == 0,
      };
      if (skipped.Count > 0)
        payload["hint"] = "Some names were not found in this network and were skipped (see skipped). Call getPipeNetwork to list the current pipe/structure names.";
      if (stillThere.Count > 0)
        payload["hint"] = "These parts still exist after Erase(): " + string.Join(", ", stillThere);
      return payload;
    });
  }

  public static Task<object?> CreatePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 2026-11-17 P29: C3D 的 Network.Create 遇到重名会**静默改名**（P29-NET → "P29-NET (1)"），
      //   接口却回 created:true，调用方按原名继续操作就会找不到/改错对象。这里先查同名再建。
      foreach (ObjectId existingId in ((CivilDocument)civilDoc).GetPipeNetworkIds())
      {
        var existing = CivilObjectUtils.GetRequiredObject<Network>(transaction, existingId, OpenMode.ForRead);
        if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"Pipe network \"{name}\" already exists (handle {CivilObjectUtils.GetHandle(existing)}). Pass a different name, or delete the existing network first.");
      }
      var networkId = CreatePipeNetwork(civilDoc, name);
      var network = CivilObjectUtils.GetRequiredObject<Network>(transaction, networkId, OpenMode.ForWrite);
      if (!string.Equals(network.Name, name, StringComparison.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"Civil 3D created the network as \"{network.Name}\" instead of \"{name}\"; the name was already taken.");

      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layerName) && network is Autodesk.AutoCAD.DatabaseServices.Entity entity)
      {
        entity.Layer = layerName;
      }

      var partsListId = FindPartsListId(civilDoc, transaction, partsListName);
      network.PartsListId = partsListId;

      var styleName = PluginRuntime.GetOptionalString(parameters, "style");
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = FindStyleId(civilDoc, transaction, styleName!, "PipeNetworkStyles", "NetworkStyles");
        if (styleId != ObjectId.Null)
        {
          network.StyleId = styleId;
        }
      }

      var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
      if (!string.IsNullOrWhiteSpace(referenceSurface))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurface!, OpenMode.ForRead);
        network.ReferenceSurfaceId = surface.ObjectId;
      }

      var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, referenceAlignment!);
        network.ReferenceAlignmentId = alignment.ObjectId;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, partsListId),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> AddStructureToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var rimElevation = PluginRuntime.GetOptionalDouble(parameters, "rimElevation") ?? 0.0;
    var sumpDepth = PluginRuntime.GetOptionalDouble(parameters, "sumpDepth") ?? 0.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var location = new Point3d(x, y, rimElevation);
      ObjectId createdStructureId;
      try
      {
        createdStructureId = AddStructureToNetwork(network, transaction, location, partName, rimElevation, sumpDepth);
      }
      catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
      {
        // 2026-11-17 P30: 真机拿 headwall（挡土墙端头）当井用 → 原始 InvalidOperationException
        //   "The structure's type should be junction." 直接冒成 INTERNAL_ERROR。翻译成可行动的输入错误。
        var junctions = EnumeratePartNamesByDomain(
          CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead), transaction)
          .Where(p => p.Domain == "structure" && p.Name.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0)
          .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{partName}' is not a junction structure ({ex.Message.TrimEnd('.')}). Headwalls / culverts / end sections cannot be used as manholes. "
          + "Pick a name that contains 'Structure' from listPipePartsCatalog partsByDomain.structure, e.g. " + string.Join(", ", junctions));
      }
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdStructureId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["structure"] = ToStructureData(structure, transaction),
        ["added"] = true,
      };
    });
  }

  public static Task<object?> AddPipeToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var diameter = PluginRuntime.GetOptionalDouble(parameters, "diameter");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var startStructureName = PluginRuntime.GetOptionalString(parameters, "startStructure");
      var endStructureName = PluginRuntime.GetOptionalString(parameters, "endStructure");
      var startStructureId = !string.IsNullOrWhiteSpace(startStructureName)
        ? FindStructureByName(network, transaction, startStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;
      var endStructureId = !string.IsNullOrWhiteSpace(endStructureName)
        ? FindStructureByName(network, transaction, endStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;

      var startPoint = ReadPoint(parameters, "startPoint");
      var endPoint = ReadPoint(parameters, "endPoint");
      ObjectId createdPipeId;
      try
      {
        createdPipeId = AddPipeToNetwork(network, transaction, partName, diameter, startPoint, endPoint, startStructureId, endStructureId);
      }
      catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Could not add pipe with part '{partName}' ({ex.Message.TrimEnd('.')}). "
          + "Use a pipe part from listPipePartsCatalog partsByDomain.pipe, and make sure startStructure/endStructure exist in this network.");
      }
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdPipeId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["pipe"] = ToPipeData(pipe, transaction),
        ["added"] = true,
      };
    });
  }

  public static Task<object?> ResizePipeInNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetOptionalString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");
    // 2026-11-17 P29: 以前 useClosestSize 写死 false，且不看结果 —— 真机请求 1.8 ft 回来还是 1.5 ft，
    //   接口照样回 resized:true。现在：默认取最接近的规格（可关），改完回读，没变就明确报错。
    var useClosestSize = PluginRuntime.GetOptionalBool(parameters, "useClosestSize") ?? true;

    if (string.IsNullOrWhiteSpace(newPartName) && !newDiameter.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Either 'newPartName' or 'newDiameter' is required.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);

      if (!string.IsNullOrWhiteSpace(newPartName))
      {
        var part = FindPartForNetwork(network, transaction, newPartName!, DomainType.Pipe);
        pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId);
      }

      // 记下"改之前"的真实直径：C3D 的 Resize 失败时会先把尺寸写成无效值（真机 1.000），
      //   事务回滚后又变回来 —— 所以只能跟改之前的值比，不能跟 resize 之后的瞬时值比。
      var diameterBefore = pipe.InnerDiameterOrWidth;
      if (newDiameter.HasValue)
      {
        pipe.ResizeByInnerDiameterOrWidth(newDiameter.Value, useClosestSize: useClosestSize);
      }

      // 回读：写操作不许只说"成功"
      var freshPipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pipe.ObjectId, OpenMode.ForRead);
      var actualDiameter = freshPipe.InnerDiameterOrWidth;
      var diameterChanged = !newDiameter.HasValue
        || (Math.Abs(actualDiameter - diameterBefore) > 1e-9 && Math.Abs(actualDiameter - newDiameter.Value) <= Math.Max(0.001, newDiameter.Value * 0.02));
      if (!diameterChanged)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Resize had no effect: requested inner diameter {newDiameter!.Value:F3}, pipe is still {diameterBefore:F3}. "
          + "The parts list has no size close enough - pass newPartName (an exact part name from the parts list) instead.");
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipeName"] = pipeName,
        ["newPartName"] = newPartName,
        ["requestedDiameter"] = newDiameter,
        ["actualDiameter"] = Math.Round(actualDiameter, 4),
        ["useClosestSize"] = useClosestSize,
        ["resized"] = true,
      };
    });
  }

  public static Task<object?> ListPipePartsCatalogAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");
    // 2026-11-17 P30: 部件表以前靠猜属性名（PartFamilies/PipeFamilies…）全落空 → parts 恒为空。
    //   debug:true 时把真实类型与成员名吐出来，用实测对齐 API，而不是继续猜。
    var debug = PluginRuntime.GetOptionalBool(parameters, "debug") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 2026-11-17 P28: 以前同一个部件表会因为多个枚举来源重复出现（真机："标准" 两次、同 handle 591），
      //   而且反射拿不到 PartFamilies 时 parts 直接是空数组、不说原因。现在按 handle+name 去重，空就给原因。
      var partsLists = EnumeratePartsLists(civilDoc, transaction)
        .Where(partsList => string.IsNullOrWhiteSpace(partsListName) || string.Equals(CivilObjectUtils.GetName(partsList), partsListName, StringComparison.OrdinalIgnoreCase))
        .Select(partsList => new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(partsList),
          ["handle"] = partsList is AcDbObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["parts"] = EnumeratePartNamesByDomain(partsList, transaction).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
          ["partsByDomain"] = new Dictionary<string, object?>
          {
            ["pipe"] = EnumeratePartNamesByDomain(partsList, transaction).Where(p => p.Domain == "pipe").Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
            ["structure"] = EnumeratePartNamesByDomain(partsList, transaction).Where(p => p.Domain == "structure").Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
          },
        })
        .GroupBy(entry => (Convert.ToString(entry["handle"]) ?? "") + "|" + (Convert.ToString(entry["name"]) ?? ""), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

      var anyParts = partsLists.Any(entry => entry["parts"] is List<string> list && list.Count > 0);
      var payload = new Dictionary<string, object?>
      {
        ["partsLists"] = partsLists,
        ["partsListsCount"] = partsLists.Count,
      };
      if (debug)
      {
        var firstList = EnumeratePartsLists(civilDoc, transaction).FirstOrDefault();
        // 2026-11-18 P32: 反射必须留在兼容边界里（tests/reflection_boundary.test.ts 扫边界外反射）。
        if (firstList != null) payload["debug"] = Civil3DCompatibility.DescribeMembers(firstList, 80);
      }

      if (partsLists.Count == 0)
      {
        payload["hint"] = partsListName == null
          ? "No parts list in this drawing (parts lists live with the drawing template)."
          : "No parts list named \"" + partsListName + "\" in this drawing.";
      }
      else if (!anyParts)
      {
        payload["partsUnavailable"] = true;
        payload["hint"] = "Parts lists were found but their part families could not be read via reflection (PART_FAMILY_MAP shape differs in this release) - parts[] is empty for that reason, not because the list is empty.";
      }
      return payload;
    });
  }

  public static int CountPipeNetworks(object civilDoc)
  {
    return ((CivilDocument)civilDoc).GetPipeNetworkIds().Count;
  }

  public static string? GetFirstPipeNetworkStyleName(object civilDoc, Transaction transaction)
  {
    var styles = ((CivilDocument)civilDoc).Styles;
    var collection = Civil3DCompatibility.GetPropertyValue(styles, "PipeNetworkStyles")
      ?? Civil3DCompatibility.GetPropertyValue(styles, "NetworkStyles");
    return LookupUtils.GetFirstStyleName(collection, transaction);
  }

  private static IEnumerable<Network> EnumeratePipeNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).GetPipeNetworkIds())
    {
      yield return CivilObjectUtils.GetRequiredObject<Network>(transaction, objectId, openMode);
    }
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
      foreach (var objectId in ToObjectIdsFlexible(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static IEnumerable<ObjectId> ToObjectIdsFlexible(object? value)
  {
    foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
    {
      yield return objectId;
    }

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId && objectId != ObjectId.Null)
        {
          yield return objectId;
          continue;
        }

        var itemObjectId = GetAnyObjectId(item, "ObjectId", "Id");
        if (itemObjectId != ObjectId.Null)
        {
          yield return itemObjectId;
        }
      }
    }
  }

  private static Network FindPipeNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (var network in EnumeratePipeNetworks(civilDoc, transaction, openMode))
    {
      if (string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
      {
        return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");
  }

  private static Pipe FindPipeByName(Network network, Transaction transaction, string pipeName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetPipeIds())
    {
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, openMode);
      if (string.Equals(pipe.Name, pipeName, StringComparison.OrdinalIgnoreCase))
      {
        return pipe;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{pipeName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Structure FindStructureByName(Network network, Transaction transaction, string structureName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetStructureIds())
    {
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, openMode);
      if (string.Equals(structure.Name, structureName, StringComparison.OrdinalIgnoreCase))
      {
        return structure;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{structureName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  // 2026-10-19: 单个属性读取失败不该让整个方法 INTERNAL_ERROR。
  //   真机：新建的**空管网**（无管、无结构）在 listPipeNetworks / getPipeNetwork 上抛
  //   CivilException "Retrieve attribute failed." —— C3D 对空管网的某些属性不返回。
  //   现在逐字段 best-effort，并在返回里带 "<字段>Error"，坏在哪儿一眼可见。
  private static object? TryReadField(Func<object?> read, string key, Dictionary<string, object?> bag)
  {
    try { return read(); }
    catch (Exception ex) { bag[key + "Error"] = ex.Message; return null; }
  }

  private static Dictionary<string, object?> ToPipeNetworkSummary(Network network, Transaction transaction)
  {
    var errs = new Dictionary<string, object?>();
    var data = new Dictionary<string, object?>
    {
      ["name"] = TryReadField(() => network.Name, "name", errs),
      ["handle"] = TryReadField(() => CivilObjectUtils.GetHandle(network), "handle", errs),
      ["pipeCount"] = TryReadField(() => network.GetPipeIds().Count, "pipeCount", errs),
      ["structureCount"] = TryReadField(() => network.GetStructureIds().Count, "structureCount", errs),
      ["surface"] = TryReadField(() => ResolveObjectName(transaction, network.ReferenceSurfaceId), "surface", errs),
    };
    foreach (var kv in errs) data[kv.Key] = kv.Value;
    return data;
  }

  private static Dictionary<string, object?> ToPipeNetworkDetail(Network network, Transaction transaction)
  {
    var preErrs = new Dictionary<string, object?>();
    var pipes = TryReadField(() => network.GetPipeIds().Cast<ObjectId>()
      .Select(objectId => ToPipeData(CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, OpenMode.ForRead), transaction))
      .ToList(), "pipes", preErrs) ?? new List<Dictionary<string, object?>>();
    var structures = TryReadField(() => network.GetStructureIds().Cast<ObjectId>()
      .Select(objectId => ToStructureData(CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, OpenMode.ForRead), transaction))
      .ToList(), "structures", preErrs) ?? new List<Dictionary<string, object?>>();

    var errs = new Dictionary<string, object?>();
    var data = new Dictionary<string, object?>
    {
      ["name"] = TryReadField(() => network.Name, "name", errs),
      ["handle"] = TryReadField(() => CivilObjectUtils.GetHandle(network), "handle", errs),
      ["partsList"] = TryReadField(() => ResolveObjectName(transaction, network.PartsListId), "partsList", errs),
      ["style"] = TryReadField(() => network.StyleName, "style", errs),
      ["referenceSurface"] = TryReadField(() => ResolveObjectName(transaction, network.ReferenceSurfaceId), "referenceSurface", errs),
      ["referenceAlignment"] = TryReadField(() => ResolveObjectName(transaction, network.ReferenceAlignmentId), "referenceAlignment", errs),
      ["pipes"] = pipes,
      ["structures"] = structures,
    };
    foreach (var kv in errs) data[kv.Key] = kv.Value;
    return data;
  }

  private static Dictionary<string, object?> ToPipeData(Pipe pipe, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = pipe.Name,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      ["startStructure"] = ResolveObjectName(transaction, pipe.StartStructureId),
      ["endStructure"] = ResolveObjectName(transaction, pipe.EndStructureId),
      ["length"] = pipe.Length3D,
      ["diameter"] = pipe.InnerDiameterOrWidth,
      ["slope"] = pipe.Slope,
      ["material"] = pipe.Material,
      ["centerlineStartElevation"] = pipe.StartPoint.Z,
      ["centerlineEndElevation"] = pipe.EndPoint.Z,
      ["invertIn"] = null,
      ["invertOut"] = null,
      ["invertNote"] = "The Civil 3D 2026 managed Pipe API does not expose endpoint invert elevations directly; centerline elevations are returned instead.",
    };
  }

  /// <summary>枚举一个管网里的所有管道实体（P28：干扰检查用；读失败的对象跳过并记日志）。</summary>
  internal static IEnumerable<Pipe> EnumeratePipes(Network network, Transaction transaction)
  {
    foreach (ObjectId objectId in network.GetPipeIds())
    {
      Pipe? pipe = null;
      try { pipe = transaction.GetObject(objectId, OpenMode.ForRead) as Pipe; }
      catch (Exception ex) { PluginLog.Warn("PipeNetwork", "pipe unreadable during interference check: " + objectId.Handle + ": " + ex.Message); }
      if (pipe != null) yield return pipe;
    }
  }

  internal static Dictionary<string, object?> ToStructureData(Structure structure, Transaction transaction)
  {
    var connectedPipeIds = Enumerable.Range(0, structure.ConnectedPipesCount)
      .Select(index => Civil3DCompatibility.GetIndexedPropertyValue(structure, "ConnectedPipe", index))
      .OfType<ObjectId>();

    return new Dictionary<string, object?>
    {
      ["name"] = structure.Name,
      ["handle"] = CivilObjectUtils.GetHandle(structure),
      ["type"] = structure.PartType.ToString(),
      ["rimElevation"] = structure.RimElevation,
      ["sumpElevation"] = structure.SumpElevation,
      ["x"] = structure.Location.X,
      ["y"] = structure.Location.Y,
      ["connectedPipes"] = connectedPipeIds.Select(objectId => ResolveObjectName(transaction, objectId) ?? objectId.Handle.ToString()).ToList(),
    };
  }

  private static ObjectId CreatePipeNetwork(object civilDoc, string name)
  {
    var requestedName = name;
    return Network.Create((CivilDocument)civilDoc, ref requestedName);
  }

  private static ObjectId AddStructureToNetwork(Network network, Transaction transaction, Point3d location, string partName, double rimElevation, double sumpDepth)
  {
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Structure);
    var createdId = ObjectId.Null;
    network.AddStructure(part.FamilyId, part.SizeId, location, 0.0, ref createdId, applyRules: false);
    var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdId, OpenMode.ForWrite);
    structure.RimElevation = rimElevation;
    structure.RimToSumpHeight = sumpDepth;
    return createdId;
  }

  private static ObjectId AddPipeToNetwork(Network network, Transaction transaction, string partName, double? diameter, Point3d? startPoint, Point3d? endPoint, ObjectId startStructureId, ObjectId endStructureId)
  {
    var start = startPoint ?? GetStructureLocation(transaction, startStructureId, "start");
    var end = endPoint ?? GetStructureLocation(transaction, endStructureId, "end");
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Pipe);
    var createdId = ObjectId.Null;
    network.AddLinePipe(part.FamilyId, part.SizeId, new LineSegment3d(start, end), ref createdId, applyRules: false);
    var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdId, OpenMode.ForWrite);
    if (startStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.Start, startStructureId, force: true);
    if (endStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.End, endStructureId, force: true);
    if (diameter.HasValue && Math.Abs(pipe.InnerDiameterOrWidth - diameter.Value) > 1.0e-6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Part size '{partName}' has inner diameter/width {pipe.InnerDiameterOrWidth}, not the requested {diameter.Value}. The pipe was not committed.");
    return createdId;
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(object owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      object? memberValue = CivilObjectUtils.InvokeMethod(owner, memberName) ?? GetNamedMemberValue(owner, memberName);
      foreach (var objectId in ToObjectIdsFlexible(memberValue))
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
    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(dbObject);
    }
    catch
    {
      return null;
    }
  }

  private static ObjectId GetAnyObjectId(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var objectId = CivilObjectUtils.GetPropertyValue<ObjectId>(value, propertyName);
      if (objectId != ObjectId.Null)
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(propertyValue))
      {
        return propertyValue;
      }
    }

    return null;
  }

  private static Point3d? GetPointProperty(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<Point3d?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static double Distance(Point3d? startPoint, Point3d? endPoint)
  {
    if (!startPoint.HasValue || !endPoint.HasValue)
    {
      return 0.0;
    }

    return startPoint.Value.DistanceTo(endPoint.Value);
  }


  private static Point3d? ReadPoint(JsonObject? parameters, string parameterName)
  {
    if (PluginRuntime.GetParameter(parameters, parameterName) is not JsonObject pointNode)
    {
      return null;
    }

    var x = pointNode["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.x is required.");
    var y = pointNode["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.y is required.");
    var z = pointNode["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.z is required; elevation 0 will not be assumed.");
    return new Point3d(x, y, z);
  }

  private static ObjectId FindStyleId(object civilDoc, Transaction transaction, string styleName, params string[] collectionNames)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    if (styles == null)
    {
      return ObjectId.Null;
    }

    foreach (var collectionName in collectionNames)
    {
      var collection = GetNamedMemberValue(styles, collectionName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(collection))
      {
        if (objectId == ObjectId.Null)
        {
          continue;
        }

        var style = transaction.GetObject(objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
        {
          return objectId;
        }
      }
    }

    return ObjectId.Null;
  }

  private static ObjectId FindPartsListId(object civilDoc, Transaction transaction, string partsListName)
  {
    foreach (var partsList in EnumeratePartsLists(civilDoc, transaction))
    {
      if (string.Equals(CivilObjectUtils.GetName(partsList), partsListName, StringComparison.OrdinalIgnoreCase) && partsList is AcDbObject dbObject)
      {
        return dbObject.ObjectId;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found.");
  }

  private static IEnumerable<object> EnumeratePartsLists(object civilDoc, Transaction transaction)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    if (styles == null)
    {
      yield break;
    }

    foreach (var collectionName in new[] { "PartsListSet", "PartsLists", "PartsListCollection", "PartsListStyles" })
    {
      var collection = GetNamedMemberValue(styles, collectionName) ?? GetNamedMemberValue(civilDoc, collectionName);
      foreach (var objectId in ToObjectIdsFlexible(collection))
      {
        yield return transaction.GetObject(objectId, OpenMode.ForRead)!;
      }
    }
  }

  /// <summary>
  /// 2026-11-17 P30: 以前靠猜属性名（PartFamilies/PipeFamilies…）→ 真机 parts 恒为空。
  ///   实测 API：Autodesk.Civil.DatabaseServices.Styles.PartsList 有
  ///   PartFamilyCount + GetPartFamilyIdsByDomain(DomainType)，PartFamily 有 PartSizeCount + 索引器。
  ///   现在按 domain 列出尺寸名（就是 addPipeToNetwork / addStructureToNetwork 要的 partName）。
  /// </summary>
  private static IEnumerable<(string Domain, string Name)> EnumeratePartNamesByDomain(object partsList, Transaction transaction)
  {
    if (partsList is not PartsList typedPartsList) yield break;
    foreach (var (domain, domainName) in new[] { (DomainType.Pipe, "pipe"), (DomainType.Structure, "structure") })
    {
      ObjectIdCollection familyIds;
      try { familyIds = typedPartsList.GetPartFamilyIdsByDomain(domain); }
      catch (Exception ex) { PluginLog.Warn("PipeNetwork", "part family ids unreadable for domain " + domainName + ": " + ex.Message); continue; }
      foreach (ObjectId familyId in familyIds)
      {
        PartFamily family;
        try { family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead); }
        catch (Exception ex) { PluginLog.Warn("PipeNetwork", "part family unreadable: " + familyId.Handle + ": " + ex.Message); continue; }
        for (var index = 0; index < family.PartSizeCount; index++)
        {
          string? sizeName = null;
          try
          {
            var size = transaction.GetObject(family[index], OpenMode.ForRead);
            sizeName = CivilObjectUtils.GetName(size) ?? CivilObjectUtils.GetStringProperty(size, "Description");
          }
          catch (Exception ex) { PluginLog.Warn("PipeNetwork", "part size unreadable in family " + family.Name + ": " + ex.Message); }
          if (!string.IsNullOrWhiteSpace(sizeName)) yield return (domainName, sizeName!);
        }
      }
    }
  }

  private static IEnumerable<object> EnumerateNamedObjects(object? collection)
  {
    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item == null)
        {
          continue;
        }

        yield return item;
      }
    }
  }

  private readonly record struct NetworkPartIds(ObjectId FamilyId, ObjectId SizeId);

  private static NetworkPartIds FindPartForNetwork(Network network, Transaction transaction, string partName, DomainType domain)
  {
    if (network.PartsListId.IsNull)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{network.Name}' does not have a parts list assigned.");
    }

    var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead);
    foreach (ObjectId familyId in partsList.GetPartFamilyIdsByDomain(domain))
    {
      var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
      for (var index = 0; index < family.PartSizeCount; index++)
      {
        var sizeId = family[index];
        var size = transaction.GetObject(sizeId, OpenMode.ForRead);
        var sizeName = CivilObjectUtils.GetName(size) ?? CivilObjectUtils.GetStringProperty(size, "Description");
        if (string.Equals(sizeName, partName, StringComparison.OrdinalIgnoreCase)
          || (family.PartSizeCount == 1 && string.Equals(family.Name, partName, StringComparison.OrdinalIgnoreCase)))
          return new NetworkPartIds(familyId, sizeId);
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Exact {domain} size '{partName}' was not found in the parts list for network '{network.Name}'.");
  }

  private static Point3d GetStructureLocation(Transaction transaction, ObjectId structureId, string endpointName)
  {
    if (structureId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Provide {endpointName}Point or {endpointName}Structure; an origin point will not be assumed.");
    return CivilObjectUtils.GetRequiredObject<Structure>(transaction, structureId, OpenMode.ForRead).Location;
  }
}
