using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Per-action undo journal. 2026-10-23 (ADR 0008): snapshots live in a separate in-memory Database,
/// never in the user's drawing (0007 kept them on a HANK_UNDO_STASH layer, which real-machine
/// testing proved unsafe: an ordinary deleteEntities wiped them, and a restart left orphan entities
/// in the DWG while the in-memory journal was gone).
///
/// Contract shared by plugin / server / panel / tests:
///   revertible          true|false
///   revertBlockedReason ""|no-inverse|unsupported-object|expired|handle-stale
///   revertBlockedText   one human sentence
/// A revert that moves nothing must report ok=false with a reason -- never a silent success.
///
/// Scope we promise: plain AutoCAD entities only. Civil 3D objects are reported as
/// unsupported-object instead of pretending they can be restored.
/// </summary>
public static class UndoJournal
{
  public sealed class Snapshot
  {
    public string Handle = "";
    public string StashHandle = "";
    public string Kind = "";          // deleted | modified
    public string Layer = "";
    public string Type = "";
  }

  public sealed class Entry
  {
    public long Seq;
    public string Method = "";
    public string TaskId = "";
    public DateTime At = DateTime.UtcNow;
    public bool Revertible = true;
    public string BlockedReason = "";
    public string BlockedText = "";
    public string Summary = "";
    public List<string> Created = new();
    public List<Snapshot> Snapshots = new();
  }

  private const int MaxEntries = 200;
  private const int MaxTombstones = 50;
  private const int MaxSnapshots = 500;

  private static readonly List<Entry> entries = new();
  private static long seqCounter;

  private static Database? stashDb;
  private static ObjectId stashSpace = ObjectId.Null;
  private static int stashObjectCount;

  /// <summary>Handles belong to one drawing: a journal must never survive a document switch.</summary>
  public static void Clear()
  {
    lock (entries) entries.Clear();
    DisposeStash();
  }

  private static readonly string[] CreatePrefixes = { "create", "add", "insert", "import", "draw", "place", "copy" };
  private static readonly string[] DeletePrefixes = { "delete", "erase", "purge", "overkill", "remove", "clear", "drop" };
  private static readonly string[] ModifyPrefixes = { "set", "move", "rotate", "scale", "edit", "rename", "transform", "align", "offset", "array", "copy", "mirror", "join", "trim", "extend", "fillet", "chamfer", "break", "explode" };
  private static readonly string[] NoInversePrefixes = { "purge", "overkill", "executecommand", "runlisp", "write", "save", "export", "import", "publish" };

  public static bool IsCreate(string method) => StartsWithAny(method, CreatePrefixes);
  public static bool IsDelete(string method) => StartsWithAny(method, DeletePrefixes);
  public static bool IsModify(string method) => StartsWithAny(method, ModifyPrefixes);
  public static bool HasNoInverse(string method) => StartsWithAny(method, NoInversePrefixes);

  public static string BlockedText(string reason) => reason switch
  {
    "no-inverse" => "this action has no inverse (purge / script / file write), so it cannot be reverted",
    "unsupported-object" => "Civil 3D objects and some entity types cannot be snapshotted, so this one cannot be reverted",
    "expired" => "this entry fell outside the undo window (entry or snapshot budget) and can no longer be reverted",
    "handle-stale" => "an earlier revert rebuilt the object this entry referenced, so its handles no longer point at it",
    _ => "",
  };

  // short, human-first description of what the action did (the panel shows this verbatim)
  private static string Summarize(string method, JsonObject? parameters)
  {
    var sb = new System.Text.StringBuilder(method ?? "");
    if (parameters != null)
    {
      var n = 0;
      foreach (var kv in parameters)
      {
        var key = kv.Key ?? "";
        if (key.StartsWith("__", StringComparison.Ordinal)) continue;
        if (n >= 3) break;
        var val = kv.Value is JsonArray arr ? (arr.Count <= 3 ? "[" + string.Join(",", arr) + "]" : "[" + arr.Count + "]") : (kv.Value?.ToString() ?? "");
        if (val.Length > 40) val = val.Substring(0, 40);
        sb.Append(n == 0 ? " " : ", ").Append(key).Append('=').Append(val);
        n++;
      }
    }
    var text = sb.ToString();
    return text.Length > 120 ? text.Substring(0, 120) : text;
  }

  private static bool StartsWithAny(string method, string[] prefixes)
  {
    var m = (method ?? "").ToLowerInvariant();
    foreach (var p in prefixes) if (m.StartsWith(p, StringComparison.Ordinal)) return true;
    return false;
  }

  /// <summary>Handles the call is about to touch (empty when it can be inferred from the result).</summary>
  public static List<string> TargetHandles(JsonObject? parameters)
  {
    var list = new List<string>();
    if (parameters == null) return list;
    void Push(JsonNode? node)
    {
      if (node is JsonArray arr)
      {
        foreach (var n in arr)
        {
          var s = n?.GetValue<string>();
          if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s!)) list.Add(s!);
        }
      }
      else if (node is JsonValue v && v.TryGetValue<string>(out var single) && !string.IsNullOrWhiteSpace(single))
      {
        if (!list.Contains(single)) list.Add(single);
      }
    }
    if (parameters["handles"] != null) Push(parameters["handles"]);
    if (parameters["handle"] != null) Push(parameters["handle"]);
    return list;
  }

  /// <summary>
  /// Opens an entry for this call; returns null when the method is not journal-relevant.
  /// The returned Entry is handed back to <see cref="RecordCreated"/> by the caller: the old code
  /// attached handles to "the last entry in the list", which mis-attributed them whenever requests
  /// interleaved (real-machine evidence: a deleteEntities entry carried the previous createPolyline
  /// handles).
  /// </summary>
  public static Entry? Prepare(string method, JsonObject? parameters, string taskId)
  {
    var noInverse = HasNoInverse(method);
    var create = IsCreate(method);
    var destructiveOrModify = IsDelete(method) || IsModify(method);
    if (!noInverse && !create && !destructiveOrModify) return null;

    var entry = new Entry
    {
      Seq = Interlocked.Increment(ref seqCounter),
      Method = method ?? "",
      TaskId = taskId ?? "",
      Summary = Summarize(method, parameters),
    };

    if (noInverse)
    {
      entry.Revertible = false;
      entry.BlockedReason = "no-inverse";
      entry.BlockedText = BlockedText("no-inverse");
      Add(entry);
      return entry;
    }

    if (!destructiveOrModify)
    {
      Add(entry);
      return entry;
    }

    var targets = TargetHandles(parameters);
    if (targets.Count == 0)
    {
      entry.Revertible = false;
      entry.BlockedReason = "unsupported-object";
      entry.BlockedText = "the call did not name the handles it touches, so nothing could be snapshotted";
      Add(entry);
      return entry;
    }

    try
    {
      SnapshotAll(entry, targets, IsDelete(method) ? "deleted" : "modified");
    }
    catch (Exception ex)
    {
      PluginLog.Warn("UndoJournal", "snapshot failed for " + method + ": " + ex.Message);
      entry.Revertible = false;
      entry.BlockedReason = "unsupported-object";
      entry.BlockedText = "snapshot failed: " + ex.Message;
    }

    if (entry.Revertible && entry.Snapshots.Count == 0)
    {
      entry.Revertible = false;
      entry.BlockedReason = "unsupported-object";
      entry.BlockedText = BlockedText("unsupported-object");
    }

    Add(entry);
    return entry;
  }

  private static void Add(Entry entry)
  {
    lock (entries)
    {
      entries.Add(entry);
      while (entries.Count > MaxEntries)
      {
        var oldest = entries[0];
        if (oldest.Snapshots.Count > 0 || oldest.Created.Count > 0 || oldest.Revertible)
        {
          // keep a tombstone so the panel can explain "outside the undo window"
          oldest.Snapshots.Clear();
          oldest.Created.Clear();
          oldest.Revertible = false;
          oldest.BlockedReason = "expired";
          oldest.BlockedText = BlockedText("expired");
          break;
        }
        entries.RemoveAt(0);
      }
      // bound tombstones too
      var tombstones = entries.FindAll(e => !e.Revertible && e.BlockedReason == "expired").Count;
      while (tombstones > MaxTombstones)
      {
        var idx = entries.FindIndex(e => !e.Revertible && e.BlockedReason == "expired");
        if (idx < 0) break;
        entries.RemoveAt(idx);
        tombstones--;
      }
    }
  }

  /// <summary>
  /// Remove entries that can never be reverted again. scope: blocked (default, every entry with
  /// revertible=false), expired (only expired / handle-stale), all (whole journal).
  /// Returns how many entries were dropped.
  /// </summary>
  public static int Prune(string scope)
  {
    var s = (scope ?? "").Trim().ToLowerInvariant();
    lock (entries)
    {
      if (s == "all")
      {
        var all = entries.Count;
        entries.Clear();
        DisposeStash();
        return all;
      }
      if (s == "expired")
        return entries.RemoveAll(e => !e.Revertible && (e.BlockedReason == "expired" || e.BlockedReason == "handle-stale"));
      return entries.RemoveAll(e => !e.Revertible);
    }
  }

  /// <summary>Drop an entry whose call failed: the journal lists actions that happened, not attempts.</summary>
  public static void Discard(Entry? entry)
  {
    if (entry == null) return;
    lock (entries) entries.Remove(entry);
  }

  /// <summary>Record what the call created, attached to ITS OWN entry (never "the last one").</summary>
  public static void RecordCreated(Entry? entry, string method, List<string> createdHandles)
  {
    if (entry == null || createdHandles == null || createdHandles.Count == 0) return;
    if (!string.Equals(entry.Method, method ?? "", StringComparison.OrdinalIgnoreCase)) return;
    lock (entries)
    {
      foreach (var h in createdHandles) if (!entry.Created.Contains(h)) entry.Created.Add(h);
    }
  }

  public static List<Entry> List(int limit)
  {
    lock (entries)
    {
      var outList = new List<Entry>();
      for (var i = entries.Count - 1; i >= 0 && outList.Count < (limit <= 0 ? 50 : limit); i--) outList.Add(entries[i]);
      return outList;
    }
  }

  /// <summary>Undo exactly one entry. Never touches anything else in the drawing.</summary>
  public static Dictionary<string, object?> Revert(long seq)
  {
    Entry? entry;
    lock (entries) entry = entries.Find(e => e.Seq == seq);
    if (entry == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "journal entry " + seq + " not found (it may have been trimmed)");
    if (!entry.Revertible)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "entry " + seq + " cannot be reverted: " + (entry.BlockedText.Length > 0 ? entry.BlockedText : entry.BlockedReason));

    var report = new Dictionary<string, object?>
    {
      ["seq"] = entry.Seq,
      ["method"] = entry.Method,
      ["restoredHandles"] = new List<string>(),
      ["erasedHandles"] = new List<string>(),
      ["newHandles"] = new List<string>(),
      ["failed"] = new List<string>(),
    };
    var restored = (List<string>)report["restoredHandles"]!;
    var erased = (List<string>)report["erasedHandles"]!;
    var newHandles = (List<string>)report["newHandles"]!;
    var failed = (List<string>)report["failed"]!;

    var snaps = new List<Snapshot>(entry.Snapshots);
    var createdHandles = new List<string>(entry.Created);

    CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

      // 1) restore deleted / modified objects from the side database
      foreach (var snap in snaps)
      {
        try
        {
          if (snap.Kind == "modified")
          {
            var current = HandleToId(database, snap.Handle);
            if (!current.IsNull)
            {
              var cur = transaction.GetObject(current, OpenMode.ForWrite, false);
              if (cur is Entity e && !e.IsErased) { e.Erase(); erased.Add(snap.Handle); }
            }
          }
          var cloned = CloneFromStash(database, space, new[] { snap.StashHandle });
          if (cloned.Count == 0) { failed.Add(snap.Handle); continue; }
          restored.Add(snap.Handle);
          foreach (var h in cloned) newHandles.Add(h);
        }
        catch (Exception ex)
        {
          failed.Add(snap.Handle);
          PluginLog.Warn("UndoJournal", "restore failed for " + snap.Handle + ": " + ex.Message);
        }
      }

      // 2) undo the creates: erase what this action made
      foreach (var h in createdHandles)
      {
        try
        {
          var id = HandleToId(database, h);
          if (id.IsNull) continue;
          var obj = transaction.GetObject(id, OpenMode.ForWrite, false);
          if (obj is Entity e && !e.IsErased) { e.Erase(); erased.Add(h); }
          else failed.Add(h);
        }
        catch (Exception ex)
        {
          failed.Add(h);
          PluginLog.Warn("UndoJournal", "erase failed for " + h + ": " + ex.Message);
        }
      }
      return null;
    }).GetAwaiter().GetResult();

    lock (entries)
    {
      entries.Remove(entry);
      // a restore produces NEW handles; entries that referenced the old ones are now stale
      if (restored.Count > 0)
      {
        foreach (var other in entries)
        {
          if (!other.Revertible) continue;
          var stale = other.Created.RemoveAll(h => restored.Contains(h)) > 0;
          foreach (var snap in other.Snapshots) if (restored.Contains(snap.Handle)) stale = true;
          if (stale)
          {
            other.Revertible = false;
            other.BlockedReason = "handle-stale";
            other.BlockedText = BlockedText("handle-stale");
          }
        }
      }
    }

    var moved = restored.Count + erased.Count;
    report["moved"] = moved;
    report["failedCount"] = failed.Count;
    if (moved == 0)
    {
      report["ok"] = false;
      report["reason"] = failed.Count > 0
        ? "nothing was reverted: every target was already gone or could not be restored"
        : "nothing was reverted: this entry recorded no handles to undo";
    }
    else
    {
      report["ok"] = true;
      report["reason"] = failed.Count > 0 ? "partly reverted: " + failed.Count + " target(s) failed" : "";
    }
    return report;
  }

  // ---------------- side database ----------------

  private static void EnsureStash()
  {
    if (stashDb != null) return;
    var db = new Database(true, true);
    using (var tr = db.TransactionManager.StartTransaction())
    {
      var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      stashSpace = ms.ObjectId;
      tr.Commit();
    }
    stashDb = db;
  }

  private static void DisposeStash()
  {
    try { stashDb?.Dispose(); } catch { }
    stashDb = null;
    stashSpace = ObjectId.Null;
    stashObjectCount = 0;
  }

  private static void SnapshotAll(Entry entry, List<string> targets, string kind)
  {
    var ids = new ObjectIdCollection();
    var meta = new List<Snapshot>();
    Database? source = null;

    // Pass 1: resolve handles, skip erased objects and Civil 3D objects (they cannot be cloned
    // across databases with any fidelity). Nothing stays open past this transaction.
    CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      source = database;
      foreach (var handle in targets)
      {
        try
        {
          var id = HandleToId(database, handle);
          if (id.IsNull) continue;
          var obj = transaction.GetObject(id, OpenMode.ForRead, false);
          if (obj is not Entity ent || ent.IsErased) continue;
          var typeName = ent.GetType().FullName ?? ent.GetType().Name;
          if (typeName.StartsWith("Autodesk.Civil", StringComparison.Ordinal) || typeName.StartsWith("Autodesk.Aecc", StringComparison.Ordinal))
          {
            PluginLog.Warn("UndoJournal", "skip non-cloneable C3D object " + typeName + " (" + handle + ")");
            continue;
          }
          ids.Add(id);
          meta.Add(new Snapshot { Handle = handle, Kind = kind, Layer = ent.Layer, Type = typeName });
        }
        catch (Exception ex)
        {
          PluginLog.Warn("UndoJournal", "snapshot scan failed for handle " + handle + ": " + ex.Message);
        }
      }
      return null;
    }).GetAwaiter().GetResult();

    if (ids.Count == 0 || source == null) return;
    if (stashObjectCount + ids.Count > MaxSnapshots)
    {
      entry.Revertible = false;
      entry.BlockedReason = "expired";
      entry.BlockedText = BlockedText("expired");
      return;
    }

    EnsureStash();
    var target = stashDb!;

    // Pass 2: clone into the side database. The destination transaction is the one that must be
    // open; the source objects are already closed.
    using (var tr = target.TransactionManager.StartTransaction())
    {
      var bt = (BlockTable)tr.GetObject(target.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      var map = new IdMapping();
      source.WblockCloneObjects(ids, ms.ObjectId, map, DuplicateRecordCloning.Ignore, false);
      foreach (var snap in meta)
      {
        var srcId = HandleToId(source, snap.Handle);
        if (srcId.IsNull) continue;
        var pair = map.Lookup(srcId);
        if (pair.Value.IsNull) continue;
        snap.StashHandle = pair.Value.Handle.ToString();
        entry.Snapshots.Add(snap);
      }
      tr.Commit();
    }
    stashObjectCount += entry.Snapshots.Count;
  }

  /// <summary>Clone one stash object back into the user's drawing; returns the new handles.</summary>
  private static List<string> CloneFromStash(Database userDb, BlockTableRecord space, string[] stashHandles)
  {
    var result = new List<string>();
    var stash = stashDb;
    if (stash == null || stashHandles.Length == 0) return result;

    var ids = new ObjectIdCollection();
    foreach (var h in stashHandles)
    {
      var id = HandleToId(stash, h);
      if (!id.IsNull) ids.Add(id);
    }
    if (ids.Count == 0) return result;

    var map = new IdMapping();
    stash.WblockCloneObjects(ids, space.ObjectId, map, DuplicateRecordCloning.Replace, false);
    foreach (ObjectId srcId in ids)
    {
      var pair = map.Lookup(srcId);
      if (!pair.Value.IsNull) result.Add(pair.Value.Handle.ToString());
    }

    // the stash copy is consumed once restored
    using (var tr = stash.TransactionManager.StartTransaction())
    {
      foreach (ObjectId srcId in ids)
      {
        try
        {
          var obj = tr.GetObject(srcId, OpenMode.ForWrite, false);
          if (obj is Entity e && !e.IsErased) e.Erase();
        }
        catch { }
      }
      tr.Commit();
    }
    stashObjectCount = Math.Max(0, stashObjectCount - ids.Count);
    return result;
  }

  private static ObjectId HandleToId(Database db, string handle)
  {
    if (string.IsNullOrWhiteSpace(handle)) return ObjectId.Null;
    long value;
    if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
    ObjectId id;
    return db.TryGetObjectId(new Handle(value), out id) ? id : ObjectId.Null;
  }
}
