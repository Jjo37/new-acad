// CivilObjectReaders.cs -- read properties of ANY Civil 3D object (and fall back to AutoCAD
// entities) without binding one command per class (2026-11-09, Bro: "全部补全").
//
// Why: the plug-in had a dedicated reader for ~14 C3D classes, and NOTHING for ProfileView,
// SectionView, SampleLineGroup, ViewFrameGroup, ViewFrame, MatchLine, MassHaulLine/View,
// SurveyNetwork, PointGroup. Those families came back empty.
//
// How: identity + a guarded reflection dump of every scalar property (numbers/strings/bools/
// enums/points) + collection sizes + geometry summaries for the classes where a property dump
// alone is useless (alignment entities, surface boundaries/breaklines, profile PVIs, sample line
// vertices, corridor baselines). Everything goes through reflection on the *instance*, so a
// rename or a class we never hard-bound cannot break the build, and an object whose getter
// throws degrades to fewer fields instead of INTERNAL_ERROR.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Collections;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

internal static class CivilObjectReaders
{
  /// <summary>
  /// Property names that must never be reflected: they cross into native Civil 3D code that
  /// faults with an access violation instead of throwing a managed exception. Proven by crash dump.
  /// </summary>
  private static readonly HashSet<string> NativeUnsafeProperties = new(StringComparer.Ordinal)
  {
    // AeccFeatureUtils.isEditableDbFeature -> AccessViolationException while reading a Profile,
    // killing acad.exe and resetting the TCP bridge mid-request (2026-11-09, two reproductions).
    "IsEditable",
  };
  /// <summary>Read one object: C3D object or AutoCAD entity.</summary>
  public static Dictionary<string, object?> Read(AcDbObject obj, Transaction tr)
  {
    // Careful: Civil 3D objects derive from Autodesk.Civil.DatabaseServices.Entity, which is
    // itself an AutoCAD Entity -- so "is Entity" is true for surfaces/alignments too. The
    // namespace decides which reader wins: Autodesk.Civil.* = Civil 3D, everything else = CAD.
    var ns = obj.GetType().Namespace ?? string.Empty;
    var isCivil = ns.StartsWith("Autodesk.Civil", StringComparison.Ordinal);
    if (!isCivil && obj is Autodesk.AutoCAD.DatabaseServices.Entity cadEntity)
      return EntityReaders.Read(cadEntity, tr);

    var info = new Dictionary<string, object?>();
    info["handle"] = Safe(() => obj.Handle.ToString(), null);
    info["objectType"] = obj.GetType().Name;
    info["civilClass"] = Safe(() => obj.GetRXClass().Name, null);
    info["name"] = Safe(() => CivilObjectUtils.GetName(obj), null);
    info["description"] = Safe(() => CivilObjectUtils.GetStringProperty(obj, "Description"), null);
    info["styleName"] = Safe(() => CivilObjectUtils.GetStringProperty(obj, "StyleName"), null);

    DumpScalars(obj, info);

    try { Enrich(obj, tr, info); }
    catch (System.Exception ex) { info["readerError"] = ex.GetType().Name + ": " + ex.Message; }

    return info;
  }

  // ------------------------------------------------------------------ scalar dump

  /// <summary>Guarded reflection dump: scalars and points only; collections keep a count.</summary>
  private static void DumpScalars(object obj, Dictionary<string, object?> info)
  {
    var bag = new Dictionary<string, object?>();
    var counts = new Dictionary<string, object?>();
    try
    {
      foreach (var kv in Civil3DCompatibility.GetReadablePropertyValues(obj))
      {
        var name = kv.Key;
        if (name is "Handle" or "ObjectId" or "OwnerId" or "Database" or "TransactionManager" or "ManagedObject") continue;
        if (NativeUnsafeProperties.Contains(name)) continue;
        if (info.ContainsKey(name) || bag.ContainsKey(name)) continue;
        var value = kv.Value;
        if (value == null) continue;

        var scalar = ToScalar(value);
        if (scalar != null) { if (bag.Count < 80) bag[name] = scalar; continue; }

        if (value is ObjectIdCollection ids) { if (counts.Count < 30) counts[name + "Count"] = ids.Count; }
        else if (value is ICollection coll) { if (counts.Count < 30) counts[name + "Count"] = coll.Count; }
      }
    }
    catch { /* metadata unavailable */ }

    if (bag.Count > 0) info["properties"] = bag;
    if (counts.Count > 0) info["collectionCounts"] = counts;
  }

  private static object? ToScalar(object value) => value switch
  {
    string or bool or int or long or short or byte or float or double or decimal => value,
    Point3d p => new Dictionary<string, object?> { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z },
    Point2d p => new Dictionary<string, object?> { ["x"] = p.X, ["y"] = p.Y },
    Vector3d v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z },
    Enum e => e.ToString(),
    _ => null,
  };

  // ------------------------------------------------------------------ geometry enrichment

  private static void Enrich(AcDbObject obj, Transaction tr, Dictionary<string, object?> info)
  {
    switch (obj)
    {
      case Alignment alignment: EnrichAlignment(alignment, info); break;
      case Profile profile: EnrichProfile(profile, tr, info); break;
      case SampleLineGroup group: EnrichSampleLines(group, tr, info); break;
      case Corridor corridor: EnrichCorridor(corridor, tr, info); break;
      default: EnrichByTypeName(obj, tr, info); break;
    }
  }

  /// <summary>Classes without a compile-time binding: surface, sample line, view frame, mass haul, ...</summary>
  private static void EnrichByTypeName(AcDbObject obj, Transaction tr, Dictionary<string, object?> info)
  {
    var typeName = obj.GetType().Name;

    // TIN surface: boundaries + breaklines hang off their *Definition objects, and the property
    // names differ between TIN / grid / volume surfaces -- probe a candidate list instead of
    // hard-binding one (this is what made the first attempt return null).
    if (typeName.Contains("Surface"))
    {
      foreach (var pair in new[]
      {
        ("boundaries", new[] { "BoundariesDefinition", "Boundaries", "BoundaryDefinition" }),
        ("breaklines", new[] { "BreaklinesDefinition", "Breaklines", "BreaklineDefinition" }),
      })
      {
        foreach (var candidate in pair.Item2)
        {
          var holder = TryGet(obj, candidate);
          if (holder == null) continue;
          info[pair.Item1 + "Source"] = holder.GetType().Name;
          var count = CountAny(holder);
          if (count != null) info[pair.Item1 + "Count"] = count;
          break;
        }
      }
    }

    // Profile / section views, view frames, match lines, mass haul, survey: the scalar dump already
    // carries most fields; these aliases make the important numbers easy to find and stable to
    // match on, whatever the property is called in a given release.
    foreach (var pair in new[]
    {
      ("stationStart", new[] { "StationStart", "StartStation", "StartingStation" }),
      ("stationEnd", new[] { "StationEnd", "EndStation", "EndingStation" }),
      ("elevationMin", new[] { "ElevationMin", "MinimumElevation", "MinElevation" }),
      ("elevationMax", new[] { "ElevationMax", "MaximumElevation", "MaxElevation" }),
      ("offsetLeft", new[] { "LeftOffset", "OffsetLeft" }),
      ("offsetRight", new[] { "RightOffset", "OffsetRight" }),
      ("parentAlignment", new[] { "AlignmentName", "Alignment", "ParentAlignmentName" }),
      ("parentProfile", new[] { "ProfileName", "Profile" }),
      ("parentSampleLineGroup", new[] { "SampleLineGroupName", "SampleLineGroup" }),
      ("pointCount", new[] { "PointCount", "NumberOfPoints", "PointsCount" }),
      ("position", new[] { "Position", "Location", "InsertionPoint", "Insertion" }),
    })
    {
      if (info.ContainsKey(pair.Item1)) continue;
      foreach (var candidate in pair.Item2)
      {
        var v = PropScalar(obj, candidate);
        if (v == null) continue;
        info[pair.Item1] = v;
        break;
      }
    }

    // sample line / view frame / match line / mass haul: expose their geometry-bearing collections
    foreach (var coll in new[] { "SampleLines", "ViewFrames", "MatchLines", "MassHaulLines", "Baselines" })
    {
      var n = CountOf(obj, coll);
      if (n != null) info[char.ToLowerInvariant(coll[0]) + coll.Substring(1) + "Count"] = n;
    }
  }

  private static void EnrichAlignment(Alignment alignment, Dictionary<string, object?> info)
  {
    var rows = new List<Dictionary<string, object?>>();
    foreach (var item in Enumerate(alignment, "Entities", null))
    {
      if (rows.Count >= 200) break;
      var row = new Dictionary<string, object?> { ["entityType"] = item.GetType().Name };
      foreach (var name in new[] { "StartStation", "EndStation", "Length", "Radius", "CurveType", "SpiralType",
                                   "SpiralDefinition", "ThroughPoint", "Center", "Direction" })
      {
        var v = PropScalar(item, name);
        if (v != null) row[name] = v;
      }
      rows.Add(row);
    }
    if (rows.Count > 0) info["alignmentEntities"] = rows;
    info["stationsRange"] = Safe<string?>(() =>
    {
      var start = PropScalar(alignment, "StartingStation");
      var end = PropScalar(alignment, "EndingStation");
      return start == null && end == null ? null : start + " .. " + end;
    }, null);
  }

  private static void EnrichProfile(Profile profile, Transaction tr, Dictionary<string, object?> info)
  {
    // PVIs only exist on layout profiles. Touching PVIs on a surface-derived profile is at best
    // pointless and at worst a native crash, so the profile type gates it (2026-11-09: AutoCAD
    // 2025 died twice while this enrichment ran; the bridge reset mid-request).
    var profileType = PropString(profile, "ProfileType") ?? "";
    info["profileType"] = profileType;
    if (!profileType.Contains("Layout", StringComparison.OrdinalIgnoreCase)) return;

    var pvis = new List<Dictionary<string, object?>>();
    try
    {
      foreach (var pvi in Enumerate(profile, "PVIs"))
      {
        if (pvis.Count >= 500) break;
        pvis.Add(new Dictionary<string, object?>
        {
          ["station"] = PropScalar(pvi, "Station"),
          ["elevation"] = PropScalar(pvi, "Elevation"),
        });
      }
    }
    catch { }
    if (pvis.Count > 0) info["pvis"] = pvis;
  }

  private static void EnrichSampleLines(SampleLineGroup group, Transaction tr, Dictionary<string, object?> info)
  {
    // Prefer the id collection method; fall back to a "SampleLines" property.
    var ids = InvokeIds(group, "GetSampleLineIds") ?? InvokeIds(group, "GetSampleLineIdsInGroup");
    if (ids == null) return;
    var lines = new List<Dictionary<string, object?>>();
    foreach (var id in ids)
    {
      if (lines.Count >= 200) break;
      try
      {
        var sl = tr.GetObject(id, OpenMode.ForRead);
        if (sl == null) continue;
        var row = new Dictionary<string, object?>
        {
          ["handle"] = Safe(() => ((AcDbObject)sl).Handle.ToString(), null),
          ["objectType"] = sl.GetType().Name,
          ["station"] = PropScalar(sl, "Station"),
          ["name"] = Safe(() => CivilObjectUtils.GetName(sl), null),
        };
        DumpScalars(sl, row);
        lines.Add(row);
      }
      catch { }
    }
    if (lines.Count > 0) info["sampleLines"] = lines;
  }

  private static void EnrichCorridor(Corridor corridor, Transaction tr, Dictionary<string, object?> info)
  {
    var baselines = new List<Dictionary<string, object?>>();
    foreach (var baseline in Enumerate(corridor, "Baselines", tr))
    {
      if (baselines.Count >= 100) break;
      var row = new Dictionary<string, object?>
      {
        ["name"] = Safe(() => CivilObjectUtils.GetName(baseline), null),
        ["regionCount"] = CountOf(baseline, "RegionIds") ?? CountOf(baseline, "Regions"),
      };
      var stations = PropScalar(baseline, "StartStation");
      if (stations != null) row["startStation"] = stations;
      var stationsEnd = PropScalar(baseline, "EndStation");
      if (stationsEnd != null) row["endStation"] = stationsEnd;
      baselines.Add(row);
    }
    if (baselines.Count > 0) info["baselines"] = baselines;
  }

  // ------------------------------------------------------------------ helpers

  private static object? TryGet(object obj, string name)
  {
    try
    {
      return Civil3DCompatibility.GetPropertyValue(obj, name);
    }
    catch { return null; }
  }

  private static object? PropScalar(object obj, string name)
  {
    var v = TryGet(obj, name);
    return v == null ? null : ToScalar(v);
  }

  private static string? PropString(object obj, string name)
  {
    var v = TryGet(obj, name);
    return v?.ToString();
  }

  /// <summary>Best-effort count for a definition object that may expose Count / Counts / an inner collection.</summary>
  private static int? CountAny(object holder)
  {
    foreach (var name in new[] { "Count", "ItemCount", "NumberOfItems" })
    {
      var v = PropScalar(holder, name);
      if (v is int i) return i;
    }
    foreach (var kv in Civil3DCompatibility.GetReadablePropertyValues(holder))
    {
      var raw = kv.Value;
      if (raw is ObjectIdCollection ids) return ids.Count;
      if (raw is ICollection coll) return coll.Count;
    }
    return null;
  }

  private static int? CountOf(object obj, string name)
  {
    var v = TryGet(obj, name);
    return v switch
    {
      ObjectIdCollection ids => ids.Count,
      ICollection coll => coll.Count,
      _ => null,
    };
  }

  /// <summary>Enumerate a property that may be a collection of objects or of ObjectIds.</summary>
  private static IEnumerable<object> Enumerate(object obj, string name, Transaction? tr = null)
  {
    IEnumerable? raw = null;
    try { raw = TryGet(obj, name) as IEnumerable; } catch { raw = null; }
    if (raw == null) yield break;
    foreach (var item in raw)
    {
      if (item == null) continue;
      if (item is ObjectId id)
      {
        if (tr == null || id.IsNull) continue;
        object? resolved = null;
        try { resolved = tr.GetObject(id, OpenMode.ForRead); } catch { resolved = null; }
        if (resolved != null) yield return resolved;
        continue;
      }
      yield return item;
    }
  }

  private static List<ObjectId>? InvokeIds(object obj, string methodName)
  {
    try
    {
      if (!Civil3DCompatibility.TryInvokeParameterlessMethod(obj, methodName, out var raw)) return null;
      var list = new List<ObjectId>();
      if (raw is ObjectIdCollection ids) { foreach (ObjectId id in ids) list.Add(id); return list; }
      if (raw is IEnumerable en) { foreach (var o in en) if (o is ObjectId id) list.Add(id); return list; }
      return null;
    }
    catch { return null; }
  }

  private static T? Safe<T>(Func<T> f, T? fallback)
  {
    try { return f(); } catch { return fallback; }
  }
}
