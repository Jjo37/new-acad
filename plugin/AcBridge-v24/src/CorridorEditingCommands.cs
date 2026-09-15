using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_corridor_editing tools:
///   getCorridorTargetMappings / setCorridorTargetMappings /
///   addCorridorRegion / deleteCorridorRegion.
///
/// Civil 3D API notes:
///   Corridor.Baselines  → Baseline collection
///   Baseline.BaselineRegions → BaselineRegion collection
///   BaselineRegion.AppliedAssemblyId, StartStation, EndStation
///   Target mappings are on SubassemblyTargetInfo objects attached to each region.
/// </summary>
public static class CorridorEditingCommands
{
  // -------------------------------------------------------------------------
  // getCorridorTargetMappings
  // -------------------------------------------------------------------------

  public static Task<object?> GetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);

      var results = new List<Dictionary<string, object?>>();

      var regions = baseline.BaselineRegions;
      for (var ri = 0; ri < regions.Count; ri++)
      {
        if (regionIndex.HasValue && ri != regionIndex.Value) continue;
        var region = regions[ri];
        var targets = ReadTargetMappings(region, transaction);
        results.Add(new Dictionary<string, object?>
        {
          ["regionIndex"] = ri,
          ["regionName"] = region.Name,
          ["startStation"] = region.StartStation,
          ["endStation"] = region.EndStation,
          ["targets"] = targets,
        });
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regions"] = results,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setCorridorTargetMappings
  // -------------------------------------------------------------------------

  public static Task<object?> SetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex") ?? 0;
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var targetsNode = PluginRuntime.GetParameter(parameters, "targets") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "targets array is required.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Region index {regionIndex} is out of range. Corridor '{corridorName}' baseline {baselineIndex} has {baseline.BaselineRegions.Count} region(s).");

      var region = baseline.BaselineRegions[regionIndex];
      var appliedCount = 0;

      foreach (var targetNode in targetsNode)
      {
        if (targetNode is not JsonObject t) continue;
        var paramName = t["parameterName"]?.GetValue<string>();
        var targetType = t["targetType"]?.GetValue<string>();
        var targetName = t["targetName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(paramName) || string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetName)) continue;

        // Find the Civil 3D object to use as target
        var targetId = ResolveTargetObjectId(civilDoc, transaction, targetType!, targetName!);
        if (targetId == null)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Target object '{targetName}' of type '{targetType}' was not found.");

        // Apply target mapping via reflection on the region's target collection
        var applied = ApplyTargetMapping(region, paramName!, targetType!, targetId.Value);
        if (applied) appliedCount++;
      }

      // Rebuild corridor to apply changes
      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = regionIndex,
        ["targetsApplied"] = appliedCount,
        ["message"] = $"Applied {appliedCount} target mapping(s) and rebuilt corridor '{corridorName}'.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // addCorridorRegion
  // -------------------------------------------------------------------------

  public static Task<object?> AddCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var frequency = PluginRuntime.GetOptionalDouble(parameters, "frequency") ?? 10.0;
    var frequencyAtCurves = PluginRuntime.GetOptionalDouble(parameters, "frequencyAtCurves") ?? frequency;
    var frequencyAtKneePoints = PluginRuntime.GetOptionalDouble(parameters, "frequencyAtKneePoints") ?? frequency;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      // Find the assembly
      ObjectId assemblyId = ObjectId.Null;
      foreach (ObjectId aid in civilDoc.AssemblyCollection)
      {
        var asm = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, aid, OpenMode.ForRead);
        if (string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        {
          assemblyId = aid;
          break;
        }
      }
      if (assemblyId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{assemblyName}' was not found.");

      // 真实 API（25.0.58，api-inspect 确认）: BaselineRegionCollection.Add(regionName, assemblyId, startStation, endStation)
      //   或 Add(regionName, assemblyId)（默认全区间）。旧反射 AddRegion 签名不匹配，从未成功过。
      var regionName = PluginRuntime.GetOptionalString(parameters, "regionName")
        ?? $"Region-{baseline.BaselineRegions.Count + 1}";
      BaselineRegion newRegion;
      if (endStation > startStation)
      {
        newRegion = baseline.BaselineRegions.Add(regionName, assemblyId, startStation, endStation);
      }
      else
      {
        newRegion = baseline.BaselineRegions.Add(regionName, assemblyId);
      }
      var newRegionIndex = baseline.BaselineRegions.IndexOf(newRegion);
      var actualStart = newRegion.StartStation;
      var actualEnd = newRegion.EndStation;

      // Rebuild corridor to apply changes
      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = newRegionIndex,
        ["regionName"] = regionName,
        ["assemblyName"] = assemblyName,
        ["startStation"] = actualStart,
        ["endStation"] = actualEnd,
        ["message"] = $"Region '{regionName}' added to corridor '{corridorName}' at stations {actualStart:F3}–{actualEnd:F3}.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // createCorridor（2026-08-16 新增：路线+装配→廊道）
  // -------------------------------------------------------------------------
  // 25.0.58 反射确认 CorridorCollection.Add 重载：
  //   Add(name, baselineName, alignmentId, profileId, regionName, assemblyId)  ← 一条龙
  //   Add(name, baselineName, alignmentId, profileId)
  //   Add(name, baselineName, featureLineId)
  //   Add(name)
  // Corridor.StyleId 可写；Corridor.Rebuild() 存在
  public static Task<object?> CreateCorridorAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var baselineName = PluginRuntime.GetOptionalString(parameters, "baselineName") ?? "Baseline-1";
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName") ?? "Region-1";
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var profileName = PluginRuntime.GetOptionalString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 名称冲突检查
      foreach (ObjectId cid in civilDoc.CorridorCollection)
      {
        var c = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, cid, OpenMode.ForRead);
        if (string.Equals(c.Name, corridorName, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Corridor '{corridorName}' already exists.");
      }

      // 找路线
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      // 找装配
      ObjectId assemblyId = ObjectId.Null;
      foreach (ObjectId aid in civilDoc.AssemblyCollection)
      {
        var asm = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, aid, OpenMode.ForRead);
        if (string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        {
          assemblyId = aid;
          break;
        }
      }
      if (assemblyId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Assembly '{assemblyName}' was not found. Use createAssembly first.");

      // 可选：找纵断面（作廊道竖向基准）
      ObjectId profileId = ObjectId.Null;
      if (!string.IsNullOrWhiteSpace(profileName))
      {
        profileId = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead).ObjectId;
      }

      // 创建廊道
      ObjectId corridorId;
      if (!profileId.IsNull && endStation > startStation)
      {
        corridorId = civilDoc.CorridorCollection.Add(
          corridorName, baselineName, alignment.ObjectId, profileId, regionName, assemblyId);
      }
      else if (!profileId.IsNull)
      {
        // 2026-09-11 fix: 只给纵断、未给桩号时，绝不能走 Add(name,baseline,alignment,profile)——
        // 那个重载只建基准线、**不挂区域/装配** → 廊道 0 几何（面板实测踩过，误判成"自定义部件不行"）。
        // 改用带 region+assembly 的重载（默认覆盖整条路线）。
        corridorId = civilDoc.CorridorCollection.Add(
          corridorName, baselineName, alignment.ObjectId, profileId, regionName, assemblyId);
      }
      else if (endStation > startStation)
      {
        // 无纵断面：用 featureLine 重载不行（无 featureLine），用 name+baseline+alignment 版本需要 profile——
        // 用 5 参版本传 regionName+assemblyId（alignment 重载必须带 profile），所以无 profile 时先建空廊道再手动加区域
        corridorId = civilDoc.CorridorCollection.Add(corridorName);
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
        var baseline = corridor.Baselines.Add(baselineName, alignment.ObjectId, ObjectId.Null);
        baseline.BaselineRegions.Add(regionName, assemblyId, startStation.Value, endStation.Value);
        corridor.Rebuild();
        return BuildCorridorResult(civilDoc, transaction, corridor, corridorName, alignmentName,
          assemblyName, profileName, surfaceName, styleName, startStation, endStation, regionName);
      }
      else
      {
        corridorId = civilDoc.CorridorCollection.Add(corridorName);
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
        var baseline = corridor.Baselines.Add(baselineName, alignment.ObjectId, ObjectId.Null);
        baseline.BaselineRegions.Add(regionName, assemblyId);
        corridor.Rebuild();
        return BuildCorridorResult(civilDoc, transaction, corridor, corridorName, alignmentName,
          assemblyName, profileName, surfaceName, styleName, startStation, endStation, regionName);
      }

      var created = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);

      // 改样式（可选）
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = LookupUtils.GetCorridorStyleId(civilDoc, transaction, styleName);
        if (!styleId.IsNull)
        {
          var w = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
          w.StyleId = styleId;
        }
      }

      // 关联曲面（可选）——CorridorSurfaceCollection.Add(name) 25.0.58 反射确认
      if (!string.IsNullOrWhiteSpace(surfaceName))
      {
        var w2 = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
        w2.CorridorSurfaces.Add(surfaceName);
        w2.Rebuild();
      }

      return BuildCorridorResult(civilDoc, transaction, created, corridorName, alignmentName,
        assemblyName, profileName, surfaceName, styleName, startStation, endStation, regionName);
    });
  }

  // -------------------------------------------------------------------------
  // addCorridorSurface（2026-09-11：一键给廊道加曲面——面板"模型算量"用）
  // 反射确认 25.0.58: Corridor.CorridorSurfaces.Add(name)->CorridorSurface;
  //   CorridorSurface.AddLinkCode(code, asBreakLine) / LinkCodes() / IsBuild(set) / SurfaceId;
  //   Baseline.AppliedAssembly.Links[].Codes -> 自动收集链接码
  // -------------------------------------------------------------------------
  // addCorridorSurface（2026-09-11：一键给廊道加曲面——面板"模型算量"用）
  // 走 job 通道：corridor.Rebuild() 同步且耗时长（实测 656m 廊道 > 25s），
  // 不能在 JSON-RPC 处理器里 await → 立即返回 jobId，调用方轮询 jobStatus。
  // 反射确认 25.0.58: Corridor.CorridorSurfaces.Add(name)->CorridorSurface;
  //   CorridorSurface.AddLinkCode(code,asBreakLine)/LinkCodes()/IsBuild(set)/SurfaceId;
  //   Baseline.GetAppliedAssemblyAtIndex(0).Links[].CorridorCodes -> 自动收集链接码
  public static Task<object?> AddCorridorSurfaceAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetOptionalString(parameters, "name")
      ?? PluginRuntime.GetOptionalString(parameters, "corridorName");
    if (string.IsNullOrWhiteSpace(corridorName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "addCorridorSurface requires 'name' (corridor name).");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName") ?? "CorridorSurface-1";
    var codesNode = PluginRuntime.GetParameter(parameters, "codes") as JsonArray;
    var asBreak = PluginRuntime.GetOptionalBool(parameters, "asBreakLine") ?? true;

    var codes = new List<string>();
    if (codesNode != null)
    {
      foreach (var n in codesNode)
      {
        var v = PluginRuntime.JsonNodeToString(n);
        if (!string.IsNullOrWhiteSpace(v)) codes.Add(v.Trim());
      }
    }
    bool auto = codes.Count == 0;

    var requestId = PluginRuntime.GetCurrentRequestId();
    var job = JobRegistry.Create(
      $"Adding corridor surface {surfaceName} to {corridorName}",
      "corridor_surface",
      requestId,
      PluginRuntime.GetActiveDrawingIdentity());
    job.CancellationSource = new CancellationTokenSource();
    var cancellationToken = job.CancellationSource.Token;

    _ = Task.Run(async () =>
    {
      try
      {
        await PluginRuntime.RunWithRequestContextAsync(
          "addCorridorSurface",
          $"{requestId ?? "job"}:job:{job.JobId}",
          cancellationToken,
          job.DrawingIdentity,
          async () =>
          {
            cancellationToken.ThrowIfCancellationRequested();
            JobRegistry.Progress(job.JobId, 10, $"Adding surface '{surfaceName}' to corridor '{corridorName}'", null);

            var result = await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
            {
              cancellationToken.ThrowIfCancellationRequested();
              var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
              try { PluginLog.Info("Corridor", "addCorridorSurface " + "step1: corridor found"); } catch { }

              Autodesk.Civil.DatabaseServices.CorridorSurface? cs = null;
              foreach (Autodesk.Civil.DatabaseServices.CorridorSurface c in corridor.CorridorSurfaces)
              {
                if (string.Equals(c.Name, surfaceName, StringComparison.OrdinalIgnoreCase)) { cs = c; break; }
              }
              if (cs == null) cs = corridor.CorridorSurfaces.Add(surfaceName);
              var csName = cs.Name;
              try { PluginLog.Info("Corridor", "addCorridorSurface " + "step2: surface ready name=" + csName); } catch { }

              var linkCodes = new List<string>();
              if (codes.Count > 0)
              {
                linkCodes.AddRange(codes);
              }
              else
              {
                // 自动收集：从已构建的 applied assembly 扒链接码
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                  foreach (Autodesk.Civil.DatabaseServices.Baseline b in corridor.Baselines)
                  {
                    var aa = b.GetAppliedAssemblyAtIndex(0);
                    if (aa == null) continue;
                    foreach (var lk in aa.Links)
                    {
                      foreach (var cd in lk.CorridorCodes)
                      {
                        var t = cd?.ToString();
                        if (!string.IsNullOrWhiteSpace(t)) set.Add(t!);
                      }
                    }
                  }
                }
                catch { }
                linkCodes = set.ToList();
              }

              try { PluginLog.Info("Corridor", "addCorridorSurface " + "step3: codes=" + string.Join(",", linkCodes)); } catch { }
              var added = new List<string>();
              foreach (var code in linkCodes)
              {
                try { cs.AddLinkCode(code, asBreak); added.Add(code); } catch { }
              }
              try { PluginLog.Info("Corridor", "addCorridorSurface " + "step4: codes added count=" + added.Count); } catch { }
              cs.IsBuild = true;
              try { PluginLog.Info("Corridor", "addCorridorSurface " + "step5: isBuild set, setup done"); } catch { }

              // 2026-09-11: **不在本 job 内重建**。实测同一事务里"挂曲面码 + corridor.Rebuild()"
              // 可跑超 300s（超时诊断显示 cmdNames='' cmdActive=0、上下文空闲 → 是重建本身太重，
              // 不是上下文被占）。拆两步：本方法只挂数据（秒回），随后调用方用 rebuildCorridor(job) 构建。
              string? sid = null;
              var surfaceCodes = new List<string>();
              bool isBuild = true;
              try { if (!cs.SurfaceId.IsNull) sid = cs.SurfaceId.Handle.ToString(); } catch { }
              try { surfaceCodes = cs.LinkCodes().ToList(); } catch { }
              try { isBuild = cs.IsBuild; } catch { }

              return new Dictionary<string, object?>
              {
                ["corridorName"] = corridor.Name,
                ["surfaceName"] = csName,
                ["surfaceId"] = sid,
                ["addedLinkCodes"] = added,
                ["surfaceLinkCodes"] = surfaceCodes,
                ["isBuild"] = isBuild,
                ["autoCollected"] = auto,
                ["surfaceCount"] = corridor.CorridorSurfaces.Count,
                ["rebuilt"] = false,
                ["note"] = "曲面数据已挂上（链接码→曲面）。**还需调 rebuildCorridor 触发构建**（构建可能耗时 1 分钟以上），构建后用 getSurfaceStatistics 验证。",
              };
            });

            JobRegistry.Complete(job.JobId, result);
            PluginLog.Info("Corridor", $"Corridor surface '{surfaceName}' added to '{corridorName}' (job {job.JobId}).");
            return result;
          },
          isJob: true);
      }
      catch (OperationCanceledException)
      {
        JobRegistry.AcknowledgeCancellation(job.JobId);
        PluginLog.Info("Corridor", $"addCorridorSurface cancelled (job {job.JobId}).");
      }
      catch (Exception ex)
      {
        PluginLog.Error("Corridor", $"addCorridorSurface failed for '{corridorName}' (job {job.JobId}).", ex);
        try { JobRegistry.Fail(job.JobId, ex.Message); } catch { }
        job.CancellationSource = null;
      }
      finally
      {
        try { job.CancellationSource?.Dispose(); } catch (ObjectDisposedException) { }
      }
    }, CancellationToken.None);

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["jobId"] = job.JobId,
      ["state"] = "running",
      ["message"] = $"Corridor surface '{surfaceName}' queued for '{corridorName}'. Poll jobStatus with the jobId.",
    });
  }

  private static Dictionary<string, object?> BuildCorridorResult(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    Corridor corridor,
    string corridorName,
    string alignmentName,
    string assemblyName,
    string? profileName,
    string? surfaceName,
    string? styleName,
    double? startStation,
    double? endStation,
    string regionName)
  {
    var result = new Dictionary<string, object?>
    {
      ["corridorName"] = corridor.Name,
      ["handle"] = CivilObjectUtils.GetHandle(corridor),
      ["alignmentName"] = alignmentName,
      ["assemblyName"] = assemblyName,
      ["created"] = true,
    };
    if (!string.IsNullOrWhiteSpace(profileName)) result["profileName"] = profileName;
    if (!string.IsNullOrWhiteSpace(surfaceName)) result["surfaceName"] = surfaceName;
    if (!string.IsNullOrWhiteSpace(styleName)) result["style"] = styleName;
    if (startStation.HasValue) result["startStation"] = startStation.Value;
    if (endStation.HasValue) result["endStation"] = endStation.Value;
    result["regionName"] = regionName;
    result["message"] = $"Corridor '{corridor.Name}' created from alignment '{alignmentName}' with assembly '{assemblyName}'.";
    return result;
  }

  // -------------------------------------------------------------------------
  // deleteCorridorRegion
  // -------------------------------------------------------------------------

  public static Task<object?> DeleteCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Region index {regionIndex} is out of range.");

      var region = baseline.BaselineRegions[regionIndex];
      var regionName = region.Name;
      var startStation = region.StartStation;
      var endStation = region.EndStation;

      // 真实 API（25.0.58）: BaselineRegionCollection.RemoveAt(index) / Remove(region) / Remove(name)
      // 旧反射 RemoveRegion/DeleteRegion 签名不匹配，从未成功过。
      baseline.BaselineRegions.RemoveAt(regionIndex);

      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["deletedRegionIndex"] = regionIndex,
        ["deletedRegionName"] = regionName,
        ["deletedStartStation"] = startStation,
        ["deletedEndStation"] = endStation,
        ["message"] = $"Region '{regionName}' deleted from corridor '{corridorName}'.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static Baseline GetBaseline(Corridor corridor, int index)
  {
    if (index < 0 || index >= corridor.Baselines.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Baseline index {index} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
    return corridor.Baselines[index];
  }

  private static List<Dictionary<string, object?>> ReadTargetMappings(
    BaselineRegion region, Transaction transaction)
  {
    var mappings = new List<Dictionary<string, object?>>();
    var targetInfo = CivilObjectUtils.GetPropertyValue<object>(region, "Targets")
      ?? CivilObjectUtils.InvokeMethod(region, "GetTargets");

    if (targetInfo is System.Collections.IEnumerable targets)
    {
      foreach (var t in targets)
      {
        var paramName = CivilObjectUtils.GetStringProperty(t, "ParameterName")
          ?? CivilObjectUtils.GetStringProperty(t, "Name");
        var targetType = CivilObjectUtils.GetStringProperty(t, "TargetType")
          ?? CivilObjectUtils.GetStringProperty(t, "Type");

        var targetId = Civil3DCompatibility.GetPropertyValue<ObjectId?>(t, "TargetId")
          ?? Civil3DCompatibility.GetPropertyValue<ObjectId?>(t, "ObjectId");
        string? targetName = null;
        if (targetId is ObjectId tid && !tid.IsNull)
        {
          var targetObj = transaction.GetObject(tid, OpenMode.ForRead);
          targetName = CivilObjectUtils.GetName(targetObj);
        }

        mappings.Add(new Dictionary<string, object?>
        {
          ["parameterName"] = paramName,
          ["targetType"] = targetType,
          ["targetName"] = targetName,
        });
      }
    }

    return mappings;
  }

  private static ObjectId? ResolveTargetObjectId(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction, string targetType, string targetName)
  {
    switch (targetType.ToLowerInvariant())
    {
      case "surface":
        foreach (ObjectId id in civilDoc.GetSurfaceIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
          if (string.Equals(s.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "alignment":
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
        {
          var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
          if (string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "profile":
        foreach (ObjectId aid in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
          foreach (ObjectId pid in alignment.GetProfileIds())
          {
            var p = CivilObjectUtils.GetRequiredObject<Profile>(transaction, pid, OpenMode.ForRead);
            if (string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase)) return pid;
          }
        }
        break;
    }
    return null;
  }

  private static bool ApplyTargetMapping(BaselineRegion region, string paramName, string targetType, ObjectId targetId)
  {
    // Try via SetTarget or AssignTarget methods
    if (Civil3DCompatibility.TryInvokeMethod(region, "SetTarget", out _, paramName, targetId)) return true;
    if (Civil3DCompatibility.TryInvokeMethod(region, "AssignTarget", out _, paramName, targetId)) return true;

    // Try via Targets collection
    var targets = CivilObjectUtils.GetPropertyValue<object>(region, "Targets");
    if (targets != null)
    {
      foreach (var methodName in new[] { "SetTarget", "Add", "AssignTarget" })
      {
        if (Civil3DCompatibility.TryInvokeMethod(targets, methodName, out _, paramName, targetId))
        {
          return true;
        }
      }
    }

    return false;
  }
}
