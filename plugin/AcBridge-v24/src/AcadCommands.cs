using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Runtime;

namespace Civil3DMcpPlugin;

public static class AcadCommands
{
  private static List<string> _savedSelection = new();

  public static Task<object?> SaveSelectionAsync(JsonObject? parameters)
  {
    return CivilExecution.ExecuteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ed = doc.Editor;
      var selRes = ed.SelectImplied();
      _savedSelection.Clear();
      var entries = new List<Dictionary<string, string?>>();

      if (selRes.Status == PromptStatus.OK && selRes.Value != null)
      {
        foreach (SelectedObject so in selRes.Value)
        {
          var obj = transaction.GetObject(so.ObjectId, OpenMode.ForRead);
          if (obj != null)
          {
            var handle = CivilObjectUtils.GetHandle(obj);
            _savedSelection.Add(handle);
            entries.Add(new Dictionary<string, string?>
            {
              { "handle", handle },
              { "rxClass", obj.GetRXClass().Name },
            });
          }
        }
      }

      // 写 _sel.json 供 getSelection 读取。2026-08-07 修复：仅捕获到选择集才写——
      // 失焦时 SelectImplied 返回空，空覆盖会丢掉 ImpliedSelectionChanged 事件保存的好数据（“对话后被取消选中”根因）
      WriteSelectionSnapshot(doc, entries);

      return new Dictionary<string, object?>
      {
        ["count"] = _savedSelection.Count,
        ["handles"] = new List<string>(_savedSelection),
        ["source"] = "implied",
      };
    }, false);
  }

  // 2026-08-10: 写选择集快照公共方法（saveSelection / selectByCriteria 共用）——
  // selectByCriteria 用 LISP 异步投递不触发快照事件，必须主动写，否则导出读旧快照
  private static void WriteSelectionSnapshot(Autodesk.AutoCAD.ApplicationServices.Document doc, List<Dictionary<string, string?>> entries)
  {
    if (entries == null || entries.Count == 0) return;
    try
    {
      var selFile = System.IO.Path.Combine(
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
          System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
          "..", "..")),
        "exchange", "_out", "_sel.json");
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(selFile) ?? ".");
      var payload = new Dictionary<string, object?>
      {
        ["docName"] = doc.Name,
        ["savedAt"] = DateTime.Now.ToString("o"),
        ["items"] = entries,
      };
      FileBoundary.WriteAllTextAtomic(selFile, System.Text.Json.JsonSerializer.Serialize(payload), System.Text.UTF8Encoding.UTF8, overwrite: true, "json");
    }
    catch (System.Exception ex)
    {
      PluginLog.Debug("Selection", "write selection snapshot failed: " + ex.Message);
    }
  }

  public static Task<object?> GetSelectionAsync()
  {
    // 尝试�?_sel.json 读取(OnSelectionChanged 保存的)
    var selFile = System.IO.Path.Combine(
      System.IO.Path.GetFullPath(System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
        "..", "..")),
      "exchange", "_out", "_sel.json");
    if (System.IO.File.Exists(selFile))
    {
      var json = System.IO.File.ReadAllText(selFile);
      // 新格式:{docName, savedAt, items:[...]};旧格式:数组
      var items = new List<Dictionary<string, string?>>();
      string docName = "";
      try
      {
        var obj = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (obj is System.Text.Json.Nodes.JsonObject jo && jo.ContainsKey("items"))
        {
          docName = PluginRuntime.JsonNodeToString(jo["docName"]) ?? "";
          var arr = jo["items"] as System.Text.Json.Nodes.JsonArray;
          if (arr != null)
            foreach (var it in arr)
              if (it is System.Text.Json.Nodes.JsonObject item)
                items.Add(new Dictionary<string, string?>
                {
                  ["handle"] = PluginRuntime.JsonNodeToString(item["handle"]),
                  ["rxClass"] = PluginRuntime.JsonNodeToString(item["rxClass"]),
                });
        }
        else
        {
          items = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json) ?? new();
        }
      }
      catch { items = new(); }
      if (items.Count > 0)
      {
        // 2026-08-12 M2: 跨图校验——快照 docName 与当前文档不一致时提示(选择集是旧图纸的, 句柄可能失效)
        var currentDocName = "";
        try { currentDocName = PluginRuntime.GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument) ?? ""; } catch { }
        var docMismatch = !string.IsNullOrEmpty(docName) && !string.IsNullOrEmpty(currentDocName)
          && !string.Equals(docName, currentDocName, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<object?>(new Dictionary<string, object?>
        {
          ["count"] = items.Count,
          ["handles"] = items.Select(i => i.GetValueOrDefault("handle") ?? "").ToList(),
          ["source"] = "file",
          ["docName"] = docName,
          ["currentDoc"] = currentDocName,
          ["docMismatch"] = docMismatch,
          ["warning"] = docMismatch
            ? "选择集快照来自图纸 '" + docName + "', 与当前图纸 '" + currentDocName + "' 不一致——句柄可能无效, 请在当前图纸重新框选后再发指令"
            : null,
        });
      }
    }
    // fallback: 内存
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["count"] = _savedSelection.Count,
      ["handles"] = new List<string>(_savedSelection),
    });
  }
  public static Task<object?> CreatePolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var polyline = new Polyline();
      var bulgesNode = PluginRuntime.GetParameter(parameters, "bulges") as JsonArray;
      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject point)
        {
          continue;
        }

        var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        double bulge = 0;
        if (bulgesNode != null && index < bulgesNode.Count && bulgesNode[index] is JsonValue bulgeValue)
        {
          bulge = bulgeValue.GetValue<double>();
        }
        polyline.AddVertexAt(index, new Point2d(x, y), bulge, 0, 0);
      }

      polyline.Closed = closed;

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        polyline.LayerId = layerId;
      }

      var polylineId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline>(transaction, polylineId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = created.NumberOfVertices,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> CreateTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 2.5d;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var dbText = new DBText
      {
        TextString = text,
        Position = new Point3d(x, y, z),
        Height = height,
        Rotation = rotation,
      };

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        dbText.LayerId = layerId;
      }

      var textId = modelSpace.AppendEntity(dbText);
      transaction.AddNewlyCreatedDBObject(dbText, true);

      var created = CivilObjectUtils.GetRequiredObject<DBText>(transaction, textId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.TextString,
        ["x"] = created.Position.X,
        ["y"] = created.Position.Y,
        ["z"] = created.Position.Z,
        ["height"] = created.Height,
        ["rotation"] = created.Rotation,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> Create3dPolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var pointCollection = new Point3dCollection();
      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject point)
        {
          continue;
        }

        var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        var z = point["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing z.");
        pointCollection.Add(new Point3d(x, y, z));
      }

      if (pointCollection.Count < 2)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two valid points.");
      }

      using var polyline3d = new Polyline3d(Poly3dType.SimplePoly, pointCollection, closed);

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        polyline3d.LayerId = layerId;
      }

      var polylineId = modelSpace.AppendEntity(polyline3d);
      transaction.AddNewlyCreatedDBObject(polyline3d, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline3d>(transaction, polylineId, OpenMode.ForRead);
      var vertexCount = 0;
      foreach (ObjectId vertexId in created)
      {
        _ = vertexId;
        vertexCount++;
      }

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = vertexCount,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> CreateLineSegmentAsync(JsonObject? parameters)
  {
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startZ = PluginRuntime.GetOptionalDouble(parameters, "startZ") ?? 0d;
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");
    var endZ = PluginRuntime.GetOptionalDouble(parameters, "endZ") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var line = new Line(new Point3d(startX, startY, startZ), new Point3d(endX, endY, endZ));

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        line.LayerId = layerId;
      }

      var lineId = modelSpace.AppendEntity(line);
      transaction.AddNewlyCreatedDBObject(line, true);

      var created = CivilObjectUtils.GetRequiredObject<Line>(transaction, lineId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["lineId"] = CivilObjectUtils.GetHandle(created),
        ["startX"] = created.StartPoint.X,
        ["startY"] = created.StartPoint.Y,
        ["startZ"] = created.StartPoint.Z,
        ["endX"] = created.EndPoint.X,
        ["endY"] = created.EndPoint.Y,
        ["endZ"] = created.EndPoint.Z,
        ["length"] = created.Length,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> CreateMTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 0d;
    var textHeight = PluginRuntime.GetOptionalDouble(parameters, "textHeight") ?? 2.5d;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var mtext = new MText
      {
        Contents = text,
        Location = new Point3d(x, y, z),
        TextHeight = textHeight,
        Rotation = rotation,
      };

      if (width > 0)
      {
        mtext.Width = width;
      }

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        mtext.LayerId = layerId;
      }

      var mtextId = modelSpace.AppendEntity(mtext);
      transaction.AddNewlyCreatedDBObject(mtext, true);

      var created = CivilObjectUtils.GetRequiredObject<MText>(transaction, mtextId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.Contents,
        ["x"] = created.Location.X,
        ["y"] = created.Location.Y,
        ["z"] = created.Location.Z,
        ["textHeight"] = created.TextHeight,
        ["rotation"] = created.Rotation,
        ["width"] = created.Width,
        ["layer"] = created.Layer,
      };
    });
  }

  public static Task<object?> GetEntityInfoAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
      if (ent == null)
        return new Dictionary<string, object?> { ["error"] = "not found" };

      var info = new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["type"] = ent.GetRXClass().Name,
        ["layer"] = ent.Layer,
        ["color"] = ent.Color.ToString(),
      };

      // 文字
      if (ent is Autodesk.AutoCAD.DatabaseServices.DBText txt)
      {
        info["text"] = txt.TextString;
        info["height"] = txt.Height;
        info["rotation"] = txt.Rotation;
      }
      else if (ent is Autodesk.AutoCAD.DatabaseServices.MText mt)
      {
        info["text"] = mt.Contents;
        info["textHeight"] = mt.TextHeight;
      }
      // 多段�?
      else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl)
      {
        info["vertexCount"] = pl.NumberOfVertices;
        info["closed"] = pl.Closed;
        info["area"] = pl.Area;
        info["length"] = pl.Length;
        var vertices = new List<Dictionary<string, double?>>();
        for (int vi = 0; vi < pl.NumberOfVertices; vi++)
          vertices.Add(new Dictionary<string, double?>
          {
            ["x"] = pl.GetPoint2dAt(vi).X,
            ["y"] = pl.GetPoint2dAt(vi).Y,
          });
        info["vertices"] = vertices;
        var bulges = new List<double>();
        for (int vi = 0; vi < pl.NumberOfVertices; vi++)
          bulges.Add(pl.GetBulgeAt(vi));
        info["bulges"] = bulges;
      }
      // �?
      else if (ent is Autodesk.AutoCAD.DatabaseServices.Circle ci)
      {
        info["center"] = new Dictionary<string, object?>
        {
          ["x"] = ci.Center.X, ["y"] = ci.Center.Y, ["z"] = ci.Center.Z
        };
        info["radius"] = ci.Radius;
        info["diameter"] = ci.Radius * 2;
        info["circumference"] = ci.Circumference;
        info["area"] = ci.Area;
      }
      // 直线
      else if (ent is Autodesk.AutoCAD.DatabaseServices.Line ln)
      {
        info["startPoint"] = new { x = ln.StartPoint.X, y = ln.StartPoint.Y, z = ln.StartPoint.Z };
        info["endPoint"] = new { x = ln.EndPoint.X, y = ln.EndPoint.Y, z = ln.EndPoint.Z };
        info["length"] = ln.Length;
        info["angle"] = ln.Angle;
      }
      // 圆弧
      else if (ent is Autodesk.AutoCAD.DatabaseServices.Arc arc)
      {
        info["center"] = new { x = arc.Center.X, y = arc.Center.Y };
        info["radius"] = arc.Radius;
        info["startAngle"] = arc.StartAngle;
        info["endAngle"] = arc.EndAngle;
        info["length"] = arc.Length;
      }

      // 3D 实体 (Solid3d)
      else if (ent is Autodesk.AutoCAD.DatabaseServices.Solid3d solid)
      {
        try
        {
          var mp = solid.MassProperties;
          info["volume"] = mp.Volume;
          info["centroid"] = new { x = mp.Centroid.X, y = mp.Centroid.Y, z = mp.Centroid.Z };
          info["note"] = "3D 实体";
        }
        catch (System.Exception)
        {
          info["volume"] = 0;
          info["note"] = "3D 实体 (质量特性不可用)";
        }
      }

      // 块引用
      else if (ent is Autodesk.AutoCAD.DatabaseServices.BlockReference br2)
      {
        info["blockName"] = br2.Name;
        info["position"] = new { x = br2.Position.X, y = br2.Position.Y, z = br2.Position.Z };
        info["rotation"] = br2.Rotation;
        info["scale"] = new { x = br2.ScaleFactors.X, y = br2.ScaleFactors.Y, z = br2.ScaleFactors.Z };
      }

      return info;
    });
  }

  /// <summary>
  /// 读取块引用信息:块名、属性(Attribute)、块定义内文字
  /// </summary>
  public static Task<object?> GetBlockInfoAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var br = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.BlockReference;
      if (br == null) return new Dictionary<string, object?> { ["error"] = "not a block reference" };

      var info = new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["type"] = br.GetRXClass().Name,
        ["layer"] = br.Layer,
        ["color"] = br.Color.ToString(),
        ["blockName"] = br.Name,
      };

      // 属性 (ATTRIB)
      var atts = new List<Dictionary<string, object?>>();
      foreach (ObjectId attId in br.AttributeCollection)
      {
        var att = transaction.GetObject(attId, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.AttributeReference;
        if (att == null) continue;
        atts.Add(new Dictionary<string, object?>
        {
          ["tag"] = att.Tag,
          ["text"] = att.TextString,
        });
      }
      info["attributes"] = atts;

      // 块定义内的文字 (TEXT/MTEXT)
      var defTexts = new List<string>();
      var btr = transaction.GetObject(br.BlockTableRecord, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.BlockTableRecord;
      if (btr != null)
      {
        foreach (ObjectId eid in btr)
        {
          var e = transaction.GetObject(eid, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
          if (e == null) continue;
          if (e is Autodesk.AutoCAD.DatabaseServices.DBText dt) defTexts.Add(dt.TextString);
          else if (e is Autodesk.AutoCAD.DatabaseServices.MText mt) defTexts.Add(mt.Contents);
        }
      }
      info["blockTexts"] = defTexts;

      return info;
    });
  }

  public static Task<object?> SetEntityColorAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var colorIndex = PluginRuntime.GetRequiredInt(parameters, "color");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForWrite) as Autodesk.AutoCAD.DatabaseServices.Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      ent.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)colorIndex);
      return new Dictionary<string, object?> { ["handle"] = handle, ["color"] = colorIndex };
    });
  }

  public static Task<object?> MirrorEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };

      var ext = ent.GeometricExtents;
      var midX = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
      var mirrorPt1 = new Autodesk.AutoCAD.Geometry.Point3d(midX, ext.MinPoint.Y, 0);
      var mirrorPt2 = new Autodesk.AutoCAD.Geometry.Point3d(midX, ext.MaxPoint.Y, 0);
      var mirrorLine = new Autodesk.AutoCAD.Geometry.Line3d(mirrorPt1, mirrorPt2);
      var matrix = Autodesk.AutoCAD.Geometry.Matrix3d.Mirroring(mirrorLine);

      var mirroredEnt = ent.GetTransformedCopy(matrix);
      if (mirroredEnt == null) return new Dictionary<string, object?> { ["error"] = "mirror failed" };

      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.BlockTable;
      if (bt == null) return new Dictionary<string, object?> { ["error"] = "no block table" };
      var ms = transaction.GetObject(bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace], OpenMode.ForWrite) as Autodesk.AutoCAD.DatabaseServices.BlockTableRecord;
      if (ms == null) return new Dictionary<string, object?> { ["error"] = "no model space" };

      ms.AppendEntity(mirroredEnt);
      transaction.AddNewlyCreatedDBObject(mirroredEnt, true);

      return new Dictionary<string, object?>
      {
        ["sourceHandle"] = handle,
        ["mirroredHandle"] = mirroredEnt.Handle.ToString(),
      };
    });
  }

  public static Task<object?> ExecuteCommandAsync(JsonObject? parameters)
  {
    var command = PluginRuntime.GetRequiredString(parameters, "command");
    // 交互式命令白名单拦截:这些命令需要用户输入,直接执行会挂起串行队列
    var interactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "BOX", "PLINE", "LINE", "CIRCLE", "ARC", "RECTANG", "POLYGON", "ELLIPSE",
      "SPLINE", "DONUT", "POINT", "HATCH", "BHATCH", "TEXT", "MTEXT", "DDEDIT",
      "DIM", "DIMLINEAR", "DIMALIGNED", "DIMRADIUS", "DIMDIAMETER", "DIMANGULAR",
      "DIMORDINATE", "DIMCONTINUE", "DIMBASELINE", "MLEADER", "LEADER", "QLEADER",
      "BLOCK", "INSERT", "WBLOCK", "XATTACH", "XREF", "LAYER", "LAYOFF", "LAYFRZ",
      "LAYLCK", "LAYULK", "LAYISO", "LAYMCH", "TRIM", "EXTEND", "FILLET", "CHAMFER",
      "BREAK", "JOIN", "EXPLODE", "OFFSET", "STRETCH", "ARRAY", "ARRAYCLASSIC",
      "ALIGN", "SCALE", "ROTATE", "MOVE", "COPY", "MIRROR", "ERASE", "EXTRUDE",
      "REVOLVE", "LOFT", "SWEEP", "UNION", "SUBTRACT", "INTERSECT", "SLICE",
      "SECTION", "SECTIONPLANE", "3DMOVE", "3DROTATE", "3DMIRROR", "3DARRAY",
      "CONVTOSURFACE", "CONVTOSOLID", "RENDER", "MATERIALATTACH", "3D", "BOX",
      // 2026-08-12 C3 补全: 会话/文件/系统级命令——QUIT/CLOSE 弹保存框挂起, SAVEAS/SAVE 副作用大, SETVAR 可改系统变量, UNDO/REDO 影响历史
      "QUIT", "EXIT", "END", "CLOSE", "SAVE", "SAVEAS", "SAVEALL", "WSAVE", "SETVAR", "FILEDIA",
      "PURGE", "AUDIT", "RECOVER", "RECOVERALL", "PLOT", "PRINT", "PAGESETUP", "EXPORT", "IMPORT",
      "_QUIT", "_CLOSE", "_SAVE", "_SAVEAS"
    };
    // 提取命令名(去掉前缀 _ 和 .)
    var cmdName = command.Trim();
    if (cmdName.StartsWith("_") || cmdName.StartsWith(".") || cmdName.StartsWith("-")) cmdName = cmdName.Substring(1);
    // 去掉参数部分(取第一个空格前)
    var spaceIdx = cmdName.IndexOf(' ');
    if (spaceIdx > 0) cmdName = cmdName.Substring(0, spaceIdx);

    if (interactive.Contains(cmdName))
    {
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["command"] = command,
        ["error"] = "interactive command blocked: " + cmdName + " - 请用专用 API 方法(如 createPolyline/createCircle),executeCommand 只允许非交互命令",
        ["status"] = "Blocked",
      });
    }

    // 2026-08-07 修复: 原 WriteAsync + ed.Command = 后台线程持文档锁再要锁 = 永久死锁。改用命令上下文执行(安全通道)
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var docPtr = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
      if (docPtr == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };

      var ed = docPtr.Editor;
      var originalCmdEcho = (short)Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDECHO");
      try
      {
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", 0);
        ed.Command(command);
        return (object?)new Dictionary<string, object?>
        {
          ["command"] = command,
          ["status"] = "OK",
        };
      }
      catch (System.Exception ex)
      {
        return (object?)new Dictionary<string, object?>
        {
          ["command"] = command,
          ["error"] = ex.Message,
          ["status"] = "Error",
        };
      }
      finally
      {
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", originalCmdEcho);
      }
    });
  }

    public static Task<object?> RunLispAsync(JsonObject? parameters)
  {
    var lisp = PluginRuntime.GetRequiredString(parameters, "lisp");
    // 2026-08-07 修复: App.Invoke 在 25.0.58 全坏(eInvalidInput)→ 改 SendStringToExecute 投递 LISP(命令队列安全通道)
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      // 2026-08-12 M1 修复: 超 400 字符不再静默截断(截断点可能在表达式中间→括号不配对→静默失败), 直接报错要求分段
      if (lisp.Length > 400)
        return (object?)new Dictionary<string, object?> { ["error"] = "LISP 脚本过长(" + lisp.Length + " 字符, 上限 400)——请拆分为多段执行或改用专用 API 方法", ["status"] = "Blocked" };
      doc.SendStringToExecute("(" + lisp + ") ", true, false, false);
      return (object?)new Dictionary<string, object?>
      {
        ["lisp"] = lisp.Length > 80 ? lisp.Substring(0, 80) + "..." : lisp,
        ["status"] = "queued",
        ["note"] = "LISP 已投递执行(无返回值通道,适合操作类脚本;查询类请用专用方法)",
      };
    });
  }

  // ========== 实体操作 ==========

  public static Task<object?> DeleteEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      ent.Erase(true);
      return new Dictionary<string, object?> { ["deleted"] = handle };
    });
  }

  public static Task<object?> MoveEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var dx = PluginRuntime.GetRequiredDouble(parameters, "dx");
    var dy = PluginRuntime.GetRequiredDouble(parameters, "dy");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      ent.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
      return new Dictionary<string, object?> { ["moved"] = handle, ["dx"] = dx, ["dy"] = dy };
    });
  }

  // ===== 2026-08-08: 批量 2D 编辑(handles 数组一次处理 N 个,减少轮次) =====
  public static Task<object?> CopyEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 0;
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForRead) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          var copy = (Entity)ent.Clone();
          copy.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Autodesk.AutoCAD.Geometry.Vector3d(dx, dy, 0)));
          var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
          var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
          ms!.AppendEntity(copy);
          transaction.AddNewlyCreatedDBObject(copy, true);
          ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  public static Task<object?> MirrorEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          var ext = ent.GeometricExtents;
          var cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
          var mirrorMat = Autodesk.AutoCAD.Geometry.Matrix3d.Mirroring(new Autodesk.AutoCAD.Geometry.Line3d(
            new Autodesk.AutoCAD.Geometry.Point3d(cx, ext.MinPoint.Y, 0),
            new Autodesk.AutoCAD.Geometry.Point3d(cx, ext.MaxPoint.Y, 0)));
          ent.TransformBy(mirrorMat);
          ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  public static Task<object?> RotateEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var angleDeg = PluginRuntime.GetRequiredDouble(parameters, "angle");
    var cx = PluginRuntime.GetOptionalDouble(parameters, "cx") ?? 0;
    var cy = PluginRuntime.GetOptionalDouble(parameters, "cy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      var rad = angleDeg * System.Math.PI / 180.0;
      var rotMat = Autodesk.AutoCAD.Geometry.Matrix3d.Rotation(rad, Autodesk.AutoCAD.Geometry.Vector3d.ZAxis, new Autodesk.AutoCAD.Geometry.Point3d(cx, cy, 0));
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.TransformBy(rotMat);
          ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  public static Task<object?> ScaleEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var factor = PluginRuntime.GetRequiredDouble(parameters, "factor");
    var cx = PluginRuntime.GetOptionalDouble(parameters, "cx") ?? 0;
    var cy = PluginRuntime.GetOptionalDouble(parameters, "cy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      var scaleMat = Autodesk.AutoCAD.Geometry.Matrix3d.Scaling(factor, new Autodesk.AutoCAD.Geometry.Point3d(cx, cy, 0));
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.TransformBy(scaleMat);
          ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  public static Task<object?> CopyEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 0;
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForRead) as Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      var copy = ent.GetTransformedCopy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
      if (copy == null) return new Dictionary<string, object?> { ["error"] = "copy failed" };
      var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      ms.AppendEntity(copy);
      transaction.AddNewlyCreatedDBObject(copy, true);
      return new Dictionary<string, object?> { ["source"] = handle, ["copy"] = copy.Handle.ToString() };
    });
  }

  public static Task<object?> RotateEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var angleDeg = PluginRuntime.GetRequiredDouble(parameters, "angle");
    var cx = PluginRuntime.GetOptionalDouble(parameters, "cx") ?? 0;
    var cy = PluginRuntime.GetOptionalDouble(parameters, "cy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      var rad = angleDeg * System.Math.PI / 180.0;
      var center = new Autodesk.AutoCAD.Geometry.Point3d(cx, cy, 0);
      ent.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Rotation(rad, Vector3d.ZAxis, center));
      return new Dictionary<string, object?> { ["rotated"] = handle, ["angle"] = angleDeg };
    });
  }

  public static Task<object?> ScaleEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var factor = PluginRuntime.GetRequiredDouble(parameters, "factor");
    var cx = PluginRuntime.GetOptionalDouble(parameters, "cx") ?? 0;
    var cy = PluginRuntime.GetOptionalDouble(parameters, "cy") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
      var id = database.GetObjectId(false, h, 0);
      if (id.IsNull) return new Dictionary<string, object?> { ["error"] = "not found" };
      var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
      if (ent == null) return new Dictionary<string, object?> { ["error"] = "not found" };
      var center = new Autodesk.AutoCAD.Geometry.Point3d(cx, cy, 0);
      ent.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Scaling(factor, center));
      return new Dictionary<string, object?> { ["scaled"] = handle, ["factor"] = factor };
    });
  }

  // ========== 选择 ==========

  public static Task<object?> GetLayersAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      var layers = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in lt)
      {
        var lr = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        layers.Add(new Dictionary<string, object?>
        {
          ["name"] = lr.Name,
          ["color"] = lr.Color.ToString(),
          ["isOff"] = lr.IsOff,
          ["isFrozen"] = lr.IsFrozen,
          ["isLocked"] = lr.IsLocked,
        });
      }
      return new Dictionary<string, object?> { ["count"] = layers.Count, ["layers"] = layers };
    });
  }

  public static Task<object?> CreateLayerAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var colorIndex = PluginRuntime.GetOptionalInt(parameters, "color") ?? 7;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (lt.Has(name))
        return new Dictionary<string, object?> { ["name"] = name, ["alreadyExists"] = true };
      lt.UpgradeOpen();
      var lr = new LayerTableRecord
      {
        Name = name,
        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)colorIndex),
      };
      lt.Add(lr);
      transaction.AddNewlyCreatedDBObject(lr, true);
      return new Dictionary<string, object?> { ["name"] = name, ["color"] = colorIndex, ["created"] = true };
    });
  }

  // ===== 2026-08-12 P0: 图层属性写（基础 CAD 缺口补齐） =====
  public static Task<object?> SetLayerColorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "layer");
    var colorIndex = PluginRuntime.GetRequiredInt(parameters, "color");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)colorIndex);
      return new Dictionary<string, object?> { ["layer"] = name, ["color"] = colorIndex };
    });
  }

  public static Task<object?> SetLayerLinetypeAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "layer");
    var linetype = PluginRuntime.GetRequiredString(parameters, "linetype");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      var linetypeId = LookupUtils.GetLinetypeId(database, transaction, linetype);
      if (linetypeId.IsNull) return new Dictionary<string, object?> { ["error"] = "linetype not found: " + linetype };
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.LinetypeObjectId = linetypeId;
      return new Dictionary<string, object?> { ["layer"] = name, ["linetype"] = linetype };
    });
  }

  public static Task<object?> SetLayerLineweightAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "layer");
    var lw = PluginRuntime.GetRequiredInt(parameters, "lineweight");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.LineWeight = (LineWeight)lw;
      return new Dictionary<string, object?> { ["layer"] = name, ["lineweight"] = lw };
    });
  }

  public static Task<object?> SetLayerTransparencyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "layer");
    // 0-100 百分比: 0=不透明, 100=全透明 → alpha 255-0
    var pct = PluginRuntime.GetRequiredInt(parameters, "transparency");
    var alpha = (byte)(255 - Math.Clamp(pct, 0, 100) * 255 / 100);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.Transparency = new Autodesk.AutoCAD.Colors.Transparency(alpha);
      return new Dictionary<string, object?> { ["layer"] = name, ["transparency"] = pct };
    });
  }

  public static Task<object?> RenameLayerAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      if (lt.Has(newName)) return new Dictionary<string, object?> { ["error"] = "target layer already exists: " + newName };
      lt.UpgradeOpen();
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.Name = newName;
      return new Dictionary<string, object?> { ["oldName"] = name, ["newName"] = newName, ["renamed"] = true };
    });
  }

  public static Task<object?> DeleteLayerAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
      if (!lt.Has(name)) return new Dictionary<string, object?> { ["error"] = "layer not found: " + name };
      // 保护：0 层和 Defpoints 不可删
      if (name.Equals("0", StringComparison.OrdinalIgnoreCase) || name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
        return new Dictionary<string, object?> { ["error"] = "layer protected: " + name };
      // 2026-08-12 修复: 删除前检查图层是否被实体引用（非空图层直接 Erase 会产生脏状态——图层记录没了但实体还挂着）
      var usedCount = 0;
      var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      foreach (ObjectId btrId in blockTable)
      {
        var btr = (BlockTableRecord)transaction.GetObject(btrId, OpenMode.ForRead);
        foreach (ObjectId entId in btr)
        {
          var ent = transaction.GetObject(entId, OpenMode.ForRead) as Entity;
          if (ent != null && string.Equals(ent.Layer, name, StringComparison.OrdinalIgnoreCase))
            usedCount++;
        }
      }
      if (usedCount > 0)
        return new Dictionary<string, object?> { ["error"] = "layer not empty: " + usedCount + " entities reference it, move them first (setEntityLayer)", ["name"] = name, ["usedCount"] = usedCount };
      lt.UpgradeOpen();
      var lr = (LayerTableRecord)transaction.GetObject(lt[name], OpenMode.ForWrite);
      lr.Erase();
      return new Dictionary<string, object?> { ["deleted"] = true, ["name"] = name };
    });
  }

  // ========== Phase 1: 编辑操作 ==========

  public static Task<object?> OffsetEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var distance = PluginRuntime.GetRequiredDouble(parameters, "distance");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var curve = CivilObjectUtils.GetRequiredObject<Curve>(transaction, id, OpenMode.ForRead);
      var offsets = curve.GetOffsetCurves(distance);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var newHandles = new List<string>();
      foreach (object? o in offsets)
      {
        if (o is not Entity ent) continue;
        if (!string.IsNullOrWhiteSpace(layerName))
        {
          var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
          ent.LayerId = layerId;
        }
        var newId = modelSpace.AppendEntity(ent);
        transaction.AddNewlyCreatedDBObject(ent, true);
        var created = CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead);
        newHandles.Add(CivilObjectUtils.GetHandle(created));
      }
      return (object?)new Dictionary<string, object?> { ["offset"] = handle, ["distance"] = distance, ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> BreakEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var curve = CivilObjectUtils.GetRequiredObject<Curve>(transaction, id, OpenMode.ForRead);
      var breakPoint = new Point3d(x, y, curve.StartPoint.Z);
      var split = curve.GetSplitCurves(new Point3dCollection { breakPoint });
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var newHandles = new List<string>();
      foreach (object? o in split)
      {
        if (o is not Entity ent) continue;
        var newId = modelSpace.AppendEntity(ent);
        transaction.AddNewlyCreatedDBObject(ent, true);
        var created = CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead);
        newHandles.Add(CivilObjectUtils.GetHandle(created));
      }
      // 删除原曲线 (2026-08-12 M5: 同事务已 ForRead, 用 UpgradeOpen 避免双开)
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      orig.UpgradeOpen();
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["break"] = handle, ["at"] = "(" + x + "," + y + ")", ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> JoinEntitiesAsync(JsonObject? parameters)
  {
    var handlesNode = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    if (handlesNode == null || handlesNode.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "JOIN requires at least 2 handles.");
    var handles = handlesNode.Select(n => n?.GetValue<string>() ?? "").Where(h => !string.IsNullOrEmpty(h)).ToList();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var curves = new List<Entity>();
      foreach (var h in handles)
      {
        var id = HandleToObjectId(database, h);
        var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
        if (ent is not Curve c)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "JOIN only supports curve entities (line/arc/polyline).");
        curves.Add(c);
      }
      if (curves.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "JOIN requires at least 2 valid curve handles.");
      // 直接对数据库实体(ForWrite)执行合并:25.0.58 的 JoinEntities(Entity[]) 原地修改第一个实体并返回被合并索引
      var ents = curves.Cast<Entity>().ToArray();
      var merged = ents[0].JoinEntities(ents);
      if (merged == null || merged.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "JOIN failed: curves are not connected at endpoints.");
      var mergedSet = new HashSet<int>();
      foreach (int i in merged) mergedSet.Add(i);
      if (mergedSet.Contains(0))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "JOIN failed: could not merge into first curve.");
      // ents[0] 保留为合并结果;被合并的曲线删除
      var erased = new List<string>();
      for (int i = 1; i < ents.Length; i++)
      {
        if (mergedSet.Contains(i))
        {
          ents[i].Erase();
          erased.Add(handles[i]);
        }
      }
      return (object?)new Dictionary<string, object?> { ["joined"] = string.Join(",", handles), ["handle"] = handles[0], ["erased"] = erased };
    });
  }

  public static Task<object?> ExplodeEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var exploded = new DBObjectCollection();
      ent.Explode(exploded);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var newHandles = new List<string>();
      foreach (object? o in exploded)
      {
        if (o is not Entity e) continue;
        var newId = modelSpace.AppendEntity(e);
        transaction.AddNewlyCreatedDBObject(e, true);
        var created = CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead);
        newHandles.Add(CivilObjectUtils.GetHandle(created));
      }
      // 删除原实体
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["exploded"] = handle, ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> FilletEntitiesAsync(JsonObject? parameters)
  {
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var handle1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var handle2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    // 2026-08-07 P1-5: App.Invoke 在 25.0.58 后台线程全坏(eInvalidInput)→ 改 SendStringToExecute 投递(命令队列安全通道)。
    // 返回语义变化:不再同步等待命令完成,只返回"已入队"(无同步结果)。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.FILLET\" \"_R\" " + radius + " (handent \"" + handle1 + "\") (handent \"" + handle2 + "\") \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["fillet"] = handle1 + "," + handle2, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> ChamferEntitiesAsync(JsonObject? parameters)
  {
    var d1 = PluginRuntime.GetRequiredDouble(parameters, "distance1");
    var d2 = PluginRuntime.GetRequiredDouble(parameters, "distance2");
    var handle1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var handle2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.CHAMFER\" \"_D\" " + d1 + " " + d2 + " (handent \"" + handle1 + "\") (handent \"" + handle2 + "\") \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["chamfer"] = handle1 + "," + handle2, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> TrimEntityAsync(JsonObject? parameters)
  {
    var cuttingHandle = PluginRuntime.GetRequiredString(parameters, "cuttingHandle");
    var targetHandle = PluginRuntime.GetRequiredString(parameters, "targetHandle");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.TRIM\" (handent \"" + cuttingHandle + "\") \"\" (handent \"" + targetHandle + "\") \"\" \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["trimmed"] = targetHandle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> ExtendEntityAsync(JsonObject? parameters)
  {
    var boundaryHandle = PluginRuntime.GetRequiredString(parameters, "boundaryHandle");
    var targetHandle = PluginRuntime.GetRequiredString(parameters, "targetHandle");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.EXTEND\" (handent \"" + boundaryHandle + "\") \"\" (handent \"" + targetHandle + "\") \"\" \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["extended"] = targetHandle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> StretchEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var dx = PluginRuntime.GetRequiredDouble(parameters, "dx");
    var dy = PluginRuntime.GetRequiredDouble(parameters, "dy");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.STRETCH\" (handent \"" + handle + "\") \"\" " + dx + " " + dy + " \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["stretched"] = handle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> ArrayEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var type = PluginRuntime.GetRequiredString(parameters, "type"); // rect | polar
    var count = PluginRuntime.GetRequiredInt(parameters, "count");
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 0.0;
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 0.0;
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows") ?? 1;
    var cols = PluginRuntime.GetOptionalInt(parameters, "cols") ?? 1;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var newHandles = new List<string>();
      if (type == "polar")
      {
        for (int i = 1; i < count; i++)
        {
          var angle = (Math.PI * 2.0 * i) / count;
          var clone = (Entity)ent.Clone();
          clone.TransformBy(Matrix3d.Rotation(angle, Vector3d.ZAxis, Point3d.Origin));
          var newId = modelSpace.AppendEntity(clone);
          transaction.AddNewlyCreatedDBObject(clone, true);
          newHandles.Add(CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead)));
        }
      }
      else
      {
        for (int r = 0; r < rows; r++)
        {
          for (int c = 0; c < cols; c++)
          {
            if (r == 0 && c == 0) continue;
            var clone = (Entity)ent.Clone();
            clone.TransformBy(Matrix3d.Displacement(new Vector3d(dx * c, dy * r, 0)));
            var newId = modelSpace.AppendEntity(clone);
            transaction.AddNewlyCreatedDBObject(clone, true);
            newHandles.Add(CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead)));
          }
        }
      }
      return (object?)new Dictionary<string, object?> { ["arrayed"] = handle, ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> AlignEntityAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var srcX = PluginRuntime.GetRequiredDouble(parameters, "srcX");
    var srcY = PluginRuntime.GetRequiredDouble(parameters, "srcY");
    var dstX = PluginRuntime.GetRequiredDouble(parameters, "dstX");
    var dstY = PluginRuntime.GetRequiredDouble(parameters, "dstY");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
      var displacement = new Vector3d(dstX - srcX, dstY - srcY, 0);
      ent.TransformBy(Matrix3d.Displacement(displacement));
      return (object?)new Dictionary<string, object?> { ["aligned"] = handle, ["dx"] = displacement.X, ["dy"] = displacement.Y };
    });
  }

  public static Task<object?> OverkillEntitiesAsync(JsonObject? parameters)
  {
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.OVERKILL\" (ssget \"_X\") \"\" \"_Y\" \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["overkill"] = "done", ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  // ========== Phase 2: 图层操作(API 版,2026-07-31 重写,替代 LAYOFF/LAYFRZ 等交互命令) ==========

  private static LayerTableRecord GetLayerRecordForWrite(Transaction transaction, Database database, string layerName)
  {
    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (!layerTable.Has(layerName))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Layer not found: " + layerName);
    }
    return CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, layerTable[layerName], OpenMode.ForWrite);
  }

  private static ObjectId HandleToObjectId(Database database, string handle)
  {
    try
    {
      return database.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
    }
    catch (System.Exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Invalid handle: " + handle);
    }
  }

  public static Task<object?> LayerOffAsync(JsonObject? parameters)
  {
    var layerName = PluginRuntime.GetRequiredString(parameters, "layerName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rec = GetLayerRecordForWrite(transaction, database, layerName);
      rec.IsOff = true;
      return (object?)new Dictionary<string, object?> { ["layerOff"] = layerName };
    });
  }

  public static Task<object?> LayerFreezeAsync(JsonObject? parameters)
  {
    var layerName = PluginRuntime.GetRequiredString(parameters, "layerName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rec = GetLayerRecordForWrite(transaction, database, layerName);
      rec.IsFrozen = true;
      return (object?)new Dictionary<string, object?> { ["layerFreeze"] = layerName };
    });
  }

  public static Task<object?> LayerLockAsync(JsonObject? parameters)
  {
    var layerName = PluginRuntime.GetRequiredString(parameters, "layerName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rec = GetLayerRecordForWrite(transaction, database, layerName);
      rec.IsLocked = true;
      return (object?)new Dictionary<string, object?> { ["layerLock"] = layerName };
    });
  }

  public static Task<object?> LayerUnlockAsync(JsonObject? parameters)
  {
    var layerName = PluginRuntime.GetRequiredString(parameters, "layerName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rec = GetLayerRecordForWrite(transaction, database, layerName);
      rec.IsLocked = false;
      return (object?)new Dictionary<string, object?> { ["layerUnlock"] = layerName };
    });
  }

  public static Task<object?> LayerIsolateAsync(JsonObject? parameters)
  {
    var handles = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 收集需要保留的图层名(选中图元所在图层)
      var keepLayers = new HashSet<string>();
      if (handles != null)
      {
        foreach (var n in handles)
        {
          var h = n?.GetValue<string>();
          if (string.IsNullOrEmpty(h)) continue;
          var id = HandleToObjectId(database, h);
          if (id.IsNull) continue;
          var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
          keepLayers.Add(ent.Layer);
        }
      }

      // 关闭所有非保留图层
      var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
      var turnedOff = new List<string>();
      foreach (ObjectId layerId in layerTable)
      {
        var rec = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, layerId, OpenMode.ForWrite);
        if (rec.IsOff || rec.IsFrozen || rec.IsLocked) continue;
        if (!keepLayers.Contains(rec.Name))
        {
          rec.IsOff = true;
          turnedOff.Add(rec.Name);
        }
      }

      return (object?)new Dictionary<string, object?> { ["layerIsolate"] = "done", ["turnedOff"] = turnedOff };
    });
  }

  public static Task<object?> LayerMatchAsync(JsonObject? parameters)
  {
    var sourceHandle = PluginRuntime.GetRequiredString(parameters, "sourceHandle");
    var targetHandle = PluginRuntime.GetRequiredString(parameters, "targetHandle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sourceId = HandleToObjectId(database, sourceHandle);
      var targetId = HandleToObjectId(database, targetHandle);
      var source = CivilObjectUtils.GetRequiredObject<Entity>(transaction, sourceId, OpenMode.ForRead);
      var target = CivilObjectUtils.GetRequiredObject<Entity>(transaction, targetId, OpenMode.ForWrite);
      target.Layer = source.Layer;
      return (object?)new Dictionary<string, object?> { ["layerMatch"] = targetHandle, ["layer"] = source.Layer };
    });
  }

  // ========== Phase 3: 块操�?==========

  public static Task<object?> CreateBlockAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var handles = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForWrite);
      if (blockTable.Has(name))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Block already exists: " + name);
      var btr = new BlockTableRecord();
      btr.Name = name;
      btr.Origin = new Point3d(x, y, 0);
      var btrId = blockTable.Add(btr);
      transaction.AddNewlyCreatedDBObject(btr, true);
      var added = new List<string>();
      if (handles != null)
      {
        foreach (var n in handles)
        {
          var h = n?.GetValue<string>();
          if (string.IsNullOrEmpty(h)) continue;
          var id = HandleToObjectId(database, h);
          var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
          var clone = (Entity)ent.Clone();
          // 世界坐标 → 块局部坐标(相对基点)
          clone.TransformBy(Matrix3d.Displacement(new Vector3d(-x, -y, 0)));
          btr.AppendEntity(clone);
          transaction.AddNewlyCreatedDBObject(clone, true);
          added.Add(h);
          // BLOCK 命令默认删除源实体
          var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
          orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
          orig.Erase();
        }
      }
      var created = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, btrId, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["block"] = name, ["handle"] = CivilObjectUtils.GetHandle(created), ["entities"] = added };
    });
  }

  public static Task<object?> InsertBlockAsync(JsonObject? parameters)
  {
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      if (!blockTable.Has(blockName))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Block not found: " + blockName);
      }
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var br = new BlockReference(new Point3d(x, y, 0), blockTable[blockName]);
      br.ScaleFactors = new Scale3d(scale, scale, scale);
      br.Rotation = rotation;
      if (!string.IsNullOrWhiteSpace(layerName)) br.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var newId = modelSpace.AppendEntity(br);
      transaction.AddNewlyCreatedDBObject(br, true);
      var created = CivilObjectUtils.GetRequiredObject<BlockReference>(transaction, newId, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["insert"] = blockName, ["handle"] = CivilObjectUtils.GetHandle(created) };
    });
  }

  public static Task<object?> ExplodeBlockAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var exploded = new DBObjectCollection();
      ent.Explode(exploded);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var newHandles = new List<string>();
      foreach (object? o in exploded)
      {
        if (o is not Entity e) continue;
        var newId = modelSpace.AppendEntity(e);
        transaction.AddNewlyCreatedDBObject(e, true);
        var created = CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead);
        newHandles.Add(CivilObjectUtils.GetHandle(created));
      }
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["explodedBlock"] = handle, ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> WriteBlockAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var filePath = PluginRuntime.GetRequiredString(parameters, "filePath");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dir = Path.GetDirectoryName(filePath);
      if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Directory not found: " + dir);
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      using var wblockDb = new Database(true, true);
      database.Wblock(wblockDb, new ObjectIdCollection { id }, new Point3d(0, 0, 0), DuplicateRecordCloning.Replace);
      wblockDb.SaveAs(filePath, DwgVersion.Current);
      return (object?)new Dictionary<string, object?> { ["wblock"] = filePath, ["saved"] = true };
    });
  }

  // ========== Phase 4: 标注 ==========

  public static Task<object?> DimLinearAsync(JsonObject? parameters)
  {
    var x1 = PluginRuntime.GetRequiredDouble(parameters, "x1"); var y1 = PluginRuntime.GetRequiredDouble(parameters, "y1");
    var x2 = PluginRuntime.GetRequiredDouble(parameters, "x2"); var y2 = PluginRuntime.GetRequiredDouble(parameters, "y2");
    var dimX = PluginRuntime.GetRequiredDouble(parameters, "dimX"); var dimY = PluginRuntime.GetRequiredDouble(parameters, "dimY");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var dim = new AlignedDimension();
      dim.XLine1Point = new Point3d(x1, y1, 0);
      dim.XLine2Point = new Point3d(x2, y2, 0);
      dim.DimLinePoint = new Point3d(dimX, dimY, 0);
      if (!string.IsNullOrWhiteSpace(layerName)) dim.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var id = modelSpace.AppendEntity(dim);
      transaction.AddNewlyCreatedDBObject(dim, true);
      var created = CivilObjectUtils.GetRequiredObject<AlignedDimension>(transaction, id, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["dimLinear"] = CivilObjectUtils.GetHandle(created) };
    });
  }

  public static Task<object?> DimAlignedAsync(JsonObject? parameters)
  {
    var x1 = PluginRuntime.GetRequiredDouble(parameters, "x1"); var y1 = PluginRuntime.GetRequiredDouble(parameters, "y1");
    var x2 = PluginRuntime.GetRequiredDouble(parameters, "x2"); var y2 = PluginRuntime.GetRequiredDouble(parameters, "y2");
    var dimX = PluginRuntime.GetRequiredDouble(parameters, "dimX"); var dimY = PluginRuntime.GetRequiredDouble(parameters, "dimY");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var dim = new AlignedDimension();
      dim.XLine1Point = new Point3d(x1, y1, 0);
      dim.XLine2Point = new Point3d(x2, y2, 0);
      dim.DimLinePoint = new Point3d(dimX, dimY, 0);
      if (!string.IsNullOrWhiteSpace(layerName)) dim.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var id = modelSpace.AppendEntity(dim);
      transaction.AddNewlyCreatedDBObject(dim, true);
      var created = CivilObjectUtils.GetRequiredObject<AlignedDimension>(transaction, id, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["dimAligned"] = CivilObjectUtils.GetHandle(created) };
    });
  }

  public static Task<object?> DimRadiusAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    // 2026-08-12 修复: dimX/dimY 改为可选(默认右侧), 语义=标注方向, 不参与测量。
    // 旧版把 ChordPoint 直接当任意坐标点 → Measurement=圆心到标注点距离, 标注点放远就标错(1500 事故)
    var dimX = PluginRuntime.GetOptionalDouble(parameters, "dimX");
    var dimY = PluginRuntime.GetOptionalDouble(parameters, "dimY");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var circle = CivilObjectUtils.GetRequiredObject<Circle>(transaction, id, OpenMode.ForRead);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var dim = new RadialDimension();
      dim.Center = circle.Center;
      // 标注方向: 默认 (1,0) 右侧; 传了 dimX/dimY 则取"圆心→该点"方向
      var dir = new Vector3d(1, 0, 0);
      if (dimX.HasValue && dimY.HasValue)
      {
        var v = new Point3d(dimX.Value, dimY.Value, 0) - circle.Center;
        if (v.Length > 1e-9) dir = v.GetNormal();
      }
      dim.ChordPoint = circle.Center + dir * circle.Radius; // 钳制到圆周 → Measurement 恒=真实半径
      dim.LeaderLength = Math.Max(circle.Radius * 0.5, 1.0);
      if (!string.IsNullOrWhiteSpace(layerName)) dim.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var newId = modelSpace.AppendEntity(dim);
      transaction.AddNewlyCreatedDBObject(dim, true);
      var created = CivilObjectUtils.GetRequiredObject<RadialDimension>(transaction, newId, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["dimRadius"] = CivilObjectUtils.GetHandle(created), ["measurement"] = created.Measurement, ["radius"] = circle.Radius };
    });
  }

  public static Task<object?> DimDiameterAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    // 2026-08-12 修复: 同 dimRadius, dimX/dimY 可选=方向; 弦点/远弦点钳制到直径两端 → Measurement 恒=2×半径
    var dimX = PluginRuntime.GetOptionalDouble(parameters, "dimX");
    var dimY = PluginRuntime.GetOptionalDouble(parameters, "dimY");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var circle = CivilObjectUtils.GetRequiredObject<Circle>(transaction, id, OpenMode.ForRead);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var dim = new DiametricDimension();
      var dir = new Vector3d(1, 0, 0);
      if (dimX.HasValue && dimY.HasValue)
      {
        var v = new Point3d(dimX.Value, dimY.Value, 0) - circle.Center;
        if (v.Length > 1e-9) dir = v.GetNormal();
      }
      dim.ChordPoint = circle.Center + dir * circle.Radius;
      dim.FarChordPoint = circle.Center - dir * circle.Radius;
      dim.LeaderLength = Math.Max(circle.Radius * 0.5, 1.0);
      if (!string.IsNullOrWhiteSpace(layerName)) dim.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var newId = modelSpace.AppendEntity(dim);
      transaction.AddNewlyCreatedDBObject(dim, true);
      var created = CivilObjectUtils.GetRequiredObject<DiametricDimension>(transaction, newId, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["dimDiameter"] = CivilObjectUtils.GetHandle(created), ["measurement"] = created.Measurement, ["diameter"] = circle.Radius * 2 };
    });
  }

  // 2026-08-12 新增: 读取标注实体真实测量值, 供 AI 创建后自检(防"报了实测值实际没读")
  public static Task<object?> GetDimensionInfoAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var dim = CivilObjectUtils.GetRequiredObject<Dimension>(transaction, id, OpenMode.ForRead);
      var info = new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["type"] = dim.GetType().Name,
        ["measurement"] = dim.Measurement,
        ["dimensionText"] = string.IsNullOrEmpty(dim.DimensionText) ? null : dim.DimensionText,
        ["textPosition"] = new[] { dim.TextPosition.X, dim.TextPosition.Y },
      };
      if (dim is RadialDimension rd)
      {
        info["center"] = new[] { rd.Center.X, rd.Center.Y };
        info["chordPoint"] = new[] { rd.ChordPoint.X, rd.ChordPoint.Y };
        info["measurementKind"] = "radius";
      }
      else if (dim is DiametricDimension dd)
      {
        info["chordPoint"] = new[] { dd.ChordPoint.X, dd.ChordPoint.Y };
        info["farChordPoint"] = new[] { dd.FarChordPoint.X, dd.FarChordPoint.Y };
        info["measurementKind"] = "diameter";
      }
      else if (dim is AlignedDimension ad)
      {
        info["xLine1"] = new[] { ad.XLine1Point.X, ad.XLine1Point.Y };
        info["xLine2"] = new[] { ad.XLine2Point.X, ad.XLine2Point.Y };
        info["dimLinePoint"] = new[] { ad.DimLinePoint.X, ad.DimLinePoint.Y };
        info["measurementKind"] = "length";
      }
      return info;
    });
  }

  public static Task<object?> DimAngularAsync(JsonObject? parameters)
  {
    var handle1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var handle2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    var dimX = PluginRuntime.GetRequiredDouble(parameters, "dimX"); var dimY = PluginRuntime.GetRequiredDouble(parameters, "dimY");
    // .NET API 无公开 AngularDimension 类,保留 command 实现(参数简单,风险低)
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.DIMANGULAR\" (handent \"" + handle1 + "\") (handent \"" + handle2 + "\") (list " + dimX + " " + dimY + " 0))";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["dimAngular"] = "(" + handle1 + "," + handle2 + ")", ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }


  // ========== Phase 5: 文字与外部参�?==========

  public static Task<object?> EditTextAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var newText = PluginRuntime.GetRequiredString(parameters, "newText");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var dbText = CivilObjectUtils.GetRequiredObject<DBText>(transaction, id, OpenMode.ForWrite);
      dbText.TextString = newText;
      return (object?)new Dictionary<string, object?> { ["editedText"] = handle, ["newText"] = newText };
    });
  }

  public static Task<object?> ScaleTextAsync(JsonObject? parameters)
  {
    var handles = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    var scaleFactor = PluginRuntime.GetRequiredDouble(parameters, "scaleFactor");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var scaled = new List<string>();
      if (handles != null)
      {
        foreach (var n in handles)
        {
          var h = n?.GetValue<string>();
          if (string.IsNullOrEmpty(h)) continue;
          var id = HandleToObjectId(database, h);
          if (id.IsNull) continue;
          var txt = CivilObjectUtils.GetRequiredObject<DBText>(transaction, id, OpenMode.ForWrite);
          txt.Height *= scaleFactor;
          scaled.Add(h);
        }
      }
      return (object?)new Dictionary<string, object?> { ["scaleText"] = scaleFactor, ["scaled"] = scaled };
    });
  }

  public static Task<object?> AttachXrefAsync(JsonObject? parameters)
  {
    var filePath = PluginRuntime.GetRequiredString(parameters, "filePath");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!File.Exists(filePath))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "File not found: " + filePath);
      var blockName = Path.GetFileNameWithoutExtension(filePath);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForWrite);
      ObjectId xrefId;
      if (blockTable.Has(blockName))
        xrefId = blockTable[blockName];
      else
        xrefId = database.AttachXref(filePath, blockName);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      using var br = new BlockReference(new Point3d(x, y, 0), xrefId);
      br.ScaleFactors = new Scale3d(scale, scale, scale);
      br.Rotation = rotation;
      var newId = modelSpace.AppendEntity(br);
      transaction.AddNewlyCreatedDBObject(br, true);
      var created = CivilObjectUtils.GetRequiredObject<BlockReference>(transaction, newId, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["xref"] = filePath, ["handle"] = CivilObjectUtils.GetHandle(created) };
    });
  }

  // ========== Phase 6: 查询(API 版,2026-07-31 重写,替代 DIST/AREA 命令) ==========

  public static Task<object?> MeasureDistAsync(JsonObject? parameters)
  {
    var x1 = PluginRuntime.GetRequiredDouble(parameters, "x1"); var y1 = PluginRuntime.GetRequiredDouble(parameters, "y1");
    var x2 = PluginRuntime.GetRequiredDouble(parameters, "x2"); var y2 = PluginRuntime.GetRequiredDouble(parameters, "y2");
    var z1 = PluginRuntime.GetOptionalDouble(parameters, "z1") ?? 0.0;
    var z2 = PluginRuntime.GetOptionalDouble(parameters, "z2") ?? 0.0;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dx = x2 - x1; var dy = y2 - y1; var dz = z2 - z1;
      var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
      var horizontal = Math.Sqrt(dx * dx + dy * dy);
      return (object?)new Dictionary<string, object?>
      {
        ["distance"] = distance,
        ["horizontal"] = horizontal,
        ["deltaX"] = dx,
        ["deltaY"] = dy,
        ["deltaZ"] = dz,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> MeasureAreaAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var typeName = ent.GetType().Name;

      // 3D 实体:返回体积(非面积)
      if (ent is Solid3d solid)
      {
        return (object?)new Dictionary<string, object?>
        {
          ["handle"] = handle,
          ["type"] = typeName,
          ["volume"] = solid.MassProperties.Volume,
          ["units"] = CivilObjectUtils.LinearUnits(database) + "3",
          ["note"] = "3D 实体返回体积",
        };
      }

      if (ent is not Curve curve)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Entity is not a curve or 3D solid: " + typeName);
      }

      // Curve 没有直接的 GetArea;封闭曲线可转为 Region 求面积,或按类型分派
      double area = 0;
      if (curve is Polyline pl && pl.Closed)
      {
        // 用鞋带公式(shoelace)计算闭合多段线面积(平面投影)
        var pts = new List<Point2d>();
        for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint2dAt(i));
        if (pts.Count >= 3)
        {
          double sum = 0;
          for (int i = 0; i < pts.Count; i++)
          {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
          }
          area = Math.Abs(sum) / 2.0;
        }
      }
      else if (curve is Circle c)
      {
        area = Math.PI * c.Radius * c.Radius;
      }
      else if (curve is Ellipse el)
      {
        area = Math.PI * el.MajorRadius * el.MinorRadius;
      }
      else
      {
        try
        {
          using var region = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(new DBObjectCollection { curve });
          if (region != null && region.Count > 0)
          {
            var reg = (Autodesk.AutoCAD.DatabaseServices.Region)region[0];
            area = reg.Area;
          }
        }
        catch (System.Exception)
        {
          area = 0;
        }
      }

      return (object?)new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["type"] = typeName,
        ["area"] = area,
        ["units"] = CivilObjectUtils.LinearUnits(database) + "2",
      };
    });
  }

  // ========== Phase 7A: 3D 实体创建(API 版,2026-07-31 新增) ==========

  // 通用:把 Solid3d 追加到模型空间
  private static Dictionary<string, object?> AppendSolid(Transaction transaction, Database database, Solid3d solid, string? layerName)
  {
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

    if (!string.IsNullOrWhiteSpace(layerName))
    {
      var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
      solid.LayerId = layerId;
    }

    var id = modelSpace.AppendEntity(solid);
    transaction.AddNewlyCreatedDBObject(solid, true);
    var created = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, id, OpenMode.ForRead);
    return new Dictionary<string, object?>
    {
      ["handle"] = CivilObjectUtils.GetHandle(created),
      ["type"] = "Solid3d",
    };
  }

  public static Task<object?> CreateBoxAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");
    var width = PluginRuntime.GetRequiredDouble(parameters, "width");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateBox(length, width, height);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

  public static Task<object?> CreateCylinderAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateFrustum(height, radius, radius, radius);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

  public static Task<object?> CreateSphereAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateSphere(radius);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

  public static Task<object?> CreateConeAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateFrustum(height, radius, radius, 0.0);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

  public static Task<object?> CreateWedgeAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");
    var width = PluginRuntime.GetRequiredDouble(parameters, "width");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateWedge(length, width, height);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

  public static Task<object?> CreateTorusAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetOptionalDouble(parameters, "x") ?? 0.0;
    var y = PluginRuntime.GetOptionalDouble(parameters, "y") ?? 0.0;
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;
    var majorRadius = PluginRuntime.GetRequiredDouble(parameters, "majorRadius");
    var minorRadius = PluginRuntime.GetRequiredDouble(parameters, "minorRadius");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var solid = new Solid3d();
      solid.CreateTorus(majorRadius, minorRadius);
      solid.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, z)));
      return AppendSolid(transaction, database, solid, layerName);
    });
  }

// ========== Phase 7: 3D 建模 ==========

  // 从闭合曲线/Region 创建 Region(内存对象,调用方负责 using 释放)
  private static Autodesk.AutoCAD.DatabaseServices.Region? CreateRegionFromEntity(Entity ent)
  {
    if (ent is Autodesk.AutoCAD.DatabaseServices.Region region) return region;
    if (ent is Curve curve)
    {
      using var curves = new DBObjectCollection();
      curves.Add(curve);
      var regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(curves);
      if (regions != null && regions.Count > 0)
      {
        var first = (Autodesk.AutoCAD.DatabaseServices.Region)regions[0];
        for (int i = 1; i < regions.Count; i++) regions[i].Dispose();
        return first;
      }
    }
    return null;
  }

  public static Task<object?> ExtrudeSolidAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var taperAngle = PluginRuntime.GetOptionalDouble(parameters, "taperAngle") ?? 0.0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      using var reg = CreateRegionFromEntity(ent);
      if (reg == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "EXTRUDE requires a closed curve (polyline/circle/ellipse).");
      using var solid = new Solid3d();
      solid.Extrude(reg, height, taperAngle * Math.PI / 180.0);
      var result = AppendSolid(transaction, database, solid, null);
      // EXTRUDE 命令默认删除原闭合曲线(DELOBJ=1),保持一致
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["extrude"] = handle, ["height"] = height, ["solidHandle"] = result["handle"] };
    });
  }

  public static Task<object?> RevolveSolidAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var axisX1 = PluginRuntime.GetRequiredDouble(parameters, "axisX1"); var axisY1 = PluginRuntime.GetRequiredDouble(parameters, "axisY1");
    var axisX2 = PluginRuntime.GetRequiredDouble(parameters, "axisX2"); var axisY2 = PluginRuntime.GetRequiredDouble(parameters, "axisY2");
    var angle = PluginRuntime.GetRequiredDouble(parameters, "angle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      using var reg = CreateRegionFromEntity(ent);
      if (reg == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "REVOLVE requires a closed curve (polyline/circle/ellipse).");
      using var solid = new Solid3d();
      var axisPt = new Point3d(axisX1, axisY1, 0);
      var axisDir = new Vector3d(axisX2 - axisX1, axisY2 - axisY1, 0).GetNormal();
      solid.Revolve(reg, axisPt, axisDir, angle * Math.PI / 180.0);
      var result = AppendSolid(transaction, database, solid, null);
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["revolve"] = handle, ["solidHandle"] = result["handle"] };
    });
  }

  public static Task<object?> LoftSolidAsync(JsonObject? parameters)
  {
    // 2026-08-11 重写：命令壳(SendStringToExecute) → CreateLoftedSolid 真 API（反射确认 25.0.58 存在）
    // 参数：crossSections[handle...] 必填≥2（闭合 Polyline/Circle/Ellipse/闭合Spline）
    //       guides[handle...] 可选引导线 | path handle 可选路径 | keepProfiles bool 默认false(删截面)
    //       ruled?/closed?/draftStart?/draftEnd?(度)/thickness?=1/bothSides?=false
    // 2026-08-11 实测结论：Solid3d.CreateLoftedSolid 在 25.0.58 托管包装内部 NRE（Circle/Region/LoftProfile 三种传法全崩）
    //   → 改用 LoftedSurface.CreateLoftedSurface（放样曲面，真 API）+ Surface.Thicken（加厚成实体，已验证）
    var crossArr = PluginRuntime.GetParameter(parameters, "crossSections") as JsonArray;
    if (crossArr == null || crossArr.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "LOFT requires crossSections array with at least 2 closed curves (polyline/circle/ellipse).");
    var guideArr = PluginRuntime.GetParameter(parameters, "guides") as JsonArray;
    var pathHandle = PluginRuntime.GetOptionalString(parameters, "path");
    var keepProfiles = PluginRuntime.GetOptionalBool(parameters, "keepProfiles") ?? false;
    var ruled = PluginRuntime.GetOptionalBool(parameters, "ruled") ?? false;
    var closed = PluginRuntime.GetOptionalBool(parameters, "closed") ?? false;
    var draftStart = PluginRuntime.GetOptionalDouble(parameters, "draftStart") ?? 0.0;
    var draftEnd = PluginRuntime.GetOptionalDouble(parameters, "draftEnd") ?? 0.0;
    var thickness = PluginRuntime.GetOptionalDouble(parameters, "thickness") ?? 1.0;
    var bothSides = PluginRuntime.GetOptionalBool(parameters, "bothSides") ?? false;
    if (thickness <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "thickness must be > 0.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sections = new Entity[crossArr.Count];
      for (int i = 0; i < crossArr.Count; i++)
      {
        var h = PluginRuntime.JsonNodeToString(crossArr[i]);
        if (string.IsNullOrEmpty(h))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "crossSections[" + i + "] is empty.");
        sections[i] = CivilObjectUtils.GetRequiredObject<Entity>(transaction, HandleToObjectId(database, h), OpenMode.ForRead);
      }
      Entity[]? guides = null;
      if (guideArr != null && guideArr.Count > 0)
      {
        var list = new List<Entity>();
        foreach (var n in guideArr)
        {
          var h = PluginRuntime.JsonNodeToString(n);
          if (string.IsNullOrEmpty(h)) continue;
          list.Add(CivilObjectUtils.GetRequiredObject<Entity>(transaction, HandleToObjectId(database, h), OpenMode.ForRead));
        }
        if (list.Count > 0) guides = list.ToArray();
      }
      Entity? path = null;
      if (!string.IsNullOrEmpty(pathHandle))
        path = CivilObjectUtils.GetRequiredObject<Entity>(transaction, HandleToObjectId(database, pathHandle), OpenMode.ForRead);

      using var lofted = new LoftedSurface();
      var builder = new LoftOptionsBuilder();
      builder.Ruled = ruled;
      builder.Closed = closed;
      builder.DraftStart = draftStart;
      builder.DraftEnd = draftEnd;
      using var opts = builder.ToLoftOptions();
      lofted.CreateLoftedSurface(sections, guides, path, opts);
      using var solid = lofted.Thicken(thickness, bothSides);
      var result = AppendSolid(transaction, database, solid, null);
      // 与 EXTRUDE/REVOLVE 一致：默认删除原截面曲线（DELOBJ=1 语义），keepProfiles=true 保留
      if (!keepProfiles)
      {
        foreach (var n in crossArr)
        {
          var h = PluginRuntime.JsonNodeToString(n);
          if (string.IsNullOrEmpty(h)) continue;
          try
          {
            var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, HandleToObjectId(database, h), OpenMode.ForWrite);
            orig.Erase();
          }
          catch { /* 可能已被自动删除 */ }
        }
      }
      return (object?)new Dictionary<string, object?> { ["loft"] = "done", ["solidHandle"] = result["handle"] };
    });
  }

  public static Task<object?> CreateNurbsSurfaceAsync(JsonObject? parameters)
  {
    // 2026-08-11 新增：NURBS 自由曲面（反射确认 25.0.58 NurbSurface 带参构造 + KnotCollection.Add 可用）
    // 参数：uDegree?/vDegree? 默认3 | uCount/vCount 必填 | points:[[x,y,z]...] 必填(u优先遍历, uCount*vCount个)
    //       weights?:[double...] 默认全1 | layer?
    var uDeg = PluginRuntime.GetOptionalInt(parameters, "uDegree") ?? 3;
    var vDeg = PluginRuntime.GetOptionalInt(parameters, "vDegree") ?? 3;
    var uCnt = PluginRuntime.GetRequiredInt(parameters, "uCount");
    var vCnt = PluginRuntime.GetRequiredInt(parameters, "vCount");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    if (uDeg < 1 || vDeg < 1) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "uDegree/vDegree must be >= 1.");
    if (uCnt < uDeg + 1 || vCnt < vDeg + 1) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "uCount/uCnt must be >= degree+1.");
    var ptsArr = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (ptsArr == null || ptsArr.Count < uCnt * vCnt)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points array must contain uCount*vCount [x,y,z] entries.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ctrlPts = new Point3dCollection();
      for (int i = 0; i < uCnt * vCnt; i++)
      {
        var p = ptsArr[i] as JsonArray;
        if (p == null || p.Count < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points[" + i + "] must be [x,y,z].");
        ctrlPts.Add(new Point3d(p[0].GetValue<double>(), p[1].GetValue<double>(), p[2].GetValue<double>()));
      }
      var weights = new DoubleCollection();
      var wArr = PluginRuntime.GetParameter(parameters, "weights") as JsonArray;
      for (int i = 0; i < uCnt * vCnt; i++)
        weights.Add(wArr != null && i < wArr.Count ? wArr[i].GetValue<double>() : 1.0);
      // Clamped 均匀 knots：前 d+1 个 0、中间均匀、后 d+1 个 1，共 n+d+1 个
      var uKnots = BuildClampedKnots(uCnt, uDeg);
      var vKnots = BuildClampedKnots(vCnt, vDeg);
      using var surf = new Autodesk.AutoCAD.DatabaseServices.NurbSurface(uDeg, vDeg, false, uCnt, vCnt, ctrlPts, weights, uKnots, vKnots);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(layerName))
        surf.LayerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var id = modelSpace.AppendEntity(surf);
      transaction.AddNewlyCreatedDBObject(surf, true);
      var created = CivilObjectUtils.GetRequiredObject<Autodesk.AutoCAD.DatabaseServices.NurbSurface>(transaction, id, OpenMode.ForRead);
      return (object?)new Dictionary<string, object?> { ["nurbsSurface"] = "done", ["handle"] = CivilObjectUtils.GetHandle(created) };
    });
  }

  private static KnotCollection BuildClampedKnots(int n, int d)
  {
    var knots = new KnotCollection();
    for (int i = 0; i <= d; i++) knots.Add(0.0);
    int mid = n - d - 1;
    for (int i = 1; i <= mid; i++) knots.Add((double)i / (mid + 1));
    for (int i = 0; i <= d; i++) knots.Add(1.0);
    return knots;
  }

  public static Task<object?> EditNurbsControlPointsAsync(JsonObject? parameters)
  {
    // 2026-08-11 新增：编辑 NURBS 曲面控制点
    // 参数：handle 必填 | moves:[[u,v,dx,dy,dz]...] 相对移动 | points:[[u,v,x,y,z]...] 绝对设置（优先）
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var ptsArr = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    var movesArr = PluginRuntime.GetParameter(parameters, "moves") as JsonArray;
    if ((ptsArr == null || ptsArr.Count == 0) && (movesArr == null || movesArr.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Need points or moves array.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var surf = CivilObjectUtils.GetRequiredObject<Autodesk.AutoCAD.DatabaseServices.NurbSurface>(transaction, id, OpenMode.ForWrite);
      int edited = 0;
      if (ptsArr != null)
      {
        foreach (var n in ptsArr)
        {
          var p = n as JsonArray;
          if (p == null || p.Count < 5) continue;
          var u = p[0].GetValue<int>(); var v = p[1].GetValue<int>();
          surf.SetControlPointAt(u, v, new Point3d(p[2].GetValue<double>(), p[3].GetValue<double>(), p[4].GetValue<double>()));
          edited++;
        }
      }
      if (movesArr != null)
      {
        foreach (var n in movesArr)
        {
          var p = n as JsonArray;
          if (p == null || p.Count < 5) continue;
          var u = p[0].GetValue<int>(); var v = p[1].GetValue<int>();
          var cur = surf.GetControlPointAt(u, v);
          surf.SetControlPointAt(u, v, cur + new Vector3d(p[2].GetValue<double>(), p[3].GetValue<double>(), p[4].GetValue<double>()));
          edited++;
        }
      }
      return (object?)new Dictionary<string, object?> { ["editNurbsControlPoints"] = "done", ["edited"] = edited };
    });
  }

  public static Task<object?> ThickenSurfaceAsync(JsonObject? parameters)
  {
    // 2026-08-11 新增：NURBS 曲面加厚成实体（薄壁壳）
    // 参数：handle 必填 | thickness 必填(>0) | bothSides? 默认 false
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var thickness = PluginRuntime.GetRequiredDouble(parameters, "thickness");
    var bothSides = PluginRuntime.GetOptionalBool(parameters, "bothSides") ?? false;
    if (thickness <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "thickness must be > 0.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      // 2026-08-11：NurbSurface → Surface 基类（兼容 LoftedSurface 等所有曲面）
      var surf = CivilObjectUtils.GetRequiredObject<Autodesk.AutoCAD.DatabaseServices.Surface>(transaction, id, OpenMode.ForRead);
      using var solid = surf.Thicken(thickness, bothSides);
      var result = AppendSolid(transaction, database, solid, null);
      return (object?)new Dictionary<string, object?> { ["thickenSurface"] = "done", ["solidHandle"] = result["handle"] };
    });
  }

  public static Task<object?> SweepSolidAsync(JsonObject? parameters)
  {
    var profileHandle = PluginRuntime.GetRequiredString(parameters, "profileHandle");
    var pathHandle = PluginRuntime.GetRequiredString(parameters, "pathHandle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileId = HandleToObjectId(database, profileHandle);
      var pathId = HandleToObjectId(database, pathHandle);
      var profileEnt = CivilObjectUtils.GetRequiredObject<Entity>(transaction, profileId, OpenMode.ForRead);
      using var reg = CreateRegionFromEntity(profileEnt);
      if (reg == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "SWEEP requires a closed profile curve.");
      var path = CivilObjectUtils.GetRequiredObject<Curve>(transaction, pathId, OpenMode.ForRead);
      using var solid = new Solid3d();
      solid.CreateSweptSolid(reg, path, new SweepOptions());
      var result = AppendSolid(transaction, database, solid, null);
      var orig = CivilObjectUtils.GetRequiredObject<Entity>(transaction, profileId, OpenMode.ForRead);
      orig.UpgradeOpen(); // 2026-08-12 M5: 同事务已 ForRead, 避免双开
      orig.Erase();
      return (object?)new Dictionary<string, object?> { ["sweep"] = "done", ["solidHandle"] = result["handle"] };
    });
  }

    public static Task<object?> BooleanUnionAsync(JsonObject? parameters)
  {
    var handles = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    if (handles == null || handles.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "booleanUnion requires at least 2 handles.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var solids = new List<Solid3d>();
      foreach (var n in handles)
      {
        var h = n?.GetValue<string>();
        if (string.IsNullOrEmpty(h)) continue;
        var id = HandleToObjectId(database, h);
        solids.Add(CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, id, OpenMode.ForWrite));
      }
      if (solids.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "booleanUnion requires at least 2 solid handles.");
      var first = solids[0];
      for (int i = 1; i < solids.Count; i++)
      {
        first.BooleanOperation(BooleanOperationType.BoolUnite, solids[i]);
        solids[i].Erase(); // 布尔运算后 other 被消耗,删除残留
      }
      return (object?)new Dictionary<string, object?> { ["union"] = "done", ["handle"] = CivilObjectUtils.GetHandle(first) };
    });
  }


    public static Task<object?> BooleanSubtractAsync(JsonObject? parameters)
  {
    var mainHandle = PluginRuntime.GetRequiredString(parameters, "mainHandle");
    var toolHandle = PluginRuntime.GetRequiredString(parameters, "toolHandle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var main = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, mainHandle), OpenMode.ForWrite);
      var tool = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, toolHandle), OpenMode.ForWrite);
      main.BooleanOperation(BooleanOperationType.BoolSubtract, tool);
      tool.Erase(); // 布尔运算后 tool 被消耗
      return (object?)new Dictionary<string, object?> { ["subtract"] = mainHandle };
    });
  }


  public static Task<object?> Move3dAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var dx = PluginRuntime.GetRequiredDouble(parameters, "dx");
    var dy = PluginRuntime.GetRequiredDouble(parameters, "dy");
    var dz = PluginRuntime.GetRequiredDouble(parameters, "dz");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
      ent.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, dz)));
      return (object?)new Dictionary<string, object?> { ["move3d"] = handle };
    });
  }

  public static Task<object?> Mirror3dAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var p1x = PluginRuntime.GetRequiredDouble(parameters, "p1x"); var p1y = PluginRuntime.GetRequiredDouble(parameters, "p1y"); var p1z = PluginRuntime.GetRequiredDouble(parameters, "p1z");
    var p2x = PluginRuntime.GetRequiredDouble(parameters, "p2x"); var p2y = PluginRuntime.GetRequiredDouble(parameters, "p2y"); var p2z = PluginRuntime.GetRequiredDouble(parameters, "p2z");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
      var p1 = new Point3d(p1x, p1y, p1z);
      var p2 = new Point3d(p2x, p2y, p2z);
      // 以 p1-p2 线段为镜像轴(XY 平面内)
      var mirrorVec = (p2 - p1).GetNormal();
      var normal = mirrorVec.CrossProduct(Vector3d.ZAxis).GetNormal();
      var plane = new Plane(p1, normal);
      ent.TransformBy(Matrix3d.Mirroring(plane));
      return (object?)new Dictionary<string, object?> { ["mirror3d"] = handle };
    });
  }

  public static Task<object?> Rotate3dAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var ax = PluginRuntime.GetRequiredDouble(parameters, "ax"); var ay = PluginRuntime.GetRequiredDouble(parameters, "ay"); var az = PluginRuntime.GetRequiredDouble(parameters, "az");
    var bx = PluginRuntime.GetRequiredDouble(parameters, "bx"); var by = PluginRuntime.GetRequiredDouble(parameters, "by"); var bz = PluginRuntime.GetRequiredDouble(parameters, "bz");
    var angleDeg = PluginRuntime.GetRequiredDouble(parameters, "angle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
      var axisStart = new Point3d(ax, ay, az);
      var axisEnd = new Point3d(bx, by, bz);
      var axis = (axisEnd - axisStart).GetNormal();
      ent.TransformBy(Matrix3d.Rotation(angleDeg * Math.PI / 180.0, axis, axisStart));
      return (object?)new Dictionary<string, object?> { ["rotate3d"] = handle };
    });
  }

  public static Task<object?> Array3dAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var type = PluginRuntime.GetRequiredString(parameters, "type"); // rect | polar
    var count = PluginRuntime.GetRequiredInt(parameters, "count");
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows") ?? 1;
    var cols = PluginRuntime.GetOptionalInt(parameters, "cols") ?? 1;
    var levels = PluginRuntime.GetOptionalInt(parameters, "levels") ?? 1;
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 1.0;
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 1.0;
    var dz = PluginRuntime.GetOptionalDouble(parameters, "dz") ?? 1.0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      var newHandles = new List<string>();
      if (type == "polar")
      {
        // 绕 Z 轴(过原点)旋转 count 份
        for (int i = 1; i < count; i++)
        {
          var angle = (Math.PI * 2.0 * i) / count;
          var clone = (Entity)ent.Clone();
          clone.TransformBy(Matrix3d.Rotation(angle, Vector3d.ZAxis, Point3d.Origin));
          var newId = modelSpace.AppendEntity(clone);
          transaction.AddNewlyCreatedDBObject(clone, true);
          newHandles.Add(CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead)));
        }
      }
      else
      {
        for (int l = 0; l < levels; l++)
          for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
              if (l == 0 && r == 0 && c == 0) continue;
              var clone = (Entity)ent.Clone();
              clone.TransformBy(Matrix3d.Displacement(new Vector3d(dx * c, dy * r, dz * l)));
              var newId = modelSpace.AppendEntity(clone);
              transaction.AddNewlyCreatedDBObject(clone, true);
              newHandles.Add(CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Entity>(transaction, newId, OpenMode.ForRead)));
            }
      }
      return (object?)new Dictionary<string, object?> { ["array3d"] = handle, ["createdHandles"] = newHandles };
    });
  }

  public static Task<object?> SliceSolidAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var p1x = PluginRuntime.GetRequiredDouble(parameters, "p1x"); var p1y = PluginRuntime.GetRequiredDouble(parameters, "p1y");
    var p2x = PluginRuntime.GetRequiredDouble(parameters, "p2x"); var p2y = PluginRuntime.GetRequiredDouble(parameters, "p2y");
    var p3x = PluginRuntime.GetRequiredDouble(parameters, "p3x"); var p3y = PluginRuntime.GetRequiredDouble(parameters, "p3y");
    var keepBoth = PluginRuntime.GetOptionalBool(parameters, "keepBoth") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var solid = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, id, OpenMode.ForWrite);
      var p1 = new Point3d(p1x, p1y, 0);
      var p2 = new Point3d(p2x, p2y, 0);
      var p3 = new Point3d(p3x, p3y, 0);
      var plane = new Plane(p1, p2, p3);
      if (keepBoth)
      {
        // Slice(plane, true) 返回另一部分(内存对象),需入库;当前实体保留一侧
        var other = solid.Slice(plane, true);
        if (other != null)
        {
          var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
          var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
          var newId = modelSpace.AppendEntity(other);
          transaction.AddNewlyCreatedDBObject(other, true);
          var created = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, newId, OpenMode.ForRead);
          return (object?)new Dictionary<string, object?> { ["slice"] = handle, ["createdHandle"] = CivilObjectUtils.GetHandle(created) };
        }
      }
      else
      {
        solid.Slice(plane);
      }
      return (object?)new Dictionary<string, object?> { ["slice"] = handle };
    });
  }

  public static Task<object?> SectionPlaneAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.SECTIONPLANE\" (handent \"" + handle + "\") \"\" (list 0 0 0) (list 1 0 0) (list 0 1 0) \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["sectionPlane"] = handle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> InterferenceCheckAsync(JsonObject? parameters)
  {
    var handle1 = PluginRuntime.GetRequiredString(parameters, "handle1");
    var handle2 = PluginRuntime.GetRequiredString(parameters, "handle2");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var s1 = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, handle1), OpenMode.ForRead);
      var s2 = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, handle2), OpenMode.ForRead);
      var interferes = s1.CheckInterference(s2);
      return (object?)new Dictionary<string, object?> { ["interference"] = interferes };
    });
  }

  public static Task<object?> ConvertToSurfaceAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.CONVTOSURFACE\" (handent \"" + handle + "\") \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["convToSurface"] = handle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> ConvertToSolidAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.CONVTOSOLID\" (handent \"" + handle + "\") \"\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["convToSolid"] = handle, ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> RenderSceneAsync(JsonObject? parameters)
  {
    // 2026-08-07 P1-5: App.Invoke 后台线程全坏 → SendStringToExecute 入队;返回语义变化:无同步结果。
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) return (object?)new Dictionary<string, object?> { ["error"] = "no active document" };
      var lisp = "(command \"_.RENDER\")";
      doc.SendStringToExecute(lisp + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["render"] = "started", ["status"] = "queued", ["note"] = "命令已入队,异步执行(无同步结果)" };
    });
  }

  public static Task<object?> AssignMaterialAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var materialName = PluginRuntime.GetRequiredString(parameters, "materialName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForWrite);
      ent.Material = materialName;
      return (object?)new Dictionary<string, object?> { ["material"] = materialName };
    });
  }

  // 2026-08-07 P1: 切换视觉样式(VSCURRENT 命令,后台线程安全入队)
  public static Task<object?> SetVisualStyleAsync(JsonObject? parameters)
  {
    var style = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var opt = style.ToLowerInvariant() switch
      {
        "xray" or "x射线" or "x-ray" or "x光" => "_X",
        "wireframe" or "线框" or "3d线框" => "_3",
        "2dwireframe" or "2d线框" or "二维线框" => "_2",
        "hidden" or "隐藏" or "三维隐藏" => "_H",
        "realistic" or "真实" => "_R",
        "conceptual" or "概念" => "_C",
        "shaded" or "着色" => "_S",
        "shadededges" or "带边缘着色" or "着色带边缘" => "_E",
        "gray" or "灰度" or "灰度渐变" => "_G",
        "sketchy" or "勾画" => "_K",
        _ => style
      };
      doc.Editor.WriteMessage("\nVSCURRENT " + opt + " 已入队");
      doc.SendStringToExecute("VSCURRENT " + opt + " ", true, false, false);
      return (object?)new Dictionary<string, object?> { ["style"] = style, ["command"] = "VSCURRENT " + opt, ["note"] = "命令已入队,稍后生效" };
    });
  }

  // 2026-08-07 P1: 按图层枚举 ModelSpace 实体(含坐标)- CASS 高程点/范围线导出场景
  public static Task<object?> GetEntitiesByLayerAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetRequiredString(parameters, "layer");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 500;
    // 2026-08-10: offset 分页--配合 maxCount 分批取全量(原来只能取前 N 条,大图层数据拿不全)
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;
    // 2026-08-13: z 范围过滤（服务端筛异常点，AI 不用拉全量被截断）——如 minZ=-1 maxZ=3 找异常低点
    var minZ = PluginRuntime.GetOptionalDouble(parameters, "minZ");
    var maxZ = PluginRuntime.GetOptionalDouble(parameters, "maxZ");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var results = new List<Dictionary<string, object?>>();
      int skipped = 0;
      foreach (ObjectId id in ms!)
      {
        if (results.Count >= maxCount) break;
        var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
        if (ent == null || ent.Layer != layer) continue;
        if (skipped++ < offset) continue;
        if (minZ != null || maxZ != null)
        {
          var pos = GetEntityPosition(ent);
          if (pos == null) continue; // 无位置实体在 z 过滤下跳过（无法判断 z）
          if (minZ != null && pos.Value.Z < minZ.Value) continue;
          if (maxZ != null && pos.Value.Z > maxZ.Value) continue;
        }
        var info = new Dictionary<string, object?>
        {
          ["handle"] = ent.Handle.ToString(),
          ["type"] = ent.GetRXClass().Name,
          ["layer"] = ent.Layer
        };
        var pos2 = GetEntityPosition(ent);
        if (pos2 != null) { info["x"] = pos2.Value.X; info["y"] = pos2.Value.Y; info["z"] = pos2.Value.Z; }
        if (ent is DBText t1) info["text"] = t1.TextString;
        else if (ent is MText t2) info["text"] = t2.Contents;
        results.Add(info);
      }
      return (object?)new Dictionary<string, object?>
      {
        ["count"] = results.Count,
        ["offset"] = offset,
        ["truncated"] = results.Count >= maxCount,
        ["entities"] = results
      };
    });
  }

  // 2026-08-07 P1: 按块名枚举 ModelSpace 块参照(含插入点坐标)- CASS 高程点块场景
  public static Task<object?> GetEntitiesByBlockAsync(JsonObject? parameters)
  {
    var block = PluginRuntime.GetRequiredString(parameters, "blockName");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 500;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var results = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in ms!)
      {
        if (results.Count >= maxCount) break;
        if (transaction.GetObject(id, OpenMode.ForRead, false) is BlockReference br && br.Name == block)
        {
          results.Add(new Dictionary<string, object?>
          {
            ["handle"] = br.Handle.ToString(),
            ["type"] = "BlockReference",
            ["layer"] = br.Layer,
            ["x"] = br.Position.X,
            ["y"] = br.Position.Y,
            ["z"] = br.Position.Z
          });
        }
      }
      return (object?)new Dictionary<string, object?>
      {
        ["count"] = results.Count,
        ["truncated"] = results.Count >= maxCount,
        ["entities"] = results
      };
    });
  }

  // 2026-08-07 P1: 选中集完整几何快照(跨图纸重建 / 复杂任务防丢选中)
  // 2026-08-07 框架: 数据出口--选中集完整几何写文件(大数据不经过 LLM 上下文,AI readFile 摘要)
  // 2026-08-08: 程序化选中--把指定 handle 设为 CAD 当前选择集(用户能看到虚线选中)
  // 2026-08-19: batch read block attributes by layer (getBlocksByLayer)
  // why: getEntitiesByLayer returns coords/text only, NOT block attrs;
  //      getBlockInfo reads attrs but ONE handle per call (758 points => 60+ calls).
  //      this returns all blocks on a layer with attrs in ONE call.
  public static Task<object?> GetBlocksByLayerAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetRequiredString(parameters, "layer");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 500;
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;
    var blockName = PluginRuntime.GetOptionalString(parameters, "blockName"); // optional filter
    var minZ = PluginRuntime.GetOptionalDouble(parameters, "minZ");
    var maxZ = PluginRuntime.GetOptionalDouble(parameters, "maxZ");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var results = new List<Dictionary<string, object?>>();
      int skipped = 0;
      foreach (ObjectId id in ms!)
      {
        if (results.Count >= maxCount) break;
        var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
        if (ent == null || ent.Layer != layer) continue;
        if (ent is not BlockReference br) continue;
        if (!string.IsNullOrEmpty(blockName) && br.Name != blockName) continue;
        if (skipped++ < offset) continue;
        if (minZ != null || maxZ != null)
        {
          var z = br.Position.Z;
          if (minZ != null && z < minZ.Value) continue;
          if (maxZ != null && z > maxZ.Value) continue;
        }
        var info = new Dictionary<string, object?>
        {
          ["handle"] = br.Handle.ToString(),
          ["type"] = "BlockReference",
          ["layer"] = br.Layer,
          ["blockName"] = br.Name,
          ["x"] = br.Position.X,
          ["y"] = br.Position.Y,
          ["z"] = br.Position.Z,
          ["rotation"] = br.Rotation,
        };
        // attributes (ATTRIB tag/text) - elevation often stored here
        var atts = new List<Dictionary<string, object?>>();
        foreach (ObjectId attId in br.AttributeCollection)
        {
          var att = transaction.GetObject(attId, OpenMode.ForRead, false) as AttributeReference;
          if (att == null) continue;
          atts.Add(new Dictionary<string, object?> { ["tag"] = att.Tag, ["text"] = att.TextString });
        }
        info["attributes"] = atts;
        // block definition texts (some CAD point blocks store value in def text)
        var defTexts = new List<string>();
        var btr = transaction.GetObject(br.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
        if (btr != null)
        {
          foreach (ObjectId eid in btr)
          {
            var e = transaction.GetObject(eid, OpenMode.ForRead, false) as Entity;
            if (e == null) continue;
            if (e is DBText dt) defTexts.Add(dt.TextString);
            else if (e is MText mt) defTexts.Add(mt.Contents);
          }
        }
        info["blockTexts"] = defTexts;
        results.Add(info);
      }
      return (object?)new Dictionary<string, object?>
      {
        ["count"] = results.Count,
        ["offset"] = offset,
        ["truncated"] = results.Count >= maxCount,
        ["blocks"] = results
      };
    });
  }


  public static Task<object?> SelectEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = new List<ObjectId>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (!id.IsNull) ids.Add(id);
        }
        catch { }
      }
      if (ids.Count > 0)
      {
        // 2026-08-08: 改用 LISP sssetfirst 投递(命令上下文里的 SetImpliedSelection 会被 CAD 清掉;
        // SendStringToExecute 投递的 LISP 在命令结束后执行,选择集保持)
                // 2026-08-08 修复: 组码5只匹配单handle, 多handle用 <OR> 组合(原逗号拼接实际失效)
        var selFilter = handles.Count == 1
          ? "'((5 . \"" + handles[0] + "\"))"
          : "'((-4 . \"<OR\") " + string.Join(" ", handles.Select(h => "(5 . \"" + h + "\")")) + " (-4 . \"OR>\"))";
        if (handles.Count > 500)
          return (object?)new Dictionary<string, object?> { ["selected"] = ids.Count, ["total"] = handles.Count, ["error"] = "handles 过多(" + handles.Count + ">500), LISP 过滤器可能被 CAD 截断——建议分批或改用 selectByCriteria" };
        doc.SendStringToExecute("(sssetfirst nil (ssget \"_X\" " + selFilter + ")) ", true, false, false);
      }
      return (object?)new Dictionary<string, object?> { ["selected"] = ids.Count, ["total"] = handles.Count, ["note"] = "已投递选中,切回 CAD 可见虚线(计数=ModelSpace 匹配数, CAD 实际选中可能含冻结/锁定层实体)", ["countSource"] = "modelspace" };
    });
  }

  // 2026-08-08: 按条件筛实体并设为 CAD 选择集(条件可选组合,不硬编码--加条件只需加一个 if)
  public static Task<object?> SelectByCriteriaAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    // 2026-08-13: 多图层支持——layers 数组/逗号分隔字符串均可（与 layer 单值可同时传，合并去重）
    var layers = PluginRuntime.GetOptionalStringArray(parameters, "layers");
    var layerFilter = layers;
    if (layer != null)
      layerFilter = layerFilter == null ? [layer] : layerFilter.Append(layer).ToArray();
    var blockName = PluginRuntime.GetOptionalString(parameters, "blockName");
    var typeName = PluginRuntime.GetOptionalString(parameters, "type");
    // 2026-08-13: 多类型支持——types 数组/逗号分隔字符串（与 type 单值可同时传，合并去重）
    var types = PluginRuntime.GetOptionalStringArray(parameters, "types");
    var typeFilter = types;
    if (typeName != null)
      typeFilter = typeFilter == null ? [typeName] : typeFilter.Append(typeName).ToArray();
    var color = PluginRuntime.GetOptionalInt(parameters, "color");
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? 5000;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var ids = new List<ObjectId>();
      var handles = new List<string>();
      var entries = new List<Dictionary<string, string?>>();  // 2026-08-10: 同步写快照用
      foreach (ObjectId id in ms!)
      {
        if (ids.Count >= maxCount) break;
        var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
        if (ent == null) continue;
        if (layerFilter != null && !layerFilter.Contains(ent.Layer, StringComparer.OrdinalIgnoreCase)) continue;
        if (typeFilter != null && !typeFilter.Any(t => MatchesType(ent, t))) continue;
        if (color != null && ent.ColorIndex != color.Value) continue;
        if (blockName != null && (ent is not BlockReference br || !string.Equals(br.Name, blockName, StringComparison.OrdinalIgnoreCase))) continue;
        ids.Add(id);
        handles.Add(ent.Handle.ToString());
        entries.Add(new Dictionary<string, string?> { { "handle", ent.Handle.ToString() }, { "rxClass", ent.GetRXClass().Name } });
      }
      // 2026-08-10: LISP 投递不触发快照事件 → 主动写快照（否则 selectByCriteria 后 exportSelectionToFile 读到旧数据）
      WriteSelectionSnapshot(doc, entries);
      if (ids.Count > 0)
      {
        // 2026-08-08: LISP sssetfirst 投递(命令上下文 SetImpliedSelection 会被清;ssget 全图按条件过滤)
        // 2026-08-13: 多图层用 DXF 组码 8 逗号分隔(ssget filter 原生支持多值匹配)
        var filters = new List<string>();
        if (layerFilter != null) filters.Add("(8 . \"" + string.Join(",", layerFilter) + "\")");
        if (typeFilter != null) filters.Add("(0 . \"" + string.Join(",", typeFilter.Select(MapTypeToDxf)) + "\")");
        if (color != null) filters.Add("(62 . " + color + ")");
        if (blockName != null) filters.Add("(2 . \"" + blockName + "\")");
        var lisp = "(sssetfirst nil (ssget \"_X\" '(" + string.Join(" ", filters) + ")))";
        doc.SendStringToExecute(lisp + " ", true, false, false);
      }
      return (object?)new Dictionary<string, object?> { ["selected"] = ids.Count, ["handles"] = handles, ["appliedToCad"] = ids.Count > 0, ["filters"] = new { layer, layers, typeName, types, color, blockName }, ["note"] = "已投递选中,切回 CAD 可见虚线(计数=ModelSpace 匹配数, CAD 实际选中可能含冻结/锁定层实体)", ["countSource"] = "modelspace" };
    });
  }

  // 2026-08-13: 类型匹配修复——"AcDbBlockReference".Contains("INSERT")=false 导致 type=INSERT 永远选 0。
  // 改用类型映射: 已知类型按实体类型判断(INSERT/block→BlockReference, TEXT→DBText/MText, POINT→DBPoint...), 未知类型透传 RXClass 包含匹配。
  private static bool MatchesType(Entity ent, string typeName)
  {
    var rx = ent.GetRXClass().Name;
    if (rx.Contains(typeName, StringComparison.OrdinalIgnoreCase)) return true;
    return typeName.ToLowerInvariant() switch
    {
      "insert" or "block" or "blockreference" or "blockref" => ent is BlockReference,
      "text" => ent is DBText or MText,
      "point" => ent is DBPoint,
      "polyline" or "lwpolyline" => ent is Polyline,
      "polyline3d" or "3dpolyline" or "3d polyline" => ent is Polyline3d,
      "line" => ent is Line,
      "circle" => ent is Circle,
      "arc" => ent is Arc,
      "hatch" => ent is Hatch,
      "spline" => ent is Spline,
      "dimension" or "dim" => ent is Dimension,
      _ => false,
    };
  }

  // 2026-08-08: 图纸概况--AI 的心智模型(图层/块/类型分布动态统计,不硬编码任何名字)
  // 2026-08-08: 类型名→DXF 组码 0 映射(常见类型映射 + 未识别透传大写,不硬编码死)
  private static string MapTypeToDxf(string typeName)
  {
    return typeName.ToLowerInvariant() switch
    {
      "circle" => "CIRCLE",
      "polyline" or "lwpolyline" => "LWPOLYLINE",
      "3dpolyline" or "polyline3d" => "POLYLINE",
      "line" => "LINE",
      "text" => "TEXT",
      "mtext" => "MTEXT",
      "block" or "insert" or "blockreference" => "INSERT",
      "arc" => "ARC",
      "point" => "POINT",
      "hatch" => "HATCH",
      "spline" => "SPLINE",
      "dimension" or "dim" => "DIMENSION",
      _ => typeName.ToUpperInvariant(),
    };
  }

  public static Task<object?> GetDrawingOverviewAsync(JsonObject? parameters)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var layers = new Dictionary<string, int>();
      var blocks = new Dictionary<string, int>();
      var types = new Dictionary<string, int>();
      int total = 0;
      foreach (ObjectId id in ms!)
      {
        total++;
        var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
        if (ent == null) continue;
        var layer = ent.Layer;
        layers.TryGetValue(layer, out var lc); layers[layer] = lc + 1;
        var rx = ent.GetRXClass().Name;
        types.TryGetValue(rx, out var tc); types[rx] = tc + 1;
        if (ent is BlockReference br)
        {
          var bn = br.Name;
          blocks.TryGetValue(bn, out var bc); blocks[bn] = bc + 1;
        }
      }
      int selCount = 0;
      try { var sr = doc.Editor.SelectImplied(); selCount = (sr.Status == PromptStatus.OK && sr.Value != null) ? sr.Value.Count : 0; } catch { }
      return (object?)new Dictionary<string, object?>
      {
        ["docName"] = doc.Name,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["totalEntities"] = total,
        ["layers"] = layers.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value),
        ["blockTypes"] = blocks.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value),
        ["entityTypes"] = types.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value),
        ["currentSelectionCount"] = selCount,
      };
    });
  }

  // 2026-08-13: 图层画像——AI 判断"这层画的是什么"的心智模型（治不规范画法）
  // 返回 type 分布/块名 Top/文字抽样+数字比例/z≠0 占比，AI 据此决定过滤条件(types/blockName/layer 组合)，不用瞎猜
  public static Task<object?> AnalyzeLayerAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetRequiredString(parameters, "layer");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var bt = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
      var ms = transaction.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
      var typeCounts = new Dictionary<string, int>();
      var blockNames = new Dictionary<string, int>();
      var textSamples = new List<string>();
      int textTotal = 0, textNumeric = 0;
      int positioned = 0, zNonZero = 0;
      int total = 0;
      foreach (ObjectId id in ms!)
      {
        var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
        if (ent == null || !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
        total++;
        var rx = ent.GetRXClass().Name;
        typeCounts.TryGetValue(rx, out var tc); typeCounts[rx] = tc + 1;
        if (ent is BlockReference br)
        {
          blockNames.TryGetValue(br.Name, out var bc); blockNames[br.Name] = bc + 1;
        }
        if (ent is DBText t1)
        {
          textTotal++;
          if (double.TryParse(t1.TextString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) textNumeric++;
          if (textSamples.Count < 5) textSamples.Add(t1.TextString);
        }
        else if (ent is MText m1)
        {
          textTotal++;
          var txt = m1.Text.Trim();
          if (double.TryParse(txt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) textNumeric++;
          if (textSamples.Count < 5) textSamples.Add(txt);
        }
        var pos = GetEntityPosition(ent);
        if (pos != null) { positioned++; if (pos.Value.Z != 0) zNonZero++; }
      }
      return (object?)new Dictionary<string, object?>
      {
        ["layer"] = layer,
        ["total"] = total,
        ["types"] = typeCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
        ["blockNames"] = blockNames.OrderByDescending(kv => kv.Value).Take(15).ToDictionary(kv => kv.Key, kv => kv.Value),
        ["text"] = new { total = textTotal, numericRatio = textTotal > 0 ? Math.Round((double)textNumeric / textTotal, 2) : 0, samples = textSamples },
        ["z"] = new { positioned, zNonZeroRatio = positioned > 0 ? Math.Round((double)zNonZero / positioned, 2) : 0, note = "z≠0 占比低 → 高程可能在块属性或文字内容里，不要按 z 过滤" },
      };
    });
  }

  // 2026-08-13: 只读文件通道——发布版 AI 的"眼睛"（listDirectory 看清单 / readTextFile 看格式）
  // 安全: 绝对路径 + FileBoundary.AssertReadablePath（L1/L2 敏感黑名单）+ 二进制检测 + 大小/行数限制
  public static Task<object?> ListDirectoryAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!Path.IsPathFullyQualified(path))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {path}");
      var full = Path.GetFullPath(path);
      FileBoundary.AssertReadablePath(full);
      if (!Directory.Exists(full))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Directory not found: {full}");
      var dirs = new List<object?>();
      foreach (var d in Directory.GetDirectories(full))
        dirs.Add(new Dictionary<string, object?> { ["name"] = Path.GetFileName(d), ["dir"] = true });
      var files = new List<object?>();
      foreach (var f in Directory.GetFiles(full))
        files.Add(new Dictionary<string, object?> { ["name"] = Path.GetFileName(f), ["size"] = new FileInfo(f).Length, ["dir"] = false });
      return (object?)new Dictionary<string, object?>
      {
        ["path"] = full,
        ["dirs"] = dirs,
        ["files"] = files,
        ["count"] = dirs.Count + files.Count,
      };
    });
  }

  // 2026-08-13: 共享读（FileShare.ReadWrite）——允许读被 C3D/CAD/Word 打开的文件；锁定/IO 错误转明确错误码
  private static byte[] ReadAllBytesShared(string full)
  {
    try
    {
      using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      var bytes = new byte[fs.Length];
      int read = 0;
      while (read < bytes.Length) read += fs.Read(bytes, read, bytes.Length - read);
      return bytes;
    }
    catch (IOException ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"File is locked by another process: {full} ({ex.Message})");
    }
  }

  public static Task<object?> ReadTextFileAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var maxLines = PluginRuntime.GetOptionalInt(parameters, "maxLines") ?? 20;
    var maxBytes = PluginRuntime.GetOptionalInt(parameters, "maxBytes") ?? 4096;
    // 2026-08-13: startLine 行偏移——AI 分页读大文件（读中段/末尾，不再"放大参数读全文"被截断）
    var startLine = PluginRuntime.GetOptionalInt(parameters, "startLine") ?? 0;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!Path.IsPathFullyQualified(path))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {path}");
      var full = Path.GetFullPath(path);
      FileBoundary.AssertReadablePath(full);
      if (!File.Exists(full))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File not found: {full}");
      var info = new FileInfo(full);
      if (info.Length > 5 * 1024 * 1024)
        throw new JsonRpcDispatchException("CIVIL3D.FILE_TOO_LARGE", $"File too large to read ({info.Length} bytes, limit 5MB): {full}");
      var bytes = ReadAllBytesShared(full);
      if (Array.IndexOf(bytes, (byte)0) >= 0)
        throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", $"Binary file is not readable as text: {full}");
      var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
      var allLines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
      var lines = allLines.Skip(startLine).Take(maxLines).ToList();
      var joined = string.Join("\n", lines);
      var content = joined;
      if (System.Text.Encoding.UTF8.GetByteCount(joined) > maxBytes)
      {
        // 按字节截断（防超长行撑爆上下文）
        var sb = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
          var candidate = sb.Length == 0 ? l : sb.ToString() + "\n" + l;
          if (System.Text.Encoding.UTF8.GetByteCount(candidate) > maxBytes) break;
          sb.Append(sb.Length == 0 ? l : "\n" + l);
        }
        content = sb.ToString();
      }
      return (object?)new Dictionary<string, object?>
      {
        ["path"] = full,
        ["totalBytes"] = bytes.Length,
        ["totalLines"] = allLines.Count,
        ["startLine"] = startLine,
        ["lineCount"] = lines.Count,
        ["truncated"] = content != joined,
        ["content"] = content,
        ["note"] = "文件内容将发送给 AI 模型服务商处理；敏感文件已被黑名单拦截；大文件用 startLine 分页读",
      };
    });
  }

  // 2026-08-13: 用户明示路径写文件（AI 生成笔记/报告到用户指定位置）——安全语义同 exportSelectionToFile
  // 用户说"生成到 X"= 授权写 X；但【只新建不覆盖】已有文件 + 敏感路径禁写 + 可执行扩展名禁写 + 2MB 限制
  public static Task<object?> WriteTextFileAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var content = PluginRuntime.GetOptionalString(parameters, "content") ?? "";
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!Path.IsPathFullyQualified(path))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {path}");
      var full = Path.GetFullPath(path);
      FileBoundary.AssertReadablePath(full); // 敏感路径（config.json/.env/密钥/.git 等）禁写
      var ext = Path.GetExtension(full).ToLowerInvariant();
      var blocked = new HashSet<string> { ".exe", ".bat", ".cmd", ".ps1", ".dll", ".scr", ".vbs", ".js", ".mjs", ".cjs", ".sh", ".py", ".com", ".msi", ".reg", ".wsf", ".jar" };
      if (blocked.Contains(ext))
        throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", $"Extension '{ext}' is not allowed for writing (可执行/脚本文件禁写)");
      if (File.Exists(full))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"File already exists, not overwriting: {full} —— 换新文件名或让用户删除旧文件后重试");
      var bytes = System.Text.Encoding.UTF8.GetBytes(content);
      if (bytes.Length > 2 * 1024 * 1024)
        throw new JsonRpcDispatchException("CIVIL3D.FILE_TOO_LARGE", $"Content too large ({bytes.Length} bytes, limit 2MB)");
      var dir = Path.GetDirectoryName(full);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
      File.WriteAllBytes(full, bytes);
      return (object?)new Dictionary<string, object?> { ["path"] = full, ["created"] = true, ["bytes"] = bytes.Length };
    });
  }

  public static Task<object?> ExportSelectionToFileAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    // 2026-08-08: 格式决策(AI 判断用户意图)--json=完整属性(AI用)/csv|txt=坐标表(用户用)
    var format = (PluginRuntime.GetOptionalString(parameters, "format") ?? "json").ToLowerInvariant();
    var delimiter = PluginRuntime.GetOptionalString(parameters, "delimiter");
    var header = PluginRuntime.GetOptionalBool(parameters, "header") ?? false;
    if (format != "json" && format != "csv" && format != "txt")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "format 仅支持 json/csv/txt: " + format);
    if (string.IsNullOrEmpty(delimiter)) delimiter = format == "csv" ? "," : " ";
    // 2026-08-08: 用户指示的导出 = 已授权写该路径,允许覆盖(writeFile 工具红线不变)--修复"连续导出同路径被拒"
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var selFile = System.IO.Path.Combine(
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
          System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
          "..", "..")),
        "exchange", "_out", "_sel.json");
      var handles = new List<string>();
      string docName = "";
      if (System.IO.File.Exists(selFile))
      {
        try
        {
          var obj = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(selFile));
          if (obj is System.Text.Json.Nodes.JsonObject jo && jo.ContainsKey("items"))
          {
            docName = PluginRuntime.JsonNodeToString(jo["docName"]) ?? "";
            var arr = jo["items"] as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
              foreach (var it in arr)
                if (it is System.Text.Json.Nodes.JsonObject item)
                {
                  var h = PluginRuntime.JsonNodeToString(item["handle"]);
                  if (!string.IsNullOrWhiteSpace(h)) handles.Add(h!);
                }
          }
        }
        catch { }
      }
      var entities = new List<Dictionary<string, object?>>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) continue;
          var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
          if (ent == null) continue;
          var info = new Dictionary<string, object?>
          {
            ["handle"] = handle,
            ["type"] = ent.GetRXClass().Name,
            ["layer"] = ent.Layer,
            ["color"] = ent.Color.ToString(),
          };
          var pos = GetEntityPosition(ent);
          if (pos != null) { info["x"] = pos.Value.X; info["y"] = pos.Value.Y; info["z"] = pos.Value.Z; }
          if (ent is DBText t1) { info["text"] = t1.TextString; info["height"] = t1.Height; }
          else if (ent is MText t2) info["text"] = t2.Contents;
          else if (ent is Polyline pl)
          {
            info["vertexCount"] = pl.NumberOfVertices; info["closed"] = pl.Closed;
            info["area"] = pl.Area; info["length"] = pl.Length;
            var verts = new List<Dictionary<string, double?>>();
            for (int vi = 0; vi < pl.NumberOfVertices; vi++)
              verts.Add(new Dictionary<string, double?> { ["x"] = pl.GetPoint2dAt(vi).X, ["y"] = pl.GetPoint2dAt(vi).Y });
            info["vertices"] = verts;
          }
          else if (ent is Circle ci) { info["diameter"] = ci.Diameter; }
          entities.Add(info);
        }
        catch { }
      }
      if (format == "json")
      {
        var payload = new Dictionary<string, object?> { ["docName"] = docName, ["count"] = entities.Count, ["entities"] = entities };
        FileBoundary.WriteAllTextAtomic(path, System.Text.Json.JsonSerializer.Serialize(payload), System.Text.UTF8Encoding.UTF8, overwrite: true, "json", "csv", "txt");
      }
      else
      {
        // csv/txt: 坐标表(块/文字/圆取位置点;多段线取全部顶点)--用户可直接用/导入
        var sb = new System.Text.StringBuilder();
        if (header) sb.Append("x").Append(delimiter).Append("y").Append(delimiter).Append("z").Append('\n');
        foreach (var e in entities)
        {
          if (e.TryGetValue("vertices", out var vertsObj) && vertsObj is List<Dictionary<string, double?>> verts)
          {
            foreach (var v in verts)
            {
              var x = v.TryGetValue("x", out var xv) ? (xv ?? 0) : 0;
              var y = v.TryGetValue("y", out var yv) ? (yv ?? 0) : 0;
              sb.Append(x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(delimiter)
                .Append(y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(delimiter).Append('0').Append('\n');
            }
          }
          else if (e.TryGetValue("x", out var xv) && e.TryGetValue("y", out var yv))
          {
            // 2026-08-10: 修复 CSV 报"内部错误"--x/y/z 是 double,原代码强转 JsonValue 抛 InvalidCastException(块引用必崩)
            sb.Append(CoordCsv(xv)).Append(delimiter)
              .Append(CoordCsv(yv)).Append(delimiter)
              .Append(e.TryGetValue("z", out var zv) ? CoordCsv(zv) : "0").Append('\n');
          }
        }
        FileBoundary.WriteAllTextAtomic(path, sb.ToString(), System.Text.UTF8Encoding.UTF8, overwrite: true, "csv", "txt", "json");
      }
      return (object?)new Dictionary<string, object?> { ["path"] = path, ["count"] = entities.Count, ["format"] = format };
    });
  }

  // 2026-08-10: CSV 坐标格式化容错(double / JsonValue / null 都处理),防类型强转崩溃
  private static string CoordCsv(object? v)
  {
    return v switch
    {
      double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
      System.Text.Json.Nodes.JsonValue jv => jv.ToString() ?? "0",
      null => "0",
      _ => v.ToString() ?? "0"
    };
  }

  public static Task<object?> GetSelectionDetailAsync(JsonObject? parameters)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var selFile = System.IO.Path.Combine(
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
          System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
          "..", "..")),
        "exchange", "_out", "_sel.json");
      var handles = new List<string>();
      string docName = "";
      if (System.IO.File.Exists(selFile))
      {
        try
        {
          var obj = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(selFile));
          if (obj is System.Text.Json.Nodes.JsonObject jo && jo.ContainsKey("items"))
          {
            docName = PluginRuntime.JsonNodeToString(jo["docName"]) ?? "";
            var arr = jo["items"] as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
              foreach (var it in arr)
                if (it is System.Text.Json.Nodes.JsonObject item)
                {
                  var h = PluginRuntime.JsonNodeToString(item["handle"]);
                  if (!string.IsNullOrWhiteSpace(h)) handles.Add(h!);
                }
          }
        }
        catch { }
      }
      var entities = new List<Dictionary<string, object?>>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) continue;
          var ent = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
          if (ent == null) continue;
          var info = new Dictionary<string, object?>
          {
            ["handle"] = handle,
            ["type"] = ent.GetRXClass().Name,
            ["layer"] = ent.Layer,
            ["color"] = ent.Color.ToString(),
          };
          var pos = GetEntityPosition(ent);
          if (pos != null) { info["x"] = pos.Value.X; info["y"] = pos.Value.Y; info["z"] = pos.Value.Z; }
          if (ent is DBText t1) { info["text"] = t1.TextString; info["height"] = t1.Height; }
          else if (ent is MText t2) info["text"] = t2.Contents;
          else if (ent is Polyline pl)
          {
            info["vertexCount"] = pl.NumberOfVertices; info["closed"] = pl.Closed;
            info["area"] = pl.Area; info["length"] = pl.Length;
            var verts = new List<Dictionary<string, double?>>();
            for (int vi = 0; vi < pl.NumberOfVertices; vi++)
              verts.Add(new Dictionary<string, double?> { ["x"] = pl.GetPoint2dAt(vi).X, ["y"] = pl.GetPoint2dAt(vi).Y });
            info["vertices"] = verts;
          }
          else if (ent is Circle ci) { info["diameter"] = ci.Diameter; }
          entities.Add(info);
        }
        catch { }
      }
      return (object?)new Dictionary<string, object?>
      {
        ["docName"] = docName,
        ["count"] = entities.Count,
        ["entities"] = entities,
      };
    });
  }

  // ===== 2026-08-07 P1: 批量方法(一次处理 N 个,减少工具循环轮次) =====
  public static Task<object?> SetEntitiesColorAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var color = PluginRuntime.GetRequiredInt(parameters, "color");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.ColorIndex = color; ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  // ===== 2026-08-12 P0: 图元属性写（批量，基础 CAD 缺口补齐） =====
  public static Task<object?> SetEntityLayerAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var layer = PluginRuntime.GetRequiredString(parameters, "layer");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layerId = LookupUtils.GetLayerId(database, transaction, layer, strict: true); // 2026-08-12 M3: 用户指定图层必须存在
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.LayerId = layerId; ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed, ["layer"] = layer };
    });
  }

  public static Task<object?> SetEntityLinetypeAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var linetype = PluginRuntime.GetRequiredString(parameters, "linetype");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var linetypeId = LookupUtils.GetLinetypeId(database, transaction, linetype);
      if (linetypeId.IsNull && !linetype.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) && !linetype.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
        return new Dictionary<string, object?> { ["error"] = "linetype not found: " + linetype };
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.LinetypeId = linetypeId; ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed, ["linetype"] = linetype };
    });
  }

  public static Task<object?> SetEntityLineweightAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var lw = PluginRuntime.GetRequiredInt(parameters, "lineweight");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.LineWeight = (LineWeight)lw; ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed, ["lineweight"] = lw };
    });
  }

  public static Task<object?> SetEntityTransparencyAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    // 0-100 百分比: 0=不透明, 100=全透明 → alpha 255-0
    var pct = PluginRuntime.GetRequiredInt(parameters, "transparency");
    var alpha = (byte)(255 - Math.Clamp(pct, 0, 100) * 255 / 100);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.Transparency = new Autodesk.AutoCAD.Colors.Transparency(alpha); ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed, ["transparency"] = pct };
    });
  }

  public static Task<object?> MoveEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    var dx = PluginRuntime.GetRequiredDouble(parameters, "dx");
    var dy = PluginRuntime.GetRequiredDouble(parameters, "dy");
    var dz = PluginRuntime.GetOptionalDouble(parameters, "dz") ?? 0.0;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      var disp = Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Autodesk.AutoCAD.Geometry.Vector3d(dx, dy, dz));
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.TransformBy(disp); ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  public static Task<object?> DeleteEntitiesAsync(JsonObject? parameters)
  {
    var handles = ParseHandleArray(parameters, "handles");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ok = 0; var failed = new List<string>();
      foreach (var handle in handles)
      {
        try
        {
          var id = database.GetObjectId(false, new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber)), 0);
          if (id.IsNull) { failed.Add(handle); continue; }
          var ent = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
          if (ent == null) { failed.Add(handle); continue; }
          ent.Erase(true); ok++;
        }
        catch { failed.Add(handle); }
      }
      return (object?)new Dictionary<string, object?> { ["ok"] = ok, ["failed"] = failed };
    });
  }

  private static List<string> ParseHandleArray(JsonObject? parameters, string name)
  {
    var list = new List<string>();
    if (PluginRuntime.GetParameter(parameters, name) is System.Text.Json.Nodes.JsonArray arr)
      foreach (var it in arr)
      {
        var s = PluginRuntime.JsonNodeToString(it);
        if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
      }
    if (list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", name + " 不能为空(传 handles 数组)");
    return list;
  }

  private static Autodesk.AutoCAD.Geometry.Point3d? GetEntityPosition(Entity ent)
  {
    switch (ent)
    {
      case DBText t: return t.Position;
      case MText m: return m.Location;
      case BlockReference b: return b.Position;
      case Circle c: return c.Center;
      case Polyline p: return new Autodesk.AutoCAD.Geometry.Point3d(p.GetPoint2dAt(0).X, p.GetPoint2dAt(0).Y, 0);
      case Line l: return l.StartPoint;
      default:
        try
        {
          var pts = new Autodesk.AutoCAD.Geometry.Point3dCollection();
          ent.GetGripPoints(pts, new Autodesk.AutoCAD.Geometry.IntegerCollection(), new Autodesk.AutoCAD.Geometry.IntegerCollection());
          return pts.Count > 0 ? pts[0] : null;
        }
        catch { return null; }
    }
  }

  // -------------------------------------------------------------------------
  // probeGeometry (2026-09-09): non-destructive geometry probe. Explodes an entity
  // in memory (never appended to modelspace, never erased) and recursively expands
  // block references to collect the visible geometric primitives (circle=point
  // marker, line/arc/polyline=link, hatch=shape). Used to read the real geometry
  // of Civil 3D subassemblies/assemblies, whose SAC Points/Links/Shapes collections
  // stay empty at runtime for .NET generators.
  // -------------------------------------------------------------------------
  public static Task<object?> ProbeGeometryAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var maxDepth = PluginRuntime.GetOptionalInt(parameters, "maxDepth") ?? 12;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = HandleToObjectId(database, handle);
      var ent = CivilObjectUtils.GetRequiredObject<Entity>(transaction, id, OpenMode.ForRead);
      var collector = new List<Dictionary<string, object?>>();
      ExplodeCollect(ent, collector, 0, maxDepth, new HashSet<string>());
      return (object?)new Dictionary<string, object?>
      {
        ["sourceHandle"] = handle,
        ["sourceType"] = ent.GetRXClass().Name,
        ["items"] = collector,
        ["itemCount"] = collector.Count,
      };
    });
  }

  internal static void ExplodeCollect(Entity ent, List<Dictionary<string, object?>> collector, int depth, int maxDepth, HashSet<string> seen)
  {
    if (depth > maxDepth) return;
    DBObjectCollection? exploded = null;
    try
    {
      exploded = new DBObjectCollection();
      ent.Explode(exploded);
      foreach (object? o in exploded)
      {
        if (o is not Entity e) continue;
        var entry = new Dictionary<string, object?> { ["type"] = e.GetRXClass().Name };
        try { entry["layer"] = e.Layer; } catch { }
        try { entry["color"] = e.Color.ToString(); } catch { }
        CollectEntityGeometry(e, entry);
        collector.Add(entry);
        // recurse into nested block references (in-memory only)
        if (e is BlockReference br)
        {
          var key = "br:" + depth + ":" + br.Handle.Value;
          if (!seen.Add(key)) continue;
          entry["nested"] = new List<Dictionary<string, object?>>();
          var sub = (List<Dictionary<string, object?>>)entry["nested"];
          var prev = collector;
          collector = sub;
          ExplodeCollect(br, collector, depth + 1, maxDepth, seen);
          collector = prev;
        }
      }
    }
    catch { /* entity not explosive - skip */ }
    finally
    {
      exploded?.Dispose();
    }
  }

  internal static void CollectEntityGeometry(Entity e, Dictionary<string, object?> entry)
  {
    try
    {
      switch (e)
      {
        case Circle c:
          entry["center"] = Pt(c.Center);
          entry["radius"] = c.Radius;
          break;
        case Arc a:
          entry["center"] = Pt(a.Center);
          entry["radius"] = a.Radius;
          entry["startAngle"] = a.StartAngle;
          entry["endAngle"] = a.EndAngle;
          break;
        case Line l:
          entry["start"] = Pt(l.StartPoint);
          entry["end"] = Pt(l.EndPoint);
          entry["length"] = l.Length;
          break;
        case Polyline pl:
          var verts = new List<Dictionary<string, object?>>();
          for (int i = 0; i < pl.NumberOfVertices; i++)
          {
            var v = pl.GetPoint2dAt(i);
            verts.Add(new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y });
          }
          entry["vertices"] = verts;
          entry["vertexCount"] = verts.Count;
          entry["closed"] = pl.Closed;
          break;
        case DBPoint dp:
          entry["position"] = Pt(dp.Position);
          break;
        case BlockReference br:
          entry["blockName"] = br.Name;
          entry["position"] = Pt(br.Position);
          break;
        case Hatch h:
          entry["area"] = h.Area;
          break;
      }
    }
    catch { /* geometry not readable - keep type only */ }
  }

  private static Dictionary<string, object?> Pt(Autodesk.AutoCAD.Geometry.Point3d p)
  {
    return new Dictionary<string, object?> { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
  }

}
