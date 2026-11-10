// CivilInfoCommands.cs -- getCivilObjectInfo / getCivilObjectsInfo (2026-11-09).
//
// One entry point that reads ANY object in the drawing: Civil 3D classes (Alignment, Surface,
// Profile, Corridor, ProfileView, SectionView, SampleLineGroup, ViewFrame, MatchLine, MassHaul,
// PointGroup, SurveyNetwork, PipeNetwork, ...) and plain AutoCAD entities (delegated to
// EntityReaders). Works by handle or by the current selection, so no per-class list API is needed
// before reading.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

internal static class CivilInfoCommands
{
  private const int DefaultBatchMax = 50;
  private const int HardBatchMax = 500;

  public static Task<object?> GetCivilObjectInfoAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetOptionalString(parameters, "handle");
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      AcDbObject? obj;
      if (!string.IsNullOrWhiteSpace(handle))
      {
        obj = Resolve(database, transaction, handle!);
        if (obj == null) return new Dictionary<string, object?> { ["error"] = "not found", ["handle"] = handle };
      }
      else if (!string.IsNullOrWhiteSpace(name))
      {
        // no handle: look the object up by its display name across the Civil 3D collections
        obj = FindByName(civilDoc, database, transaction, name!);
        if (obj == null) return new Dictionary<string, object?> { ["error"] = "not found", ["name"] = name };
      }
      else
      {
        obj = FirstSelected(doc, transaction);
        if (obj == null) return new Dictionary<string, object?>
        {
          ["error"] = "nothing selected",
          ["hint"] = "pass handle, pass name, or select one object in CAD",
        };
      }
      return CivilObjectReaders.Read(obj, transaction);
    });
  }

  public static Task<object?> GetCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? DefaultBatchMax;
    if (maxCount <= 0) maxCount = DefaultBatchMax;
    if (maxCount > HardBatchMax) maxCount = HardBatchMax;
    var handles = PluginRuntime.GetOptionalStringArray(parameters, "handles");

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var wanted = new List<string>();
      string source;
      if (handles != null && handles.Length > 0) { source = "handles"; wanted.AddRange(handles); }
      else
      {
        source = "selection";
        try
        {
          var res = doc.Editor.SelectImplied();
          if (res.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK && res.Value != null)
            foreach (var id in res.Value.GetObjectIds()) if (!id.IsNull) wanted.Add(id.Handle.ToString());
        }
        catch { }
      }

      var items = new List<Dictionary<string, object?>>();
      var failed = new List<object>();
      var truncated = false;
      foreach (var h in wanted)
      {
        if (items.Count >= maxCount) { truncated = true; break; }
        var obj = Resolve(database, transaction, h);
        if (obj == null) { failed.Add(new Dictionary<string, object?> { ["handle"] = h, ["error"] = "not found" }); continue; }
        try { items.Add(CivilObjectReaders.Read(obj, transaction)); }
        catch (System.Exception ex) { failed.Add(new Dictionary<string, object?> { ["handle"] = h, ["error"] = ex.GetType().Name + ": " + ex.Message }); }
      }

      return new Dictionary<string, object?>
      {
        ["source"] = source,
        ["requested"] = wanted.Count,
        ["count"] = items.Count,
        ["truncated"] = truncated,
        ["maxCount"] = maxCount,
        ["items"] = items,
        ["failed"] = failed,
      };
    });
  }

  // ------------------------------------------------------------------ name lookup

  /// <summary>
  /// Find a Civil 3D object by display name. Civil objects are AutoCAD entities, so model space
  /// covers surfaces/alignments/views; Show-All collections are also probed for non-entity objects
  /// (point groups, survey networks, ...). See all 20000 objects at most -- a lookup is not a scan tool.
  /// </summary>
  private static AcDbObject? FindByName(CivilDocument civilDoc, Database database, Transaction transaction, string name)
  {
    var seen = new HashSet<ObjectId>();
    var budget = 20000;

    // 2026-11-09: the model-space scan was removed. Walking every entity and reading "Name" off it
    // crashed AutoCAD 2025 twice in a row (native crash while a C3D view object was open -- not a
    // managed exception, the TCP bridge died mid-request). Name lookup now covers only the
    // collections reached through documented APIs below; everything else must pass a handle.

    foreach (var id in CollectionIds(civilDoc, transaction))
    {
      if (budget-- <= 0) break;
      if (!seen.Add(id)) continue;
      var match = MatchName(transaction, id, name);
      if (match != null) return match;
    }

    return null;
  }

  private static AcDbObject? MatchName(Transaction transaction, ObjectId id, string name)
  {
    try
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead);
      if (obj == null) return null;
      var candidate = CivilObjectUtils.GetName(obj);
      return string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) ? obj : null;
    }
    catch { return null; }
  }


  /// <summary>
  /// Object ids worth name-matching, limited to lookups this codebase already uses.
  /// DO NOT go back to reflecting over every Get*Ids() on CivilDocument: that crashed AutoCAD
  /// 2025 outright (native crash, no managed exception, the TCP bridge died mid-request).
  /// </summary>
  private static List<ObjectId> CollectionIds(CivilDocument civilDoc, Transaction transaction)
  {
    var ids = new List<ObjectId>();

    try { foreach (ObjectId id in civilDoc.GetSurfaceIds()) ids.Add(id); } catch { }

    try
    {
      foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
      {
        ids.Add(alignmentId);
        try
        {
          var alignment = (Autodesk.Civil.DatabaseServices.Alignment)transaction.GetObject(alignmentId, OpenMode.ForRead);
          foreach (ObjectId profileId in alignment.GetProfileIds()) ids.Add(profileId);
          // sample-line groups and profile views are children of the alignment too
          try { foreach (ObjectId groupId in alignment.GetSampleLineGroupIds()) ids.Add(groupId); } catch { }
          try { foreach (ObjectId viewId in alignment.GetProfileViewIds()) ids.Add(viewId); } catch { }
        }
        catch { }
      }
    }
    catch { }

    try { foreach (ObjectId corridorId in civilDoc.CorridorCollection) ids.Add(corridorId); } catch { }


    try
    {
      foreach (ObjectId siteId in civilDoc.GetSiteIds())
      {
        ids.Add(siteId);
        try
        {
          var site = (Autodesk.Civil.DatabaseServices.Site)transaction.GetObject(siteId, OpenMode.ForRead);
          foreach (ObjectId parcelId in site.GetParcelIds()) ids.Add(parcelId);
        }
        catch { }
      }
    }
    catch { }

    return ids;
  }

  private static AcDbObject? Resolve(Database database, Transaction transaction, string handle)
  {
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
      return null;
    ObjectId id;
    try { id = database.GetObjectId(false, new Handle(value), 0); }
    catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    if (id.IsNull) return null;
    try { return transaction.GetObject(id, OpenMode.ForRead); }
    catch { return null; }
  }

  private static AcDbObject? FirstSelected(Autodesk.AutoCAD.ApplicationServices.Document doc, Transaction transaction)
  {
    try
    {
      var res = doc.Editor.SelectImplied();
      if (res.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || res.Value == null) return null;
      foreach (var id in res.Value.GetObjectIds())
      {
        if (id.IsNull) continue;
        var obj = transaction.GetObject(id, OpenMode.ForRead);
        if (obj != null) return obj;
      }
    }
    catch { }
    return null;
  }
}
