// ========== 3D solids: helix + shell (2026-10-29) ==========
// Both are real managed APIs on 25.0.58 (verified by reflection against the installed
// assemblies -- see tools/api-probe):
//   Helix : StartPoint / AxisVector / BaseRadius / TopRadius / Turns / TurnHeight / Twist
//           + CreateHelix()  (inherits Spline)
//   Solid3d.ShellBody(SubentityId[] facesToRemove, double offsetDistance)
//           SubentityType.Face == 1
// sliceSolid / revolveSolid live in AcadCommands.cs and were upgraded the same day
// (arbitrary 3D cutting plane, 3D revolve axis) -- kept there to avoid two homes per method.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.BoundaryRepresentation;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BrepFace = Autodesk.AutoCAD.BoundaryRepresentation.Face;   // "Face" is ambiguous with DatabaseServices.Face

namespace Civil3DMcpPlugin;

public static partial class AcadCommands
{
  // createHelix {center:[x,y,z], baseRadius, height, turns?|turnHeight?, topRadius?, axis?:[x,y,z], clockwise?}
  // Either turns or turnHeight is enough: the other one is derived from height so that
  // "height 100, 5 turns" always means 5 full turns over 100 units.
  public static Task<object?> CreateHelixAsync(JsonObject? parameters)
  {
    var centerArr = parameters?["center"] as JsonArray;
    var axisArr = parameters?["axis"] as JsonArray;
    var baseRadius = PluginRuntime.GetRequiredDouble(parameters, "baseRadius");
    var height = PluginRuntime.GetRequiredDouble(parameters, "height");
    var topRadiusOpt = PluginRuntime.GetOptionalDouble(parameters, "topRadius");
    var turnsOpt = PluginRuntime.GetOptionalDouble(parameters, "turns");
    var turnHeightOpt = PluginRuntime.GetOptionalDouble(parameters, "turnHeight");
    var clockwise = PluginRuntime.GetOptionalBool(parameters, "clockwise") ?? false;

    if (centerArr == null || centerArr.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createHelix requires center:[x,y] or [x,y,z] (base centre point).");
    if (baseRadius <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createHelix requires baseRadius > 0.");
    if (height <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createHelix requires height > 0.");
    if (turnsOpt == null && turnHeightOpt == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createHelix requires turns or turnHeight (one of them).");
    if (turnsOpt is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "turns must be > 0.");
    if (turnHeightOpt is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "turnHeight must be > 0.");

    var cx = centerArr[0]?.GetValue<double>() ?? 0;
    var cy = centerArr[1]?.GetValue<double>() ?? 0;
    var cz = centerArr.Count > 2 ? (centerArr[2]?.GetValue<double>() ?? 0) : 0;
    var ax = axisArr != null && axisArr.Count > 0 ? (axisArr[0]?.GetValue<double>() ?? 0) : 0;
    var ay = axisArr != null && axisArr.Count > 1 ? (axisArr[1]?.GetValue<double>() ?? 0) : 0;
    var az = axisArr != null && axisArr.Count > 2 ? (axisArr[2]?.GetValue<double>() ?? 1) : 1;

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      var axisVec = new Vector3d(ax, ay, az);
      if (axisVec.Length < 1e-9)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "axis must not be zero length.");

      var turns = turnsOpt ?? (height / turnHeightOpt!.Value);
      var turnHeight = turnHeightOpt ?? (height / turns);

      var helix = new Helix();
      helix.SetDatabaseDefaults();
      helix.StartPoint = new Point3d(cx, cy, cz);
      helix.AxisVector = axisVec.GetNormal();
      helix.BaseRadius = baseRadius;
      helix.TopRadius = topRadiusOpt is > 0 ? topRadiusOpt.Value : baseRadius;
      helix.Twist = clockwise;
      // Constrain tells AutoCAD which of the three related values is the driven one.
      helix.Constrain = ConstrainType.Turns;
      helix.Turns = turns;
      helix.TurnHeight = turnHeight;
      try
      {
        helix.CreateHelix();
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        helix.Dispose();
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "createHelix: AutoCAD rejected the parameters (" + ex.ErrorStatus + "). Check baseRadius/topRadius, height and turns/turnHeight.");
      }

      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      var id = modelSpace.AppendEntity(helix);
      transaction.AddNewlyCreatedDBObject(helix, true);

      var created = CivilObjectUtils.GetRequiredObject<Helix>(transaction, id, OpenMode.ForRead);
      var extents = created.GeometricExtents;
      return (object?)new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["type"] = "Helix",
        ["turns"] = Math.Round(turns, 6),
        ["turnHeight"] = Math.Round(turnHeight, 6),
        ["baseRadius"] = Math.Round(baseRadius, 6),
        ["topRadius"] = Math.Round(created.TopRadius, 6),
        // measured, not echoed: proves the curve really rose by `height`
        ["measuredHeight"] = Math.Round(extents.MaxPoint.Z - extents.MinPoint.Z, 6),
      };
    });
  }

  // BoundBlock3d is an ORIENTED box: BasePoint + three edge vectors. There is no MinPoint/MaxPoint,
  // so the world Z range has to be derived from the 8 corners.
  private static (double minZ, double maxZ) ZRangeOf(BoundBlock3d bb)
  {
    var b = bb.BasePoint;
    var d1 = bb.Direction1;
    var d2 = bb.Direction2;
    var d3 = bb.Direction3;
    var minZ = double.PositiveInfinity;
    var maxZ = double.NegativeInfinity;
    for (var i = 0; i <= 1; i++)
      for (var j = 0; j <= 1; j++)
        for (var k = 0; k <= 1; k++)
        {
          var z = b.Z + i * d1.Z + j * d2.Z + k * d3.Z;
          if (z < minZ) minZ = z;
          if (z > maxZ) maxZ = z;
        }
    return (minZ, maxZ);
  }

  // listSolidFaces {handle}
  // Face enumeration lives in acdbmgdbrep.dll (Autodesk.AutoCAD.BoundaryRepresentation). It is NOT
  // in acdbmgd/acmgd/accoremgd -- that is why the first version of shellSolid could not tell the
  // caller which face ids exist. Each BREP face carries SubentityPath.SubentId.Index, which is
  // exactly the id ShellBody wants.
  public static Task<object?> ListSolidFacesAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var solid = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, handle), OpenMode.ForRead);
      var faces = new List<object?>();
      var faceErrors = new List<string>();
      Autodesk.AutoCAD.BoundaryRepresentation.Brep brep;
      try
      {
        brep = new Brep(solid);
      }
      catch (System.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "listSolidFaces: new Brep() failed for this handle (" + Describe(ex) + "). Only 3D solids expose faces.");
      }
      using (brep)
      {
        var i = 0;
        foreach (BrepFace face in brep.Faces)
        {
          i++;
          // 2026-10-29 real-machine finding: Face.SubentityPath throws MissingSubentity for faces
          // obtained from Brep.Enumeration (and BoundBlock is not guaranteed either). What IS
          // reliable: the enumeration order is 1-based and matches the SubentityId that
          // Solid3d.ShellBody expects (measured: extruded circle = 3 faces, ids 1..3 accepted,
          // 0 / 4+ rejected). So the index is ours to assign; geometry is best-effort extra.
          var entry = new Dictionary<string, object?> { ["index"] = i };
          try
          {
            var zr = ZRangeOf(face.BoundBlock);
            entry["minZ"] = Math.Round(zr.minZ, 6);
            entry["maxZ"] = Math.Round(zr.maxZ, 6);
          }
          catch (System.Exception ex) { entry["boundsError"] = Describe(ex); }
          try { entry["area"] = Math.Round(face.GetSurfaceArea(), 6); }
          catch (System.Exception ex) { entry["areaError"] = Describe(ex); }
          try
          {
            var sp = face.SubentityPath;
            entry["subentityIndex"] = sp.IsNull ? (int?)null : (int?)sp.SubentId.Index;
          }
          catch (System.Exception ex) { entry["subentityError"] = Describe(ex); }
          faces.Add(entry);
        }
      }
      return (object?)new Dictionary<string, object?>
      {
        ["handle"] = handle,
        ["faceCount"] = faces.Count,
        ["faces"] = faces,
        ["faceErrors"] = faceErrors,
      };
    });
  }

  // BREP exceptions carry the real reason in ErrorStatus; Message is often useless.
  private static string Describe(System.Exception ex)
  {
    if (ex is Autodesk.AutoCAD.BoundaryRepresentation.Exception brepEx) return "ErrorStatus=" + brepEx.ErrorStatus + ", " + brepEx.Message;
    if (ex is Autodesk.AutoCAD.Runtime.Exception acadEx) return "ErrorStatus=" + acadEx.ErrorStatus + ", " + acadEx.Message;
    return ex.GetType().Name + ": " + ex.Message;
  }

  // shellSolid {handle, thickness, removeFaces?|removeTop?, removeBottom?}
  // removeFaces wins if given; otherwise removeTop/removeBottom pick the face by geometry
  // (highest / lowest maxZ, tie-break by larger area).
  public static Task<object?> ShellSolidAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var thickness = PluginRuntime.GetRequiredDouble(parameters, "thickness");
    var facesArr = parameters?["removeFaces"] as JsonArray;
    var removeTop = PluginRuntime.GetOptionalBool(parameters, "removeTop") ?? false;
    var removeBottom = PluginRuntime.GetOptionalBool(parameters, "removeBottom") ?? false;
    if (thickness <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "shellSolid requires thickness > 0 (wall thickness, inward).");

    var faces = new List<long>();
    if (facesArr != null)
      foreach (var node in facesArr)
        if (node != null) faces.Add(node.GetValue<long>());

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
    {
      var solid = CivilObjectUtils.GetRequiredObject<Solid3d>(transaction, HandleToObjectId(database, handle), OpenMode.ForWrite);

      var resolved = new List<long>(faces);
      if (resolved.Count == 0 && (removeTop || removeBottom))
      {
        using var brepTop = new Brep(solid);
        long bestTop = -1, bestBottom = -1;
        double bestTopZ = double.NegativeInfinity, bestBottomZ = double.PositiveInfinity;
        double bestTopArea = -1, bestBottomArea = -1;
        var seen = 0;
        foreach (BrepFace face in brepTop.Faces)
        {
          seen++;
          try
          {
            var zr = ZRangeOf(face.BoundBlock);
            double area;
            try { area = face.GetSurfaceArea(); } catch (System.Exception) { area = 0; }
            var idx = seen;
            if (zr.maxZ > bestTopZ + 1e-9 || (Math.Abs(zr.maxZ - bestTopZ) <= 1e-9 && area > bestTopArea))
            { bestTopZ = zr.maxZ; bestTopArea = area; bestTop = idx; }
            if (zr.minZ < bestBottomZ - 1e-9 || (Math.Abs(zr.minZ - bestBottomZ) <= 1e-9 && area > bestBottomArea))
            { bestBottomZ = zr.minZ; bestBottomArea = area; bestBottom = idx; }
          }
          catch (System.Exception)
          {
            // one unreadable face must not stop the shell: keep scanning the rest
          }
        }
        if (removeTop && bestTop < 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            "shellSolid: could not read face geometry for removeTop (" + seen + " face(s) seen). Pass removeFaces:[id...] explicitly (ids are 1..N, see listSolidFaces).");
        if (removeTop && bestTop >= 0 && !resolved.Contains(bestTop)) resolved.Add(bestTop);
        if (removeBottom && bestBottom >= 0 && !resolved.Contains(bestBottom)) resolved.Add(bestBottom);
      }

      if (resolved.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "shellSolid needs at least one face to open: pass removeFaces:[id...] (see listSolidFaces) or removeTop / removeBottom.");

      var ids = resolved.Select(i => new SubentityId(SubentityType.Face, i)).ToArray();
      try
      {
        solid.ShellBody(ids, thickness);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "shellSolid: AutoCAD rejected face id(s) [" + string.Join(",", resolved) + "] (" + ex.ErrorStatus + "). " +
          "Use listSolidFaces to get the real ids. Nothing was changed.");
      }
      return (object?)new Dictionary<string, object?>
      {
        ["shell"] = handle,
        ["thickness"] = thickness,
        ["removedFaces"] = ids.Length,
        ["faceIds"] = resolved.ToArray(),
        ["type"] = "Solid3d",
      };
    });
  }
}
