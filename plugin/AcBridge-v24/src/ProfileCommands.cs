using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class ProfileCommands
{
  public static Task<object?> ListProfilesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead))
        .Select(ToProfileSummary)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profiles"] = profiles,
      };
    });
  }

  public static Task<object?> GetProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var entities = ReadProfileEntities(profile);

      return new Dictionary<string, object?>
      {
        ["name"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["type"] = MapProfileType(profile.ProfileType.ToString()),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(profile.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = profile.Layer,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = GetElevationExtents(profile).Min,
        ["maxElevation"] = GetElevationExtents(profile).Max,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["pviCount"] = CountPvis(profile),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = CivilObjectUtils.LinearUnits(database),
          ["vertical"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  public static Task<object?> GetProfileElevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var elevation = ReadProfileElevation(profile, station);
      var grade = ReadProfileGrade(profile, station);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["elevation"] = elevation,
        ["grade"] = grade,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> SampleProfileElevationsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    // 2026-11-16 P9: interval 与 stations 二选一 —— 以前只认 interval，
    //   同一个"按桩号采样"的动作，alignmentSampleStations 用 stations[]、这里要 interval，
    //   调用方（面板 AI 尤其）很容易猜错并拿到 "Missing required parameter 'interval'"。
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval");
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;
    if (interval == null && (stationsNode == null || stationsNode.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "sampleProfileElevations requires either 'interval' (regular sampling) or 'stations' (explicit list of stations).");
    var explicitStations = stationsNode != null && stationsNode.Count > 0
      ? stationsNode.Select(n => PluginRuntime.GetRequiredDoubleFromNode(n, "stations[]")).ToList()
      : null;
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var from = startStation ?? profile.StartingStation;
      var to = endStation ?? profile.EndingStation;
      var samples = new List<Dictionary<string, object?>>();
      // 2026-11-17 P22: 显式桩号超出 profile 范围时以前静默丢弃（传 4 个只回 2 个，sampleCount=2，无任何说明）
      //   —— 调用方会以为"这些桩号就是没有高程"。现在逐个回 skippedStations + hint。
      var skippedStations = new List<double>();

      void AddSample(double station)
      {
        samples.Add(new Dictionary<string, object?>
        {
          ["station"] = station,
          ["elevation"] = ReadProfileElevation(profile, station),
          ["grade"] = ReadProfileGrade(profile, station),
        });
      }

      if (explicitStations != null)
      {
        foreach (var station in explicitStations)
        {
          if (station < profile.StartingStation - 1e-6 || station > profile.EndingStation + 1e-6)
          {
            skippedStations.Add(station);
            continue;
          }
          AddSample(station);
        }
      }
      else
      {
        for (var station = from; station <= to; station += interval!.Value)
          AddSample(station);
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["startStation"] = from,
        ["endStation"] = to,
        ["mode"] = explicitStations != null ? "stations" : "interval",
        ["interval"] = interval,
        ["sampleCount"] = samples.Count,
        ["requestedCount"] = explicitStations != null ? explicitStations.Count : (int?)null,
        ["profileStartStation"] = profile.StartingStation,
        ["profileEndStation"] = profile.EndingStation,
        ["skippedStationCount"] = skippedStations.Count,
        ["skippedStations"] = skippedStations,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["hint"] = skippedStations.Count == 0
          ? null
          : "skippedStations are outside the profile range " + Math.Round(profile.StartingStation, 4) + ".."
            + Math.Round(profile.EndingStation, 4) + "; returned " + samples.Count + " of "
            + (explicitStations?.Count ?? 0) + " requested. Extend the profile (createProfileFromSurface / profileAddPvis)"
            + " or sample inside the range instead of reading these as missing elevations.",
      };
    });
  }

  public static Task<object?> CreateProfileFromSurfaceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateLayoutProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> DeleteProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      profile.Erase();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profileName,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToProfileSummary(Profile profile)
  {
    var extents = GetElevationExtents(profile);
    return new Dictionary<string, object?>
    {
      ["name"] = profile.Name,
      ["handle"] = CivilObjectUtils.GetHandle(profile),
      ["type"] = MapProfileType(profile.ProfileType.ToString()),
      ["style"] = string.Empty,
      ["startStation"] = profile.StartingStation,
      ["endStation"] = profile.EndingStation,
      ["minElevation"] = extents.Min,
      ["maxElevation"] = extents.Max,
    };
  }

  private static List<Dictionary<string, object?>> ReadProfileEntities(Profile profile)
  {
    var entities = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (ProfileEntity entity in profile.Entities)
    {
      entities.Add(new Dictionary<string, object?>
      {
        ["index"] = index++,
        ["type"] = MapProfileEntityType(entity.EntityType.ToString()),
        ["startStation"] = entity.StartStation,
        ["endStation"] = entity.EndStation,
        ["startElevation"] = entity.StartElevation,
        ["endElevation"] = entity.EndElevation,
        ["grade"] = entity is ProfileTangent tangent ? tangent.Grade : null,
        ["length"] = entity.Length,
      });
    }

    return entities;
  }

  private static (double Min, double Max) GetElevationExtents(Profile profile)
  {
    // Civil 3D calculates profile-wide extents, including interior vertical
    // curve extrema and the numeric fallback required for empty layouts.
    return (profile.ElevationMin, profile.ElevationMax);
  }

  private static int CountPvis(Profile profile)
  {
    return profile.PVIs.Count;
  }

  private static double ReadProfileElevation(Profile profile, double station)
  {
    try
    {
      return profile.ElevationAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read elevation on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static double ReadProfileGrade(Profile profile, double station)
  {
    try
    {
      return profile.GradeAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read grade on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static string MapProfileType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("surface"))
    {
      return "surface";
    }

    if (text.Contains("super"))
    {
      return "superimposed";
    }

    return "layout";
  }

  private static string MapProfileEntityType(string value)
  {
    var text = value.ToLowerInvariant();
    if (text.Contains("asymmetric"))
    {
      return "asymmetric_parabola";
    }

    if (text.Contains("parabola"))
    {
      return "parabola";
    }

    if (text.Contains("curve"))
    {
      return "circular_curve";
    }

    return "tangent";
  }
}
