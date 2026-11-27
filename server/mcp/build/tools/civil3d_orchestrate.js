import { z } from "zod";
import { routeIntent } from "../orchestration/IntentRouter.js";
import { findToolCatalogEntry } from "../orchestration/ToolCatalog.js";
import { ROUTE_PARAM_FIELDS } from "../orchestration/routeParams.js";
import { getProjectContext } from "../orchestration/ProjectContextService.js";
import { buildWorkflowPlan } from "../orchestration/WorkflowPlanner.js";
import { resolveParamsFromSelection } from "../orchestration/SelectionResolver.js";
import { executeRegisteredTool } from "./toolHandlerRegistry.js";
const Civil3DOrchestrateInputShape = {
    request: z.string().min(1).optional().describe("Natural-language Civil 3D request."),
    execute: z.boolean().optional().describe("When true, execute the selected action if enough parameters are available."),
    toolName: z.string().optional().describe("Exact registered tool name to plan or execute through the orchestrator."),
    toolAction: z.string().optional().describe("Optional exact action when targeting a multi-action tool directly."),
    toolParameters: z.record(z.string(), z.unknown()).optional().describe("Optional exact tool parameters when targeting a tool directly."),
    name: z.string().optional(),
    alignmentName: z.string().optional(),
    corridorName: z.string().optional(),
    profileName: z.string().optional(),
    surfaceName: z.string().optional(),
    projectFolder: z.string().optional(),
    shortcutName: z.string().optional(),
    shortcutType: z.string().optional(),
    templatePath: z.string().optional(),
    saveAs: z.string().optional(),
    limit: z.number().optional(),
    layerPrefix: z.string().optional(),
    networkName: z.string().optional(),
    pipeName: z.string().optional(),
    structureName: z.string().optional(),
    fittingName: z.string().optional(),
    partName: z.string().optional(),
    partsList: z.string().optional(),
    targetType: z.string().optional(),
    targetName: z.string().optional(),
    targetNetwork: z.string().optional(),
    sourceNetwork: z.string().optional(),
    newPartName: z.string().optional(),
    startPoint: z.object({ x: z.number(), y: z.number(), z: z.number().optional() }).optional(),
    endPoint: z.object({ x: z.number(), y: z.number(), z: z.number().optional() }).optional(),
    position: z.object({ x: z.number(), y: z.number(), z: z.number().optional() }).optional(),
    baseSurface: z.string().optional(),
    comparisonSurface: z.string().optional(),
    style: z.string().optional(),
    layer: z.string().optional(),
    labelSet: z.string().optional(),
    filePath: z.string().optional(),
    outputPath: z.string().optional(),
    overwrite: z.boolean().optional(),
    gridSpacing: z.number().optional(),
    designSpeed: z.number().optional(),
    inflow: z.number().optional(),
    outflow: z.number().optional(),
    bottomElevation: z.number().optional(),
    topElevation: z.number().optional(),
    minCoverDepth: z.number().optional(),
    maxCoverDepth: z.number().optional(),
    payItems: z.array(z.object({
        code: z.string(),
        description: z.string(),
        unit: z.string(),
        unitPrice: z.number().nonnegative(),
    })).optional(),
};
const Civil3DOrchestrateInputSchema = z.object(Civil3DOrchestrateInputShape);
// 2026-10-15: 白名单统一到 orchestration/routeParams.ts（与 ToolCatalog 共用，消除漂移）
const ROUTE_PARAM_KEYS = [...ROUTE_PARAM_FIELDS];
function pickRouteParams(source) {
    if (!source) {
        return {};
    }
    return Object.fromEntries(ROUTE_PARAM_KEYS
        .filter((key) => source[key] !== undefined)
        .map((key) => [key, source[key]]));
}
/** 2026-10-15: 按 ROUTE_PARAM_KEYS 把 args 覆盖到 fallback 之上（buildMerged/Direct 共用，替代两处各手抄 40+ 字段）。 */
function mapRouteParams(args, fallback) {
    const a = args;
    const fb = fallback;
    const out = {};
    for (const key of ROUTE_PARAM_KEYS)
        out[key] = a[key] !== undefined ? a[key] : fb[key];
    return out;
}
function buildMergedParams(args, routed) {
    return mapRouteParams(args, routed.extractedParams);
}
function buildDirectParams(args) {
    const extractedRequestParams = args.request ? routeIntent(args.request).extractedParams : {};
    return { ...extractedRequestParams, ...mapRouteParams(args, extractedRequestParams), ...pickRouteParams(args.toolParameters) };
}
function buildDirectRoute(args) {
    const requestedToolName = args.toolName;
    if (!requestedToolName) {
        throw new Error("toolName is required for direct tool orchestration.");
    }
    const requestedAction = args.toolAction
        ?? (typeof args.toolParameters?.action === "string"
            ? String(args.toolParameters.action)
            : undefined);
    const match = findToolCatalogEntry(requestedToolName, requestedAction);
    if (!match) {
        throw new Error(requestedAction
            ? `Tool '${requestedToolName}' with action '${requestedAction}' was not found in the tool catalog.`
            : `Tool '${requestedToolName}' was not found in the tool catalog.`);
    }
    return {
        match,
        confidence: 1,
        missingFields: [],
        extractedParams: {},
        reasoning: requestedAction
            ? `Used exact tool override for '${requestedToolName}' action '${requestedAction}'.`
            : `Used exact tool override for '${requestedToolName}'.`,
    };
}
function findMissingRequiredFields(requiredFields, params) {
    const paramRecord = params;
    return requiredFields.filter((field) => !hasRequiredValue(paramRecord[field]));
}
function buildDirectExecutionContext(args) {
    const routed = buildDirectRoute(args);
    const params = buildDirectParams(args);
    const exactParameters = buildExactToolParameters(args, params, routed.match.action);
    const missingFields = findMissingRequiredFields(routed.match.requiredFields, exactParameters);
    return {
        routed,
        params,
        exactParameters,
        missingFields,
    };
}
function buildExactToolParameters(args, params, selectedAction) {
    const parameterObject = args.toolParameters ?? {};
    const explicitAction = args.toolAction
        ?? (typeof parameterObject.action === "string" ? String(parameterObject.action) : undefined)
        ?? (selectedAction !== "execute" ? selectedAction : undefined);
    const exactParameters = {
        ...(explicitAction ? { action: explicitAction } : {}),
        ...params,
        ...parameterObject,
    };
    return Object.fromEntries(Object.entries(exactParameters).filter(([, value]) => value !== undefined));
}
function hasRequiredValue(value) {
    if (typeof value === "string") {
        return value.trim().length > 0;
    }
    if (typeof value === "number") {
        return Number.isFinite(value);
    }
    if (Array.isArray(value)) {
        return value.length > 0;
    }
    return value != null;
}
function buildCorridorAdvice() {
    return {
        recommendedSequence: [
            "Create or identify an alignment.",
            "Create or identify an existing ground surface.",
            "Create an EG profile from that alignment and surface.",
            "Create a design profile or layout profile.",
            "Choose an assembly before corridor creation.",
        ],
        requiredObjects: ["alignment", "surface", "profile", "assembly"],
    };
}
function buildContextAwareAdvice(intent, projectContext) {
    const drawingInfo = (projectContext.drawingInfo ?? {});
    const objectCounts = drawingInfo.objectCounts ?? {};
    const hasSurfaces = (objectCounts.surfaces ?? 0) > 0;
    const hasAlignments = (objectCounts.alignments ?? 0) > 0;
    const hasProfiles = (objectCounts.profiles ?? 0) > 0;
    if (intent.match.intent === "create_profile_from_surface") {
        return {
            prerequisitesSatisfied: hasSurfaces && hasAlignments,
            suggestions: [
                !hasAlignments ? "No alignments detected. Create or identify an alignment before creating a profile from surface." : null,
                !hasSurfaces ? "No surfaces detected. Create or identify a surface before creating a profile from surface." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "list_profiles" || intent.match.intent === "create_layout_profile") {
        return {
            prerequisitesSatisfied: hasAlignments,
            suggestions: [
                !hasAlignments ? "No alignments detected. Select or identify an alignment before working with profiles." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "corridor_prerequisites") {
        return {
            prerequisitesSatisfied: hasAlignments && hasSurfaces && hasProfiles,
            suggestions: [
                !hasAlignments ? "No alignments detected. Corridor creation usually starts with an alignment." : null,
                !hasSurfaces ? "No surfaces detected. Existing ground is commonly needed for corridor targeting and profile generation." : null,
                !hasProfiles ? "No profiles detected. A design or existing-ground profile is typically needed before creating a corridor." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "get_surface_statistics" || intent.match.intent === "analyze_surface_slope" || intent.match.intent === "calculate_surface_volume") {
        return {
            prerequisitesSatisfied: hasSurfaces,
            suggestions: [
                !hasSurfaces ? "No surfaces detected. Create or identify the required Civil 3D surfaces before running this analysis." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "generate_surface_volume_report" || intent.match.intent === "sample_surface_elevations") {
        return {
            prerequisitesSatisfied: hasSurfaces,
            suggestions: [
                !hasSurfaces ? "No surfaces detected. Create or identify the required Civil 3D surface before running this request." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "check_stopping_distance") {
        return {
            prerequisitesSatisfied: hasAlignments && hasProfiles,
            suggestions: [
                !hasAlignments ? "No alignments detected. Select or identify an alignment before running stopping sight distance checks." : null,
                !hasProfiles ? "No profiles detected. Select or identify a profile before running stopping sight distance checks." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "generate_detention_stage_storage") {
        return {
            prerequisitesSatisfied: hasSurfaces,
            suggestions: [
                !hasSurfaces ? "No surfaces detected. A basin surface is required before generating a detention stage-storage table." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "calculate_slope_geometry") {
        return {
            prerequisitesSatisfied: hasAlignments,
            suggestions: [
                !hasAlignments ? "No alignments detected. Select or identify an alignment before running slope analysis." : null,
            ].filter(Boolean),
        };
    }
    if (intent.match.intent === "export_pay_items" || intent.match.intent === "estimate_material_cost") {
        return {
            prerequisitesSatisfied: true,
            suggestions: [],
        };
    }
    return {
        prerequisitesSatisfied: true,
        suggestions: [],
    };
}
async function executeIntent(intent, params) {
    if (intent.match.intent === "corridor_prerequisites") {
        return buildCorridorAdvice();
    }
    const toolArgs = intent.match.buildToolArgs(params);
    return await executeRegisteredToolDirectly(intent.match.toolName, toolArgs);
}
async function executeRegisteredToolDirectly(toolName, parameters) {
    return await executeRegisteredTool(toolName, parameters);
}
export async function executeToolCallViaOrchestrator(toolName, parameters) {
    const directExecution = buildDirectExecutionContext({
        toolName,
        toolAction: typeof parameters.action === "string"
            ? String(parameters.action)
            : undefined,
        toolParameters: parameters,
        ...pickRouteParams(parameters),
    });
    if (directExecution.missingFields.length > 0) {
        throw new Error(`Missing required fields: ${directExecution.missingFields.join(", ")}`);
    }
    return await executeRegisteredToolDirectly(directExecution.routed.match.toolName, directExecution.exactParameters);
}
export async function executeCivil3DOrchestrate(rawArgs) {
    const args = Civil3DOrchestrateInputSchema.parse(rawArgs);
    if (!args.request && !args.toolName) {
        throw new Error("civil3d_orchestrate requires either a request or a toolName.");
    }
    const directExecution = args.toolName ? buildDirectExecutionContext(args) : null;
    const routed = directExecution?.routed ?? routeIntent(args.request);
    const projectContext = await getProjectContext();
    const mergedParams = directExecution?.params ?? buildMergedParams(args, routed);
    const selectionResolution = resolveParamsFromSelection(mergedParams, projectContext);
    const params = selectionResolution.resolvedParams;
    const contextAdvice = buildContextAwareAdvice(routed, projectContext);
    const workflowPlan = buildWorkflowPlan(routed, params, projectContext);
    const missingFields = findMissingRequiredFields(routed.match.requiredFields, directExecution?.exactParameters ?? params);
    const response = {
        request: args.request ?? `direct:${args.toolName}`,
        selectedIntent: routed.match.intent,
        selectedTool: routed.match.toolName,
        selectedAction: routed.match.action,
        confidence: routed.confidence,
        reasoning: routed.reasoning,
        params,
        inferredFromSelection: selectionResolution.inferredFromSelection,
        projectContext,
        contextAdvice,
        workflowPlan,
        missingFields,
        canExecute: missingFields.length === 0,
    };
    if (args.execute === true) {
        if (missingFields.length > 0) {
            response.status = "needs_input";
            response.message = `Missing required fields: ${missingFields.join(", ")}`;
            response.clarificationQuestions = workflowPlan.clarificationQuestions;
        }
        else {
            response.status = "executed";
            response.result = directExecution
                ? await executeRegisteredToolDirectly(routed.match.toolName, directExecution.exactParameters)
                : await executeIntent(routed, params);
        }
    }
    else {
        response.status = missingFields.length > 0 ? "planned_needs_input" : "planned";
        response.clarificationQuestions = workflowPlan.clarificationQuestions;
    }
    return response;
}
