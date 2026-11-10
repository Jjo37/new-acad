// EntityReaders.cs -- one place that knows how to turn ANY AutoCAD entity into JSON fields.
//
// Why this file exists (2026-11-09, Bro: "全部补全 + 反射兜底"):
//   getEntityInfo used to be a 9-branch if/else chain inside AcadCommands: text/mtext/polyline/
//   circle/line/arc/hatch/solid3d/blockref. Everything else (spline, helix, ellipse, 3D polyline,
//   region, mesh, mleader, table, underlay, ...) came back with only handle/type/layer/color.
//
// Design rules:
//   1. Registry keyed by CLR type; lookup walks the runtime type's base chain, so the most
//      derived reader always wins (Helix before Spline, MLeader before its base, ...).
//   2. Common properties are filled once for every entity (lineweight/linetype/transparency/
//      visible/extents/xdata ...) -- these used to be missing on ALL types.
//   3. Unregistered types fall back to a guarded reflection dump, so a new Autodesk entity type
//      degrades to "fewer fields" instead of "nothing at all".
//   4. Anything that can throw (mass properties on an open solid, extents on an empty curve,
//      underlay files that are gone) is wrapped: a reader must never turn into INTERNAL_ERROR.
//   5. Members that are not stable across 2025/2026/2027 (or that live on a class whose name we
//      do not want to hard-bind) are read through P()/Prop() reflection instead of being bound.
//      A rename upstream therefore cannot break the build.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
// name clashes with System.Drawing / Geometry: pin the entity types we mean
using AcRegion = Autodesk.AutoCAD.DatabaseServices.Region;
using AcSurface = Autodesk.AutoCAD.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

internal static class EntityReaders
{
  private static readonly Dictionary<Type, Action<Entity, Transaction, Dictionary<string, object?>>> Registry = Build();

  /// <summary>Read one entity: common fields + the registered reader + (unregistered) reflection dump.</summary>
  public static Dictionary<string, object?> Read(Entity ent, Transaction tr)
  {
    var info = new Dictionary<string, object?>();
    FillCommon(ent, info);

    var handler = FindHandler(ent.GetType());
    if (handler != null)
    {
      try { handler(ent, tr, info); }
      catch (System.Exception ex) { info["readerError"] = ex.GetType().Name + ": " + ex.Message; }
    }
    else
    {
      info["readerSource"] = "reflection";
      ReflectionDump(ent, info);
    }
    return info;
  }

  public static string TypeName(Entity ent)
  {
    try { return ent.GetRXClass().Name; } catch { return ent.GetType().Name; }
  }

  private static Action<Entity, Transaction, Dictionary<string, object?>>? FindHandler(Type t)
  {
    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
      if (Registry.TryGetValue(cur, out var h)) return h;
    return null;
  }

  // ------------------------------------------------------------------ common

  private static void FillCommon(Entity ent, Dictionary<string, object?> info)
  {
    info["handle"] = Safe(() => ent.Handle.ToString(), null);
    info["type"] = TypeName(ent);
    info["layer"] = ent.Layer;
    info["color"] = ent.Color.ToString();          // localized colour name (zh: 红) -- do not match on it
    info["colorIndex"] = ent.ColorIndex;           // stable ACI index (ByLayer=256 / ByBlock=0)
    info["colorMethod"] = ent.Color.ColorMethod.ToString();
    info["linetype"] = Safe(() => ent.Linetype, null);
    info["linetypeScale"] = Safe<double?>(() => ent.LinetypeScale, null);
    info["lineweight"] = ent.LineWeight.ToString();
    info["lineweightMm"] = (int)ent.LineWeight / 100.0;
    info["visible"] = Safe<bool?>(() => ent.Visible, null);
    info["transparency"] = Safe(() => ent.Transparency.ToString(), null);
    info["extents"] = Extents(ent);
    info["hasXData"] = Safe<bool?>(() => XDataCount(ent) > 0, null);
    info["xdataCount"] = Safe<int?>(() => XDataCount(ent), null);
    info["hasExtensionDictionary"] = Safe<bool?>(() => !ent.ExtensionDictionary.IsNull, null);
  }

  private static int XDataCount(Entity ent)
  {
    try
    {
      var rb = ent.XData;
      if (rb == null) return 0;
      var arr = rb.AsArray();
      return arr?.Length ?? 0;
    }
    catch { return 0; }
  }

  /// <summary>Bounding box in WCS. Some entities raise (empty/parametric); we answer null instead.</summary>
  public static object? Extents(Entity ent)
  {
    try
    {
      var ex = ent.GeometricExtents;
      return new Dictionary<string, object?>
      {
        ["minPoint"] = Pt(ex.MinPoint),
        ["maxPoint"] = Pt(ex.MaxPoint),
        ["size"] = new Dictionary<string, object?>
        {
          ["x"] = ex.MaxPoint.X - ex.MinPoint.X,
          ["y"] = ex.MaxPoint.Y - ex.MinPoint.Y,
          ["z"] = ex.MaxPoint.Z - ex.MinPoint.Z,
        },
      };
    }
    catch { return null; }
  }

  // ------------------------------------------------------------------ helpers

  private static Dictionary<string, object?> Pt(Point3d p) => new() { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
  private static Dictionary<string, object?> Pt(Point2d p) => new() { ["x"] = p.X, ["y"] = p.Y };
  private static Dictionary<string, object?> Vec(Vector3d v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
  private static Dictionary<string, object?> Vec(Vector2d v) => new() { ["x"] = v.X, ["y"] = v.Y };
  private static Dictionary<string, object?> Vec(Scale3d s) => new() { ["x"] = s.X, ["y"] = s.Y, ["z"] = s.Z };

  private static T? Safe<T>(Func<T> f, T? fallback)
  {
    try { return f(); } catch { return fallback; }
  }

  /// <summary>Read a property by name through the sanctioned boundary (survives API renames).</summary>
  internal static object? Prop(object? o, string name)
  {
    if (o == null) return null;
    try
    {
      var v = Civil3DCompatibility.GetPropertyValue(o, name);
      if (v == null) return null;
      return v switch
      {
        null => null,
        string or bool or int or long or short or byte or float or double or decimal => v,
        Point3d p3 => Pt(p3),
        Point2d p2 => Pt(p2),
        Vector3d v3 => Vec(v3),
        Vector2d v2 => Vec(v2),
        Scale3d s3 => Vec(s3),
        Enum e => e.ToString(),
        _ => null,                     // objects/collections are not useful as scalars
      };
    }
    catch { return null; }
  }

  /// <summary>Number of items in a collection-ish property (vertices, faces, loops ...).</summary>
  internal static int? PropCount(object? o, string name)
  {
    if (o == null) return null;
    try
    {
      var v = Civil3DCompatibility.GetPropertyValue(o, name);
      return v switch
      {
        null => null,
        int i => i,
        System.Collections.ICollection c => c.Count,
        _ => null,
      };
    }
    catch { return null; }
  }

  private static void ReflectionDump(Entity ent, Dictionary<string, object?> info)
  {
    var dumped = new Dictionary<string, object?>();
    try
    {
      foreach (var kv in Civil3DCompatibility.GetReadablePropertyValues(ent))
      {
        if (dumped.Count >= 40) break;
        var name = kv.Key;
        if (name is "Handle" or "ObjectId" or "OwnerId" or "Database" or "TransactionManager") continue;
        try
        {
          var v = kv.Value;
          var scalar = v switch
          {
            null => null,
            string or bool or int or long or short or byte or float or double or decimal => v,
            Point3d p3 => Pt(p3),
            Point2d p2 => Pt(p2),
            Vector3d v3 => Vec(v3),
            Vector2d v2 => Vec(v2),
            Enum e => e.ToString(),
            _ => null,
          };
          if (scalar != null && !info.ContainsKey(name)) dumped[name] = scalar;
        }
        catch { /* property getter threw -- skip it, keep going */ }
      }
    }
    catch { /* type metadata unavailable -- nothing to add */ }
    if (dumped.Count > 0) info["properties"] = dumped;
  }

  // ------------------------------------------------------------------ registry

  private static Dictionary<Type, Action<Entity, Transaction, Dictionary<string, object?>>> Build()
  {
    var r = new Dictionary<Type, Action<Entity, Transaction, Dictionary<string, object?>>>();

    // ---- text / annotation -------------------------------------------------
    r[typeof(DBText)] = (e, t, i) =>
    {
      var x = (DBText)e;
      i["text"] = x.TextString;
      i["height"] = x.Height;
      i["rotation"] = x.Rotation;
      i["position"] = Pt(x.Position);
      i["horizontalMode"] = Safe(() => x.HorizontalMode.ToString(), null);
      i["verticalMode"] = Safe(() => x.VerticalMode.ToString(), null);
      i["widthFactor"] = Safe<double?>(() => x.WidthFactor, null);
      i["oblique"] = Safe<double?>(() => x.Oblique, null);
      i["mirrored"] = Prop(x, "Mirror");
      i["styleName"] = Safe(() => x.TextStyleName, null);
      i["isBackward"] = Prop(x, "IsMirrored");
    };

    r[typeof(MText)] = (e, t, i) =>
    {
      var x = (MText)e;
      i["text"] = x.Contents;
      i["plainText"] = Safe(() => x.Text, null);
      i["textHeight"] = x.TextHeight;
      i["position"] = Pt(x.Location);
      i["rotation"] = Safe<double?>(() => x.Rotation, null);
      i["width"] = Safe<double?>(() => x.Width, null);
      i["attachment"] = Safe(() => x.Attachment.ToString(), null);
      i["direction"] = Safe(() => x.Direction.ToString(), null);
      i["lineSpacingFactor"] = Safe<double?>(() => x.LineSpacingFactor, null);
      i["lineSpacingStyle"] = Safe(() => x.LineSpacingStyle.ToString(), null);
      i["styleName"] = Safe(() => x.TextStyleName, null);
    };

    r[typeof(MLeader)] = (e, t, i) =>
    {
      var x = (MLeader)e;
      i["contentType"] = Safe(() => x.ContentType.ToString(), null);
      i["text"] = Safe(() => x.MText?.Contents, null);
      i["textHeight"] = Safe<double?>(() => x.MText?.TextHeight, null);
      i["textLocation"] = Prop(x, "TextLocation");
      i["leaderLineCount"] = PropCount(x, "LeaderLineCount") ?? PropCount(x, "LeaderCount");
      i["scale"] = Prop(x, "Scale");
      i["styleName"] = Prop(x, "StyleName") ?? Prop(x, "MLeaderStyleName");
      i["blockContent"] = Safe(() => x.BlockContentId.IsNull ? null : x.BlockContentId.Handle.ToString(), null);
    };

    r[typeof(Leader)] = (e, t, i) =>
    {
      var x = (Leader)e;
      i["numVertices"] = Safe<int?>(() => x.NumVertices, null);
      i["hasArrowHead"] = Safe<bool?>(() => x.HasArrowHead, null);
      i["annotation"] = Safe(() => x.Annotation.IsNull ? null : x.Annotation.Handle.ToString(), null);
      i["dimensionStyle"] = Safe(() => x.DimensionStyleName, null);
      var verts = new List<Dictionary<string, object?>>();
      try { for (var vi = 0; vi < x.NumVertices; vi++) verts.Add(Pt(x.VertexAt(vi))); } catch { }
      if (verts.Count > 0) i["vertices"] = verts;
    };

    r[typeof(Table)] = (e, t, i) =>
    {
      var x = (Table)e;
      i["rows"] = Safe<int?>(() => x.Rows.Count, null);
      i["columns"] = Safe<int?>(() => x.Columns.Count, null);
      i["tableStyle"] = Prop(x, "TableStyleName");
      i["title"] = Safe(() => x.Cells[0, 0].TextString, null);
      i["position"] = Safe(() => x.Position, (Point3d?)null) is Point3d p ? Pt(p) : null;
    };

    r[typeof(AttributeDefinition)] = (e, t, i) =>
    {
      var x = (AttributeDefinition)e;
      i["tag"] = x.Tag;
      i["prompt"] = Safe(() => x.Prompt, null);
      i["text"] = x.TextString;
      i["height"] = x.Height;
      i["isConstant"] = Safe<bool?>(() => x.Constant, null);
      i["isInvisible"] = Safe<bool?>(() => x.Invisible, null);
    };

    r[typeof(AttributeReference)] = (e, t, i) =>
    {
      var x = (AttributeReference)e;
      i["tag"] = x.Tag;
      i["text"] = x.TextString;
      i["height"] = x.Height;
      i["isInvisible"] = Safe<bool?>(() => x.Invisible, null);
      i["position"] = Safe(() => x.Position, (Point3d?)null) is Point3d p ? Pt(p) : null;
    };

    // Tolerance (GD&T) has no managed wrapper in AcDbMgd -- it is covered by the reflection fallback.
    // ---- dimensions (all kinds, not just 3) --------------------------------
    r[typeof(RotatedDimension)] = (e, t, i) =>
    {
      var x = (RotatedDimension)e;
      i["measurementKind"] = "length";
      i["rotation"] = x.Rotation;
      i["xLine1"] = Pt(x.XLine1Point);
      i["xLine2"] = Pt(x.XLine2Point);
      i["dimLinePoint"] = Pt(x.DimLinePoint);
      i["dimensionStyle"] = Safe(() => x.DimensionStyleName, null);
    };
    r[typeof(AlignedDimension)] = (e, t, i) =>
    {
      var x = (AlignedDimension)e;
      i["measurementKind"] = "length";
      i["xLine1"] = Pt(x.XLine1Point);
      i["xLine2"] = Pt(x.XLine2Point);
      i["dimLinePoint"] = Pt(x.DimLinePoint);
      i["dimensionStyle"] = Safe(() => x.DimensionStyleName, null);
    };
    r[typeof(RadialDimension)] = (e, t, i) =>
    {
      var x = (RadialDimension)e;
      i["measurementKind"] = "radius";
      i["center"] = Pt(x.Center);
      i["chordPoint"] = Pt(x.ChordPoint);
      i["leaderLength"] = Safe<double?>(() => x.LeaderLength, null);
    };
    r[typeof(DiametricDimension)] = (e, t, i) =>
    {
      var x = (DiametricDimension)e;
      i["measurementKind"] = "diameter";
      i["chordPoint"] = Pt(x.ChordPoint);
      i["farChordPoint"] = Pt(x.FarChordPoint);
      i["leaderLength"] = Safe<double?>(() => x.LeaderLength, null);
    };
    // angular / ordinate / arc: property names drift between releases -> reflection
    r[typeof(LineAngularDimension2)] = (e, t, i) => FillByNames(e, i, "angle", new[]
    {
      "ArcPoint", "XLine1Start", "XLine1End", "XLine2Start", "XLine2End",
    });
    r[typeof(Point3AngularDimension)] = (e, t, i) => FillByNames(e, i, "angle", new[]
    {
      "CenterPoint", "ArcPoint", "XLine1Point", "XLine1End", "XLine2Point", "XLine2End",
    });
    r[typeof(OrdinateDimension)] = (e, t, i) => FillByNames(e, i, "ordinate", new[]
    {
      "Origin", "DefiningPoint", "LeaderEndPoint", "UsingXAxis",
    });
    r[typeof(ArcDimension)] = (e, t, i) => FillByNames(e, i, "arcLength", new[]
    {
      "Center", "ArcPoint", "XLine1Point", "XLine2Point", "ArcStartPoint", "ArcEndPoint",
    });

    // ---- curves / geometry -------------------------------------------------
    r[typeof(Line)] = (e, t, i) =>
    {
      var x = (Line)e;
      i["startPoint"] = Pt(x.StartPoint);
      i["endPoint"] = Pt(x.EndPoint);
      i["length"] = x.Length;
      i["angle"] = x.Angle;
      i["thickness"] = x.Thickness;
      i["normal"] = Vec(x.Normal);
    };

    r[typeof(Arc)] = (e, t, i) =>
    {
      var x = (Arc)e;
      i["center"] = Pt(x.Center);
      i["radius"] = x.Radius;
      i["startAngle"] = x.StartAngle;
      i["endAngle"] = x.EndAngle;
      i["totalAngle"] = x.TotalAngle;
      i["length"] = x.Length;
      i["startPoint"] = Pt(x.StartPoint);
      i["endPoint"] = Pt(x.EndPoint);
      i["thickness"] = x.Thickness;
    };

    r[typeof(Circle)] = (e, t, i) =>
    {
      var x = (Circle)e;
      i["center"] = Pt(x.Center);
      i["radius"] = x.Radius;
      i["diameter"] = x.Radius * 2;
      i["circumference"] = x.Circumference;
      i["area"] = x.Area;
      i["normal"] = Vec(x.Normal);
      i["thickness"] = x.Thickness;
    };

    r[typeof(Ellipse)] = (e, t, i) =>
    {
      var x = (Ellipse)e;
      i["center"] = Pt(x.Center);
      i["majorRadius"] = x.MajorRadius;
      i["minorRadius"] = x.MinorRadius;
      i["radiusRatio"] = x.RadiusRatio;
      i["startAngle"] = Safe<double?>(() => x.StartAngle, null);
      i["endAngle"] = Safe<double?>(() => x.EndAngle, null);
      i["startParam"] = Safe<double?>(() => x.StartParam, null);
      i["endParam"] = Safe<double?>(() => x.EndParam, null);
      i["majorAxis"] = Safe(() => x.MajorAxis, (Vector3d?)null) is Vector3d v ? Vec(v) : null;
      i["normal"] = Vec(x.Normal);
    };

    // Helix before Spline: lookup starts at the runtime type, so Helix wins; both are registered.
    r[typeof(Helix)] = (e, t, i) =>
    {
      var x = (Helix)e;
      i["startPoint"] = Pt(x.StartPoint);
      i["axisVector"] = Vec(x.AxisVector);
      i["baseRadius"] = x.BaseRadius;
      i["topRadius"] = x.TopRadius;
      i["turns"] = x.Turns;
      i["turnHeight"] = x.TurnHeight;
      i["clockwise"] = Safe<bool?>(() => x.Twist, null);
      i["constrain"] = Safe(() => x.Constrain.ToString(), null);
      i["height"] = Safe<double?>(() => x.Turns * x.TurnHeight, null);
      i["note"] = "helix (Spline subclass) -- geometry fields below are the underlying NURBS";
      AddSplineCore(x, i);
    };

    r[typeof(Spline)] = (e, t, i) => AddSplineCore((Spline)e, i);

    r[typeof(Polyline)] = (e, t, i) =>
    {
      var x = (Polyline)e;
      i["vertexCount"] = x.NumberOfVertices;
      i["closed"] = x.Closed;
      i["area"] = Safe<double?>(() => x.Area, null);
      i["length"] = Safe<double?>(() => x.Length, null);
      i["elevation"] = Safe<double?>(() => x.Elevation, null);
      i["hasBulges"] = Safe<bool?>(() => x.HasBulges, null);
      i["hasWidth"] = Safe<bool?>(() => x.HasWidth, null);
      var verts = new List<Dictionary<string, object?>>();
      var bulges = new List<double>();
      try
      {
        for (var vi = 0; vi < x.NumberOfVertices; vi++)
        {
          var p = x.GetPoint2dAt(vi);
          verts.Add(new Dictionary<string, object?>
          {
            ["x"] = p.X, ["y"] = p.Y,
            ["z"] = Safe(() => x.GetPoint3dAt(vi).Z, 0.0),
            ["bulge"] = x.GetBulgeAt(vi),
            ["startWidth"] = Safe(() => x.GetStartWidthAt(vi), 0.0),
            ["endWidth"] = Safe(() => x.GetEndWidthAt(vi), 0.0),
          });
          bulges.Add(x.GetBulgeAt(vi));
        }
      }
      catch { }
      if (verts.Count > 0) { i["vertices"] = verts; i["bulges"] = bulges; }
    };

    r[typeof(Polyline2d)] = (e, t, i) =>
    {
      var x = (Polyline2d)e;
      i["closed"] = x.Closed;
      i["elevation"] = Safe<double?>(() => x.Elevation, null);
      var verts = ReadSubEntityPositions(x, t, "Autodesk.AutoCAD.DatabaseServices.Vertex2d");
      if (verts != null) { i["vertexCount"] = verts.Count; i["vertices"] = verts; }
    };

    r[typeof(Polyline3d)] = (e, t, i) =>
    {
      var x = (Polyline3d)e;
      i["closed"] = x.Closed;
      i["polylineType"] = Safe(() => x.PolyType.ToString(), null);
      var verts = ReadSubEntityPositions(x, t, "Autodesk.AutoCAD.DatabaseServices.PolylineVertex3d");
      if (verts != null) { i["vertexCount"] = verts.Count; i["vertices"] = verts; }
    };

    r[typeof(DBPoint)] = (e, t, i) =>
    {
      var x = (DBPoint)e;
      i["position"] = Pt(x.Position);
      i["normal"] = Vec(x.Normal);
      i["thickness"] = x.Thickness;
      i["angle"] = Prop(x, "Angle");
    };

    r[typeof(Xline)] = (e, t, i) =>
    {
      var x = (Xline)e;
      i["basePoint"] = Pt(x.BasePoint);
      i["unitDirection"] = Prop(x, "UnitDirection");
      i["isInfinite"] = true;
    };

    r[typeof(Ray)] = (e, t, i) =>
    {
      var x = (Ray)e;
      i["basePoint"] = Pt(x.BasePoint);
      i["unitDirection"] = Prop(x, "UnitDirection");
      i["isInfinite"] = "half-line";
    };

    r[typeof(AcRegion)] = (e, t, i) =>
    {
      var x = (AcRegion)e;
      i["area"] = Safe<double?>(() => x.Area, null);
      i["perimeter"] = Safe<double?>(() => x.Perimeter, null);
      i["normal"] = Safe(() => x.Normal, (Vector3d?)null) is Vector3d v ? Vec(v) : null;
      FillMassProperties(x, i);
    };

    r[typeof(AcSurface)] = (e, t, i) =>
    {
      var x = (AcSurface)e;
      i["area"] = Prop(x, "Area");
      i["uIsolineDensity"] = Prop(x, "UIsolineDensity");
      i["vIsolineDensity"] = Prop(x, "VIsolineDensity");
      i["note"] = "AcDbSurface (NURBS/procedural)";
    };

    r[typeof(SubDMesh)] = (e, t, i) =>
    {
      var x = (SubDMesh)e;
      i["numberOfVertices"] = Prop(x, "NumberOfVertices") ?? PropCount(x, "Vertices");
      i["numberOfFaces"] = Prop(x, "NumberOfFaces") ?? PropCount(x, "FaceList");
      i["surfaceArea"] = Prop(x, "SurfaceArea");
      i["volume"] = Prop(x, "Volume");
      i["subDivisionLevel"] = Prop(x, "SubDivisionLevel");
      i["note"] = "SubD mesh";
    };

    r[typeof(PolyFaceMesh)] = (e, t, i) =>
    {
      var x = (PolyFaceMesh)e;
      i["numVertices"] = Safe<int?>(() => x.NumVertices, null);
      i["numFaces"] = Safe<int?>(() => x.NumFaces, null);
      i["note"] = "PolyFaceMesh";
    };

    r[typeof(PolygonMesh)] = (e, t, i) =>
    {
      var x = (PolygonMesh)e;
      i["mSize"] = Safe<int?>(() => x.MSize, null);
      i["nSize"] = Safe<int?>(() => x.NSize, null);
      i["closedInMDirection"] = Prop(x, "ClosedInMDirection") ?? Prop(x, "MClosed");
      i["closedInNDirection"] = Prop(x, "ClosedInNDirection") ?? Prop(x, "NClosed");
      i["note"] = "PolygonMesh";
    };

    r[typeof(Face)] = (e, t, i) =>
    {
      var x = (Face)e;
      var pts = new List<Dictionary<string, object?>>();
      for (short vi = 0; vi < 4; vi++) { try { pts.Add(Pt(x.GetVertexAt(vi))); } catch { } }
      if (pts.Count > 0) i["vertices"] = pts;
      i["note"] = "3D face";
    };

    r[typeof(Solid)] = (e, t, i) =>
    {
      var x = (Solid)e;
      var pts = new List<Dictionary<string, object?>>();
      for (short vi = 0; vi < 4; vi++) { try { pts.Add(Pt(x.GetPointAt(vi))); } catch { } }
      if (pts.Count > 0) i["vertices"] = pts;
      i["note"] = "2D solid";
    };

    r[typeof(Trace)] = (e, t, i) =>
    {
      var x = (Trace)e;
      var pts = new List<Dictionary<string, object?>>();
      for (short vi = 0; vi < 4; vi++) { try { pts.Add(Pt(x.GetPointAt(vi))); } catch { } }
      if (pts.Count > 0) i["vertices"] = pts;
      i["note"] = "trace";
    };

    r[typeof(Solid3d)] = (e, t, i) =>
    {
      FillMassProperties(e, i);
      i["note"] = "3D solid";
    };

    r[typeof(Body)] = (e, t, i) =>
    {
      i["area"] = Prop(e, "Area");
      FillMassProperties(e, i);
      i["note"] = "body";
    };

    r[typeof(BlockReference)] = (e, t, i) =>
    {
      var x = (BlockReference)e;
      i["blockName"] = x.Name;
      i["position"] = Pt(x.Position);
      i["rotation"] = x.Rotation;
      i["scale"] = Vec(x.ScaleFactors);
      i["isDynamicBlock"] = Safe<bool?>(() => x.IsDynamicBlock, null);
      i["isAnonymous"] = Prop(x, "IsAnonymous") ?? Prop(x, "Anonymous");
      i["attributeCount"] = Safe<int?>(() => x.AttributeCollection.Count, null);
    };

    // ---- raster / underlay -------------------------------------------------
    r[typeof(RasterImage)] = (e, t, i) =>
    {
      var x = (RasterImage)e;
      i["imageSize"] = Prop(x, "ImageSize");
      i["width"] = Safe<double?>(() => x.Width, null);
      i["height"] = Safe<double?>(() => x.Height, null);
      i["rotation"] = Safe<double?>(() => x.Rotation, null);
      i["position"] = Safe(() => x.Position, (Point3d?)null) is Point3d p ? Pt(p) : null;
      i["brightness"] = Safe<int?>(() => x.Brightness, null);
      i["contrast"] = Safe<int?>(() => x.Contrast, null);
      i["fade"] = Safe<int?>(() => x.Fade, null);
      i["imageFile"] = Safe(() => ((RasterImageDef)t.GetObject(x.ImageDefId, OpenMode.ForRead)).SourceFileName, null);
      i["note"] = e is Wipeout ? "wipeout" : "raster image";
    };

    // PDF / DWF / DGN underlays: their managed types live in underlay assemblies that are not
    // part of the plug-in reference set, so these fall through to the reflection fallback
    // (position/rotation/scale still come out, the underlay definition does not).

    // ---- misc --------------------------------------------------------------
    r[typeof(Ole2Frame)] = (e, t, i) =>
    {
      var x = (Ole2Frame)e;
      i["oleType"] = Safe(() => x.Type.ToString(), null);
      i["position"] = Prop(x, "Position");
      i["note"] = "OLE frame";
    };

    r[typeof(Shape)] = (e, t, i) =>
    {
      var x = (Shape)e;
      i["name"] = Safe(() => x.Name, null);
      i["size"] = Safe<double?>(() => x.Size, null);
      i["rotation"] = Safe<double?>(() => x.Rotation, null);
      i["position"] = Safe(() => x.Position, (Point3d?)null) is Point3d p ? Pt(p) : null;
    };

    r[typeof(Viewport)] = (e, t, i) =>
    {
      var x = (Viewport)e;
      i["number"] = Safe<int?>(() => x.Number, null);
      i["centerPoint"] = Safe(() => x.CenterPoint, (Point3d?)null) is Point3d c ? Pt(c) : null;
      i["width"] = Safe<double?>(() => x.Width, null);
      i["height"] = Safe<double?>(() => x.Height, null);
      i["customScale"] = Safe<double?>(() => x.CustomScale, null);
      i["viewCenter"] = Prop(x, "ViewCenter");
      i["viewHeight"] = Safe<double?>(() => x.ViewHeight, null);
      i["note"] = "paper-space viewport";
    };

    r[typeof(ProxyEntity)] = (e, t, i) =>
    {
      i["isProxy"] = true;
      i["classId"] = Safe(() => ((ProxyEntity)e).ClassID.ToString(), null);
      i["note"] = "proxy entity -- its class lives in an unloaded ObjectARX module, only generic properties are readable";
    };

    return r;
  }

  // ------------------------------------------------------------------ shared readers

  private static void FillByNames(Entity e, Dictionary<string, object?> i, string kind, string[] names)
  {
    i["measurementKind"] = kind;
    foreach (var n in names)
    {
      var v = Prop(e, n);
      if (v != null) i[char.ToLowerInvariant(n[0]) + n.Substring(1)] = v;
    }
    i["dimensionStyle"] = Prop(e, "DimensionStyleName");
  }

  private static void AddSplineCore(Spline x, Dictionary<string, object?> i)
  {
    i["degree"] = Safe<int?>(() => x.Degree, null);
    i["closed"] = Safe<bool?>(() => x.Closed, null);
    i["planar"] = Safe<bool?>(() => x.IsPlanar, null);
    i["rational"] = Safe<bool?>(() => x.IsRational, null);
    i["periodic"] = Safe<bool?>(() => x.IsPeriodic, null);
    i["numberOfControlPoints"] = Safe<int?>(() => x.NumControlPoints, null);
    i["numberOfFitPoints"] = Safe<int?>(() => x.NumFitPoints, null);
    var cps = new List<Dictionary<string, object?>>();
    var fpts = new List<Dictionary<string, object?>>();
    try { for (var n = 0; n < x.NumControlPoints; n++) cps.Add(Pt(x.GetControlPointAt(n))); } catch { }
    try { for (var n = 0; n < x.NumFitPoints; n++) fpts.Add(Pt(x.GetFitPointAt(n))); } catch { }
    if (cps.Count > 0) i["controlPoints"] = cps;
    if (fpts.Count > 0) i["fitPoints"] = fpts;
    i["startPoint"] = Safe(() => x.StartPoint, (Point3d?)null) is Point3d sp ? Pt(sp) : null;
    i["endPoint"] = Safe(() => x.EndPoint, (Point3d?)null) is Point3d ep ? Pt(ep) : null;
  }

  private static void FillMassProperties(Entity e, Dictionary<string, object?> i)
  {
    try
    {
      var mp = Civil3DCompatibility.GetPropertyValue(e, "MassProperties");
      if (mp == null) { i["massPropertiesUnavailable"] = true; return; }
      i["volume"] = Prop(mp, "Volume");
      i["massPropertiesArea"] = Prop(mp, "SurfaceArea") ?? Prop(mp, "Area");
      var c = Prop(mp, "Centroid");
      if (c != null) i["centroid"] = c;
    }
    catch
    {
      i["volume"] = Prop(e, "Volume");
      i["massPropertiesArea"] = Prop(e, "Area");
      i["massPropertiesUnavailable"] = true;
    }
  }

  private static void FillUnderlay(Entity x, Dictionary<string, object?> i, string kind)
  {
    i["underlayKind"] = kind;
    i["name"] = Prop(x, "Name");
    i["position"] = Prop(x, "Position");
    i["rotation"] = Prop(x, "Rotation");
    i["scaleFactors"] = Prop(x, "ScaleFactors");
    i["contrast"] = Prop(x, "Contrast");
    i["fade"] = Prop(x, "Fade");
    i["isOn"] = Prop(x, "Enabled");
    i["isMonochrome"] = Prop(x, "Monochrome");
  }

  /// <summary>Walk a 2D/3D polyline's vertex sub-entities through the transaction.</summary>
  private static List<Dictionary<string, object?>>? ReadSubEntityPositions(Entity owner, Transaction tr, string vertexTypeName)
  {
    try
    {
      var list = new List<Dictionary<string, object?>>();
      if (owner is not System.Collections.IEnumerable ids) return list;
      foreach (var o in ids)
      {
        if (o is not ObjectId vid || vid.IsNull) continue;
        var v = tr.GetObject(vid, OpenMode.ForRead);
        if (v.GetType().FullName != vertexTypeName) continue;
        var pos = Civil3DCompatibility.GetPropertyValue(v, "Position");
        var pt = pos switch
        {
          Point3d p3 => Pt(p3),
          Point2d p2 => Pt(p2),
          _ => null,
        };
        if (pt != null) list.Add(pt);
      }
      return list;
    }
    catch { return null; }
  }
}
