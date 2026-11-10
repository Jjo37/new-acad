// EntityInfoCommands.cs -- getEntityInfo / getEntitiesInfo on top of EntityReaders (2026-11-09).
//
// getEntityInfo kept its wire contract: {handle} in, one flat object out, and "not found" for a
// handle that does not resolve (including erased objects -- an erased handle used to escape as
// INTERNAL_ERROR before 2026-11-04, see the comment history in AcadCommands.cs).
//
// What changed: the body is now EntityReaders.Read(), so every entity type is covered by a
// dedicated reader (or the guarded reflection fallback) and every entity carries the common
// properties the old implementation never filled (linetype / lineweight / transparency /
// visible / extents / xdata flags ...).

using Autodesk.AutoCAD.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class EntityInfoCommands
{
  private const int DefaultBatchMax = 50;
  private const int HardBatchMax = 500;

  public static Task<object?> GetEntityInfoAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var includeGeometry = GetBool(parameters, "includeVertices", true);
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var handleValue))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", Loc.Tk("error.handleBadFormat", ("handle", handle)));

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var ent = Resolve(database, transaction, handleValue, out var missing);
      if (ent == null) return missing;
      var info = EntityReaders.Read(ent, transaction);
      if (!includeGeometry) StripHeavyCollections(info);
      return info;
    });
  }

  /// <summary>Batch read: {handles?: string[], maxCount?, includeVertices?} — no handles = current selection.</summary>
  public static Task<object?> GetEntitiesInfoAsync(JsonObject? parameters)
  {
    var includeGeometry = GetBool(parameters, "includeVertices", true);
    var maxCount = PluginRuntime.GetOptionalInt(parameters, "maxCount") ?? DefaultBatchMax;
    if (maxCount <= 0) maxCount = DefaultBatchMax;
    if (maxCount > HardBatchMax) maxCount = HardBatchMax;
    var handles = PluginRuntime.GetOptionalStringArray(parameters, "handles");

    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var wanted = new List<string>();
      string source;
      if (handles != null && handles.Length > 0)
      {
        source = "handles";
        wanted.AddRange(handles);
      }
      else
      {
        source = "selection";
        try
        {
          var res = doc.Editor.SelectImplied();
          if (res.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK && res.Value != null)
            foreach (var id in res.Value.GetObjectIds())
              if (!id.IsNull) wanted.Add(id.Handle.ToString());
        }
        catch { /* no document / no active selection view */ }
      }

      var items = new List<Dictionary<string, object?>>();
      var failed = new List<object>();
      var truncated = false;
      foreach (var h in wanted)
      {
        if (items.Count >= maxCount) { truncated = true; break; }
        if (!long.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hv))
        {
          failed.Add(new Dictionary<string, object?> { ["handle"] = h, ["error"] = "bad handle format" });
          continue;
        }
        var ent = Resolve(database, transaction, hv, out _);
        if (ent == null)
        {
          failed.Add(new Dictionary<string, object?> { ["handle"] = h, ["error"] = "not found" });
          continue;
        }
        var info = EntityReaders.Read(ent, transaction);
        if (!includeGeometry) StripHeavyCollections(info);
        items.Add(info);
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

  // ------------------------------------------------------------------ helpers

  private static Entity? Resolve(Database database, Transaction transaction, long handleValue, out Dictionary<string, object?> notFound)
  {
    notFound = new Dictionary<string, object?> { ["error"] = "not found", ["handle"] = handleValue.ToString("X") };
    Autodesk.AutoCAD.DatabaseServices.ObjectId id;
    try { id = database.GetObjectId(false, new Handle(handleValue), 0); }
    catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    if (id.IsNull) return null;
    try { return transaction.GetObject(id, OpenMode.ForRead) as Entity; }
    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (CivilObjectUtils.IsErasedException(ex))
    {
      notFound["erased"] = true;
      return null;
    }
    catch { return null; }
  }

  /// <summary>Drop the big arrays when the caller only wants a summary.</summary>
  private static void StripHeavyCollections(Dictionary<string, object?> info)
  {
    foreach (var key in new[] { "vertices", "bulges", "controlPoints", "fitPoints", "loops" })
      if (info.ContainsKey(key)) info.Remove(key);
  }

  private static bool GetBool(JsonObject? parameters, string name, bool fallback)
  {
    try
    {
      var node = parameters?[name];
      if (node == null) return fallback;
      return bool.TryParse(PluginRuntime.JsonNodeToString(node), out var value) ? value : fallback;
    }
    catch { return fallback; }
  }
}
