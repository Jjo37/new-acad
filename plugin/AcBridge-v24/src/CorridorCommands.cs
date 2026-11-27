using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class CorridorCommands
{
  public static Task<object?> ListCorridorsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridors = new List<Dictionary<string, object?>>();
      foreach (ObjectId objectId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, objectId, OpenMode.ForRead);
        corridors.Add(ToCorridorSummary(corridor));
      }

      return new Dictionary<string, object?>
      {
        ["corridors"] = corridors,
      };
    });
  }

  public static Task<object?> GetCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var baselines = new List<Dictionary<string, object?>>();

      foreach (Baseline baseline in corridor.Baselines)
      {
        var regions = new List<Dictionary<string, object?>>();
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
          regions.Add(new Dictionary<string, object?>
          {
            ["name"] = region.Name,
            ["assemblyName"] = CivilObjectUtils.GetName(transaction.GetObject(region.AssemblyId, OpenMode.ForRead)) ?? string.Empty,
            ["startStation"] = region.StartStation,
            ["endStation"] = region.EndStation,
            // 2026-11-16 P6: 回读真实频率。以前写死 0 —— 调用方设了 frequency 也看不出生效没有
        //   （实际早已写进 AppliedAssemblySetting，只是这里没读回来）。
        ["frequency"] = TryReadRegionFrequency(region),
          });
        }

        baselines.Add(new Dictionary<string, object?>
        {
          ["name"] = baseline.Name,
          ["alignmentName"] = CivilObjectUtils.GetName(transaction.GetObject(baseline.AlignmentId, OpenMode.ForRead)) ?? string.Empty,
          ["profileName"] = CivilObjectUtils.GetName(transaction.GetObject(baseline.ProfileId, OpenMode.ForRead)) ?? string.Empty,
          ["regions"] = regions,
        });
      }

      var surfaces = ReadCorridorSurfaces(corridor, transaction);
      var featureLines = ReadCorridorFeatureLineCodes(corridor).Select(code => new Dictionary<string, object?> { ["name"] = code }).ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = corridor.Name,
        ["handle"] = CivilObjectUtils.GetHandle(corridor),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(corridor.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = corridor.Layer,
        ["baselines"] = baselines,
        ["surfaces"] = surfaces,
        ["featureLineCount"] = featureLines.Count,
        ["state"] = GetCorridorState(corridor),
      };
    });
  }

  // 2026-11-16 P6: 读 region 的真实采样频率；读不到返回 null，绝不假报 0
  private static double? TryReadRegionFrequency(BaselineRegion region)
  {
    try
    {
      var applied = region.AppliedAssemblySetting;
      return applied?.FrequencyAlongTangents;
    }
    catch (System.Exception)
    {
      return null;
    }
  }

  public static Task<object?> RebuildCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    // Create the job up front so the caller gets a jobId immediately — they
    // poll civil3d_job(status, jobId) until the background rebuild finishes.
    var requestId = PluginRuntime.GetCurrentRequestId();
    var job = JobRegistry.Create(
      $"Rebuilding corridor {name}",
      "corridor_rebuild",
      requestId,
      PluginRuntime.GetActiveDrawingIdentity());
    job.CancellationSource = new CancellationTokenSource();
    var cancellationToken = job.CancellationSource.Token;

    // Dispatch the rebuild to the Civil 3D command thread via
    // CivilExecution.WriteAsync, but do NOT await it here so the JSON-RPC
    // handler returns immediately and the HTTP/TCP command timeout does not
    // apply to long-running rebuilds.
    _ = Task.Run(async () =>
    {
      try
      {
        await PluginRuntime.RunWithRequestContextAsync(
          "rebuildCorridor",
          $"{requestId ?? "job"}:job:{job.JobId}",
          cancellationToken,
          job.DrawingIdentity,
          async () =>
          {
            cancellationToken.ThrowIfCancellationRequested();
            JobRegistry.Progress(job.JobId, 10, $"Scheduling rebuild for corridor {name}", null);

            // 2026-09-28 D3: 走再生护栏（App.Idle 路径，见 CivilExecution.RegeneratingOperations）
            var result = await CivilExecution.WriteViaIdleAsync<object?>((doc, civilDoc, database, transaction) =>
            {
              cancellationToken.ThrowIfCancellationRequested();

              var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);

              // Corridor.Rebuild() is synchronous and exposes no cancellation
              // handle. Abort before entering and after it returns.
              JobRegistry.Progress(job.JobId, 40, $"Rebuilding corridor {name}", null);
              corridor.Rebuild();
              cancellationToken.ThrowIfCancellationRequested();

              return new Dictionary<string, object?>
              {
                ["corridorName"] = name,
                ["state"] = GetCorridorState(corridor),
              };
            });

            JobRegistry.Complete(job.JobId, result);
            PluginLog.Info("Corridor", $"Rebuild completed for corridor '{name}' (job {job.JobId}).");
            return result;
          },
          isJob: true);   // 2026-08-19 方案3: 长超时通道（300s）
      }
      catch (OperationCanceledException)
      {
        JobRegistry.AcknowledgeCancellation(job.JobId);
        PluginLog.Info("Corridor", $"Rebuild cancelled for corridor '{name}' (job {job.JobId}).");
      }
      catch (Exception ex)
      {
        PluginLog.Error("Corridor", $"Rebuild failed for corridor '{name}' (job {job.JobId}).", ex);
        try
        {
          JobRegistry.Fail(job.JobId, ex.Message);
        }
        catch (Exception failEx)
        {
          PluginLog.Error("Corridor", $"Unable to mark job {job.JobId} as failed.", failEx);
        }
        job.CancellationSource = null;
      }
      finally
      {
        // Release the cancellation source once the worker has exited.
        try
        {
          job.CancellationSource?.Dispose();
        }
        catch (ObjectDisposedException)
        {
          // Already disposed (e.g. by Cancel/Dispose race); safe to ignore.
        }
      }
    }, CancellationToken.None);

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["jobId"] = job.JobId,
      ["state"] = "running",
      ["message"] = $"Corridor '{name}' rebuild queued. Poll civil3d_job with action='status' to track progress.",
    });
  }

  public static Task<object?> GetCorridorSurfacesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["surfaces"] = ReadCorridorSurfaces(corridor, transaction),
      };
    });
  }

    /// <summary>
  /// Read corridor feature lines. Default returns code names only (backward compatible);
  /// includePoints=true returns per-point {station, offset, elevation} for each code.
  /// Feature lines are exact corridor geometry (no TIN triangulation) - use them for
  /// quantities and handover; corridor surfaces are visualization only.
  /// </summary>
  public static Task<object?> GetCorridorFeatureLinesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var includePoints = PluginRuntime.GetOptionalBool(parameters, "includePoints") ?? false;
    var wantCodes = PluginRuntime.GetOptionalStringArray(parameters, "codes");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var maxPoints = PluginRuntime.GetOptionalInt(parameters, "maxPoints") ?? 2000;
    // 2026-11-17 P22: 响应体字节预算。
    //   ResultSummarizer 超过 64KB 会做"形状保持式裁剪"（保留数组前缀、丢掉后半截），但它不认识本方法的计数：
    //   真机事故 = returnedPointCount 说 1993，featureLines 里只剩 4 个码（9 码请求），调用方以为后 5 个码没数据。
    //   这里先按序列化后的真实字节数自己裁，计数只描述"实际返回的内容"，同时把体积压在 summarizer 阈值以内。
    //   注意：把预算抬到 summarizer 阈值以上是没用的 —— 它照样会把数组剪一半、计数又对不上。
    //   所以未显式 fullResult:true 时预算硬顶在阈值内；真要更大的响应必须连 fullResult 一起给。
    var summarizeThreshold = ResultSummarizer.DefaultThresholdBytes;
    var wantsFullResult = PluginRuntime.GetOptionalBool(parameters, "fullResult") ?? false;
    var requestedByteBudget = PluginRuntime.GetOptionalInt(parameters, "maxResponseBytes")
      ?? (int)(summarizeThreshold * 0.7);
    var responseByteBudget = wantsFullResult
      ? requestedByteBudget
      : Math.Min(requestedByteBudget, (int)(summarizeThreshold * 0.9));
    var byteBudgetClamped = !wantsFullResult && requestedByteBudget > responseByteBudget;
    var unlimitedPoints = maxPoints <= 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var allCodes = ReadCorridorFeatureLineCodes(corridor);

      var selected = (wantCodes == null || wantCodes.Length == 0)
        ? allCodes
        : allCodes.Where(c => wantCodes.Any(w => string.Equals(w, c, StringComparison.OrdinalIgnoreCase))).ToList();
      var missing = (wantCodes == null)
        ? new List<string>()
        : wantCodes.Where(w => !allCodes.Any(c => string.Equals(c, w, StringComparison.OrdinalIgnoreCase))).ToList();

      var buckets = new Dictionary<string, FeatureLineBucket>(StringComparer.OrdinalIgnoreCase);

      foreach (Baseline baseline in corridor.Baselines)
      {
        var groups = new List<BaselineFeatureLines> { baseline.MainBaselineFeatureLines };
        groups.AddRange(baseline.OffsetBaselineFeatureLinesCol.Cast<BaselineFeatureLines>());

        foreach (var group in groups)
        {
          var collections = group.FeatureLineCollectionMap;
          foreach (var codeName in collections.CodeNames())
          {
            if (!selected.Any(c => string.Equals(c, codeName, StringComparison.OrdinalIgnoreCase))) continue;
            if (!buckets.TryGetValue(codeName, out var bucket))
            {
              bucket = new FeatureLineBucket { Code = codeName };
              buckets[codeName] = bucket;
            }
            var collection = collections[codeName];
            for (var i = 0; i < collection.Count; i++)
            {
              CorridorFeatureLine featureLine;
              try { featureLine = collection[i]; } catch { continue; }
              if (featureLine == null) continue;
              bucket.LineCount++;
              foreach (FeatureLinePoint point in featureLine.FeatureLinePoints)
              {
                var station = point.Station;
                var offset = point.Offset;
                var elevation = ReadFeatureLinePointElevation(point, baseline.AlignmentId, transaction);
                bucket.Add(station, offset, elevation);
                if (!includePoints) continue;
                if (stationStart.HasValue && station < stationStart.Value) continue;
                if (stationEnd.HasValue && station > stationEnd.Value) continue;
                bucket.AddFiltered(station, offset, elevation);
              }
            }
          }
        }
      }

      var ordered = buckets.Values.OrderBy(b => b.Code, StringComparer.OrdinalIgnoreCase).ToList();
      var availablePointTotal = 0;
      foreach (var bucket in ordered) availablePointTotal += bucket.FilteredPoints.Count;

      // 点数预算：按各码可用点数比例分配（每码至少 1 点），避免"先遍历到的码吃光预算、后面的码静默 0 点"。
      var keepCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var bucket in ordered)
      {
        var availableCount = bucket.FilteredPoints.Count;
        var keepCount = availableCount;
        if (includePoints && !unlimitedPoints && availableCount > 0 && availablePointTotal > 0)
        {
          keepCount = (int)Math.Floor((double)maxPoints * availableCount / availablePointTotal);
          if (keepCount < 1) keepCount = 1;
          if (keepCount > availableCount) keepCount = availableCount;
        }
        keepCounts[bucket.Code] = keepCount;
      }

      List<Dictionary<string, object?>> BuildPoints(FeatureLineBucket bucket, int keepCount)
      {
        var points = new List<Dictionary<string, object?>>(keepCount);
        for (var pointIndex = 0; pointIndex < keepCount; pointIndex++)
        {
          var filtered = bucket.FilteredPoints[pointIndex];
          points.Add(new Dictionary<string, object?>
          {
            ["station"] = Math.Round(filtered.Station, 4),
            ["offset"] = Math.Round(filtered.Offset, 4),
            ["elevation"] = Math.Round(filtered.Elevation, 4),
          });
        }
        return points;
      }

      Dictionary<string, object?> BuildPayload()
      {
        var result = new List<Dictionary<string, object?>>();
        var returnedPointTotal = 0;
        var omittedPointTotal = 0;
        var truncatedCodes = new List<string>();
        foreach (var bucket in ordered)
        {
          var availableCount = bucket.FilteredPoints.Count;
          var keepCount = keepCounts[bucket.Code];
          var omittedCount = availableCount - keepCount;
          if (omittedCount > 0) truncatedCodes.Add(bucket.Code);
          returnedPointTotal += keepCount;
          omittedPointTotal += omittedCount;

          var entry = new Dictionary<string, object?>
          {
            ["code"] = bucket.Code,
            ["lineCount"] = bucket.LineCount,
            ["pointCount"] = bucket.PointTotal,
            ["stationStart"] = bucket.HasAny ? Math.Round(bucket.MinStation, 4) : (double?)null,
            ["stationEnd"] = bucket.HasAny ? Math.Round(bucket.MaxStation, 4) : (double?)null,
            ["minOffset"] = bucket.HasAny ? Math.Round(bucket.MinOffset, 4) : (double?)null,
            ["maxOffset"] = bucket.HasAny ? Math.Round(bucket.MaxOffset, 4) : (double?)null,
            ["minElevation"] = bucket.HasAny ? Math.Round(bucket.MinElevation, 4) : (double?)null,
            ["maxElevation"] = bucket.HasAny ? Math.Round(bucket.MaxElevation, 4) : (double?)null,
          };
          if (includePoints)
          {
            entry["points"] = BuildPoints(bucket, keepCount);
            entry["pointsAvailable"] = availableCount;
            entry["pointsReturned"] = keepCount;
            entry["pointsOmitted"] = omittedCount;
          }
          result.Add(entry);
        }

        var built = new Dictionary<string, object?>
        {
          ["corridorName"] = corridor.Name,
          ["codeCount"] = allCodes.Count,
          ["availableCodes"] = allCodes,
          ["featureLines"] = result,
          ["includePoints"] = includePoints,
          ["maxPoints"] = maxPoints,
          ["maxPointsMode"] = unlimitedPoints ? "unlimited" : "budget",
          ["availablePointCount"] = includePoints ? availablePointTotal : 0,
          ["returnedPointCount"] = returnedPointTotal,
          ["omittedPointCount"] = omittedPointTotal,
          ["truncated"] = omittedPointTotal > 0,
          ["truncatedCodes"] = truncatedCodes,
        };
        if (missing.Count > 0)
        {
          built["missingCodes"] = missing;
          built["hint"] = "Codes not found on this corridor. Pick from availableCodes (case-insensitive).";
        }
        if (omittedPointTotal > 0)
        {
          built["truncatedHint"] = "Only " + returnedPointTotal + " of " + availablePointTotal + " point(s) returned (maxPoints="
            + maxPoints + ", responseByteBudget=" + responseByteBudget + "); per-code pointsOmitted shows what was left out. "
            + "Raise maxPoints (0 = unlimited) / maxResponseBytes, narrow stationStart/stationEnd, or request fewer codes.";
        }
        return built;
      }

      long SerializedBytes(Dictionary<string, object?> payload)
        => System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(payload));

      // 字节预算收敛：先按点数配额，再按真实序列化体积按比例缩（每码至少留 1 点）；
      // 缩不动（1 点/码 仍超预算）就如实回 byteBudgetHint，不假装成功。
      if (includePoints && responseByteBudget > 0)
      {
        for (var guard = 0; guard < 40; guard++)
        {
          if (SerializedBytes(BuildPayload()) <= responseByteBudget) break;
          var shrunk = false;
          foreach (var bucket in ordered)
          {
            var current = keepCounts[bucket.Code];
            if (current <= 1) continue;
            keepCounts[bucket.Code] = Math.Max(1, (int)Math.Floor(current * 0.75));
            shrunk = true;
          }
          if (!shrunk) break;
        }
      }

      var payload = BuildPayload();
      var responseBytes = SerializedBytes(payload);
      payload["responseByteBudget"] = responseByteBudget;
      payload["responseBytes"] = responseBytes;
      if (byteBudgetClamped)
      {
        payload["byteBudgetNote"] = "maxResponseBytes=" + requestedByteBudget + " was clamped to " + responseByteBudget
          + " B: anything above the summarizer threshold (" + summarizeThreshold + " B) gets trimmed structurally, which would"
          + " make the counts disagree with the arrays. Pass fullResult:true together with maxResponseBytes to receive the larger payload.";
      }
      if (includePoints && responseByteBudget > 0 && responseBytes > responseByteBudget)
      {
        payload["byteBudgetHint"] = "One point per code already exceeds responseByteBudget=" + responseByteBudget
          + " (actual " + responseBytes + " B). Raise maxResponseBytes or pass fullResult:true.";
      }
      return payload;
    });
  }

  /// <summary>
  /// Turn corridor feature lines into real 3D polylines (editable, deliverable geometry).
  /// One code may produce several lines (one per corridor feature line instance).
  /// </summary>
    /// <summary>
  /// Turn corridor feature lines into real 3D polylines (editable, deliverable geometry).
  /// Supports batch mode across corridors (allCorridors/corridors[]) and optional merging of
  /// adjacent segments of the same code into continuous polylines (merge=true).
  /// </summary>
    /// <summary>
  /// Turn corridor feature lines into real 3D polylines (editable, deliverable geometry).
  /// Supports batch mode (allCorridors / corridors[]) and optional merge of adjacent segments
  /// of the same code into continuous polylines.
  /// </summary>
  public static Task<object?> ExtractCorridorFeatureLinesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var corridorNames = PluginRuntime.GetOptionalStringArray(parameters, "corridors");
    var allCorridors = PluginRuntime.GetOptionalBool(parameters, "allCorridors") ?? false;
    var wantCodes = PluginRuntime.GetOptionalStringArray(parameters, "codes");
    var targetLayer = PluginRuntime.GetOptionalString(parameters, "targetLayer") ?? "C-ROAD-FL";
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var maxLines = PluginRuntime.GetOptionalInt(parameters, "maxLines") ?? 2000;
    var merge = PluginRuntime.GetOptionalBool(parameters, "merge") ?? false;
    var mergeTolerance = PluginRuntime.GetOptionalDouble(parameters, "mergeTolerance") ?? 0.05;
    var stationTolerance = PluginRuntime.GetOptionalDouble(parameters, "stationTolerance") ?? 0.1;
    var replaceExisting = PluginRuntime.GetOptionalBool(parameters, "replaceExisting") ?? false;

    if (string.IsNullOrWhiteSpace(name) && (corridorNames == null || corridorNames.Length == 0) && !allCorridors)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "extractCorridorFeatureLines needs one of: name (single corridor), corridors[] (list) or allCorridors=true.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var targets = new List<Corridor>();
      if (allCorridors)
      {
        foreach (ObjectId corridorId in civilDoc.CorridorCollection)
        {
          var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
          if (corridor != null) targets.Add(corridor);
        }
      }
      else if (corridorNames != null && corridorNames.Length > 0)
      {
        foreach (var corridorName in corridorNames)
        {
          targets.Add(CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead));
        }
      }
      else
      {
        targets.Add(CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name!, OpenMode.ForRead));
      }
      targets = targets.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

      var availableCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var corridor in targets)
      {
        foreach (var code in ReadCorridorFeatureLineCodes(corridor)) availableCodes.Add(code);
      }
      var selected = (wantCodes == null || wantCodes.Length == 0)
        ? availableCodes.ToList()
        : availableCodes.Where(c => wantCodes.Any(w => string.Equals(w, c, StringComparison.OrdinalIgnoreCase))).ToList();
      if (selected.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          "None of the requested codes exist on the selected corridors. Available codes: " + string.Join(", ", availableCodes));
      }

      var segments = new Dictionary<string, List<FeatureLineSegment>>(StringComparer.OrdinalIgnoreCase);
      foreach (var code in selected) segments[code] = new List<FeatureLineSegment>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var unresolved = 0;
      var duplicates = 0;

      foreach (var corridor in targets)
      {
        foreach (Baseline baseline in corridor.Baselines)
        {
          var groups = new List<BaselineFeatureLines> { baseline.MainBaselineFeatureLines };
          groups.AddRange(baseline.OffsetBaselineFeatureLinesCol.Cast<BaselineFeatureLines>());

          foreach (var group in groups)
          {
            var collections = group.FeatureLineCollectionMap;
            foreach (var codeName in collections.CodeNames())
            {
              var key = selected.FirstOrDefault(c => string.Equals(c, codeName, StringComparison.OrdinalIgnoreCase));
              if (key == null) continue;
              var collection = collections[codeName];
              for (var i = 0; i < collection.Count; i++)
              {
                CorridorFeatureLine featureLine;
                try { featureLine = collection[i]; } catch { continue; }
                if (featureLine == null) continue;

                var segment = new FeatureLineSegment { CorridorName = corridor.Name, Code = key };
                foreach (FeatureLinePoint point in featureLine.FeatureLinePoints)
                {
                  var station = point.Station;
                  if (stationStart.HasValue && station < stationStart.Value) continue;
                  if (stationEnd.HasValue && station > stationEnd.Value) continue;
                  if (!TryResolveFeatureLinePointXyz(point, baseline.AlignmentId, transaction, out var vertex))
                  {
                    unresolved++;
                    continue;
                  }
                  if (segment.Points.Count == 0) segment.Offset = point.Offset;
                  segment.Stations.Add(station);
                  segment.Points.Add(vertex);
                }
                if (segment.Points.Count < 2) continue;

                // The same corridor feature line can be reachable through both the main baseline
                // and the offset baseline collections; dedupe so merge does not stitch a copy of itself.
                var fingerprint = key + "|" + Math.Round(segment.Offset, 2).ToString("F2") + "|" + segment.Stations[0].ToString("F3") + "|" +
                  segment.Stations[segment.Stations.Count - 1].ToString("F3") + "|" + segment.Points.Count + "|" +
                  segment.Points[0].X.ToString("F3") + "," + segment.Points[0].Y.ToString("F3") + "," + segment.Points[0].Z.ToString("F3");
                if (!seen.Add(fingerprint)) { duplicates++; continue; }
                segments[key].Add(segment);
              }
            }
          }
        }
      }

      var runs = new List<FeatureLineSegment>();
      foreach (var code in selected)
      {
        var list = segments[code];
        if (list.Count == 0) continue;
        if (!merge) { runs.AddRange(list); continue; }

        // Same code can describe several parallel lines (left/right, inner/outer edge).
        // Merge only within one offset group, otherwise opposite sides truncate each other.
        foreach (var group in list.GroupBy(x => Math.Round(x.Offset, 2)).OrderBy(g => g.Key))
        {
        var ordered = group.OrderBy(s => s.Stations[0]).ThenBy(s => s.Stations[s.Stations.Count - 1]).ToList();
        var current = new FeatureLineSegment { CorridorName = ordered[0].CorridorName, Code = code, Offset = ordered[0].Offset };
        current.Points.AddRange(ordered[0].Points);
        current.Stations.AddRange(ordered[0].Stations);
        for (var i = 1; i < ordered.Count; i++)
        {
          var next = ordered[i];
          var currentEnd = current.Stations[current.Stations.Count - 1];
          var gap = next.Stations[0] - currentEnd;
          var tail = current.Points[current.Points.Count - 1];
          var head = next.Points[0];
          if (gap >= -stationTolerance && gap <= stationTolerance && tail.DistanceTo(head) <= mergeTolerance)
          {
            current.Points.AddRange(next.Points);
            current.Stations.AddRange(next.Stations);
          }
          else
          {
            runs.Add(current);
            current = new FeatureLineSegment { CorridorName = next.CorridorName, Code = code, Offset = next.Offset };
            current.Points.AddRange(next.Points);
            current.Stations.AddRange(next.Stations);
          }
        }
        runs.Add(current);
        }
      }

      var modelSpaceId = CivilObjectUtils.GetModelSpaceBlockId(database, transaction);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, modelSpaceId, OpenMode.ForWrite);

      // Re-running an extraction used to pile up duplicate polylines on the same layer.
      // Count what is already there, and erase it first only when the caller asks for it.
      var existingOnLayer = new List<ObjectId>();
      foreach (ObjectId entityId in modelSpace)
      {
        var entity = transaction.GetObject(entityId, OpenMode.ForRead, false);
        if (entity is Polyline3d existing && string.Equals(existing.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
        {
          existingOnLayer.Add(entityId);
        }
      }
      var existingCount = existingOnLayer.Count;
      var replacedExisting = 0;
      if (replaceExisting)
      {
        foreach (var entityId in existingOnLayer)
        {
          var entity = transaction.GetObject(entityId, OpenMode.ForWrite, false);
          entity.Erase();
          replacedExisting++;
        }
      }

      var created = new List<Dictionary<string, object?>>();
      var skipped = new List<Dictionary<string, object?>>();

      foreach (var run in runs)
      {
        if (created.Count >= maxLines)
        {
          skipped.Add(new Dictionary<string, object?> { ["code"] = run.Code, ["reason"] = "maxLines reached" });
          continue;
        }
        var points = new Point3dCollection();
        foreach (var p in run.Points) points.Add(p);

        using var polyline3d = new Polyline3d(Poly3dType.SimplePoly, points, false);
        modelSpace.AppendEntity(polyline3d);
        transaction.AddNewlyCreatedDBObject(polyline3d, true);
        CivilObjectUtils.TrySetLayer(polyline3d, targetLayer, database, transaction);
        created.Add(new Dictionary<string, object?>
        {
          ["code"] = run.Code,
          ["sourceCorridor"] = run.CorridorName,
          ["offset"] = Math.Round(run.Offset, 3),
          ["handle"] = polyline3d.Handle.ToString(),
          ["layer"] = targetLayer,
          ["vertexCount"] = run.Points.Count,
          ["stationStart"] = Math.Round(run.Stations[0], 4),
          ["stationEnd"] = Math.Round(run.Stations[run.Stations.Count - 1], 4),
        });
      }

      var perCode = created
        .GroupBy(c => (string)c["code"]!)
        .Select(g => new Dictionary<string, object?>
        {
          ["code"] = g.Key,
          ["polylines"] = g.Count(),
          ["vertices"] = g.Sum(x => (int)x["vertexCount"]!),
          ["stationStart"] = g.Min(x => (double)x["stationStart"]!),
          ["stationEnd"] = g.Max(x => (double)x["stationEnd"]!),
        })
        .OrderBy(x => (string)x["code"]!, StringComparer.OrdinalIgnoreCase)
        .ToList();

      var payload = new Dictionary<string, object?>
      {
        ["corridorCount"] = targets.Count,
        ["corridors"] = targets.Select(c => c.Name).ToList(),
        ["targetLayer"] = targetLayer,
        ["merge"] = merge,
        ["mergeTolerance"] = mergeTolerance,
        ["stationTolerance"] = stationTolerance,
        ["duplicateSegmentsSkipped"] = duplicates,
        ["existingOnLayerBefore"] = existingCount,
        ["replaceExisting"] = replaceExisting,
        ["replacedExisting"] = replacedExisting,
        ["created"] = created,
        ["totalCreated"] = created.Count,
        ["perCode"] = perCode,
        ["skipped"] = skipped,
        ["unresolvedPoints"] = unresolved,
        ["availableCodes"] = availableCodes.ToList(),
      };
      if (created.Count == 0)
      {
        payload["hint"] = "Nothing created. Check stationStart/stationEnd, codes against availableCodes, and that corridors are built.";
      }
      if (unresolved > 0)
      {
        payload["unresolvedHint"] = "Some feature line points could not be resolved to XYZ and were skipped; vertexCount covers resolved points only.";
      }
      if (skipped.Count > 0)
      {
        payload["skippedHint"] = "maxLines cap reached; raise maxLines or narrow the station window.";
      }
      if (!replaceExisting && existingCount > 0)
      {
        payload["existingLayerHint"] = "targetLayer already had " + existingCount + " polyline(s) before this run; pass replaceExisting=true to clear them first and avoid duplicates.";
      }
      return payload;
    });
  }



  private sealed class FeatureLineBucket
  {
    public string Code = string.Empty;
    public int LineCount;
    public int PointTotal;
    public bool HasAny;
    public double MinStation = double.MaxValue;
    public double MaxStation = double.MinValue;
    public double MinOffset = double.MaxValue;
    public double MaxOffset = double.MinValue;
    public double MinElevation = double.MaxValue;
    public double MaxElevation = double.MinValue;
    public readonly List<(double Station, double Offset, double Elevation)> FilteredPoints
      = new List<(double Station, double Offset, double Elevation)>();

    public void Add(double station, double offset, double elevation)
    {
      HasAny = true;
      PointTotal++;
      if (station < MinStation) MinStation = station;
      if (station > MaxStation) MaxStation = station;
      if (offset < MinOffset) MinOffset = offset;
      if (offset > MaxOffset) MaxOffset = offset;
      if (elevation < MinElevation) MinElevation = elevation;
      if (elevation > MaxElevation) MaxElevation = elevation;
    }

    public void AddFiltered(double station, double offset, double elevation)
    {
      FilteredPoints.Add((station, offset, elevation));
    }
  }

  private static double ReadFeatureLinePointElevation(FeatureLinePoint point, ObjectId alignmentId, Transaction transaction)
  {
    // FeatureLinePoint exposes XYZ only (no Elevation property); XYZ.Z is the elevation truth.
    return TryResolveFeatureLinePointXyz(point, alignmentId, transaction, out var xyz) ? xyz.Z : 0.0;
  }

  private static bool TryResolveFeatureLinePointXyz(FeatureLinePoint point, ObjectId alignmentId, Transaction transaction, out Point3d xyz)
  {
    xyz = Point3d.Origin;
    try
    {
      // FeatureLinePoint exposes XYZ (Point3d). Older builds used Location; keep it as a fallback.
      var xyzValue = CivilObjectUtils.GetPropertyValue<object>(point, "XYZ")
        ?? CivilObjectUtils.GetPropertyValue<object>(point, "Location");
      if (xyzValue is Point3d located) { xyz = located; return true; }
    }
    catch { }

    var elevation = CivilObjectUtils.GetDoubleProperty(point, "Elevation") ?? 0.0;
    try
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);
      var centerline = alignment.GetPointAtDist(point.Station);
      var direction = alignment.GetFirstDerivative(centerline);
      var normal = new Vector3d(-direction.Y, direction.X, 0.0);
      if (normal.Length < 1e-9) return false;
      normal = normal.GetNormal();
      xyz = new Point3d(centerline.X + normal.X * point.Offset, centerline.Y + normal.Y * point.Offset, elevation);
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static List<string> ReadCorridorFeatureLineCodes(Corridor corridor)
  {
    var codes = new List<string>();
    foreach (FeatureLineCodeInfo info in corridor.FeatureLineCodeInfos)
    {
      if (info == null) continue;
      var code = info.CodeName;
      if (string.IsNullOrWhiteSpace(code)) continue;
      if (!codes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase))) codes.Add(code);
    }
    return codes;
  }


  public static Task<object?> ComputeCorridorVolumesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var corridorSurfaceName = PluginRuntime.GetRequiredString(parameters, "corridorSurface");
    var referenceSurfaceName = PluginRuntime.GetRequiredString(parameters, "referenceSurface");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var corridorSurface = FindCorridorSurfaceByName(corridor, corridorSurfaceName, transaction);
      var referenceSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurfaceName, OpenMode.ForRead);
      return ComputeVolumeBetweenSurfaces(database, transaction, corridorSurface, referenceSurface);
    });
  }

  private static Dictionary<string, object?> ToCorridorSummary(Corridor corridor)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = corridor.Name,
      ["handle"] = CivilObjectUtils.GetHandle(corridor),
      ["baselineCount"] = corridor.Baselines.Count,
      ["regionCount"] = corridor.Baselines.Cast<Baseline>().Sum(baseline => baseline.BaselineRegions.Count),
      ["surfaceCount"] = ReadCorridorSurfaces(corridor, null).Count,
      ["state"] = GetCorridorState(corridor),
      ["lastBuildTime"] = CivilObjectUtils.GetPropertyValue<DateTime?>(corridor, "RebuildDate")?.ToString("O"),
    };
  }

  private static List<Dictionary<string, object?>> ReadCorridorSurfaces(Corridor corridor, Transaction? transaction)
  {
    var corridorSurfaces = corridor.CorridorSurfaces;
    return corridorSurfaces.Cast<CorridorSurface>()
      .Select(surface => new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["surfaceId"] = surface.SurfaceId == ObjectId.Null ? null : surface.SurfaceId.Handle.ToString(),
        ["boundaries"] = surface.Boundaries.Cast<CorridorSurfaceBoundary>().Select(boundary => boundary.Name).ToList(),
      })
      .ToList();
  }

  /// <summary>
  /// A1 收口（2026-09-28）：用**廊道要素线**定义"设计曲面真实覆盖范围"（左右外沿 offset）。
  /// 链条：Corridor.Baselines[i].MainBaselineFeatureLines.FeatureLineCollectionMap[code].Item[j].FeatureLinePoints{Station,Offset}
  /// 用途：断面法算量必须把窗口切到该范围内，否则 C3D 断面会向采样线两端**外插**，产生假挖填。
  /// </summary>
  public static Task<object?> GetCorridorCoverageAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var map = new SortedDictionary<double, Coverage>();
      var codeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (Baseline baseline in corridor.Baselines)
      {
        var groups = new List<BaselineFeatureLines> { baseline.MainBaselineFeatureLines };
        groups.AddRange(baseline.OffsetBaselineFeatureLinesCol.Cast<BaselineFeatureLines>());

        foreach (var group in groups)
        {
          var collections = group.FeatureLineCollectionMap;
          foreach (var codeName in collections.CodeNames())
          {
            codeNames.Add(codeName);
            var collection = collections[codeName];
            for (var i = 0; i < collection.Count; i++)
            {
              CorridorFeatureLine featureLine;
              try { featureLine = collection[i]; } catch { continue; }
              if (featureLine == null) continue;
              foreach (FeatureLinePoint point in featureLine.FeatureLinePoints)
              {
                var station = Math.Round(point.Station, 3);
                if (stationStart.HasValue && station < stationStart.Value) continue;
                if (stationEnd.HasValue && station > stationEnd.Value) continue;
                if (!map.TryGetValue(station, out var cov))
                {
                  cov = new Coverage { LeftOffset = double.MaxValue, RightOffset = double.MinValue };
                  map[station] = cov;
                }
                var offset = point.Offset;
                if (offset < cov.LeftOffset) { cov.LeftOffset = offset; cov.LeftCode = codeName; }
                if (offset > cov.RightOffset) { cov.RightOffset = offset; cov.RightCode = codeName; }
                cov.PointCount++;
              }
            }
          }
        }
      }

      var coverage = map.Select(kv => (object)new Dictionary<string, object?>
      {
        ["station"] = kv.Key,
        ["leftOffset"] = kv.Value.LeftOffset,
        ["rightOffset"] = kv.Value.RightOffset,
        ["leftCode"] = kv.Value.LeftCode,
        ["rightCode"] = kv.Value.RightCode,
        ["pointCount"] = kv.Value.PointCount,
        ["width"] = kv.Value.RightOffset - kv.Value.LeftOffset,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["stationCount"] = coverage.Count,
        ["featureLineCodes"] = codeNames.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray(),
        ["minLeftOffset"] = coverage.Count > 0 ? map.Values.Min(v => v.LeftOffset) : (double?)null,
        ["maxRightOffset"] = coverage.Count > 0 ? map.Values.Max(v => v.RightOffset) : (double?)null,
        ["coverage"] = coverage,
      };
    });
  }

  private sealed class Coverage
  {
    public double LeftOffset { get; set; }
    public double RightOffset { get; set; }
    public string? LeftCode { get; set; }
    public string? RightCode { get; set; }
    public int PointCount { get; set; }
  }

  

  private static string GetCorridorState(Corridor corridor) => corridor.IsOutOfDate ? "out_of_date" : "built";

  private static Autodesk.Civil.DatabaseServices.Surface FindCorridorSurfaceByName(Corridor corridor, string name, Transaction transaction)
  {
    var definition = corridor.CorridorSurfaces.Cast<CorridorSurface>()
      .FirstOrDefault(surface => string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface '{name}' was not found on corridor '{corridor.Name}'.");

    if (definition.SurfaceId == ObjectId.Null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor surface '{name}' has no built Civil 3D surface ObjectId.");
    }

    return CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Surface>(transaction, definition.SurfaceId, OpenMode.ForRead);
  }

  private static Dictionary<string, object?> ComputeVolumeBetweenSurfaces(
    Database database,
    Transaction transaction,
    Autodesk.Civil.DatabaseServices.Surface baseSurface,
    Autodesk.Civil.DatabaseServices.Surface comparisonSurface)
  {
    var temporaryName = $"MCP_TMP_CORRIDOR_VOLUME_{Guid.NewGuid():N}";
    var volumeSurfaceId = TinVolumeSurface.Create(temporaryName, baseSurface.ObjectId, comparisonSurface.ObjectId);
    var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeSurfaceId, OpenMode.ForRead);
    var properties = volumeSurface.GetVolumeProperties();
    return new Dictionary<string, object?>
    {
      ["cutVolume"] = properties.UnadjustedCutVolume,
      ["fillVolume"] = properties.UnadjustedFillVolume,
      ["netVolume"] = properties.UnadjustedNetVolume,
      ["units"] = new Dictionary<string, object?>
      {
        ["volume"] = CivilObjectUtils.VolumeUnits(database),
      },
      ["source"] = "TinVolumeSurface.GetVolumeProperties",
    };
  }

  private sealed class FeatureLineSegment
  {
    public string CorridorName = string.Empty;
    public string Code = string.Empty;
    public double Offset;
    public List<Point3d> Points = new List<Point3d>();
    public List<double> Stations = new List<double>();
  }
}
