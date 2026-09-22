using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class DrawingCommands
{
  public static Task<object?> GetCivil3DHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    var doc = App.DocumentManager.MdiActiveDocument;
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var jobs = JobRegistry.GetStats();

    object response = new Dictionary<string, object?>
    {
      ["connected"] = true,
      ["civil3dVersion"] = App.GetSystemVariable("ACADVER")?.ToString(),
      ["pluginVersion"] = typeof(PluginEntry).Assembly.GetName().Version?.ToString(),
      ["drawingLoaded"] = doc != null,
      ["operationInProgress"] = status.OperationInProgress,
      ["currentOperation"] = status.CurrentOperation,
      ["queueDepth"] = status.QueueDepth,
      ["queueCapacity"] = status.QueueCapacity,
      ["currentOperationStartedAtUnixMs"] = status.CurrentOperationStartedAtUnixMs,
      ["currentRequestId"] = status.CurrentRequestId,
      ["currentOperationDurationMs"] = status.CurrentOperationStartedAtUnixMs is long startedAt
        ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt)
        : null,
      ["memoryUsageMb"] = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
      ["logFilePath"] = PluginLog.LogFilePath,
      ["fileLoggingHealthy"] = PluginLog.IsFileLoggingHealthy,
      ["fileLoggingError"] = PluginLog.LastFileError,
      ["jobs"] = new Dictionary<string, object?>
      {
        ["total"] = jobs.Total,
        ["running"] = jobs.Running,
        ["completed"] = jobs.Completed,
        ["failed"] = jobs.Failed,
        ["cancelled"] = jobs.Cancelled,
        ["capacity"] = jobs.Capacity,
        ["terminalRetentionMinutes"] = jobs.TerminalRetentionMinutes,
      },
    };

    return Task.FromResult<object?>(response);
  }

  public static Task<object?> GetDrawingInfoAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildDrawingInfo(doc, civilDoc, database, transaction));
  }

  public static Task<object?> GetProjectContextAsync(JsonObject? parameters)
  {
    var requestedLimit = PluginRuntime.GetOptionalInt(parameters, "selectedObjectLimit") ?? 25;
    var selectedObjectLimit = Math.Clamp(requestedLimit, 1, 100);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?>
      {
        ["drawingInfo"] = BuildDrawingInfo(doc, civilDoc, database, transaction),
        ["objectTypes"] = BuildCivilObjectTypes(),
        ["selectedObjects"] = BuildSelectedCivilObjects(doc, transaction, selectedObjectLimit),
      });
  }

  public static Task<object?> GetDrawingSettingsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var currentLayer = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, database.Clayer, OpenMode.ForRead);
      var coords = ReadCoordinateSystemFields(civilDoc);

      return new Dictionary<string, object?>
      {
        ["coordinateSystem"] = coords.code,
        ["coordinateZone"] = coords.zone,
        ["datum"] = coords.datum,
        ["scaleFactor"] = Convert.ToDouble(App.GetSystemVariable("DIMSCALE") ?? 1d),
        ["elevationReference"] = coords.verticalDatum,
        ["defaultLayer"] = currentLayer.Name,
        ["defaultStyles"] = new Dictionary<string, object?>
        {
          ["surface"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.SurfaceStyles, transaction),
          ["alignment"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.AlignmentStyles, transaction),
          ["profile"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.ProfileStyles, transaction),
          ["corridor"] = null,
          ["pipeNetwork"] = PipeNetworkCommands.GetFirstPipeNetworkStyleName(civilDoc, transaction),
        },
      };
    });
  }

  public static Task<object?> SaveDrawingAsync(JsonObject? parameters)
  {
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var saveAs = PluginRuntime.GetOptionalString(parameters, "saveAs");
      var targetPath = string.IsNullOrWhiteSpace(saveAs) ? database.Filename : saveAs;
      if (string.IsNullOrWhiteSpace(targetPath))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "saveDrawing requires 'saveAs' when the drawing has not been saved yet.");
      }

      // Saving the active drawing is not an export conflict: its current file
      // necessarily exists. Boundary/overwrite checks apply only to Save As.
      targetPath = string.IsNullOrWhiteSpace(saveAs)
        ? Path.GetFullPath(targetPath)
        : FileBoundary.ResolveExportPath(targetPath, overwrite, ".dwg");
      try
      {
        database.SaveAs(targetPath, true, DwgVersion.Current, database.SecurityParameters);
      }
      catch (System.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR",
          "保存失败（" + ex.Message + "）。常见原因：图纸以只读方式打开（重复打开同一 dwg 会产生 \":2 只读\" 副本）。请关掉只读副本后用 openDrawing 以可写方式重开: " + targetPath);
      }

      return new Dictionary<string, object?>
      {
        ["saved"] = true,
        ["filePath"] = targetPath,
      };
    });
  }

  public static Task<object?> NewDrawingAsync(JsonObject? parameters)
  {
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    if (!string.IsNullOrWhiteSpace(templatePath))
    {
      templatePath = FileBoundary.ResolveImportPath(templatePath, ".dwt", ".dwg");
    }
    var useTemplate = templatePath;

    // 2026-09-10 fix: DocumentManager.Add 必须在 AutoCAD UI 线程执行——
    //   ExecuteDocumentOpAsync 里直接调 Add 时线程是 TCP 线程池线程，
    //   会抛 WPF InvalidOperationException（acmgdEnableCmdLine: 调用线程无法访问该对象）。
    //   与 openDrawing 同理：把 Add 放进 App.Idle 回调（UI 线程、非命令上下文）。
    return CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
    {
      var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        try
        {
          var createdDocument = string.IsNullOrWhiteSpace(useTemplate)
            ? App.DocumentManager.Add(string.Empty)
            : App.DocumentManager.Add(useTemplate);
          App.DocumentManager.MdiActiveDocument = createdDocument;
          tcs.TrySetResult(new Dictionary<string, object?>
          {
            ["drawingName"] = createdDocument.Name,
            ["filePath"] = createdDocument.Database.Filename,
            ["templatePath"] = useTemplate,
            ["note"] = "newDrawing 在 UI 线程(Idle)执行",
          });
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
      };
      App.Idle += idleHandler;

      // fallback: CAD busy -> Idle never fires -> 20s timeout
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT",
          "newDrawing timeout: CAD not idle, Add not executed." + (string.IsNullOrWhiteSpace(useTemplate) ? "" : " template=" + useTemplate));
      }
      return await tcs.Task;
    });
  }

  public static Task<object?> OpenDrawingAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var readOnly = PluginRuntime.GetOptionalBool(parameters, "readOnly") ?? false;
    var resolved = FileBoundary.ResolveImportPath(path, ".dwg", ".dwt");

    // 2026-08-13 fix v3: Open must run on the document thread but NOT inside a command context.
    //   - ExecuteInCommandContextAsync (command ctx): deadlock -> 25s timeout -> C3D crash
    //   - plain background thread: eFileInternalErr (Open) / INTERNAL_ERROR (Add)
    //   - SendStringToExecute OPEN: broken for Chinese paths + eNoDocument on template doc
    //   - App.Idle event: document thread, idle (no command ctx), direct Unicode path
    return CivilExecution.ExecuteDocumentOpAsync<object?>(async () =>
    {
      var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler idleHandler = null!;
      idleHandler = (_, _) =>
      {
        App.Idle -= idleHandler;
        try
        {
          // save current drawing first (prevents OPEN save-prompt; skips template/unnamed)
          var savedPrevious = false;
          var current = App.DocumentManager.MdiActiveDocument;
          if (current?.Database != null && !string.IsNullOrWhiteSpace(current.Database.Filename))
          {
            try { current.Database.Save(); savedPrevious = true; } catch { }
          }

          // 2026-09-11: target already open -> just activate it. Re-opening an already-open dwg
          // makes AutoCAD create a READ-ONLY duplicate (window title "<name>:2 - 只读"), and the
          // active doc becomes read-only -> every later save fails eFileAccessErr and unsaved work is lost.
          var existing = FindOpenDocument(resolved);
          if (existing != null)
          {
            App.DocumentManager.MdiActiveDocument = existing;
            tcs.TrySetResult(new Dictionary<string, object?>
            {
              ["drawingName"] = existing.Name,
              ["filePath"] = existing.Database?.Filename,
              ["alreadyOpen"] = true,
              ["savedPreviousDrawing"] = savedPrevious,
              ["note"] = "已在 CAD 中打开，直接切换（未重复打开）: " + existing.Name,
            });
            return;
          }

          var opened = App.DocumentManager.Open(resolved, readOnly);
          App.DocumentManager.MdiActiveDocument = opened;
          tcs.TrySetResult(new Dictionary<string, object?>
          {
            ["drawingName"] = opened.Name,
            ["filePath"] = opened.Database.Filename,
            ["readOnly"] = readOnly,
            ["savedPreviousDrawing"] = savedPrevious,
            ["note"] = "Switched to " + opened.Name + (savedPrevious ? " (previous auto-saved)" : ""),
          });
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
      };
      App.Idle += idleHandler;

      // fallback: CAD busy -> Idle never fires -> 20s timeout
      var timeoutTask = Task.Delay(-1, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
      var done = await Task.WhenAny(tcs.Task, timeoutTask);
      if (done != tcs.Task)
      {
        App.Idle -= idleHandler;
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_TIMEOUT",
          "openDrawing timeout: CAD not idle, OPEN not executed: " + resolved);
      }
      return await tcs.Task;
    });
  }

  // 2026-09-11: find an already-open Document whose file path matches (case-insensitive full path).
  private static Document? FindOpenDocument(string fullPath)
  {
    var target = Path.GetFullPath(fullPath);
    foreach (Document d in App.DocumentManager)
    {
      try
      {
        var fn = d?.Database?.Filename;
        if (!string.IsNullOrWhiteSpace(fn) &&
            string.Equals(Path.GetFullPath(fn), target, StringComparison.OrdinalIgnoreCase))
          return d;
      }
      catch { }
    }
    return null;
  }

  public static Task<object?> UndoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.UNDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "undo",
      };
    });
  }

  public static Task<object?> RedoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.REDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "redo",
      };
    });
  }

  public static Task<object?> ListCivilObjectTypesAsync()
  {
    return Task.FromResult<object?>(BuildCivilObjectTypes());
  }

  public static Task<object?> ListTextsAsync(JsonObject? parameters)
  {
    // 2026-08-10: 支持 layer 过滤（子串匹配）+ maxCount/offset 分页——解决大数据量结果被 AI 侧截断拿不全（统计标注场景）
    var layerFilter = PluginRuntime.GetOptionalString(parameters, "layer");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount");
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      var texts = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in ms)
      {
        try
        {
          var ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
          if (ent == null) continue;
          var rclass = ent.GetRXClass().Name;
          if (!rclass.Contains("Text") && !rclass.Contains("ATTRIB")) continue;
          if (!string.IsNullOrEmpty(layerFilter) && !ent.Layer.Contains(layerFilter)) continue;
          var info = new Dictionary<string, object?>
          {
            ["handle"] = id.Handle.ToString(),
            ["type"] = rclass,
            ["layer"] = ent.Layer,
          };
          if (ent is Autodesk.AutoCAD.DatabaseServices.DBText txt) { info["text"] = txt.TextString; info["height"] = txt.Height; }
          else if (ent is Autodesk.AutoCAD.DatabaseServices.MText mtext) { info["text"] = mtext.Text; info["height"] = mtext.TextHeight; }
          texts.Add(info);
        }
        catch { }
      }
      var total = texts.Count;
      IEnumerable<Dictionary<string, object?>> page = texts;
      if (offset > 0) page = page.Skip(offset);
      if (maxCount.HasValue) page = page.Take(maxCount.Value);
      var arr = page.ToList();
      return new { count = total, returned = arr.Count, truncated = offset + arr.Count < total, texts = arr };
    });
  }

  private static readonly string SelFile = System.IO.Path.Combine(
    System.IO.Path.GetFullPath(System.IO.Path.Combine(
      System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
      "..", "..")),
    "exchange", "_out", "_sel.json");

  /// <summary>
  /// 选中集变化时自动保存到文件（ImpliedSelectionChanged 事件触发）
  /// </summary>
  public static void OnSelectionChanged(object? sender, EventArgs e)
  {
    try
    {
      // 用事件来源文档（sender），避免多图纸切换时读到新文档的选择集
      var doc = sender as Autodesk.AutoCAD.ApplicationServices.Document ?? App.DocumentManager.MdiActiveDocument;
      if (doc == null) { PluginLog.Debug("Selection", "No active doc"); return; }
      var result = doc.Editor.SelectImplied();
      if (result.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || result.Value == null)
      {
        // 不直接清空 _sel.json：SelectImplied 在失焦/后台时返回空，
        // 清空会误伤“用户刚框选好、正要发指令”的数据。
        // 面板发送前已主动捕获（HankPalette.Send → SaveSelectionAsync），这里保留旧值即可。
        PluginLog.Debug("Selection", "SelectImplied empty, keeping existing _sel.json");
        return;
      }

      var ids = result.Value.GetObjectIds();
      PluginLog.Debug("Selection", $"选中 {ids.Length} 个对象");
      if (ids.Length == 0) return;

      // 保存全部图元的 handle（不限于文字）
      var entries = new List<Dictionary<string, string?>>();
      foreach (var id in ids)
      {
        try
        {
          entries.Add(new Dictionary<string, string?>
          {
            { "handle", id.Handle.ToString() },
            { "rxClass", id.ObjectClass.Name },
          });
        }
        catch (Exception ex2) { PluginLog.Debug("Selection", $"Entity error: {ex2.Message}"); }
      }

      var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
      {
        ["docName"] = doc.Name,
        ["savedAt"] = DateTime.Now.ToString("o"),
        ["items"] = entries,
      });
      FileBoundary.WriteAllTextAtomic(SelFile, json, System.Text.UTF8Encoding.UTF8, overwrite: true, "json");
      PluginLog.Debug("Selection", $"已保存 {entries.Count} 个图元到 {SelFile} (doc: {doc.Name})");
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Selection", $"Error: {ex.Message}");
    }
  }

  /// <summary>
  /// 读取选中的文字（从文件，不受失焦影响）
  /// </summary>
  public static Task<object?> ListSelectedTextsAsync()
  {
    try
    {
      if (!System.IO.File.Exists(SelFile)) return Task.FromResult<object?>(new { count = 0, texts = new List<object>() });
      var json = System.IO.File.ReadAllText(SelFile);
      var texts = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json) ?? new();
      return Task.FromResult<object?>(new { count = texts.Count, texts });
    }
    catch
    {
      return Task.FromResult<object?>(new { count = 0, texts = new List<object>() });
    }
  }

  public static Task<object?> GetSelectedCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildSelectedCivilObjects(doc, transaction, limit));
  }

  private static Dictionary<string, object?> BuildDrawingInfo(
    Document doc,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction)
  {
    var unsavedChanges = Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    var angularUnits = CivilObjectUtils.AngularUnits(Convert.ToInt16(App.GetSystemVariable("AUNITS") ?? 0));
    var coordinateSystemCode = ReadCoordinateSystemCode(civilDoc);

    return new Dictionary<string, object?>
    {
      ["fileName"] = Path.GetFileName(database.Filename),
      ["filePath"] = database.Filename,
      ["coordinateSystem"] = coordinateSystemCode,
      ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
      ["angularUnits"] = angularUnits,
      ["unsavedChanges"] = unsavedChanges,
      ["objectCounts"] = new Dictionary<string, object?>
      {
        ["surfaces"] = civilDoc.GetSurfaceIds().Count,
        ["alignments"] = civilDoc.GetAlignmentIds().Count,
        ["profiles"] = CountProfiles(civilDoc, transaction),
        ["corridors"] = civilDoc.CorridorCollection.Count,
        ["pipeNetworks"] = PipeNetworkCommands.CountPipeNetworks(civilDoc),
        ["points"] = civilDoc.CogoPoints.Count,
        ["parcels"] = CountParcels(civilDoc, transaction),
      },
      ["drawingName"] = doc.Name,
      ["projectName"] = null,
      ["units"] = CivilObjectUtils.LinearUnits(database),
    };
  }

  private static object[] BuildCivilObjectTypes() =>
  [
    "Alignment",
    "Surface",
    "Profile",
    "Corridor",
    "CogoPoint",
    "SampleLineGroup",
    "FeatureLine",
    "Parcel",
    "PipeNetwork",
  ];

  private static List<Dictionary<string, object?>> BuildSelectedCivilObjects(
    Document doc,
    Transaction transaction,
    int limit)
  {
    var result = new List<Dictionary<string, object?>>();
    var selection = doc.Editor.SelectImplied();
    if (selection.Status != PromptStatus.OK || selection.Value == null)
    {
      return result;
    }

    foreach (var objectId in selection.Value.GetObjectIds().Take(limit))
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      if (dbObject == null)
      {
        continue;
      }

      result.Add(new Dictionary<string, object?>
      {
        ["handle"] = dbObject.Handle.ToString(),
        ["objectType"] = dbObject.GetType().Name,
        ["name"] = CivilObjectUtils.GetName(dbObject),
        ["description"] = CivilObjectUtils.GetStringProperty(dbObject, "Description"),
      });
    }

    return result;
  }

  private static int CountProfiles(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      count += alignment.GetProfileIds().Count;
    }

    return count;
  }

  private static int CountParcels(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;

    foreach (ObjectId siteId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
      count += site.GetParcelIds().Count;
    }

    return count;
  }

  // Single-field coordinate system code (for getDrawingInfo).
  private static string? ReadCoordinateSystemCode(CivilDocument civilDoc)
  {
    return ReadCoordinateSystemFields(civilDoc).code;
  }

  private static (string? code, string? zone, string? datum, string? verticalDatum) ReadCoordinateSystemFields(CivilDocument civilDoc)
  {
    var unitZone = civilDoc.Settings.DrawingSettings.UnitZoneSettings;
    var code = unitZone.CoordinateSystemCode;
    if (string.IsNullOrWhiteSpace(code))
    {
      return (null, null, null, null);
    }

    var coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
    return (code, coordinateSystem.Category, coordinateSystem.Datum, null);
  }

  public static Task<object?> CreateCircleAsync(System.Text.Json.Nodes.JsonObject? parameters)
  {
    var centerArr = parameters?["center"] as System.Text.Json.Nodes.JsonArray;
    var diameter = parameters?["diameter"]?.GetValue<double>() ?? 0;

    if (centerArr == null || centerArr.Count < 2 || diameter <= 0)
      return Task.FromResult<object?>(new System.Text.Json.Nodes.JsonObject { ["error"] = "center[] and diameter required" });

    var cx = centerArr[0]?.GetValue<double>() ?? 0;
    var cy = centerArr[1]?.GetValue<double>() ?? 0;
    var cz = centerArr.Count > 2 ? centerArr[2]?.GetValue<double>() ?? 0 : 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var btr = (BlockTableRecord)transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var circle = new Circle(
        new Point3d(cx, cy, cz),
        new Vector3d(0, 0, 1),
        diameter / 2.0);
      btr.AppendEntity(circle);
      transaction.AddNewlyCreatedDBObject(circle, true);

      return new Dictionary<string, object?> { ["handle"] = CivilObjectUtils.GetHandle(circle) };
    });
  }
}
