using System.Text.Json.Nodes;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.SubassemblyComposer.FileAccess;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Assembly operations implemented with the documented Civil 3D 2026 API.
/// Dynamic stock-subassembly parameters are the only compatibility access.
/// </summary>
public static class AssemblyCreationCommands
{
  // 2026-08-16: C3D stock 子装配类名目录（解析自 C3D Metric Tool Catalogs .atc 文件）
  // ImportStockSubassembly 第二参数 = 这些类名。带注释的是道路结构层常用件。
  private static readonly Dictionary<string, string[]> StockSubassemblyCatalog = new()
  {
    ["Basic"] = new[] { "Subassembly.BasicBarrier", "Subassembly.BasicCurb", "Subassembly.BasicCurbAndGutter", "Subassembly.BasicGuardrail", "Subassembly.BasicLane", "Subassembly.BasicLaneTransition", "Subassembly.BasicShoulder", "Subassembly.BasicSideSlopeCutDitch", "Subassembly.BasicSidewalk" },
    ["Lanes"] = new[] { "Subassembly.LaneSuperelevationAOR", "Subassembly.CrownedLane", "Subassembly.GenericPavementStructure", "Subassembly.LaneBrokenBack", "Subassembly.LaneFromTaperedMedian1", "Subassembly.LaneFromTaperedMedian2", "Subassembly.LaneInsideSuperLayerVaryingWidth", "Subassembly.LaneInsideSuperMultiLayer", "Subassembly.LaneOutsideSuperLayerVaryingWidth", "Subassembly.LaneOutsideSuperMultiLayer", "Subassembly.LaneOutsideSuperWithWidening", "Subassembly.LaneParabolic", "Subassembly.ShapeTrapezoidal" },
    ["Shoulders"] = new[] { "Subassembly.ShoulderExtendAll", "Subassembly.ShoulderExtendSubbase", "Subassembly.ShoulderMultiLayer", "Subassembly.ShoulderMultiSurface", "Subassembly.ShoulderMultilayerVaryingWidth", "Subassembly.ShoulderVerticalSubbase", "Subassembly.ShoulderWidening", "Subassembly.ShoulderWithSubbaseInterlaced", "Subassembly.ShoulderWithSubbaseInterlacedAndDitch" },
    ["Daylight"] = new[] { "Subassembly.DaylightBasin", "Subassembly.DaylightBasin2", "Subassembly.DaylightBench", "Subassembly.DaylightGeneral", "Subassembly.DaylightInsideROW", "Subassembly.DaylightMaxOffset", "Subassembly.DaylightMaxWidth", "Subassembly.DaylightMinOffset", "Subassembly.DaylightMinWidth", "Subassembly.DaylightMultiIntercept", "Subassembly.DaylightMultipleSurface", "Subassembly.DaylightRockCut", "Subassembly.DaylightStandard", "Subassembly.DaylightToOffset", "Subassembly.DaylightToROW", "Subassembly.StrippingPavement", "Subassembly.StrippingTopSoil" },
    ["CurbAndGutters"] = new[] { "Subassembly.UrbanCurbGutterGeneral", "Subassembly.UrbanCurbGutterValley1", "Subassembly.UrbanCurbGutterValley2", "Subassembly.UrbanCurbGutterValley3", "Subassembly.UrbanReplaceCurbGutter1", "Subassembly.UrbanReplaceCurbGutter2", "Subassembly.UrbanReplaceSidewalk", "Subassembly.UrbanSidewalk" },
    ["Generic"] = new[] { "Subassembly.LinkMulti", "Subassembly.LinkOffsetAndElevation", "Subassembly.LinkOffsetAndSlope", "Subassembly.LinkOffsetOnSurface", "Subassembly.LinkSlopeAndVerticalDeflection", "Subassembly.LinkSlopeToElevation", "Subassembly.LinkSlopeToSurface", "Subassembly.LinkSlopesBetweenPoints", "Subassembly.LinkToMarkedPoint", "Subassembly.LinkToMarkedPoint2", "Subassembly.LinkVertical", "Subassembly.LinkWidthAndSlope", "Subassembly.LotGrade", "Subassembly.MarkPoint" },
    ["Medians"] = new[] { "Subassembly.MedianConstantSlopeWithBarrier", "Subassembly.MedianDepressed", "Subassembly.MedianDepressedShoulderExt", "Subassembly.MedianDepressedShoulderVert", "Subassembly.MedianFlushWithBarrier", "Subassembly.MedianRaisedConstantSlope", "Subassembly.MedianRaisedWithCrown" },
    ["Conditional"] = new[] { "Subassembly.ConditionalCutOrFill", "Subassembly.ConditionalHorizontalTarget" },
    ["ChannelAndTrenchPipe"] = new[] { "Subassembly.Channel", "Subassembly.ChannelParabolicBottom", "Subassembly.Ditch", "Subassembly.SideDitch", "Subassembly.SideDitchUShape", "Subassembly.SideDitchWithLid", "Subassembly.TrenchPipe1", "Subassembly.TrenchPipe2", "Subassembly.TrenchPipe3", "Subassembly.TrenchWithPipe" },
    ["RetainingWall"] = new[] { "Subassembly.RetainWallTapered", "Subassembly.RetainWallTaperedWide", "Subassembly.RetainWallTieToDitch", "Subassembly.RetainWallToLowSide", "Subassembly.RetainWallVertical", "Subassembly.SimpleNoiseBarrier" },
    ["Bridge"] = new[] { "Subassembly.BridgeBoxGirder1", "Subassembly.BridgeBoxGirder2" },
    ["Rehab"] = new[] { "Subassembly.OverlayBrokenBackBetweenEdges", "Subassembly.OverlayBrokenBackOverGutters", "Subassembly.OverlayCrownBetweenEdges", "Subassembly.OverlayMedianAsymmetrical", "Subassembly.OverlayMedianSymmetrical", "Subassembly.OverlayMillAndLevel1", "Subassembly.OverlayMillAndLevel2", "Subassembly.OverlayParabolic", "Subassembly.OverlayWidenFromCurb", "Subassembly.OverlayWidenMatchSlope1", "Subassembly.OverlayWidenMatchSlope2", "Subassembly.OverlayWidenWithSuper1" },
  };

  public static Task<object?> ListStockSubassembliesAsync(JsonObject? parameters)
  {
    var category = PluginRuntime.GetOptionalString(parameters, "category");
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["catalog"] = string.IsNullOrWhiteSpace(category)
        ? StockSubassemblyCatalog
        : StockSubassemblyCatalog
            .Where(kv => kv.Key.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
      ["hint"] = "createSubassembly 的 subassemblyType 参数 = 下列类名（带 Subassembly. 前缀，如 Subassembly.BasicLane）。道路结构层常用：Subassembly.BasicLane/Subassembly.LaneSuperelevationAOR（路面）、Subassembly.ShoulderExtendAll（路肩）、Subassembly.DaylightStandard（放坡）。",
    });
  }

  // -------------------------------------------------------------------------
  // importSACSubassembly（2026-08-16 新增：导入 SAC 自定义部件 .pkt 文件）
  // -------------------------------------------------------------------------
  // 25.0.58 反射确认: SubassemblyCollection.ImportSACSubassembly(name, pktFilePath, location) -> ObjectId
  // 用途: 用户有 Subassembly Composer 做好的 .pkt 部件文件（如花岗岩平石/侧石/边石），
  //       导入后即可像标准部件一样加入装配。
  public static Task<object?> BuildSACSubassemblyAsync(JsonObject? parameters)
  {
    // ============================================================
    // buildSACSubassembly（2026-08-16：AI 生成 SAC 部件核心方法）
    //   AI 给 XAML 工作流内容（+可选 atc/category 信息）→ 插件自动生成
    //   cfg/emd/pvd 最小模板 → PktFileAccess.CreatePktFile 打包 → 返回 .pkt 路径
    // 实测边界（PoC）：最小可导入组合 = xaml + atc + cfg + emd + pvd（png 可选）
    // ============================================================
    var subassemblyName = PluginRuntime.GetRequiredString(parameters, "subassemblyName");
    var xamlContent = PluginRuntime.GetRequiredString(parameters, "xamlContent");
    var categoryName = PluginRuntime.GetOptionalString(parameters, "categoryName") ?? "HankCustom";
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? $"{subassemblyName} (AI-generated SAC subassembly)";
    var atcContent = PluginRuntime.GetOptionalString(parameters, "atcContent");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");

    // 参数列表（AI 可选传；从 xaml 的 x:Members 提取或默认空）
    var paramsText = PluginRuntime.GetOptionalString(parameters, "paramsXml");

    var guid = Guid.NewGuid().ToString("N");
    var workDir = Path.Combine(Path.GetTempPath(), "hank_sac", guid);
    Directory.CreateDirectory(workDir);

    try
    {
      // 1) 写 xaml
      var xamlFile = Path.Combine(workDir, guid + ".xaml");
      File.WriteAllText(xamlFile, xamlContent, new System.Text.UTF8Encoding(false));

      // 2) 写 cfg（固定模板）
      var cfgFile = Path.Combine(workDir, guid + ".cfg");
      File.WriteAllText(cfgFile,
        "<?xml version=\"1.0\"?>\n" +
        "<Configuration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <CreatedWith>\n" +
        "    <ProductName>Autodesk Subassembly Composer</ProductName>\n" +
        "    <Version>ForMatterhorn</Version>\n" +
        "    <VersionNumber>12.0.842.0</VersionNumber>\n" +
        "  </CreatedWith>\n" +
        "</Configuration>\n", new System.Text.UTF8Encoding(false));

      // 3) 写 atc（AI 传了就用，否则自动生成最小版）
      var atcFile = Path.Combine(workDir, guid + ".atc");
      if (string.IsNullOrWhiteSpace(atcContent))
      {
        var paramsXml = string.IsNullOrWhiteSpace(paramsText)
          ? $"<Side DataType=\"long\" TypeInfo=\"16\" DisplayName=\"Side\" Description=\"Side\">0<Enum><Left DisplayName=\"Left\">1</Left><Right DisplayName=\"Right\">0</Right></Enum></Side>"
          : paramsText;
        atcContent =
          "<?xml version=\"1.0\"?>\n" +
          "<Category xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
          "  <ItemID idValue=\"{63282f13-63e3-4f65-a83e-514e21ecb030}\" />\n" +
          "  <Properties><ItemName>" + categoryName + "</ItemName><Images><Image cx=\"93\" cy=\"123\" /></Images></Properties>\n" +
          "  <CustomData /><Source /><Palettes /><Packages />\n" +
          "  <Tools>\n" +
          "    <Tool Name=\"" + subassemblyName + "\">\n" +
          "      <ItemID idValue=\"{" + Guid.NewGuid().ToString("D") + "}\" />\n" +
          "      <Properties><ItemName>" + subassemblyName + "</ItemName><Images><Image cx=\"64\" cy=\"64\" /></Images>" +
          "<Description>" + description + "</Description><Help><HelpFile /><HelpCommand /><HelpData /></Help></Properties>\n" +
          "      <Source /><StockToolRef idValue=\"{7F55AAC0-0256-48D7-BFA5-914702663FDE}\" />\n" +
          "      <Data>\n" +
          "        <AeccDbSubassembly>\n" +
          "          <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>\n" +
          "          <DotNetClass Assembly=\"" + guid + ".dll\">Subassembly." + subassemblyName + "</DotNetClass>\n" +
          "          <Params>" + paramsXml + "</Params>\n" +
          "        </AeccDbSubassembly>\n" +
          "        <Units>m</Units>\n" +
          "      </Data>\n" +
          "    </Tool>\n" +
          "  </Tools>\n" +
          "  <StockTools />\n" +
          "</Category>\n";
      }
      File.WriteAllText(atcFile, atcContent, new System.Text.UTF8Encoding(false));

      // 4) 写 emd（最小模板，无枚举组）
      var emdFile = Path.Combine(workDir, guid + ".emd");
      File.WriteAllText(emdFile,
        "<?xml version=\"1.0\"?>\n" +
        "<EnumData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <EnumDatas><Groups /></EnumDatas>\n" +
        "  <DefinedVariables />\n" +
        "</EnumData>\n", new System.Text.UTF8Encoding(false));

      // 5) 写 pvd（最小模板）
      var pvdFile = Path.Combine(workDir, guid + ".pvd");
      File.WriteAllText(pvdFile,
        "<?xml version=\"1.0\"?>\n" +
        "<PreviewData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <Superelevation><CrossSlopes /></Superelevation>\n" +
        "  <Cant><CantParams /></Cant>\n" +
        "</PreviewData>\n", new System.Text.UTF8Encoding(false));

      // 6) 构造 PktStructure 调 CreatePktFile
      var pktPath = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(workDir, subassemblyName + ".pkt")
        : outputPath;
      if (!pktPath.EndsWith(".pkt", StringComparison.OrdinalIgnoreCase)) pktPath += ".pkt";

      PktFileAccess.CreatePktFile(new PktStructure
      {
        Guid = guid,
        XamlFile = xamlFile,
        AtcFile = atcFile,
        CfgFile = cfgFile,
        EnumDataFile = emdFile,
        PreviewDataFile = pvdFile,
        WorkingFolder = workDir,
        // ImageFile = 可选；CodeDataFile = 不需要（C3D 导入时实时编译 xaml）
      }, pktPath);

      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["pktFilePath"] = pktPath,
        ["subassemblyName"] = subassemblyName,
        ["size"] = File.Exists(pktPath) ? new FileInfo(pktPath).Length : 0,
        ["built"] = true,
        ["hint"] = "用 importSACSubassembly {subassemblyName, pktFilePath} 导入后即可像标准部件使用",
      });
    }
    catch (Exception ex)
    {
      // 清理临时目录
      try { Directory.Delete(workDir, true); } catch { }
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "buildSACSubassembly failed: " + ex.Message);
    }
  }

  public static Task<object?> ImportSACSubassemblyAsync(JsonObject? parameters)
  {
    var subassemblyName = PluginRuntime.GetRequiredString(parameters, "subassemblyName");
    var pktFilePath = PluginRuntime.GetRequiredString(parameters, "pktFilePath");
    var insertX = PluginRuntime.GetOptionalDouble(parameters, "insertX") ?? 0;
    var insertY = PluginRuntime.GetOptionalDouble(parameters, "insertY") ?? 0;
    var assemblyName = PluginRuntime.GetOptionalString(parameters, "assemblyName"); // 2026-08-16: 可选，导入后自动挂装配

    if (!File.Exists(pktFilePath))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"SAC file not found: '{pktFilePath}'.");
    if (!pktFilePath.EndsWith(".pkt", StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Not a .pkt SAC file: '{pktFilePath}'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var subassemblyId = civilDoc.SubassemblyCollection.ImportSACSubassembly(
        subassemblyName, pktFilePath, new Point3d(insertX, insertY, 0));

      // 可选：挂到指定装配
      if (!string.IsNullOrWhiteSpace(assemblyName))
      {
        var asm = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
        asm.AddSubassembly(subassemblyId);
      }

      var subassembly = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, subassemblyId, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassemblyName,
        ["handle"] = CivilObjectUtils.GetHandle(subassembly),
        ["pktFilePath"] = pktFilePath,
        ["attachedToAssembly"] = string.IsNullOrWhiteSpace(assemblyName) ? null : assemblyName,
        ["imported"] = true,
      };
    });
  }

  public static Task<object?> ListAssembliesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblies = civilDoc.AssemblyCollection
        .Select(id => CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForRead))
        .Select(assembly =>
        {
          var summary = ToAssemblySummary(assembly);
          summary["usedByCorridors"] = new List<string>();
          return summary;
        })
        .ToList();

      return new Dictionary<string, object?> { ["assemblies"] = assemblies };
    });
  }

  // -------------------------------------------------------------------------
  // createCustomSubassembly（2026-08-16 新增：编程创建自定义子装配几何）
  // 2026-09-09 重写：不再用 ImportStockSubassembly(BasicLane) 载体 + proxy Add
  // （.NET 生成器几何由编译代码生成，运行时 Points/Links/Shapes 集合为空，
  //  炸开永远显示 BasicLane 默认几何——旧实现实测失效）。
  // 新实现：把 points/links/shapes 翻译成 SAC XAML 工作流 → 打包 .pkt →
  //  ImportSACSubassembly 导入（C3D 实时编译 XAML）→ 几何真生成，
  //  实测 0.5x0.15 矩形 probe 出 4 circle + 4 line + 1 hatch(area=0.075) ✓
  // 用途: 无 .pkt 文件时直接定义断面几何（平石/侧石/边石等）
  public static Task<object?> CreateCustomSubassemblyAsync(JsonObject? parameters)
  {
    var subassemblyName = PluginRuntime.GetRequiredString(parameters, "subassemblyName");
    var assemblyName = PluginRuntime.GetOptionalString(parameters, "assemblyName");
    var categoryName = PluginRuntime.GetOptionalString(parameters, "categoryName") ?? "HankCustom";
    var insertX = PluginRuntime.GetOptionalDouble(parameters, "insertX") ?? 0;
    var insertY = PluginRuntime.GetOptionalDouble(parameters, "insertY") ?? 0;
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createCustomSubassembly requires 'points' array.");
    var linksNode = PluginRuntime.GetParameter(parameters, "links") as JsonArray ?? new JsonArray();
    var shapesNode = PluginRuntime.GetParameter(parameters, "shapes") as JsonArray ?? new JsonArray();

    if (pointsNode.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least 2 points are required.");

    // 1) 翻译成 SAC XAML（C3D 实时编译，几何真生成）
    // 2026-09-11: autoLinkCodes 默认开——廊道曲面由**链接码**生成（形状码只进材料表），
    // 链接无码 → 用所属形状的码，仍无 → Link{n}；保证 addCorridorSurface 自动收集不为空。
    var autoLinkCodes = PluginRuntime.GetOptionalBool(parameters, "autoLinkCodes") ?? true;
    string xamlContent;
    List<string> autoCodedLinkCodes;
    try
    {
      xamlContent = PointsToSacXaml(subassemblyName, pointsNode, linksNode, shapesNode, autoLinkCodes, out autoCodedLinkCodes);
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createCustomSubassembly XAML translation failed: " + ex.Message);
    }

    // 2) 打包 .pkt
    var pktPath = PackSacPkt(subassemblyName, xamlContent, categoryName,
      $"{subassemblyName} (generated from points/links/shapes)");

    // 3) 导入 + 可选挂装配（与 ImportSACSubassemblyAsync 同链路，但不开嵌套事务）
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      ObjectId assemblyId = ObjectId.Null;
      if (!string.IsNullOrWhiteSpace(assemblyName))
      {
        assemblyId = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite).ObjectId;
      }

      var subassemblyId = civilDoc.SubassemblyCollection.ImportSACSubassembly(
        subassemblyName, pktPath, new Point3d(insertX, insertY, 0));

      if (!assemblyId.IsNull)
      {
        var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, assemblyId, OpenMode.ForWrite);
        assembly.AddSubassembly(subassemblyId);
      }

      var subassembly = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, subassemblyId, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassemblyName,
        ["handle"] = CivilObjectUtils.GetHandle(subassembly),
        ["points"] = pointsNode.Count,
        ["links"] = linksNode.Count,
        ["shapes"] = shapesNode.Count,
        ["pktFilePath"] = pktPath,
        ["attachedToAssembly"] = !assemblyId.IsNull ? assemblyName : null,
        ["autoLinkCodes"] = autoLinkCodes,
        ["autoCodedLinkCodes"] = autoCodedLinkCodes,
        ["created"] = true,
        ["note"] = "几何走 SAC XAML 实时编译生成（probeGeometry 可验证）；autoLinkCodes 默认给无码链接补码（形状码→Link{n}）——廊道曲面/算量靠链接码",
      };
    });
  }

  // -------------------------------------------------------------------------
  // PointsToSacXaml: 把 points/links/shapes 数组翻译成 SAC 工作流 XAML。
  // 语法要点（2026-09-09 实测校正）:
  //  - CreatePoint: Positioning="DeltaXAndDeltaY", 每点相对前一点增量（首点相对原点）
  //    FromPoint 引用前一点名; PointCodes 子元素 List
  //  - CreateLink: StartPoint/EndPoint/LinkNumber 属性（不是 Point1/Point2/LinkType）
  //    + LinkCodes 子元素; 真实模板 PermeablePavement 验证
  //  - CreateShape: Links="L1,L2,..." 属性 + ShapeNumber + ComponentNames/ShapeCodes
  //  - Flowchart 尾部需 x:Reference 注册所有 FlowStep
  //  - 无参数部件（固定几何）: x:Members 只需 Geometry/SubassemblyErrorCenter/
  //    SubassemblyRunMode/Side
  // -------------------------------------------------------------------------
  private static string PointsToSacXaml(string subassemblyName, JsonArray pointsNode, JsonArray linksNode, JsonArray shapesNode, bool autoLinkCodes, out List<string> autoCodedLinkCodes)
  {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    string Fmt(double v) => v.ToString("0.######", inv);
    string XmlEsc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // 1) 解析点（保留序号供 link 引用）
    var ptOffset = new List<double>();
    var ptElev = new List<double>();
    var ptCodes = new List<string?>();
    foreach (var pn in pointsNode)
    {
      if (pn is not JsonObject po) continue;
      ptOffset.Add(PluginRuntime.GetRequiredDoubleFromNode(po["offset"], "points[].offset"));
      ptElev.Add(PluginRuntime.GetRequiredDoubleFromNode(po["elevation"], "points[].elevation"));
      ptCodes.Add(PluginRuntime.GetOptionalString(po, "code"));
    }
    if (ptOffset.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least 2 valid points are required.");

    // 2) 解析 links（引用点索引 0-based）
    var links = new List<(int A, int B, string? Code)>();
    foreach (var ln in linksNode)
    {
      if (ln is not JsonObject lo) continue;
      var idxArr = lo["pointIndices"] as JsonArray
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "links[].pointIndices is required.");
      if (idxArr.Count < 2) continue;
      var a = PluginRuntime.GetRequiredIntFromNode(idxArr[0], "links[].pointIndices[0]");
      var b = PluginRuntime.GetRequiredIntFromNode(idxArr[1], "links[].pointIndices[1]");
      if (a < 0 || a >= ptOffset.Count || b < 0 || b >= ptOffset.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"links[].pointIndices {a}/{b} out of range (0..{ptOffset.Count - 1}).");
      links.Add((a, b, PluginRuntime.GetOptionalString(lo, "code")));
    }

    // 3) 解析 shapes（引用 link 索引 0-based）
    var shapes = new List<(List<int> Idx, string? Code)>();
    foreach (var sn in shapesNode)
    {
      if (sn is not JsonObject so) continue;
      var idxArr = so["linkIndices"] as JsonArray
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "shapes[].linkIndices is required.");
      var ids = new List<int>();
      foreach (var n in idxArr)
      {
        var idx = PluginRuntime.GetRequiredIntFromNode(n, "shapes[].linkIndices[]");
        if (idx < 0 || idx >= links.Count)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"shapes[].linkIndices index {idx} out of range (0..{links.Count - 1}).");
        ids.Add(idx);
      }
      if (ids.Count > 0) shapes.Add((ids, PluginRuntime.GetOptionalString(so, "code")));
    }

    // 3.5) 链接码补齐（2026-09-11）：廊道曲面由链接码生成，链接无码 → 曲面收集为空。
    //      规则：用所属形状的码；仍无 → Link{n}。autoLinkCodes:false 可关闭。
    autoCodedLinkCodes = new List<string>();
    if (autoLinkCodes)
    {
      for (int i = 0; i < links.Count; i++)
      {
        if (!string.IsNullOrWhiteSpace(links[i].Code)) continue;
        string? shapeCode = null;
        foreach (var s in shapes)
        {
          if (!string.IsNullOrWhiteSpace(s.Code) && s.Idx.Contains(i)) { shapeCode = s.Code; break; }
        }
        var newCode = string.IsNullOrWhiteSpace(shapeCode) ? ("Link" + (i + 1)) : shapeCode!;
        var l = links[i];
        l.Code = newCode;
        links[i] = l;
        autoCodedLinkCodes.Add(newCode);
      }
    }

    var sb = new System.Text.StringBuilder();
    int activityId = 1;

    string CodesList(string? code, string owner, string elementName)
    {
      var codes = string.IsNullOrWhiteSpace(code) ? new List<string>() : code.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
      if (codes.Count == 0) return $"<asa1:{owner}.{elementName} />";
      var items = string.Join("\n", codes.Select(c => $"      <InArgument x:TypeArguments=\"x:String\">{XmlEsc(c)}</InArgument>"));
      return $"<asa1:{owner}.{elementName}>\n      <scg:List x:TypeArguments=\"InArgument(x:String)\" Capacity=\"{codes.Count}\">\n{items}\n      </scg:List>\n    </asa1:{owner}.{elementName}>";
    }

    // 每个活动生成 FlowStep 内 XML（嵌套链由最后统一组装）
    var stepXmls = new List<string>();

    // 4) CreatePoint 活动
    double prevX = 0, prevY = 0;
    for (int i = 0; i < ptOffset.Count; i++)
    {
      double dx = i == 0 ? ptOffset[i] : ptOffset[i] - prevX;
      double dy = i == 0 ? ptElev[i] : ptElev[i] - prevY;
      prevX = ptOffset[i]; prevY = ptElev[i];
      var fromPoint = i == 0 ? "{x:Null}" : $"P{i}";
      var inner = $"<asa1:CreatePoint AutoLinkCodes=\"{{x:Null}}\" AutoLinkGeometryName=\"{{x:Null}}\" FromPoint=\"{fromPoint}\" ActivityId=\"{activityId++}\" ApplyAOR=\"False\" AutoLink=\"False\" DisplayName=\"P{i + 1}\" Geometry=\"[Geometry]\" PointNumber=\"P{i + 1}\" Positioning=\"DeltaXAndDeltaY\" ShowErrors=\"True\" Side=\"[Side]\" SubassemblyErrorCenter=\"[SubassemblyErrorCenter]\" SubassemblyRunMode=\"[SubassemblyRunMode]\">\n" +
        $"  <asa1:CreatePoint.Arguments>\n    <InArgument x:TypeArguments=\"x:Double\" x:Key=\"DeltaX1\">{Fmt(dx)}</InArgument>\n    <InArgument x:TypeArguments=\"x:Double\" x:Key=\"DeltaY1\">{Fmt(dy)}</InArgument>\n  </asa1:CreatePoint.Arguments>\n" +
        (string.IsNullOrWhiteSpace(ptCodes[i]) ? "" : CodesList(ptCodes[i], "CreatePoint", "PointCodes") + "\n") +
        $"</asa1:CreatePoint>";
      stepXmls.Add(inner);
    }

    // 5) CreateLink 活动
    for (int i = 0; i < links.Count; i++)
    {
      var l = links[i];
      var linkName = $"L{i + 1}";
      var codes = string.IsNullOrWhiteSpace(l.Code) ? "" : CodesList(l.Code, "CreateLink", "LinkCodes") + "\n";
      var inner = $"<asa1:CreateLink ActivityId=\"{activityId++}\" ApplyAOR=\"False\" DisplayName=\"{linkName}\" EndPoint=\"P{l.B + 1}\" Geometry=\"[Geometry]\" IsEnabled=\"True\" LinkNumber=\"{linkName}\" ShowErrors=\"True\" StartPoint=\"P{l.A + 1}\" SubassemblyErrorCenter=\"[SubassemblyErrorCenter]\" SubassemblyRunMode=\"[SubassemblyRunMode]\">\n" +
        codes +
        $"</asa1:CreateLink>";
      stepXmls.Add(inner);
    }

    // 6) CreateShape 活动
    for (int i = 0; i < shapes.Count; i++)
    {
      var s = shapes[i];
      var linkNames = s.Idx.Select(idx => $"L{idx + 1}").ToList();
      var components = string.Join("\n", linkNames.Select(n => $"      <x:String>{n}</x:String>"));
      var codes = string.IsNullOrWhiteSpace(s.Code) ? "Body" : s.Code;
      var inner = $"<asa1:CreateShape ActivityId=\"{activityId++}\" DisplayName=\"S{i + 1}\" Geometry=\"[Geometry]\" Links=\"{string.Join(",", linkNames)}\" ShapeNumber=\"S{i + 1}\" ShowErrors=\"True\" SubassemblyErrorCenter=\"[SubassemblyErrorCenter]\" SubassemblyRunMode=\"[SubassemblyRunMode]\">\n" +
        $"  <asa1:CreateShape.ComponentNames>\n{components}\n  </asa1:CreateShape.ComponentNames>\n" +
        $"  <asa1:CreateShape.ShapeCodes>\n    <scg:List x:TypeArguments=\"InArgument(x:String)\" Capacity=\"1\">\n      <InArgument x:TypeArguments=\"x:String\">{XmlEsc(codes)}</InArgument>\n    </scg:List>\n  </asa1:CreateShape.ShapeCodes>\n" +
        $"</asa1:CreateShape>";
      stepXmls.Add(inner);
    }

    // 7) 组装 FlowStep 嵌套链（从后往前包 Next）
    string flowXml = "";
    for (int i = stepXmls.Count - 1; i >= 0; i--)
    {
      var refName = $"__ReferenceID{i}";
      flowXml = i == stepXmls.Count - 1
        ? $"<FlowStep x:Name=\"{refName}\">\n{stepXmls[i]}\n</FlowStep>"
        : $"<FlowStep x:Name=\"{refName}\">\n{stepXmls[i]}\n<FlowStep.Next>\n{flowXml}\n</FlowStep.Next>\n</FlowStep>";
    }
    var refsXml = string.Join("\n", stepXmls.Select((_, i) => $"    <x:Reference>__ReferenceID{i}</x:Reference>"));

    sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
    sb.Append("<Activity mc:Ignorable=\"sads sap\" x:Class=\"Subassembly\" this:Subassembly.Side=\"[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]\"\n");
    sb.Append(" xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"\n");
    sb.Append(" xmlns:asa=\"clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API\"\n");
    sb.Append(" xmlns:asa1=\"clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary\"\n");
    sb.Append(" xmlns:asw=\"clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine\"\n");
    sb.Append(" xmlns:av=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n");
    sb.Append(" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n");
    sb.Append(" xmlns:mva=\"clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities\"\n");
    sb.Append(" xmlns:s=\"clr-namespace:System;assembly=mscorlib\"\n");
    sb.Append(" xmlns:sa=\"clr-namespace:System.Activities;assembly=System.Activities\"\n");
    sb.Append(" xmlns:sads=\"http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger\"\n");
    sb.Append(" xmlns:sap=\"http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation\"\n");
    sb.Append(" xmlns:scg=\"clr-namespace:System.Collections.Generic;assembly=mscorlib\"\n");
    sb.Append(" xmlns:this=\"clr-namespace:\"\n");
    sb.Append(" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n");
    sb.Append("  <x:Members>\n");
    sb.Append("    <x:Property Name=\"Geometry\" Type=\"InOutArgument(asw:Geometry)\" />\n");
    sb.Append("    <x:Property Name=\"SubassemblyErrorCenter\" Type=\"InOutArgument(asw:SubassemblyErrorCenter)\" />\n");
    sb.Append("    <x:Property Name=\"SubassemblyRunMode\" Type=\"InOutArgument(asw:SubassemblyRunMode)\" />\n");
    sb.Append("    <x:Property Name=\"Side\" Type=\"InArgument(asw:EnumType)\">\n");
    sb.Append("      <x:Property.Attributes>\n        <asw:EnabledFlag2Attribute EnabledFlag=\"True\" />\n      </x:Property.Attributes>\n    </x:Property>\n");
    sb.Append("  </x:Members>\n");
    sb.Append("  <sap:VirtualizedContainerService.HintSize>600,420</sap:VirtualizedContainerService.HintSize>\n");
    sb.Append("  <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>\n");
    sb.Append("  <Flowchart mva:VisualBasic.Settings=\"Assembly references and imported namespaces serialized as XML namespaces\">\n");
    sb.Append("    <Flowchart.Variables>\n");
    sb.Append("      <Variable x:TypeArguments=\"asw:EnumType\" Default=\"[new EnumType(1, &quot;Left&quot;)]\" Modifiers=\"ReadOnly\" Name=\"Left\" />\n");
    sb.Append("      <Variable x:TypeArguments=\"asw:EnumType\" Default=\"[new EnumType(0, &quot;Right&quot;)]\" Modifiers=\"ReadOnly\" Name=\"Right\" />\n");
    sb.Append("    </Flowchart.Variables>\n");
    sb.Append("    <Flowchart.StartNode>\n");
    sb.Append(flowXml);
    sb.Append("\n    </Flowchart.StartNode>\n");
    sb.Append(refsXml);
    sb.Append("\n  </Flowchart>\n</Activity>\n");
    return sb.ToString();
  }

  // -------------------------------------------------------------------------
  // PackSacPkt: 打包 SAC .pkt（从 buildSACSubassembly 提取的公共逻辑）
  // 最小可导入组合 = xaml + atc + cfg + emd + pvd（png 可选，无 dll——C3D 导入时实时编译）
  // -------------------------------------------------------------------------
  private static string PackSacPkt(string subassemblyName, string xamlContent, string categoryName, string description)
  {
    var guid = Guid.NewGuid().ToString("N");
    var workDir = Path.Combine(Path.GetTempPath(), "hank_sac", guid);
    Directory.CreateDirectory(workDir);
    try
    {
      var xamlFile = Path.Combine(workDir, guid + ".xaml");
      File.WriteAllText(xamlFile, xamlContent, new System.Text.UTF8Encoding(false));

      var cfgFile = Path.Combine(workDir, guid + ".cfg");
      File.WriteAllText(cfgFile,
        "<?xml version=\"1.0\"?>\n" +
        "<Configuration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <CreatedWith>\n    <ProductName>Autodesk Subassembly Composer</ProductName>\n    <Version>ForMatterhorn</Version>\n    <VersionNumber>12.0.842.0</VersionNumber>\n  </CreatedWith>\n" +
        "</Configuration>\n", new System.Text.UTF8Encoding(false));

      var atcFile = Path.Combine(workDir, guid + ".atc");
      var atcContent =
        "<?xml version=\"1.0\"?>\n" +
        "<Category xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <ItemID idValue=\"{63282f13-63e3-4f65-a83e-514e21ecb030}\" />\n" +
        "  <Properties><ItemName>" + categoryName + "</ItemName><Images><Image cx=\"93\" cy=\"123\" /></Images></Properties>\n" +
        "  <CustomData /><Source /><Palettes /><Packages />\n" +
        "  <Tools>\n" +
        "    <Tool Name=\"" + subassemblyName + "\">\n" +
        "      <ItemID idValue=\"{" + Guid.NewGuid().ToString("D") + "}\" />\n" +
        "      <Properties><ItemName>" + subassemblyName + "</ItemName><Images><Image cx=\"64\" cy=\"64\" /></Images>" +
        "<Description>" + description + "</Description><Help><HelpFile /><HelpCommand /><HelpData /></Help></Properties>\n" +
        "      <Source /><StockToolRef idValue=\"{7F55AAC0-0256-48D7-BFA5-914702663FDE}\" />\n" +
        "      <Data>\n" +
        "        <AeccDbSubassembly>\n" +
        "          <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>\n" +
        "          <DotNetClass Assembly=\"" + guid + ".dll\">Subassembly." + subassemblyName + "</DotNetClass>\n" +
        "          <Params><Side DataType=\"long\" TypeInfo=\"16\" DisplayName=\"Side\" Description=\"Side\">0<Enum><Left DisplayName=\"Left\">1</Left><Right DisplayName=\"Right\">0</Right></Enum></Side></Params>\n" +
        "        </AeccDbSubassembly>\n" +
        "        <Units>m</Units>\n" +
        "      </Data>\n" +
        "    </Tool>\n" +
        "  </Tools>\n" +
        "  <StockTools />\n" +
        "</Category>\n";
      File.WriteAllText(atcFile, atcContent, new System.Text.UTF8Encoding(false));

      var emdFile = Path.Combine(workDir, guid + ".emd");
      File.WriteAllText(emdFile,
        "<?xml version=\"1.0\"?>\n" +
        "<EnumData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <EnumDatas><Groups /></EnumDatas>\n  <DefinedVariables />\n" +
        "</EnumData>\n", new System.Text.UTF8Encoding(false));

      var pvdFile = Path.Combine(workDir, guid + ".pvd");
      File.WriteAllText(pvdFile,
        "<?xml version=\"1.0\"?>\n" +
        "<PreviewData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
        "  <Superelevation><CrossSlopes /></Superelevation>\n  <Cant><CantParams /></Cant>\n" +
        "</PreviewData>\n", new System.Text.UTF8Encoding(false));

      var pktPath = Path.Combine(workDir, subassemblyName + ".pkt");
      PktFileAccess.CreatePktFile(new PktStructure
      {
        Guid = guid,
        XamlFile = xamlFile,
        AtcFile = atcFile,
        CfgFile = cfgFile,
        EnumDataFile = emdFile,
        PreviewDataFile = pvdFile,
        WorkingFolder = workDir,
      }, pktPath);
      return pktPath;
    }
    catch
    {
      try { Directory.Delete(workDir, true); } catch { }
      throw;
    }
  }

  public static Task<object?> GetAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, name, OpenMode.ForRead);
      var usedByCorridors = new List<string>();

      foreach (ObjectId corridorId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
        foreach (Baseline baseline in corridor.Baselines)
        {
          foreach (BaselineRegion region in baseline.BaselineRegions)
          {
            if (region.AssemblyId == assembly.ObjectId && !usedByCorridors.Contains(corridor.Name))
              usedByCorridors.Add(corridor.Name);
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
        ["style"] = GetStyleName(assembly, transaction),
        ["type"] = assembly.Type.ToString(),
        ["subassemblies"] = GetSubassemblies(assembly, transaction),
        ["usedByCorridors"] = usedByCorridors,
      };
    });
  }

  public static Task<object?> CreateAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var assemblyTypeText = PluginRuntime.GetRequiredString(parameters, "assemblyType");
    if (!Enum.TryParse<AssemblyType>(assemblyTypeText, ignoreCase: true, out var assemblyType))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Invalid assemblyType '{assemblyTypeText}'. Use {string.Join(", ", Enum.GetNames<AssemblyType>())}.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblyId = civilDoc.AssemblyCollection.Add(name, assemblyType, new Point3d(insertX, insertY, 0));
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, assemblyId, OpenMode.ForWrite);
      assembly.Description = description;

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["assemblyType"] = assembly.Type.ToString(),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateSubassemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyType = PluginRuntime.GetRequiredString(parameters, "subassemblyType");
    // 2026-08-16: ImportStockSubassembly 需要带 "Subassembly." 前缀的类名（ATC DotNetClass 格式）；
    // 兼容不带前缀的输入（AI 可能从目录或记忆拿名字）
    if (!subassemblyType.StartsWith("Subassembly.", StringComparison.OrdinalIgnoreCase))
    {
      subassemblyType = "Subassembly." + subassemblyType;
    }
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    if (!new[] { "Left", "Right", "Both" }.Contains(side, StringComparer.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be Left, Right, or Both.");

    // 2026-09-03: 链条式挂载——跟随道路边线（点代码 hook）
    //   hookToSubassembly: 父部件名称（本部件将挂在父部件上）
    //   hookToPointCode: 父部件上的点代码（如 ETW），按代码自动定位挂点；
    //                    缺省时 hook 父部件第一个点（通常=附着点）
    var hookToSubassembly = PluginRuntime.GetOptionalString(parameters, "hookToSubassembly");
    var hookToPointCode = PluginRuntime.GetOptionalString(parameters, "hookToPointCode");

    var subParams = ReadParameters(parameters?["parameters"] as JsonObject);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
      // 预解析 hook 父部件（同一装配内，按名称找）
      ObjectId hookParentId = ObjectId.Null;
      int hookPointIndex = -1;
      if (!string.IsNullOrWhiteSpace(hookToSubassembly))
      {
        foreach (var id in GetSubassemblyIds(assembly))
        {
          var cand = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
          var candName = CivilObjectUtils.GetName(cand);
          if (string.Equals(candName, hookToSubassembly, StringComparison.OrdinalIgnoreCase) ||
              candName != null && candName.EndsWith(hookToSubassembly, StringComparison.OrdinalIgnoreCase))
          {
            hookParentId = id;
            break;
          }
        }
        if (hookParentId.IsNull)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"hookToSubassembly '{hookToSubassembly}' was not found in assembly '{assemblyName}'.");
        }
        // 找父部件上带指定点代码的点索引（需要 ForWrite 打开——只读模式 Points 集合为空）
        // CodeCollection.Item 是 String，直接用 InvokeMethod 取值即可
        var parent = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, hookParentId, OpenMode.ForWrite);
        var points = CivilObjectUtils.GetPropertyValue<object>(parent, "Points");
        var availableCodes = new List<string>();
        if (points != null)
        {
          var count = Civil3DCompatibility.GetPropertyValue<int?>(points, "Count") ?? 0;
          for (var i = 0; i < count; i++)
          {
            var pt = CivilObjectUtils.InvokeMethod(points, "Item", i);
            if (pt == null) continue;
            var codes = CivilObjectUtils.GetPropertyValue<object>(pt, "Codes");
            if (codes == null) continue;
            var codeCount = Civil3DCompatibility.GetPropertyValue<int?>(codes, "Count") ?? 0;
            var codeMatch = false;
            for (var j = 0; j < codeCount; j++)
            {
              var codeStr = CivilObjectUtils.InvokeMethod(codes, "Item", j)?.ToString() ?? string.Empty;
              if (!string.IsNullOrWhiteSpace(codeStr) && !availableCodes.Contains(codeStr))
                availableCodes.Add(codeStr);
              if (string.Equals(codeStr, hookToPointCode, StringComparison.OrdinalIgnoreCase)) { codeMatch = true; break; }
            }
            if (codeMatch) { hookPointIndex = Civil3DCompatibility.GetPropertyValue<int?>(pt, "Index") ?? i; break; }
          }
        }
        if (hookPointIndex < 0 && !string.IsNullOrWhiteSpace(hookToPointCode))
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Point code '{hookToPointCode}' was not found on subassembly '{hookToSubassembly}'. Available point codes: {(availableCodes.Count > 0 ? string.Join(", ", availableCodes) : "(none)")}");
        }
        if (hookPointIndex < 0) hookPointIndex = 0; // 缺省挂第一个点（附着点）
      }

      var requestedSides = side.Equals("Both", StringComparison.OrdinalIgnoreCase)
        ? new[] { "Left", "Right" }
        : new[] { side };
      var created = new List<Dictionary<string, object?>>();

      foreach (var requestedSide in requestedSides)
      {
        var subassemblyName = $"{subassemblyType}-{requestedSide}-{Guid.NewGuid():N}";
        var subassemblyId = civilDoc.SubassemblyCollection.ImportStockSubassembly(
          subassemblyName,
          subassemblyType,
          assembly.Location);
        assembly.AddSubassembly(subassemblyId);

        var subassembly = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, subassemblyId, OpenMode.ForWrite);
        // 2026-09-03: 链条式 hook——挂到父部件指定点（跟随道路边线，不写死 offset）
        if (!hookParentId.IsNull)
        {
          try
          {
            Civil3DCompatibility.TrySetProperty(subassembly, "SubassemblyHookTo", hookParentId);
            Civil3DCompatibility.TrySetProperty(subassembly, "PointIndexHookTo", hookPointIndex);
          }
          catch (Exception ex)
          {
            PluginLog.Error("Subasm", $"Hook setup failed: {ex.Message}");
          }
        }
        if (!Civil3DCompatibility.TrySetProperty(subassembly, "Side", requestedSide))
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.API_ERROR",
            $"Stock subassembly '{subassemblyType}' does not expose a writable Side parameter. No implicit side was assumed.");
        }
        try
        {
          ApplySubassemblyParameters(subassembly, subParams);
        }
        catch (Exception ex)
        {
          // 2026-08-16 临时诊断：参数设置失败不应阻断创建，记日志
          PluginLog.Error("Subasm", $"ApplySubassemblyParameters failed: {ex}");
        }

        created.Add(new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassemblyName,
          ["handle"] = CivilObjectUtils.GetHandle(subassembly),
          ["side"] = requestedSide,
          ["hookTo"] = hookParentId.IsNull ? null : hookToSubassembly,
          ["hookPointIndex"] = hookParentId.IsNull ? (int?)null : hookPointIndex,
        });
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyType"] = subassemblyType,
        ["subassemblies"] = created,
        ["added"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // removeSubassembly（2026-08-18 新增：显式删除子装配）
  // -------------------------------------------------------------------------
  // 25.0.58 反射确认: Assembly / AssemblyGroup 均无 Remove 方法；
  // 官方删除入口 = SubassemblyCollection.Remove(ObjectId) -> bool；
  // 兜底 = Subassembly.Erase()（DBObject 基类）。
  // 背景: editAssembly 的 delete:true 走 Erase()，但方法名不带 remove，
  // AI 按关键词搜清单搜不到 —— 新增显式方法补齐“删除子装配”语义。
  public static Task<object?> RemoveSubassemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var handle = PluginRuntime.GetOptionalString(parameters, "handle");

    if (string.IsNullOrWhiteSpace(subassemblyName) && string.IsNullOrWhiteSpace(handle))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "removeSubassembly requires 'subassemblyName' or 'handle'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
      var subassemblyIds = GetSubassemblyIds(assembly);

      // 定位目标：handle 精确优先，其次按名称（大小写不敏感）
      ObjectId targetId = ObjectId.Null;
      Dictionary<string, object?>? summary = null;
      foreach (var id in subassemblyIds)
      {
        var candidate = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
        var candidateHandle = CivilObjectUtils.GetHandle(candidate);
        var candidateName = CivilObjectUtils.GetName(candidate);
        var match = !string.IsNullOrWhiteSpace(handle)
          ? string.Equals(candidateHandle, handle, StringComparison.OrdinalIgnoreCase)
          : string.Equals(candidateName, subassemblyName, StringComparison.OrdinalIgnoreCase);
        if (!match) continue;
        targetId = id;
        summary = ToSubassemblySummary(candidate);
        break;
      }

      if (targetId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Subassembly '{subassemblyName ?? handle}' was not found in assembly '{assemblyName}'.");

      // 官方入口：SubassemblyCollection.Remove；失败则兜底 Erase()
      var removedFromCollection = false;
      try
      {
        removedFromCollection = civilDoc.SubassemblyCollection.Remove(targetId);
      }
      catch (Exception ex)
      {
        PluginLog.Debug("Subasm", $"SubassemblyCollection.Remove failed: {ex.Message}");
      }
      if (!removedFromCollection)
      {
        var target = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, targetId, OpenMode.ForWrite);
        target.Erase();
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyName"] = summary?["name"],
        ["handle"] = summary?["handle"],
        ["deleted"] = true,
        ["removedFromCollection"] = removedFromCollection,
      };
    });
  }

  public static Task<object?> EditAssemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var deleteSubassembly = PluginRuntime.GetOptionalBool(parameters, "delete") ?? false;
    var editParameters = ReadParameters(parameters?["parameters"] as JsonObject);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var openMode = deleteSubassembly || editParameters.Count > 0 ? OpenMode.ForWrite : OpenMode.ForRead;
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, openMode);
      var subassemblyIds = GetSubassemblyIds(assembly);

      if (string.IsNullOrWhiteSpace(subassemblyName))
      {
        var subassemblies = subassemblyIds
          .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
          .Select(ToSubassemblySummary)
          .ToList();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyCount"] = subassemblies.Count,
          ["subassemblies"] = subassemblies,
        };
      }

      var targetId = subassemblyIds.FirstOrDefault(id =>
      {
        var candidate = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
        return string.Equals(CivilObjectUtils.GetName(candidate), subassemblyName, StringComparison.OrdinalIgnoreCase);
      });
      if (targetId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Subassembly '{subassemblyName}' not found in assembly '{assemblyName}'.");

      var target = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, targetId, OpenMode.ForWrite);
      if (deleteSubassembly)
      {
        target.Erase();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyName"] = subassemblyName,
          ["deleted"] = true,
        };
      }

      var updated = ApplySubassemblyParameters(target, editParameters);
      if (editParameters.Count > 0 && updated.Count != editParameters.Count)
      {
        var missing = editParameters.Keys.Except(updated, StringComparer.OrdinalIgnoreCase);
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Subassembly parameters were not writable: {string.Join(", ", missing)}. The transaction was not committed.");
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyName"] = subassemblyName,
        ["updatedParameters"] = updated,
        ["updated"] = updated.Count > 0,
      };
    });
  }

  private static Dictionary<string, object?> ReadParameters(JsonObject? values)
  {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    if (values == null) return result;
    foreach (var pair in values)
    {
      // 2026-08-16: JsonValue.GetValue<object>() 会抛 InvalidOperationException——
      // 用 JsonNodeToString 安全取字符串再按类型还原
      var raw = pair.Value;
      if (raw is System.Text.Json.Nodes.JsonValue jv)
      {
        if (jv.TryGetValue<double>(out var d)) result[pair.Key] = d;
        else if (jv.TryGetValue<bool>(out var b)) result[pair.Key] = b;
        else if (jv.TryGetValue<long>(out var l)) result[pair.Key] = l;
        else if (jv.TryGetValue<int>(out var i)) result[pair.Key] = i;
        else result[pair.Key] = PluginRuntime.JsonNodeToString(raw);
      }
      else
      {
        result[pair.Key] = PluginRuntime.JsonNodeToString(raw);
      }
    }
    return result;
  }

  private static Assembly FindAssemblyByName(
    CivilDocument civilDocument,
    Transaction transaction,
    string name,
    OpenMode openMode)
  {
    foreach (ObjectId id in civilDocument.AssemblyCollection)
    {
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, openMode);
      if (string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
        return assembly;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{name}' was not found in the drawing.");
  }

  private static List<ObjectId> GetSubassemblyIds(Assembly assembly)
  {
    return assembly.Groups
      .SelectMany(group => group.GetSubassemblyIds().Cast<ObjectId>())
      .Where(id => !id.IsNull)
      .Distinct()
      .ToList();
  }

  private static List<string> ApplySubassemblyParameters(AcDbObject subassembly, IReadOnlyDictionary<string, object?> parameters)
  {
    var updated = new List<string>();

    // 2026-08-16: 真实参数入口 = Subassembly.ParamsDouble/ParamsLong/ParamsBool/ParamsString 集合
    // （反射 TrySetProperty 设的是 CLR 元数据属性，Width/Slope/Thickness 等几何参数是哑的——静默失败）
    var paramCollections = new[]
    {
      (Collection: CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsDouble"), Kind: "double"),
      (Collection: CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsLong"), Kind: "long"),
      (Collection: CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsBool"), Kind: "bool"),
      (Collection: CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsString"), Kind: "string"),
    };

    foreach (var pair in parameters)
    {
      var set = false;
      foreach (var pc in paramCollections)
      {
        if (pc.Collection == null) continue;
        // 用索引器取参数对象（Item[string]，GetIndexedPropertyValue 已支持重载精确匹配）
        object? param = null;
        try
        {
          param = Civil3DCompatibility.GetIndexedPropertyValue(pc.Collection, "Item", pair.Key);
        }
        catch
        {
          param = null;
        }
        if (param == null) continue;

        try
        {
          var converted = ConvertParamValue(pair.Value, pc.Kind);
          if (Civil3DCompatibility.TrySetProperty(param, "Value", converted))
          {
            updated.Add(pair.Key);
            set = true;
            break;
          }
          PluginLog.Debug("Subasm", $"Param '{pair.Key}' value set returned false (kind={pc.Kind}, param={param.GetType().Name})");
        }
        catch (Exception ex)
        {
          PluginLog.Debug("Subasm", $"Param '{pair.Key}' set failed (kind={pc.Kind}): {ex.Message}");
        }
      }

      // 兜底：CLR 属性（旧逻辑）
      if (!set)
      {
        if (Civil3DCompatibility.TrySetProperty(subassembly, pair.Key, pair.Value))
          updated.Add(pair.Key);
      }
    }
    return updated;
  }

  private static object? ConvertParamValue(object? value, string kind)
  {
    if (value == null) return null;
    return kind switch
    {
      "double" => Convert.ToDouble(value),
      "long" => Convert.ToInt64(value),
      "bool" => Convert.ToBoolean(value),
      "string" => value.ToString(),
      _ => value,
    };
  }

  private static string? GetStyleName(Assembly assembly, Transaction transaction)
  {
    if (assembly.StyleId.IsNull) return null;
    return CivilObjectUtils.GetName(transaction.GetObject(assembly.StyleId, OpenMode.ForRead));
  }

  private static List<Dictionary<string, object?>> GetSubassemblies(Assembly assembly, Transaction transaction)
  {
    return GetSubassemblyIds(assembly)
      .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
      .Select(ToSubassemblySummary)
      .ToList();
  }

  private static Dictionary<string, object?> ToSubassemblySummary(AcDbObject subassembly)
  {
    var parameters = new Dictionary<string, object?>();
    // 2026-08-16: 读真实参数（ParamsDouble/ParamsLong/ParamsBool/ParamsString），不是 CLR 元数据
    var collections = new[]
    {
      CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsDouble"),
      CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsLong"),
      CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsBool"),
      CivilObjectUtils.GetPropertyValue<object>(subassembly, "ParamsString"),
    };
    foreach (var coll in collections)
    {
      if (coll == null) continue;
      var count = Civil3DCompatibility.GetPropertyValue<int?>(coll, "Count") ?? 0;
      for (var i = 0; i < count; i++)
      {
        // 用 Item[int] 索引器取参数（GetIndexedPropertyValue 已支持重载精确匹配）
        object? param = null;
        try
        {
          param = Civil3DCompatibility.GetIndexedPropertyValue(coll, "Item", i);
        }
        catch
        {
          param = null;
        }
        if (param == null) continue;
        var key = Civil3DCompatibility.GetPropertyValue(param, "Key")?.ToString()
          ?? Civil3DCompatibility.GetPropertyValue(param, "DisplayName")?.ToString();
        if (string.IsNullOrWhiteSpace(key)) continue;
        parameters[key] = Civil3DCompatibility.GetPropertyValue(param, "Value");
      }
    }

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassembly.Handle.ToString(),
      ["handle"] = CivilObjectUtils.GetHandle(subassembly),
      ["type"] = subassembly.GetType().Name,
      ["className"] = subassembly.GetType().Name,
      ["side"] = Civil3DCompatibility.GetPropertyValue(subassembly, "Side")?.ToString()?.ToLowerInvariant() ?? "none",
      ["parameters"] = parameters,
    };
  }

  private static Dictionary<string, object?> ToAssemblySummary(Assembly assembly)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = assembly.Name,
      ["handle"] = CivilObjectUtils.GetHandle(assembly),
      ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
      ["type"] = assembly.Type.ToString(),
    };
  }

  // -------------------------------------------------------------------------
  // getSubassemblyGeometry (2026-09-03): read subassembly geometry (points/point codes)
  // usage: diagnose freshly created subassemblies. Geometry points only appear once the
  // assembly layout has been generated.
  // 2026-09-08: hook chain resolved to readable parent names; points read with ForWrite
  // open (ForRead returns an empty collection); added side + point Offset/Elevation.
  // -------------------------------------------------------------------------
  public static Task<object?> GetSubassemblyGeometryAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead);
      var result = new List<Dictionary<string, object?>>();

      // NOTE: Subassembly.Points is only populated when the subassembly object is
      // opened ForWrite (a ForRead open returns an empty collection). The transaction
      // is not committed here (ReadAsync), so no drawing changes are persisted.
      var subs = GetSubassemblyIds(assembly)
        .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForWrite))
        .ToList();
      var byId = new Dictionary<ObjectId, AcDbObject>();
      foreach (var s in subs) byId[s.ObjectId] = s;

      foreach (var sub in subs)
      {
        var entry = new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(sub) ?? sub.Handle.ToString(),
          ["handle"] = CivilObjectUtils.GetHandle(sub),
          ["side"] = Civil3DCompatibility.GetPropertyValue(sub, "Side")?.ToString()?.ToLowerInvariant() ?? "none",
          ["pointIndexHookTo"] = NormalizeHookIndex(Civil3DCompatibility.GetPropertyValue(sub, "PointIndexHookTo")),
          ["status"] = Civil3DCompatibility.GetPropertyValue(sub, "Status")?.ToString(),
          ["isDynamic"] = Civil3DCompatibility.GetPropertyValue(sub, "IsDynamic")?.ToString(),
          ["generatorMode"] = ReadGeneratorMode(sub),
        };

        // resolve hook target into readable parent name + handle
        var hookRaw = Civil3DCompatibility.GetPropertyValue(sub, "SubassemblyHookTo");
        entry["hookToRaw"] = hookRaw?.ToString();
        AcDbObject? hookTarget = null;
        if (hookRaw is ObjectId hookOid && !hookOid.IsNull)
        {
          byId.TryGetValue(hookOid, out hookTarget);
        }
        entry["hookTo"] = hookTarget == null ? null : CivilObjectUtils.GetName(hookTarget) ?? hookTarget.Handle.ToString();
        entry["hookToHandle"] = hookTarget == null ? null : CivilObjectUtils.GetHandle(hookTarget);

        // read Points + per-point codes (+ Offset/Elevation when available)
        var points = new List<Dictionary<string, object?>>();
        var pointColl = CivilObjectUtils.GetPropertyValue<object>(sub, "Points");
        if (pointColl != null)
        {
          var count = Civil3DCompatibility.GetPropertyValue<int?>(pointColl, "Count") ?? 0;
          for (var i = 0; i < count; i++)
          {
            var pt = CivilObjectUtils.InvokeMethod(pointColl, "Item", i);
            if (pt == null) continue;
            var codes = new List<string?>();
            var codeColl = CivilObjectUtils.GetPropertyValue<object>(pt, "Codes");
            if (codeColl != null)
            {
              var cc = Civil3DCompatibility.GetPropertyValue<int?>(codeColl, "Count") ?? 0;
              for (var j = 0; j < cc; j++)
              {
                var codeVal = CivilObjectUtils.InvokeMethod(codeColl, "Item", j);
                if (codeVal != null) codes.Add(codeVal.ToString());
              }
            }
            var pEntry = new Dictionary<string, object?>
            {
              ["index"] = Civil3DCompatibility.GetPropertyValue<int?>(pt, "Index") ?? i,
              ["codes"] = codes,
            };
            TryAddPointCoord(pt, pEntry, "Offset");
            TryAddPointCoord(pt, pEntry, "Elevation");
            points.Add(pEntry);
          }
        }
        entry["points"] = points;
        entry["pointCount"] = points.Count;

        // read Links (each link has its own Points collection - never probed before)
        var links = new List<Dictionary<string, object?>>();
        var linkColl = CivilObjectUtils.GetPropertyValue<object>(sub, "Links");
        if (linkColl != null)
        {
          var linkCount = Civil3DCompatibility.GetPropertyValue<int?>(linkColl, "Count") ?? 0;
          for (var i = 0; i < linkCount; i++)
          {
            var link = CivilObjectUtils.InvokeMethod(linkColl, "Item", i);
            if (link == null) continue;
            var linkEntry = new Dictionary<string, object?>
            {
              ["index"] = Civil3DCompatibility.GetPropertyValue<int?>(link, "Index") ?? i,
              ["codes"] = ReadCodes(link, "Codes"),
              ["isHidden"] = Civil3DCompatibility.GetPropertyValue(link, "IsHidden")?.ToString(),
            };
            var lpColl = CivilObjectUtils.GetPropertyValue<object>(link, "Points");
            var lps = new List<Dictionary<string, object?>>();
            if (lpColl != null)
            {
              var lpCount = Civil3DCompatibility.GetPropertyValue<int?>(lpColl, "Count") ?? 0;
              for (var j = 0; j < lpCount; j++)
              {
                var lp = CivilObjectUtils.InvokeMethod(lpColl, "Item", j);
                if (lp == null) continue;
                var pe = new Dictionary<string, object?>
                {
                  ["index"] = Civil3DCompatibility.GetPropertyValue<int?>(lp, "Index") ?? j,
                  ["codes"] = ReadCodes(lp, "Codes"),
                };
                TryAddPointCoord(lp, pe, "Offset");
                TryAddPointCoord(lp, pe, "Elevation");
                lps.Add(pe);
              }
            }
            linkEntry["points"] = lps;
            links.Add(linkEntry);
          }
        }
        entry["links"] = links;
        entry["linkCount"] = links.Count;

        // read Shapes (each shape references links)
        var shapes = new List<Dictionary<string, object?>>();
        var shapeColl = CivilObjectUtils.GetPropertyValue<object>(sub, "Shapes");
        if (shapeColl != null)
        {
          var shapeCount = Civil3DCompatibility.GetPropertyValue<int?>(shapeColl, "Count") ?? 0;
          for (var i = 0; i < shapeCount; i++)
          {
            var shape = CivilObjectUtils.InvokeMethod(shapeColl, "Item", i);
            if (shape == null) continue;
            shapes.Add(new Dictionary<string, object?>
            {
              ["index"] = Civil3DCompatibility.GetPropertyValue<int?>(shape, "Index") ?? i,
              ["codes"] = ReadCodes(shape, "Codes"),
              ["linkCount"] = GetLinkCount(shape),
            });
          }
        }
        entry["shapes"] = shapes;
        entry["shapeCount"] = shapes.Count;
        result.Add(entry);
      }
      return new Dictionary<string, object?> { ["subassemblies"] = result };
    });
  }

  private static List<string?> ReadCodes(object? owner, string collProp)
  {
    var result = new List<string?>();
    var coll = CivilObjectUtils.GetPropertyValue<object>(owner, collProp);
    if (coll == null) return result;
    var cc = Civil3DCompatibility.GetPropertyValue<int?>(coll, "Count") ?? 0;
    for (var j = 0; j < cc; j++)
    {
      var v = CivilObjectUtils.InvokeMethod(coll, "Item", j);
      if (v != null) result.Add(v.ToString());
    }
    return result;
  }

  private static int GetLinkCount(object? shape)
  {
    var lc = CivilObjectUtils.GetPropertyValue<object>(shape, "Links");
    return lc == null ? 0 : (Civil3DCompatibility.GetPropertyValue<int?>(lc, "Count") ?? 0);
  }

  private static string? ReadGeneratorMode(AcDbObject sub)
  {
    try
    {
      var gen = CivilObjectUtils.GetPropertyValue<object>(sub, "GeometryGenerator");
      if (gen == null) return null;
      return Civil3DCompatibility.GetPropertyValue(gen, "GeometryGenerateMode")?.ToString()
        ?? Civil3DCompatibility.GetPropertyValue(gen, "MacroOrClassName")?.ToString();
    }
    catch
    {
      return null;
    }
  }

  // 2026-09-10: AutoCAD 用 int.MinValue 表示"该子部件没有挂到任何 hook 点"。
  // 之前直接透传，输出 -2147483648 让 AI 看不懂；统一归一化为 null。
  private static object? NormalizeHookIndex(object? raw)
  {
    if (raw == null) return null;
    try
    {
      var i = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
      if (i == int.MinValue || i < -1) return null;
      return i;
    }
    catch { return raw; }
  }

  private static void TryAddPointCoord(object pt, Dictionary<string, object?> pEntry, string prop)
  {
    try
    {
      var v = Civil3DCompatibility.GetPropertyValue(pt, prop);
      if (v != null) pEntry[prop] = v;
    }
    catch
    {
      // property not present on this point type - ignore
    }
  }

  // -------------------------------------------------------------------------
  // verifyAssemblyGeometry (2026-09-09): 装配几何校验——probe 每子部件的实体几何，
  // 提取可验证特征（点相对坐标/宽度/层高、hatch 面积=层面积），供 AI 自查建出的
  // 装配是否符合预期参数。非破坏（内存 Explode）。
  // -------------------------------------------------------------------------
  public static Task<object?> VerifyAssemblyGeometryAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead);
      var subs = GetSubassemblyIds(assembly)
        .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
        .ToList();

      var results = new List<Dictionary<string, object?>>();
      foreach (var sub in subs)
      {
        var collector = new List<Dictionary<string, object?>>();
        if (sub is Autodesk.AutoCAD.DatabaseServices.Entity ent)
        {
          AcadCommands.ExplodeCollect(ent, collector, 0, 15, new HashSet<string>());
        }

        // 展平 nested
        var flat = new List<Dictionary<string, object?>>();
        void Flatten(List<Dictionary<string, object?>> items)
        {
          foreach (var it in items)
          {
            flat.Add(it);
            if (it.TryGetValue("nested", out var nv) && nv is List<Dictionary<string, object?>> nested)
              Flatten(nested);
          }
        }
        Flatten(collector);

        var circles = flat.Where(i => i.GetValueOrDefault("type")?.ToString() == "AcDbCircle").ToList();
        var hatches = flat.Where(i => i.GetValueOrDefault("type")?.ToString() == "AcDbHatch").ToList();
        var allLines = flat.Where(i => i.GetValueOrDefault("type")?.ToString() == "AcDbLine").ToList();
        var points = flat.Where(i => i.GetValueOrDefault("type")?.ToString() == "AcDbPoint").ToList();

        // 2026-09-10 修正: 炸开子部件后 C3D 会同时产出两类线——
        //   C-ROAD-LINK = 真正的连接线(link)
        //   C-ROAD-SHAP = 造型/结构面的边界线(shape 外框, 与 link 重叠)
        // 混在一起数会让 linkCount 约为真实值的 2 倍(实测 4 条 link 报 8)，故按图层拆分。
        static string LayerOf(Dictionary<string, object?> it) => it.GetValueOrDefault("layer")?.ToString() ?? "";
        var linkLines = allLines.Where(i => LayerOf(i).Contains("LINK", StringComparison.OrdinalIgnoreCase)).ToList();
        var shapeLines = allLines.Where(i => LayerOf(i).Contains("SHAP", StringComparison.OrdinalIgnoreCase)).ToList();
        var otherLines = allLines.Where(i =>
          !LayerOf(i).Contains("LINK", StringComparison.OrdinalIgnoreCase) &&
          !LayerOf(i).Contains("SHAP", StringComparison.OrdinalIgnoreCase)).ToList();
        // 图层不可识别时(其他生成器/自定义)回退到全部线，避免误报 0
        var linkCount = linkLines.Count > 0 ? linkLines.Count : allLines.Count;

        // 从 circle 提取相对坐标（相对最小 x/y）——注意 collector 是插件内存对象，值为 double 而非 JsonValue
        double? minX = null, minY = null;
        var coords = new List<(double X, double Y)>();
        foreach (var c in circles)
        {
          if (!c.TryGetValue("center", out var cv) || cv is not Dictionary<string, object?> ct) continue;
          if (!TryGetDouble(ct, "x", out var x) || !TryGetDouble(ct, "y", out var y)) continue;
          coords.Add((x, y));
          if (minX == null || x < minX) minX = x;
          if (minY == null || y < minY) minY = y;
        }
        var relPts = coords.Select(c => new Dictionary<string, object?>
        {
          ["x"] = Math.Round(c.X - (minX ?? 0), 4),
          ["y"] = Math.Round(c.Y - (minY ?? 0), 4),
        }).ToList();

        var width = coords.Count > 0 ? Math.Round((coords.Max(c => c.X) - (minX ?? 0)), 4) : (double?)null;
        var height = coords.Count > 0 ? Math.Round((coords.Max(c => c.Y) - (minY ?? 0)), 4) : (double?)null;

        results.Add(new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(sub) ?? sub.Handle.ToString(),
          ["handle"] = CivilObjectUtils.GetHandle(sub),
          ["side"] = Civil3DCompatibility.GetPropertyValue(sub, "Side")?.ToString()?.ToLowerInvariant() ?? "none",
          ["pointCount"] = circles.Count,
          ["linkCount"] = linkCount,
          ["shapeLineCount"] = shapeLines.Count,
          ["otherLineCount"] = otherLines.Count,
          ["shapeCount"] = hatches.Count,
          ["extraPointCount"] = points.Count,
          ["width"] = width,
          ["height"] = height,
          ["hatchAreas"] = hatches
            .Select(h => h.TryGetValue("area", out var av) && TryGetDouble(h, "area", out var a)
              ? Math.Round(a, 4) : (double?)null)
            .Where(a => a != null).Select(a => a!.Value).ToList(),
          ["points"] = relPts,
        });
      }

      return new Dictionary<string, object?>
      {
        ["assembly"] = assemblyName,
        ["subassemblyCount"] = results.Count,
        ["subassemblies"] = results,
        ["note"] = "pointCount=几何点数(圆点); linkCount=连接线(C-ROAD-LINK); shapeLineCount=造型边界线(C-ROAD-SHAP,与link重叠故不计入linkCount); shapeCount=结构层(hatch); width/height=点相对跨度; hatchAreas=各层面积(宽x层深可反推验证)",
      };
    });
  }

  // 容错取 double（内存对象是 double；经过 JSON 往返可能是 JsonValue/数字串）
  private static bool TryGetDouble(Dictionary<string, object?> dict, string key, out double value)
  {
    value = 0;
    if (!dict.TryGetValue(key, out var v) || v == null) return false;
    if (v is double d) { value = d; return true; }
    if (v is JsonValue jv)
    {
      try { value = jv.GetValue<double>(); return true; } catch { }
    }
    if (v is IConvertible conv)
    {
      try { value = conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture); return true; } catch { }
    }
    return false;
  }

}
