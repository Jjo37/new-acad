using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class LookupUtils
{
  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName)
  {
    return GetLayerId(database, transaction, layerName, strict: false);
  }

  // 2026-08-12 M3: strict 模式——名称明确给定但图层不存在时抛错(不静默回退到当前图层, 防画错地方)
  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName, bool strict)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return database.Clayer;
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    if (strict)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "图层不存在: " + layerName + "——请先 createLayer 创建或核对图层名");
    }
    return database.Clayer;
  }

  // 2026-08-12: 线型名 → ObjectId（不存在返回 Null=ByLayer；ByLayer/ByBlock 直接返 Null）
  public static ObjectId GetLinetypeId(Database database, Transaction transaction, string? linetypeName)
  {
    if (string.IsNullOrWhiteSpace(linetypeName)) return ObjectId.Null;
    var name = linetypeName.Trim();
    if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
      return ObjectId.Null;
    var linetypeTable = CivilObjectUtils.GetRequiredObject<LinetypeTable>(transaction, database.LinetypeTableId, OpenMode.ForRead);
    if (linetypeTable.Has(name)) return linetypeTable[name];
    return ObjectId.Null;
  }

  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  public static ObjectId GetAlignmentStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.AlignmentStyles, transaction, styleName);
  }

  public static ObjectId GetProfileStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ProfileStyles, transaction, styleName);
  }

  public static ObjectId GetCorridorStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.CorridorStyles, transaction, styleName);
  }

  public static ObjectId GetSurfaceStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SurfaceStyles, transaction, styleName);
  }

  public static ObjectId GetAlignmentLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    var styles = CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewStyles");
    return styles != null
      ? GetStyleId(styles, transaction, styleName)
      : ObjectId.Null;
  }

  public static ObjectId GetProfileViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    if (string.IsNullOrWhiteSpace(bandSetName))
    {
      return ObjectId.Null;
    }

    // 2026-08-16: 与 listStyles objectType:'profileviewbandset' 统一用同一集合
    // （LabelSetStyles.ProfileViewBandSetStyles 与 Styles.ProfileViewBandSetStyles 不是同一集合）
    var bandSetStyles = CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewBandSetStyles");
    return bandSetStyles != null
      ? GetStyleId(bandSetStyles, transaction, bandSetName)
      : ObjectId.Null;
  }

  public static ObjectId GetParcelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ParcelStyles, transaction, styleName);
  }

  public static ObjectId GetParcelAreaLabelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelStyles.ParcelLabelStyles.AreaLabelStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SectionViewStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return string.IsNullOrWhiteSpace(bandSetName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.SectionViewBandSetStyles, transaction, bandSetName);
  }

  public static ObjectId GetGroupPlotStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return string.IsNullOrWhiteSpace(styleName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.GroupPlotStyles, transaction, styleName);
  }

  public static string? GetFirstStyleName(object? collection, Transaction transaction)
  {
    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }

    return null;
  }

  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName)
  {
    var fallback = ObjectId.Null;

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return fallback;
  }

  private static IEnumerable<ObjectId> EnumerateObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }

      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }
}
