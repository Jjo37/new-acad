using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-09-23: 填充（Hatch）创建 / 批量导入 / 编辑（Bro 要求补齐"填充"能力，含读属性与编辑）。
///
/// 25.0.58 已反射确认可用：
///   SetHatchPattern(HatchPatternType, string) / AppendLoop(HatchLoopTypes, Point2dCollection, DoubleCollection)
///   InsertLoopAt / RemoveLoopAt / EvaluateHatch(bool) / PatternScale / PatternAngle / Origin / Elevation
///   HatchStyle / Associative / IsSolidFill / PatternName / PatternType / Area / NumberOfLoops / LoopTypeAt
///   SetGradient / SetGradientColors（渐变填充）
///
/// 用法要点：先 AppendEntity + AddNewlyCreatedDBObject，再 SetDatabaseDefaults → SetHatchPattern →
///   AppendLoop → EvaluateHatch(true)（Evaluate 必须在实体入库后调用）。
/// </summary>
internal static class HatchCommands
{
    private const int MaxBatchItems = 5000;

    private static HatchStyle ParseStyle(string? s) => (s ?? string.Empty).ToLowerInvariant() switch
    {
        "outer" => HatchStyle.Outer,
        "ignore" => HatchStyle.Ignore,
        _ => HatchStyle.Normal,
    };

    private static string StyleName(HatchStyle st) => st switch
    {
        HatchStyle.Outer => "outer",
        HatchStyle.Ignore => "ignore",
        _ => "normal",
    };

    private static bool IsSolid(Hatch h) => string.Equals(h.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);

    /// <summary>从 {points:[{x,y}|[x,y]], bulges?:[]} 追加一个环；第 0 环=External，其余=Default（岛）</summary>
    private static bool TryAddLoop(Hatch hatch, JsonNode? loopNode, int index, string? islandStyle, out string error)
    {
        error = string.Empty;
        var ptsNode = loopNode is JsonObject o ? o["points"] as JsonArray : loopNode as JsonArray;
        if (ptsNode == null || ptsNode.Count < 3) { error = "loop 需要 >=3 个点"; return false; }

        var pts = new Point2dCollection();
        for (var i = 0; i < ptsNode.Count; i++)
        {
            double x, y;
            var p = ptsNode[i];
            if (p is JsonArray pa && pa.Count >= 2 && pa[0] != null && pa[1] != null)
            {
                x = pa[0]!.GetValue<double>(); y = pa[1]!.GetValue<double>();
            }
            else if (p is JsonObject po && po["x"] != null && po["y"] != null)
            {
                x = po["x"]!.GetValue<double>(); y = po["y"]!.GetValue<double>();
            }
            else { error = "第 " + i + " 个点格式不对"; return false; }
            pts.Add(new Point2d(x, y));
        }

        var bulges = new DoubleCollection();
        for (var i = 0; i < pts.Count; i++) bulges.Add(0);
        if (loopNode is JsonObject lo && lo["bulges"] is JsonArray bulNode)
        {
            for (var i = 0; i < bulNode.Count && i < bulges.Count; i++)
            {
                try
                {
                    var b = bulNode[i]!.GetValue<double>();
                    if (!double.IsNaN(b) && !double.IsInfinity(b)) bulges[i] = Math.Max(-1.9, Math.Min(1.9, b));
                }
                catch { /* 忽略非法 bulge */ }
            }
        }

        var loopType = index == 0
            ? HatchLoopTypes.External
            : (string.Equals(islandStyle, "outermost", StringComparison.OrdinalIgnoreCase) ? HatchLoopTypes.Outermost : HatchLoopTypes.Default);
        hatch.AppendLoop(loopType, pts, bulges);
        return true;
    }

    private static Dictionary<string, object?> Describe(Hatch h) => new()    {
        ["pattern"] = h.PatternName,
        ["patternType"] = h.PatternType.ToString(),
        ["isSolid"] = IsSolid(h) || h.IsSolidFill,
        ["isGradient"] = h.IsGradient,
        ["gradientName"] = h.IsGradient ? h.GradientName : null,
        ["gradientAngle"] = h.IsGradient ? h.GradientAngle : (double?)null,
        ["gradientOneColor"] = h.IsGradient ? h.GradientOneColorMode : (bool?)null,
        ["gradientColors"] = h.IsGradient ? GradientColorsOf(h) : null,
        ["patternScale"] = h.PatternScale,
        ["patternAngle"] = h.PatternAngle,
        ["islandStyle"] = StyleName(h.HatchStyle),
        ["associative"] = h.Associative,
        ["area"] = h.Area,
        ["loopCount"] = h.NumberOfLoops,
        ["layer"] = h.Layer,
    };

    /// <summary>[[r,g,b],[r,g,b],...] → GradientColor[]（value 从 0 到 1 均匀分布）</summary>
    private static List<GradientColor>? ParseGradientColors(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count < 2) return null;
        var list = new List<GradientColor>();
        for (var i = 0; i < arr.Count; i++)
        {
            var col = ColorUtils.Parse(arr[i]);
            if (col == null) return null;
            list.Add(new GradientColor(col, (float)i / (arr.Count - 1)));
        }
        return list;
    }

    private static List<Dictionary<string, object?>> GradientColorsOf(Hatch h)
    {
        var list = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var gc in h.GetGradientColors())
            {
                // 2026-09-23: 25.0.58 里 GradientColor 的成员实际是普通方法 get_Color()/get_Value()
                // （不是属性），所以属性名找不到 —— 两种形式都试
                var t = gc.GetType();
                object? cv = t.GetProperty("Color")?.GetValue(gc) ?? t.GetMethod("get_Color")?.Invoke(gc, null) ?? t.GetField("Color")?.GetValue(gc);
                object? vv = t.GetProperty("Value")?.GetValue(gc) ?? t.GetMethod("get_Value")?.Invoke(gc, null) ?? t.GetField("Value")?.GetValue(gc);
                var rgb = new Dictionary<string, object?>();
                if (cv != null)
                {
                    var ct = cv.GetType();
                    rgb["r"] = ct.GetProperty("Red")?.GetValue(cv) ?? ct.GetMethod("get_Red")?.Invoke(cv, null);
                    rgb["g"] = ct.GetProperty("Green")?.GetValue(cv) ?? ct.GetMethod("get_Green")?.Invoke(cv, null);
                    rgb["b"] = ct.GetProperty("Blue")?.GetValue(cv) ?? ct.GetMethod("get_Blue")?.Invoke(cv, null);
                }
                rgb["value"] = vv;
                list.Add(rgb);
            }
        }
        catch { /* 读不到就空表 */ }
        return list;
    }

    // ---------------------------------------------------------------- createHatch
    public static Task<object?> CreateHatchAsync(JsonObject? parameters)
    {
        var loopsNode = PluginRuntime.GetParameter(parameters, "loops") as JsonArray;
        var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
        if ((loopsNode == null || loopsNode.Count == 0) && (pointsNode == null || pointsNode.Count < 3))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
                "createHatch 需要 loops:[{points,bulges?},...]（多环/带岛）或 points:[...]（单环）");

        var pattern = PluginRuntime.GetOptionalString(parameters, "pattern") ?? "SOLID";
        var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
        var angle = PluginRuntime.GetOptionalDouble(parameters, "angle") ?? 0.0;
        var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
        var islandStyle = PluginRuntime.GetOptionalString(parameters, "islandStyle");
        var associative = PluginRuntime.GetOptionalInt(parameters, "associative") == 1;
        var gradientName = PluginRuntime.GetOptionalString(parameters, "gradient");
        var colorNode = PluginRuntime.GetParameter(parameters, "color") as JsonNode;

        return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
            var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var hatch = new Hatch();
            var hatchId = modelSpace.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            hatch.SetDatabaseDefaults();
            hatch.Associative = associative;

            if (!string.IsNullOrWhiteSpace(gradientName))
            {
                // 2026-09-23: 必须先声明为渐变对象，否则 SetGradient 不生效（实测会静默退化成 SOLID）
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                hatch.HatchObjectType = HatchObjectType.GradientObject;
                try { hatch.SetGradient(GradientPatternType.PreDefinedGradient, gradientName!); }
                catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "渐变名无效: " + gradientName + " (" + ex.Message + ")"); }
                if (!hatch.IsGradient)
                    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "渐变未生效（名称可能无效）: " + gradientName + "；常见可用名: LINEAR/CYLINDER/SPHERICAL/HEMISPHERICAL/CURVED");
                var gAngle = PluginRuntime.GetOptionalDouble(parameters, "gradientAngle");
                if (gAngle.HasValue) hatch.GradientAngle = gAngle.Value;
                var gCols = ParseGradientColors(PluginRuntime.GetParameter(parameters, "gradientColors") as JsonNode);
                if (gCols != null) { hatch.GradientOneColorMode = false; hatch.SetGradientColors(gCols.ToArray()); }
                else
                {
                    hatch.GradientOneColorMode = true;
                    var tint = PluginRuntime.GetOptionalDouble(parameters, "shadeTint");
                    if (tint.HasValue) hatch.ShadeTintValue = (float)Math.Max(0, Math.Min(1, tint.Value));
                }
            }
            else
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.PatternAngle = angle;
            }
            hatch.HatchStyle = ParseStyle(islandStyle);

            var count = 0;
            if (loopsNode != null && loopsNode.Count > 0)
            {
                for (var i = 0; i < loopsNode.Count; i++)
                {
                    if (!TryAddLoop(hatch, loopsNode[i], i, islandStyle, out var err))
                        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "loops[" + i + "]: " + err);
                    count++;
                }
            }
            else
            {
                if (!TryAddLoop(hatch, pointsNode, 0, islandStyle, out var err))
                    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points: " + err);
                count = 1;
            }

            hatch.EvaluateHatch(true);

            if (!string.IsNullOrWhiteSpace(layerName))
            {
                try { hatch.LayerId = LookupUtils.GetLayerId(database, transaction, layerName); } catch { /* 图层不存在则保持当前层 */ }
            }
            var col = ColorUtils.Parse(colorNode);
            if (col != null) hatch.Color = col;

            var res = Describe(hatch);
            res["handle"] = CivilObjectUtils.GetHandle(hatch);
            res["loops"] = count;
            res["ok"] = true;
            return (object?)res;
        });
    }

    // ---------------------------------------------------------------- importHatches（批量，一次事务）
    public static Task<object?> ImportHatchesAsync(JsonObject? parameters)
    {
        var itemsNode = PluginRuntime.GetParameter(parameters, "items") as JsonArray
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items 数组必填: {items:[{loops|points, pattern?, scale?, angle?, color?, layer?}], layer?, pattern?, scale?, angle?}");
        if (itemsNode.Count > MaxBatchItems)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items 太多 (" + itemsNode.Count + " > " + MaxBatchItems + ")");

        var defLayer = PluginRuntime.GetOptionalString(parameters, "layer");
        var defPattern = PluginRuntime.GetOptionalString(parameters, "pattern");
        var defScale = PluginRuntime.GetOptionalDouble(parameters, "scale");
        var defAngle = PluginRuntime.GetOptionalDouble(parameters, "angle");
        var defIsland = PluginRuntime.GetOptionalString(parameters, "islandStyle");

        return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
            var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var created = 0; var failed = 0; var loopTotal = 0; var vertexTotal = 0; var areaTotal = 0.0;
            var failedSamples = new List<string>();
            var layers = new List<string>();

            foreach (var node in itemsNode)
            {
                if (node is not JsonObject item) { failed++; if (failedSamples.Count < 5) failedSamples.Add("item 不是对象"); continue; }
                var loopsNode = item["loops"] as JsonArray;
                var ptsNode = item["points"] as JsonArray;
                if ((loopsNode == null || loopsNode.Count == 0) && (ptsNode == null || ptsNode.Count < 3))
                {
                    failed++; if (failedSamples.Count < 5) failedSamples.Add("item 缺 loops/points"); continue;
                }

                var layerName = item["layer"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : defLayer;
                var pattern = (item["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : defPattern) ?? "SOLID";
                var scale = (item["scale"] is JsonValue sv && sv.TryGetValue<double>(out var sc) ? sc : defScale) ?? 1.0;
                var angle = (item["angle"] is JsonValue av && av.TryGetValue<double>(out var ag) ? ag : defAngle) ?? 0.0;
                var island = item["islandStyle"] is JsonValue iv && iv.TryGetValue<string>(out var iss) ? iss : defIsland;

                var hatch = new Hatch();
                modelSpace.AppendEntity(hatch);
                transaction.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetDatabaseDefaults();
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.PatternAngle = angle;
                hatch.HatchStyle = ParseStyle(island);

                var n = 0; var bad = false;
                if (loopsNode != null && loopsNode.Count > 0)
                {
                    for (var i = 0; i < loopsNode.Count; i++)
                    {
                        var c = loopsNode[i];
                        if (!TryAddLoop(hatch, c, i, island, out var err))
                        {
                            bad = true; failed++; if (failedSamples.Count < 5) failedSamples.Add("loops[" + i + "]: " + err);
                            break;
                        }
                        var lp = c is JsonObject lo2 ? (lo2["points"] as JsonArray)?.Count ?? 0 : (c as JsonArray)?.Count ?? 0;
                        vertexTotal += lp; n++;
                    }
                }
                else
                {
                    if (!TryAddLoop(hatch, ptsNode, 0, island, out var err))
                    {
                        bad = true; failed++; if (failedSamples.Count < 5) failedSamples.Add("points: " + err);
                    }
                    else { vertexTotal += ptsNode!.Count; n = 1; }
                }

                if (bad) { try { hatch.Erase(); } catch { } continue; }

                hatch.EvaluateHatch(true);
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    try { hatch.LayerId = LookupUtils.GetLayerId(database, transaction, layerName); } catch { }
                }
                var col = ColorUtils.Parse(item["color"]);
                if (col != null) hatch.Color = col;

                created++; loopTotal += n; areaTotal += hatch.Area;
                if (!layers.Contains(hatch.Layer, StringComparer.OrdinalIgnoreCase)) layers.Add(hatch.Layer);
            }

            return (object?)new Dictionary<string, object?>
            {
                ["created"] = created,
                ["failed"] = failed,
                ["failedSamples"] = failedSamples.ToArray(),
                ["loops"] = loopTotal,
                ["vertexCount"] = vertexTotal,
                ["areaTotal"] = areaTotal,
                ["layers"] = layers.ToArray(),
            };
        });
    }

    // ---------------------------------------------------------------- editHatch
    public static Task<object?> EditHatchAsync(JsonObject? parameters)
    {
        var handle = PluginRuntime.GetRequiredString(parameters, "handle");
        var pattern = PluginRuntime.GetOptionalString(parameters, "pattern");
        var scale = PluginRuntime.GetOptionalDouble(parameters, "scale");
        var angle = PluginRuntime.GetOptionalDouble(parameters, "angle");
        var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
        var islandStyle = PluginRuntime.GetOptionalString(parameters, "islandStyle");
        var colorNode = PluginRuntime.GetParameter(parameters, "color") as JsonNode;
        var loopsNode = PluginRuntime.GetParameter(parameters, "loops") as JsonArray;
        var originNode = PluginRuntime.GetParameter(parameters, "origin");
        var gradientName = PluginRuntime.GetOptionalString(parameters, "gradient");
        var gradientAngle = PluginRuntime.GetOptionalDouble(parameters, "gradientAngle");
        var gradientColorsNode = PluginRuntime.GetParameter(parameters, "gradientColors") as JsonNode;
        var shadeTint = PluginRuntime.GetOptionalDouble(parameters, "shadeTint");

        if (pattern == null && !scale.HasValue && !angle.HasValue && layerName == null && islandStyle == null
            && colorNode == null && (loopsNode == null || loopsNode.Count == 0) && originNode == null
            && gradientName == null && !gradientAngle.HasValue && gradientColorsNode == null && !shadeTint.HasValue)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
                "editHatch 至少要给一个要改的项: pattern/scale/angle/color/layer/islandStyle/origin/loops/gradient/gradientAngle/gradientColors/shadeTint");

        return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var hash = new Autodesk.AutoCAD.DatabaseServices.Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
            var id = database.GetObjectId(false, hash, 0);
            if (id.IsNull) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", "handle 找不到: " + handle);
            if (transaction.GetObject(id, OpenMode.ForRead) is not Hatch hatch)
                throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "该 handle 不是填充(HATCH): " + handle);

            transaction.GetObject(id, OpenMode.ForWrite);
            var changed = new List<string>();

            if (loopsNode != null && loopsNode.Count > 0)
            {
                for (var i = hatch.NumberOfLoops - 1; i >= 0; i--) hatch.RemoveLoopAt(i);
                for (var i = 0; i < loopsNode.Count; i++)
                {
                    if (!TryAddLoop(hatch, loopsNode[i], i, islandStyle, out var err))
                        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "loops[" + i + "]: " + err);
                }
                changed.Add("loops");
            }

            // 2026-09-23: 渐变编辑 —— 必须排在 pattern 块之前（渐变态下设 pattern 会 eNotApplicable）
            if (gradientName != null)
            {
                try
                {
                    if (gradientName.Length == 0)
                    {
                        // 转回图案填充：先切回 HatchObject，再设图案（顺序反了会 eNotApplicable）
                        hatch.HatchObjectType = HatchObjectType.HatchObject;
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, string.IsNullOrWhiteSpace(pattern) ? "SOLID" : pattern);
                        changed.Add("gradient(off)");
                    }
                    else
                    {
                        // 已是渐变对象时不要再 SetHatchPattern（会把它打坏 → INTERNAL_ERROR）
                        if (!hatch.IsGradient)
                        {
                            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                            hatch.HatchObjectType = HatchObjectType.GradientObject;
                        }
                        hatch.SetGradient(GradientPatternType.PreDefinedGradient, gradientName);
                        if (!hatch.IsGradient) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "渐变未生效（名称可能无效）: " + gradientName);
                        changed.Add("gradient");
                    }
                }
                catch (JsonRpcDispatchException) { throw; }
                catch (Exception ex)
                {
                    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "渐变处理失败: '" + gradientName + "' (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
            }

            if (pattern != null)
            {
                try { hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern); changed.Add("pattern"); }
                catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "图案名无效: " + pattern + " (" + ex.Message + ")"); }
            }
            if (scale.HasValue) { hatch.PatternScale = scale.Value; changed.Add("scale"); }
            if (angle.HasValue) { hatch.PatternAngle = angle.Value; changed.Add("angle"); }
            if (islandStyle != null) { hatch.HatchStyle = ParseStyle(islandStyle); changed.Add("islandStyle"); }
            if (gradientAngle.HasValue) { hatch.GradientAngle = gradientAngle.Value; changed.Add("gradientAngle"); }
            var gCols2 = ParseGradientColors(gradientColorsNode);
            if (gCols2 != null) { hatch.GradientOneColorMode = false; hatch.SetGradientColors(gCols2.ToArray()); changed.Add("gradientColors"); }
            if (shadeTint.HasValue) { hatch.ShadeTintValue = (float)Math.Max(0, Math.Min(1, shadeTint.Value)); changed.Add("shadeTint"); }
            if (originNode is JsonArray oa && oa.Count >= 2 && oa[0] != null && oa[1] != null)
            {
                hatch.Origin = new Point2d(oa[0]!.GetValue<double>(), oa[1]!.GetValue<double>());
                changed.Add("origin");
            }

            hatch.EvaluateHatch(true);

            if (!string.IsNullOrWhiteSpace(layerName))
            {
                try { hatch.LayerId = LookupUtils.GetLayerId(database, transaction, layerName); changed.Add("layer"); } catch { }
            }
            var col = ColorUtils.Parse(colorNode);
            if (col != null) { hatch.Color = col; changed.Add("color"); }

            var res = Describe(hatch);
            res["handle"] = handle;
            res["changed"] = changed.ToArray();
            res["ok"] = true;
            return (object?)res;
        });
    }
}
