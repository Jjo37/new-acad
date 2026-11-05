using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Typed Civil 3D 2026 intersection queries. The managed API exposes
/// Intersection inspection, but it does not expose an Intersection.Create
/// factory; creation therefore returns an explicit capability error.
/// </summary>
public static class IntersectionCommands
{
  public static Task<object?> ListIntersectionsAsync(JsonObject? parameters)
  {
    var siteNameFilter = PluginRuntime.GetOptionalString(parameters, "siteName");
    if (!string.IsNullOrWhiteSpace(siteNameFilter))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D 2026 intersections do not expose site membership through the managed API, so siteName cannot be used as an intersection filter.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var intersections = new List<Dictionary<string, object?>>();

      foreach (ObjectId id in civilDoc.GetIntersectionIds())
      {
        var intersection = CivilObjectUtils.GetRequiredObject<Intersection>(transaction, id, OpenMode.ForRead);
        var roads = intersection.IntersectionRoads.ToList();

        intersections.Add(new Dictionary<string, object?>
        {
          ["name"] = intersection.Name,
          ["handle"] = CivilObjectUtils.GetHandle(intersection),
          ["intersectionX"] = intersection.Location.X,
          ["intersectionY"] = intersection.Location.Y,
          ["mainRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineAlignmentId),
          ["intersectingRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineAlignmentId),
        });
      }

      return new Dictionary<string, object?> { ["intersections"] = intersections };
    });
  }

  public static Task<object?> CreateIntersectionAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "name");
    _ = PluginRuntime.GetRequiredString(parameters, "mainRoadAlignment");
    _ = PluginRuntime.GetRequiredString(parameters, "mainRoadProfile");
    _ = PluginRuntime.GetRequiredString(parameters, "intersectingRoadAlignment");
    _ = PluginRuntime.GetRequiredString(parameters, "intersectingRoadProfile");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D 2026 does not expose an Intersection.Create method in the managed .NET API. No intersection or related corridor data was created; use the Civil 3D Create Intersection command for this operation.");
  }

  public static Task<object?> GetIntersectionAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var includeCorridorInfo = PluginRuntime.GetOptionalBool(parameters, "includeCorridorInfo") ?? false;
    var includeCurbReturns = PluginRuntime.GetOptionalBool(parameters, "includeCurbReturns") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      foreach (ObjectId id in civilDoc.GetIntersectionIds())
      {
        var intersection = CivilObjectUtils.GetRequiredObject<Intersection>(transaction, id, OpenMode.ForRead);
        if (!string.Equals(intersection.Name, name, StringComparison.OrdinalIgnoreCase))
          continue;

        var roads = intersection.IntersectionRoads.ToList();
        var result = new Dictionary<string, object?>
        {
          ["name"] = intersection.Name,
          ["handle"] = CivilObjectUtils.GetHandle(intersection),
          ["intersectionX"] = intersection.Location.X,
          ["intersectionY"] = intersection.Location.Y,
          ["mainRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineAlignmentId),
          ["mainRoadProfile"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineProfileId),
          ["intersectingRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineAlignmentId),
          ["intersectingRoadProfile"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineProfileId),
        };

        if (includeCurbReturns)
        {
          result["curbReturns"] = intersection.IntersectionRegions.Select(region =>
            new Dictionary<string, object?>
            {
              ["name"] = region.Name,
              ["angle"] = region.Angle,
              ["alignment"] = GetObjectName(transaction, region.CurbReturnAlignmentId),
              ["profile"] = GetObjectName(transaction, region.CurbReturnProfileId),
              ["inAlignment"] = GetObjectName(transaction, region.InAlignmentId),
              ["outAlignment"] = GetObjectName(transaction, region.OutAlignmentId),
            }).ToList();
        }

        if (includeCorridorInfo)
        {
          result["corridors"] = intersection.CorridorId.IsNull
            ? new List<string>()
            : new List<string> { GetObjectName(transaction, intersection.CorridorId) ?? intersection.CorridorId.Handle.ToString() };
        }

        return result;
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Intersection '{name}' was not found.");
    });
  }

  private static string? GetObjectName(Transaction transaction, ObjectId? id)
  {
    if (!id.HasValue || id.Value.IsNull)
      return null;

    return CivilObjectUtils.GetName(transaction.GetObject(id.Value, OpenMode.ForRead));
  }
}
