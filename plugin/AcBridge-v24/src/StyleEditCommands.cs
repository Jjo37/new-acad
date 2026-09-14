using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// 样式/标签集修改（2026-08-16 新增）：
///   setAlignmentStyle    — 修改已有路线的样式 + 应用标签集（ImportLabelSet）
///   setProfileStyle      — 修改已有纵断面的样式（Profile 无 ImportLabelSet，标签集走 ProfileView）
///   setProfileViewStyle  — 修改纵断面图框样式 + 带集（ImportBandSetStyle）
///
/// API 依据（25.0.58，api-inspect 反射确认）：
///   Alignment.StyleId / StyleName 可写；Alignment.ImportLabelSet(name|id) 存在
///   Profile.StyleId / StyleName 可写；Profile 无 ImportLabelSet
///   ProfileView.StyleId / StyleName 可写；ProfileView.Bands.ImportBandSetStyle(id) 存在
/// </summary>
public static class StyleEditCommands
{
  // -------------------------------------------------------------------------
  // setAlignmentStyle
  // -------------------------------------------------------------------------

  public static Task<object?> SetAlignmentStyleAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");
    var labelSetName = PluginRuntime.GetOptionalString(parameters, "labelSet");

    if (string.IsNullOrWhiteSpace(styleName) && string.IsNullOrWhiteSpace(labelSetName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "setAlignmentStyle requires at least one of 'style' or 'labelSet'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 找路线
      ObjectId alignmentId = ObjectId.Null;
      foreach (ObjectId id in civilDoc.GetAlignmentIds())
      {
        var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
        if (string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase))
        {
          alignmentId = id;
          break;
        }
      }
      if (alignmentId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment '{alignmentName}' was not found.");

      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForWrite);
      var result = new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["styleChanged"] = false,
        ["labelSetApplied"] = false,
      };

      // 改样式
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, styleName);
        if (styleId.IsNull)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Alignment style '{styleName}' was not found. Use listStyles with objectType 'alignment' to see available styles.");
        alignment.StyleId = styleId;
        result["styleChanged"] = true;
        result["style"] = styleName;
      }

      // 应用标签集
      if (!string.IsNullOrWhiteSpace(labelSetName))
      {
        var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelSetName);
        if (labelSetId.IsNull)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Alignment label set '{labelSetName}' was not found.");
        // 25.0.58 反射确认: Alignment.ImportLabelSet(ObjectId) 存在
        CivilObjectUtils.InvokeMethod(alignment, "ImportLabelSet", labelSetId);
        result["labelSetApplied"] = true;
        result["labelSet"] = labelSetName;
      }

      return result;
    });
  }

  // -------------------------------------------------------------------------
  // setProfileStyle
  // -------------------------------------------------------------------------

  public static Task<object?> SetProfileStyleAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");

    if (string.IsNullOrWhiteSpace(styleName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "setProfileStyle requires 'style'. (Profile 标签集只能在创建时指定；纵断面图的带集请用 setProfileViewStyle)");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 找路线
      ObjectId alignmentId = ObjectId.Null;
      foreach (ObjectId id in civilDoc.GetAlignmentIds())
      {
        var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
        if (string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase))
        {
          alignmentId = id;
          break;
        }
      }
      if (alignmentId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment '{alignmentName}' was not found.");

      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);

      // 找纵断面
      ObjectId profileId = ObjectId.Null;
      foreach (ObjectId pid in alignment.GetProfileIds())
      {
        var p = CivilObjectUtils.GetRequiredObject<Profile>(transaction, pid, OpenMode.ForRead);
        if (string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
        {
          profileId = pid;
          break;
        }
      }
      if (profileId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Profile '{profileName}' was not found on alignment '{alignmentName}'.");

      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, styleName);
      if (styleId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Profile style '{styleName}' was not found. Use listStyles with objectType 'profile' to see available styles.");

      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForWrite);
      profile.StyleId = styleId;

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["profileName"] = profileName,
        ["styleChanged"] = true,
        ["style"] = styleName,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setProfileViewStyle
  // -------------------------------------------------------------------------

  public static Task<object?> SetProfileViewStyleAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetName = PluginRuntime.GetOptionalString(parameters, "bandSet");

    if (string.IsNullOrWhiteSpace(styleName) && string.IsNullOrWhiteSpace(bandSetName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "setProfileViewStyle requires at least one of 'style' or 'bandSet'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // 找纵断面图框
      ObjectId pvId = ObjectId.Null;
      foreach (ObjectId aid in civilDoc.GetAlignmentIds())
      {
        var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
        foreach (ObjectId vid in a.GetProfileViewIds())
        {
          var pv = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, vid, OpenMode.ForRead);
          if (string.Equals(pv.Name, profileViewName, StringComparison.OrdinalIgnoreCase))
          {
            pvId = vid;
            break;
          }
        }
        if (!pvId.IsNull) break;
      }
      if (pvId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"ProfileView '{profileViewName}' was not found.");

      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, pvId, OpenMode.ForWrite);
      var result = new Dictionary<string, object?>
      {
        ["profileViewName"] = profileViewName,
        ["styleChanged"] = false,
        ["bandSetChanged"] = false,
      };

      // 改图框样式
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = LookupUtils.GetProfileViewStyleId(civilDoc, transaction, styleName);
        if (styleId.IsNull)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"ProfileView style '{styleName}' was not found. Use listStyles with objectType 'profileview' to see available styles.");
        profileView.StyleId = styleId;
        result["styleChanged"] = true;
        result["style"] = styleName;
      }

      // 换带集（纵断面图框的数据带：高程/桩号/坡度等 → 中文标注入口）
      if (!string.IsNullOrWhiteSpace(bandSetName))
      {
        var bandSetId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);
        if (bandSetId.IsNull)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"ProfileView band set '{bandSetName}' was not found.");
        // 25.0.58 反射确认: ProfileView.Bands.ImportBandSetStyle(ObjectId) 存在
        var bands = CivilObjectUtils.GetPropertyValue<object>(profileView, "Bands");
        if (bands == null)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "ProfileView has no Bands collection.");
        CivilObjectUtils.InvokeMethod(bands, "ImportBandSetStyle", bandSetId);
        result["bandSetChanged"] = true;
        result["bandSet"] = bandSetName;
      }

      return result;
    });
  }
}
