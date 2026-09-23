// @ts-nocheck — 汉克本地定制（从编译产物保留，避免 tsc 类型检查）
// 来源: 上游 Civil3D-mcp v1.2.1 + 本地 Phase 1-7 扩展
// hankDomain.js - Hank self-developed MCP tools (with Phase 1-7 extensions)
import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
const GenericResponseSchema = z.object({}).passthrough();
// Helper to make standard modify tool defs
function makeModifyTool(method, argsSchema, executeFn) {
    return {
        action: method, inputSchema: argsSchema, responseSchema: GenericResponseSchema,
        capabilities: ["modify"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: [method],
        execute: executeFn,
    };
}
// ========== 1. getSelection ==========
const GetSelectionArgs = z.object({});
const getSelectionExecute = async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getSelection", {}));
const getSelectionShape = {};
// ========== 2. getEntityInfo ==========
const GetEntityInfoArgs = z.object({ handle: z.string().min(1) });
const getEntityInfoExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getEntityInfo", { handle: args.handle }));
const getEntityInfoShape = { handle: z.string().min(1) };
// ========== 3. setEntityColor ==========
const SetEntityColorArgs = z.object({ handle: z.string().min(1), color: z.number().int().min(1).max(255) });
const setEntityColorExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setEntityColor", { handle: args.handle, color: args.color }));
const setEntityColorShape = { handle: z.string().min(1), color: z.number().int().min(1).max(255) };
// ========== 4-6. Basic ops: mirror, executeCommand, delete ==========
const MirrorEntityArgs = z.object({ handle: z.string().min(1) });
const mirrorEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("mirrorEntity", { handle: args.handle }));
const mirrorEntityShape = { handle: z.string().min(1) };
const ExecuteCommandArgs = z.object({ command: z.string().min(1) });
// 2026-08-14 P1-2: execute_command 命令白名单——只放行确定无副作用、非交互的命令；复合/参数命令拒绝
const EXECUTE_CMD_ALLOW = new Set([
    "REGEN", "REGENALL", "REDRAW", "GRAPHSCR", "TEXTSCR", "CLEANSCREENON", "CLEANSCREENOFF", "STATUS",
]);
const executeCommandExecute = async (args) => {
    const raw = String(args.command ?? "").trim();
    if (!raw) {
        const e = new Error("execute_command: command 为空");
        e.code = "CIVIL3D.INVALID_INPUT";
        throw e;
    }
    const single = raw.split(/[;\n]/)[0].trim(); // 拒绝复合命令（; 或换行拼接多条）
    const noPrefix = single.replace(/^[_\-.]/, "");
    const cmdName = noPrefix.split(/\s+/)[0].toUpperCase(); // 取命令名（丢弃参数）
    if (raw !== single || !EXECUTE_CMD_ALLOW.has(cmdName)) {
        const e = new Error("execute_command 拒绝: 仅允许白名单命令 " + [...EXECUTE_CMD_ALLOW].join("/") + "，且不支持复合/参数命令。请改用专用 hank_* 工具");
        e.code = "CIVIL3D.INVALID_INPUT";
        throw e;
    }
    return await withApplicationConnection(async (appClient) => await appClient.sendCommand("executeCommand", { command: cmdName }));
};
const executeCommandShape = { command: z.string().min(1) };
const DeleteEntityArgs = z.object({ handle: z.string().min(1) });
const deleteEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("deleteEntity", { handle: args.handle }));
const deleteEntityShape = { handle: z.string().min(1) };
// ========== 7-10. Transform ops: move, copy, rotate, scale ==========
const MoveEntityArgs = z.object({ handle: z.string().min(1), dx: z.number(), dy: z.number() });
const moveEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("moveEntity", { handle: args.handle, dx: args.dx, dy: args.dy }));
const moveEntityShape = { handle: z.string().min(1), dx: z.number(), dy: z.number() };
const CopyEntityArgs = z.object({ handle: z.string().min(1), dx: z.number().optional(), dy: z.number().optional() });
const copyEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("copyEntity", { handle: args.handle, dx: args.dx, dy: args.dy }));
const copyEntityShape = { handle: z.string().min(1), dx: z.number().optional(), dy: z.number().optional() };
const RotateEntityArgs = z.object({ handle: z.string().min(1), angle: z.number(), cx: z.number().optional(), cy: z.number().optional() });
const rotateEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("rotateEntity", { handle: args.handle, angle: args.angle, cx: args.cx, cy: args.cy }));
const rotateEntityShape = { handle: z.string().min(1), angle: z.number(), cx: z.number().optional(), cy: z.number().optional() };
const ScaleEntityArgs = z.object({ handle: z.string().min(1), factor: z.number(), cx: z.number().optional(), cy: z.number().optional() });
const scaleEntityExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("scaleEntity", { handle: args.handle, factor: args.factor, cx: args.cx, cy: args.cy }));
const scaleEntityShape = { handle: z.string().min(1), factor: z.number(), cx: z.number().optional(), cy: z.number().optional() };
// ========== 11-12. Selection & Layers ==========
const SelectByCriteriaArgs = z.object({ criteria: z.enum(["layer", "type", "color"]), value: z.string().min(1) });
const selectByCriteriaExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("selectByCriteria", { criteria: args.criteria, value: args.value }));
const selectByCriteriaShape = { criteria: z.enum(["layer", "type", "color"]), value: z.string().min(1) };
const GetLayersArgs = z.object({});
const getLayersExecute = async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getLayers", {}));
const getLayersShape = {};
const CreateLayerArgs = z.object({ name: z.string().min(1), color: z.number().int().min(1).max(255).optional() });
const createLayerExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("createLayer", { name: args.name, color: args.color }));
const createLayerShape = { name: z.string().min(1), color: z.number().int().min(1).max(255).optional() };
// ========== 13. Create: circle ==========
const CreateCircleArgs = z.object({ center: z.array(z.number()).length(2), diameter: z.number().positive() });
const createCircleExecute = async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("createCircle", { center: args.center, diameter: args.diameter }));
const createCircleShape = { center: z.array(z.number()).length(2), diameter: z.number().positive() };
// ========== Phase 1: Edit Tools ==========
const OffsetEntityArgs = z.object({ handle: z.string().min(1), distance: z.number().positive() });
const BreakEntityArgs = z.object({ handle: z.string().min(1), x: z.number(), y: z.number() });
const JoinEntitiesArgs = z.object({ handles: z.array(z.string()).min(2) });
const ExplodeEntityArgs = z.object({ handle: z.string().min(1) });
const FilletEntitiesArgs = z.object({ radius: z.number().positive(), handle1: z.string().min(1), handle2: z.string().min(1) });
const ChamferEntitiesArgs = z.object({ distance1: z.number().positive(), distance2: z.number().positive(), handle1: z.string().min(1), handle2: z.string().min(1) });
const TrimEntityArgs = z.object({ cuttingHandle: z.string().min(1), targetHandle: z.string().min(1) });
const ExtendEntityArgs = z.object({ boundaryHandle: z.string().min(1), targetHandle: z.string().min(1) });
const StretchEntityArgs = z.object({ handle: z.string().min(1), dx: z.number(), dy: z.number() });
const ArrayEntityArgs = z.object({ handle: z.string().min(1), type: z.enum(["rectangular", "polar", "path"]), count: z.number().int().positive(), cx: z.number().optional(), cy: z.number().optional(), angle: z.number().optional(), rows: z.number().int().optional(), cols: z.number().int().optional(), levels: z.number().int().optional() });
const AlignEntityArgs = z.object({ handle: z.string().min(1), srcX: z.number(), srcY: z.number(), dstX: z.number(), dstY: z.number() });
const OverkillEntitiesArgs = z.object({});
const genExec = (method) => async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand(method, args || {}));
const offsetEntityExec = genExec("offsetEntity");
const breakEntityExec = genExec("breakEntity");
const joinEntitiesExec = genExec("joinEntities");
const explodeEntityExec = genExec("explodeEntity");
const filletEntitiesExec = genExec("filletEntities");
const chamferEntitiesExec = genExec("chamferEntities");
const trimEntityExec = genExec("trimEntity");
const extendEntityExec = genExec("extendEntity");
const stretchEntityExec = genExec("stretchEntity");
const arrayEntityExec = genExec("arrayEntity");
const alignEntityExec = genExec("alignEntity");
const overkillEntitiesExec = genExec("overkillEntities");
const OffsetEntityShape = { handle: z.string().min(1), distance: z.number().positive() };
const BreakEntityShape = { handle: z.string().min(1), x: z.number(), y: z.number() };
const JoinEntitiesShape = { handles: z.array(z.string()).min(2) };
const ExplodeEntityShape = { handle: z.string().min(1) };
const FilletEntitiesShape = { radius: z.number().positive(), handle1: z.string().min(1), handle2: z.string().min(1) };
const ChamferEntitiesShape = { distance1: z.number().positive(), distance2: z.number().positive(), handle1: z.string().min(1), handle2: z.string().min(1) };
const TrimEntityShape = { cuttingHandle: z.string().min(1), targetHandle: z.string().min(1) };
const ExtendEntityShape = { boundaryHandle: z.string().min(1), targetHandle: z.string().min(1) };
const StretchEntityShape = { handle: z.string().min(1), dx: z.number(), dy: z.number() };
const ArrayEntityShape = { handle: z.string().min(1), type: z.enum(["rectangular", "polar", "path"]), count: z.number().int().positive() };
const AlignEntityShape = { handle: z.string().min(1), srcX: z.number(), srcY: z.number(), dstX: z.number(), dstY: z.number() };
const OverkillEntitiesShape = {};
// ========== Phase 2: Layer Tools ==========
const LayArgs = (name) => z.object({ layerName: z.string().min(1) });
const layExec = (method) => async (args) => await withApplicationConnection(async (appClient) => appClient.sendCommand(method, { layerName: args.layerName }));
const layShape = { layerName: z.string().min(1) };
const LayerIsolateArgs = z.object({ handles: z.array(z.string()).optional() });
const layerIsolateExec = async (args) => await withApplicationConnection(async (appClient) => appClient.sendCommand("layerIsolate", { handles: args.handles }));
const layerIsolateShape = { handles: z.array(z.string()).optional() };
const LayerMatchArgs = z.object({ sourceHandle: z.string().min(1), targetHandle: z.string().min(1) });
const layerMatchExec = async (args) => await withApplicationConnection(async (appClient) => appClient.sendCommand("layerMatch", { sourceHandle: args.sourceHandle, targetHandle: args.targetHandle }));
const layerMatchShape = { sourceHandle: z.string().min(1), targetHandle: z.string().min(1) };
// ========== Phase 3: Block Tools ==========
const CreateBlockArgs = z.object({ name: z.string().min(1), handles: z.array(z.string()).optional(), x: z.number().optional(), y: z.number().optional() });
const InsertBlockArgs = z.object({ blockName: z.string().min(1), x: z.number(), y: z.number(), scale: z.number().optional(), rotation: z.number().optional() });
const WriteBlockArgs = z.object({ handle: z.string().min(1), filePath: z.string().min(1) });
const createBlockExec = genExec("createBlock");
const insertBlockExec = genExec("insertBlock");
const writeBlockExec = genExec("writeBlock");
const CreateBlockShape = { name: z.string().min(1) };
const InsertBlockShape = { blockName: z.string().min(1), x: z.number(), y: z.number() };
const WriteBlockShape = { handle: z.string().min(1), filePath: z.string().min(1) };
// ========== Phase 4: Dim Tools ==========
const dimExec = (method) => async (args) => await withApplicationConnection(async (appClient) => appClient.sendCommand(method, args));
const DimLinearArgs = z.object({ x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number(), dimX: z.number(), dimY: z.number() });
const DimAlignedArgs = z.object({ x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number(), dimX: z.number(), dimY: z.number() });
const DimRadiusArgs = z.object({ handle: z.string().min(1), dimX: z.number(), dimY: z.number() });
const DimDiameterArgs = z.object({ handle: z.string().min(1), dimX: z.number(), dimY: z.number() });
const DimAngularArgs = z.object({ handle1: z.string().min(1), handle2: z.string().min(1), dimX: z.number(), dimY: z.number() });
const dimLinearExec = dimExec("dimLinear");
const dimAlignedExec = dimExec("dimAligned");
const dimRadiusExec = dimExec("dimRadius");
const dimDiameterExec = dimExec("dimDiameter");
const dimAngularExec = dimExec("dimAngular");
const DimLinearShape = { x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number() };
const DimAlignedShape = { x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number() };
const DimRadiusShape = { handle: z.string().min(1), dimX: z.number(), dimY: z.number() };
const DimDiameterShape = { handle: z.string().min(1), dimX: z.number(), dimY: z.number() };
const DimAngularShape = { handle1: z.string().min(1), handle2: z.string().min(1), dimX: z.number(), dimY: z.number() };
// ========== Phase 5: Text/Xref ==========
const EditTextArgs = z.object({ handle: z.string().min(1), newText: z.string() });
const ScaleTextArgs = z.object({ handles: z.array(z.string()), scaleFactor: z.number().positive() });
const AttachXrefArgs = z.object({ filePath: z.string().min(1), x: z.number(), y: z.number(), scale: z.number().optional(), rotation: z.number().optional() });
const editTextExec = dimExec("editText");
const scaleTextExec = dimExec("scaleText");
const attachXrefExec = dimExec("attachXref");
const EditTextShape = { handle: z.string().min(1), newText: z.string() };
const ScaleTextShape = { handles: z.array(z.string()), scaleFactor: z.number().positive() };
const AttachXrefShape = { filePath: z.string().min(1), x: z.number(), y: z.number() };
// ========== Phase 6: Query ==========
const MeasureDistArgs = z.object({ x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number() });
const MeasureAreaArgs = z.object({ handles: z.array(z.string()).optional() });
const measureDistExec = dimExec("measureDist");
const measureAreaExec = dimExec("measureArea");
const MeasureDistShape = { x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number() };
const MeasureAreaShape = { handles: z.array(z.string()).optional() };
// ========== Phase 7: 3D Modeling ==========
const ExtrudeSolidArgs = z.object({ handle: z.string().min(1), height: z.number(), taperAngle: z.number().optional() });
const RevolveSolidArgs = z.object({ handle: z.string().min(1), axisX1: z.number(), axisY1: z.number(), axisX2: z.number(), axisY2: z.number(), angle: z.number() });
const LoftSolidArgs = z.object({ handles: z.array(z.string()).min(2) });
const SweepSolidArgs = z.object({ profileHandle: z.string().min(1), pathHandle: z.string().min(1) });
const BooleanUnionArgs = z.object({ handles: z.array(z.string()).min(2) });
const BooleanSubtractArgs = z.object({ mainHandle: z.string().min(1), toolHandle: z.string().min(1) });
const Move3dArgs = z.object({ handle: z.string().min(1), dx: z.number(), dy: z.number(), dz: z.number() });
const Mirror3dArgs = z.object({ handle: z.string().min(1), p1x: z.number(), p1y: z.number(), p1z: z.number(), p2x: z.number(), p2y: z.number(), p2z: z.number() });
const Rotate3dArgs = z.object({ handle: z.string().min(1), ax: z.number(), ay: z.number(), az: z.number(), bx: z.number(), by: z.number(), bz: z.number(), angle: z.number() });
const Array3dArgs = z.object({ handle: z.string().min(1), type: z.enum(["rectangular", "polar"]), count: z.number().int().positive(), rows: z.number().int().optional(), cols: z.number().int().optional(), levels: z.number().int().optional() });
const SliceSolidArgs = z.object({ handle: z.string().min(1), p1x: z.number(), p1y: z.number(), p2x: z.number(), p2y: z.number(), p3x: z.number(), p3y: z.number() });
const SectionPlaneArgs = z.object({ handle: z.string().min(1) });
const InterferenceCheckArgs = z.object({ handle1: z.string().min(1), handle2: z.string().min(1) });
const ConvertToSurfaceArgs = z.object({ handle: z.string().min(1) });
const ConvertToSolidArgs = z.object({ handle: z.string().min(1) });
const RenderSceneArgs = z.object({});
const AssignMaterialArgs = z.object({ handle: z.string().min(1), materialName: z.string().min(1) });
const b3dExec = (method) => async (args) => await withApplicationConnection(async (appClient) => appClient.sendCommand(method, args));
const ExtrudeSolidShape = { handle: z.string().min(1), height: z.number() };
const RevolveSolidShape = { handle: z.string().min(1), angle: z.number() };
const LoftSolidShape = { handles: z.array(z.string()) };
const SweepSolidShape = { profileHandle: z.string().min(1), pathHandle: z.string().min(1) };
const BooleanUnionShape = { handles: z.array(z.string()) };
const BooleanSubtractShape = { mainHandle: z.string().min(1), toolHandle: z.string().min(1) };
const Move3dShape = { handle: z.string().min(1), dx: z.number(), dy: z.number(), dz: z.number() };
const Mirror3dShape = { handle: z.string().min(1), p1x: z.number(), p1y: z.number() };
const Rotate3dShape = { handle: z.string().min(1), angle: z.number() };
const Array3dShape = { handle: z.string().min(1), type: z.enum(["rectangular", "polar"]), count: z.number().int() };
const SliceSolidShape = { handle: z.string().min(1) };
const SectionPlaneShape = { handle: z.string().min(1) };
const InterferenceCheckShape = { handle1: z.string().min(1), handle2: z.string().min(1) };
const ConvertToSurfaceShape = { handle: z.string().min(1) };
const ConvertToSolidShape = { handle: z.string().min(1) };
const RenderSceneShape = {};
const AssignMaterialShape = { handle: z.string().min(1), materialName: z.string().min(1) };
// Build actions map
const mkAct = (action, argsSchema, exec, caps = "modify") => ({
    action, inputSchema: argsSchema, responseSchema: GenericResponseSchema,
    capabilities: [caps], requiresActiveDrawing: true, safeForRetry: false,
    pluginMethods: [action.replace(/_([a-z0-9])/g, (_, c) => c.toUpperCase()).replace(/3D$/, "3d")], execute: exec,
});
const actions = {
    get_selection: mkAct("get_selection", GetSelectionArgs, getSelectionExecute, "query"),
    get_entity_info: mkAct("get_entity_info", GetEntityInfoArgs, getEntityInfoExecute, "query"),
    set_entity_color: mkAct("set_entity_color", SetEntityColorArgs, setEntityColorExecute),
    mirror_entity: mkAct("mirror_entity", MirrorEntityArgs, mirrorEntityExecute),
    execute_command: mkAct("execute_command", ExecuteCommandArgs, executeCommandExecute, "execute"),
    delete_entity: mkAct("delete_entity", DeleteEntityArgs, deleteEntityExecute),
    move_entity: mkAct("move_entity", MoveEntityArgs, moveEntityExecute),
    copy_entity: mkAct("copy_entity", CopyEntityArgs, copyEntityExecute),
    rotate_entity: mkAct("rotate_entity", RotateEntityArgs, rotateEntityExecute),
    scale_entity: mkAct("scale_entity", ScaleEntityArgs, scaleEntityExecute),
    select_by_criteria: mkAct("select_by_criteria", SelectByCriteriaArgs, selectByCriteriaExecute, "query"),
    get_layers: mkAct("get_layers", GetLayersArgs, getLayersExecute, "query"),
    create_layer: mkAct("create_layer", CreateLayerArgs, createLayerExecute, "create"),
    create_circle: mkAct("create_circle", CreateCircleArgs, createCircleExecute, "create"),
    // Phase 1
    offset_entity: mkAct("offset_entity", OffsetEntityArgs, offsetEntityExec),
    break_entity: mkAct("break_entity", BreakEntityArgs, breakEntityExec),
    join_entities: mkAct("join_entities", JoinEntitiesArgs, joinEntitiesExec),
    explode_entity: mkAct("explode_entity", ExplodeEntityArgs, explodeEntityExec),
    fillet_entities: mkAct("fillet_entities", FilletEntitiesArgs, filletEntitiesExec),
    chamfer_entities: mkAct("chamfer_entities", ChamferEntitiesArgs, chamferEntitiesExec),
    trim_entity: mkAct("trim_entity", TrimEntityArgs, trimEntityExec),
    extend_entity: mkAct("extend_entity", ExtendEntityArgs, extendEntityExec),
    stretch_entity: mkAct("stretch_entity", StretchEntityArgs, stretchEntityExec),
    array_entity: mkAct("array_entity", ArrayEntityArgs, arrayEntityExec),
    align_entity: mkAct("align_entity", AlignEntityArgs, alignEntityExec),
    overkill_entities: mkAct("overkill_entities", OverkillEntitiesArgs, overkillEntitiesExec),
    // Phase 2
    layer_off: mkAct("layer_off", LayArgs(), layExec("layerOff")),
    layer_freeze: mkAct("layer_freeze", LayArgs(), layExec("layerFreeze")),
    layer_lock: mkAct("layer_lock", LayArgs(), layExec("layerLock")),
    layer_unlock: mkAct("layer_unlock", LayArgs(), layExec("layerUnlock")),
    layer_isolate: mkAct("layer_isolate", LayerIsolateArgs, layerIsolateExec),
    layer_match: mkAct("layer_match", LayerMatchArgs, layerMatchExec),
    // Phase 3
    create_block: mkAct("create_block", CreateBlockArgs, createBlockExec, "create"),
    insert_block: mkAct("insert_block", InsertBlockArgs, insertBlockExec, "create"),
    write_block: mkAct("write_block", WriteBlockArgs, writeBlockExec),
    // Phase 4
    dim_linear: mkAct("dim_linear", DimLinearArgs, dimLinearExec, "create"),
    dim_aligned: mkAct("dim_aligned", DimAlignedArgs, dimAlignedExec, "create"),
    dim_radius: mkAct("dim_radius", DimRadiusArgs, dimRadiusExec, "create"),
    dim_diameter: mkAct("dim_diameter", DimDiameterArgs, dimDiameterExec, "create"),
    dim_angular: mkAct("dim_angular", DimAngularArgs, dimAngularExec, "create"),
    // Phase 5
    edit_text: mkAct("edit_text", EditTextArgs, editTextExec),
    scale_text: mkAct("scale_text", ScaleTextArgs, scaleTextExec),
    attach_xref: mkAct("attach_xref", AttachXrefArgs, attachXrefExec, "create"),
    // Phase 6
    measure_dist: mkAct("measure_dist", MeasureDistArgs, measureDistExec, "query"),
    measure_area: mkAct("measure_area", MeasureAreaArgs, measureAreaExec, "query"),
    // Phase 7
    extrude_solid: mkAct("extrude_solid", ExtrudeSolidArgs, b3dExec("extrudeSolid"), "create"),
    revolve_solid: mkAct("revolve_solid", RevolveSolidArgs, b3dExec("revolveSolid"), "create"),
    loft_solid: mkAct("loft_solid", LoftSolidArgs, b3dExec("loftSolid"), "create"),
    sweep_solid: mkAct("sweep_solid", SweepSolidArgs, b3dExec("sweepSolid"), "create"),
    boolean_union: mkAct("boolean_union", BooleanUnionArgs, b3dExec("booleanUnion")),
    boolean_subtract: mkAct("boolean_subtract", BooleanSubtractArgs, b3dExec("booleanSubtract")),
    move_3d: mkAct("move_3d", Move3dArgs, b3dExec("move3d")),
    mirror_3d: mkAct("mirror_3d", Mirror3dArgs, b3dExec("mirror3d")),
    rotate_3d: mkAct("rotate_3d", Rotate3dArgs, b3dExec("rotate3d")),
    array_3d: mkAct("array_3d", Array3dArgs, b3dExec("array3d"), "create"),
    slice_solid: mkAct("slice_solid", SliceSolidArgs, b3dExec("sliceSolid")),
    section_plane: mkAct("section_plane", SectionPlaneArgs, b3dExec("sectionPlane")),
    interference_check: mkAct("interference_check", InterferenceCheckArgs, b3dExec("interferenceCheck")),
    convert_to_surface: mkAct("convert_to_surface", ConvertToSurfaceArgs, b3dExec("convertToSurface")),
    convert_to_solid: mkAct("convert_to_solid", ConvertToSolidArgs, b3dExec("convertToSolid")),
    render_scene: mkAct("render_scene", RenderSceneArgs, b3dExec("renderScene"), "create"),
    assign_material: mkAct("assign_material", AssignMaterialArgs, b3dExec("assignMaterial")),
};
// Build exposure entry
function expose(actionName, toolName, displayName, description, shape) {
    return {
        toolName: "hank_" + toolName, displayName: "Hank " + displayName, description,
        inputShape: shape, supportedActions: [actionName],
        resolveAction: (rawArgs) => ({ action: actionName, args: rawArgs || {} }),
    };
}
const exposures = [
    expose("get_selection", "get_selection", "GetSelection", "获取当前选中实体：返回当前图形中选中的实体列表", getSelectionShape),
    expose("get_entity_info", "get_entity_info", "GetEntityInfo", "查询实体详情：按 handle 获取实体的类型、图层、几何等信息", getEntityInfoShape),
    expose("set_entity_color", "set_entity_color", "SetEntityColor", "设置实体颜色：按 handle 修改实体颜色（1-255 索引色）", setEntityColorShape),
    expose("mirror_entity", "mirror_entity", "MirrorEntity", "镜像实体：以当前 UCS 原点为基点镜像指定实体", mirrorEntityShape),
    expose("execute_command", "execute_command", "ExecuteCommand", "执行 AutoCAD 命令：向 AutoCAD 命令行发送任意命令字符串", executeCommandShape),
    expose("delete_entity", "delete_entity", "DeleteEntity", "删除实体：按 handle 删除图形中的指定实体", deleteEntityShape),
    expose("move_entity", "move_entity", "MoveEntity", "移动实体：按 dx/dy 位移移动指定实体", moveEntityShape),
    expose("copy_entity", "copy_entity", "CopyEntity", "复制实体：按 dx/dy 位移复制指定实体", copyEntityShape),
    expose("rotate_entity", "rotate_entity", "RotateEntity", "旋转实体：按角度（度）旋转指定实体，可指定旋转中心", rotateEntityShape),
    expose("scale_entity", "scale_entity", "ScaleEntity", "缩放实体：按比例因子缩放指定实体，可指定基点", scaleEntityShape),
    expose("select_by_criteria", "select_by_criteria", "SelectByCriteria", "按条件选择：按图层/类型/颜色条件批量选中实体", selectByCriteriaShape),
    expose("get_layers", "get_layers", "GetLayers", "获取图层列表：返回当前图形中所有图层及其状态", getLayersShape),
    expose("create_layer", "create_layer", "CreateLayer", "创建图层：新建图层并可指定颜色", createLayerShape),
    expose("create_circle", "create_circle", "CreateCircle", "创建圆：按圆心和直径创建圆实体", createCircleShape),
    expose("offset_entity", "offset_entity", "OffsetEntity", "偏移实体：按指定距离偏移实体生成新对象", OffsetEntityShape),
    expose("break_entity", "break_entity", "BreakEntity", "打断实体：在指定坐标处将实体打断为两段", BreakEntityShape),
    expose("join_entities", "join_entities", "JoinEntities", "合并实体：将多个实体合并为一个实体", JoinEntitiesShape),
    expose("explode_entity", "explode_entity", "ExplodeEntity", "分解实体：将复合对象（块/多段线）分解为基本图元", ExplodeEntityShape),
    expose("fillet_entities", "fillet_entities", "FilletEntities", "圆角：在两个实体间按半径创建圆角", FilletEntitiesShape),
    expose("chamfer_entities", "chamfer_entities", "ChamferEntities", "倒角：在两个实体间按距离创建倒角", ChamferEntitiesShape),
    expose("trim_entity", "trim_entity", "TrimEntity", "修剪：以 cuttingHandle 为边界修剪 targetHandle 实体", TrimEntityShape),
    expose("extend_entity", "extend_entity", "ExtendEntity", "延伸：将目标实体延伸到边界实体", ExtendEntityShape),
    expose("stretch_entity", "stretch_entity", "StretchEntity", "拉伸：按 dx/dy 拉伸指定实体", StretchEntityShape),
    expose("array_entity", "array_entity", "ArrayEntity", "阵列复制：按矩形/环形/路径方式阵列复制实体", ArrayEntityShape),
    expose("align_entity", "align_entity", "AlignEntity", "对齐：按源点和目标点将实体对齐", AlignEntityShape),
    expose("overkill_entities", "overkill_entities", "OverkillEntities", "清理重叠：删除图形中重叠或重复的实体", OverkillEntitiesShape),
    expose("layer_off", "layer_off", "LayerOff", "关闭图层：关闭指定名称的图层", layShape),
    expose("layer_freeze", "layer_freeze", "LayerFreeze", "冻结图层：冻结指定名称的图层", layShape),
    expose("layer_lock", "layer_lock", "LayerLock", "锁定图层：锁定指定名称的图层", layShape),
    expose("layer_unlock", "layer_unlock", "LayerUnlock", "解锁图层：解锁指定名称的图层", layShape),
    expose("layer_isolate", "layer_isolate", "LayerIsolate", "隔离图层：仅显示选中实体所在的图层，隐藏其他图层", layerIsolateShape),
    expose("layer_match", "layer_match", "LayerMatch", "图层匹配：将目标实体的图层改为源实体的图层", layerMatchShape),
    expose("create_block", "create_block", "CreateBlock", "创建块：将选中实体定义为块", CreateBlockShape),
    expose("insert_block", "insert_block", "InsertBlock", "插入块：在指定坐标插入块引用，可设比例和旋转", InsertBlockShape),
    expose("write_block", "write_block", "WriteBlock", "写块：将实体导出为外部 .dwg 文件", WriteBlockShape),
    expose("dim_linear", "dim_linear", "DimLinear", "线性标注：两点间创建水平/垂直线性标注", DimLinearShape),
    expose("dim_aligned", "dim_aligned", "DimAligned", "对齐标注：两点间创建对齐标注", DimAlignedShape),
    expose("dim_radius", "dim_radius", "DimRadius", "半径标注：为圆/圆弧创建半径标注", DimRadiusShape),
    expose("dim_diameter", "dim_diameter", "DimDiameter", "直径标注：为圆/圆弧创建直径标注", DimDiameterShape),
    expose("dim_angular", "dim_angular", "DimAngular", "角度标注：在两个实体间创建角度标注", DimAngularShape),
    expose("edit_text", "edit_text", "EditText", "编辑文字：修改指定文字实体的内容", EditTextShape),
    expose("scale_text", "scale_text", "ScaleText", "缩放文字：批量缩放文字实体的高度", ScaleTextShape),
    expose("attach_xref", "attach_xref", "AttachXref", "附着外部参照：在指定位置附着外部 DWG 参照", AttachXrefShape),
    expose("measure_dist", "measure_dist", "MeasureDist", "测量距离：计算两点间的距离", MeasureDistShape),
    expose("measure_area", "measure_area", "MeasureArea", "测量面积：计算选中实体围合的面积", MeasureAreaShape),
    expose("extrude_solid", "extrude_solid", "ExtrudeSolid", "拉伸成体：将闭合轮廓按高度拉伸为三维实体", ExtrudeSolidShape),
    expose("revolve_solid", "revolve_solid", "RevolveSolid", "旋转成体：将轮廓绕指定轴旋转生成三维实体", RevolveSolidShape),
    expose("loft_solid", "loft_solid", "LoftSolid", "放样成体：通过多个截面放样生成三维实体", LoftSolidShape),
    expose("sweep_solid", "sweep_solid", "SweepSolid", "扫掠成体：沿路径扫掠轮廓生成三维实体", SweepSolidShape),
    expose("boolean_union", "boolean_union", "BooleanUnion", "布尔并集：把多个实体合并为一个实体", BooleanUnionShape),
    expose("boolean_subtract", "boolean_subtract", "BooleanSubtract", "布尔差集：从主实体中减去工具实体", BooleanSubtractShape),
    expose("move_3d", "move_3d", "Move3d", "三维移动：按 dx/dy/dz 移动三维实体", Move3dShape),
    expose("mirror_3d", "mirror_3d", "Mirror3d", "三维镜像：关于指定平面镜像三维实体", Mirror3dShape),
    expose("rotate_3d", "rotate_3d", "Rotate3d", "三维旋转：绕空间轴旋转三维实体", Rotate3dShape),
    expose("array_3d", "array_3d", "Array3d", "三维阵列：按矩形或环形阵列复制三维实体", Array3dShape),
    expose("slice_solid", "slice_solid", "SliceSolid", "剖切实体：用平面剖切三维实体", SliceSolidShape),
    expose("section_plane", "section_plane", "SectionPlane", "创建截面：为实体创建剖面平面", SectionPlaneShape),
    expose("interference_check", "interference_check", "InterferenceCheck", "干涉检查：检查两个实体是否相互干涉", InterferenceCheckShape),
    expose("convert_to_surface", "convert_to_surface", "ConvertToSurface", "转换为曲面：将实体转换为曲面", ConvertToSurfaceShape),
    expose("convert_to_solid", "convert_to_solid", "ConvertToSolid", "转换为实体：将曲面转换为三维实体", ConvertToSolidShape),
    expose("render_scene", "render_scene", "RenderScene", "渲染场景：渲染当前三维场景", RenderSceneShape),
    expose("assign_material", "assign_material", "AssignMaterial", "指定材质：为实体指定材质", AssignMaterialShape),
];
export const HANK_DOMAIN_DEFINITION = { domain: "hank", actions, exposures };
