import { z } from "zod";
import { approvalPolicy, getActiveDrawingFingerprint, hasApprovalRisk } from "./approvalPolicy.js";
import { captureToolHandler } from "./toolHandlerRegistry.js";
import { maybeStoreReportResource } from "./reportResourceStore.js";
import { idempotencyStore } from "./idempotencyStore.js";
import { createLogger } from "../utils/logger.js";
import { createRequestId, runWithRequestId } from "../utils/requestContext.js";
const MUTATING_CAPABILITIES = new Set([
    "create",
    "edit",
    "delete",
    "manage",
    "generate",
    "register",
    "export",
    "import",
]);
const log = createLogger("DomainRuntime");
function uniqueStrings(values) {
    const unique = [...new Set([...values].filter((value) => Boolean(value)))];
    return unique.length > 0 ? unique : undefined;
}
function uniqueCapabilities(values) {
    return [...new Set([...values].filter((value) => Boolean(value)))];
}
function buildToolErrorResult(toolName, actionName, error) {
    const message = error instanceof Error ? error.message : String(error);
    const typedErrorCode = error && typeof error === "object" && "code" in error
        && typeof error.code === "string"
        ? error.code
        : undefined;
    const errorCode = typedErrorCode ?? inferDomainErrorCode(message);
    const typedRpcCode = error && typeof error === "object" && "rpcCode" in error
        && typeof error.rpcCode === "number"
        ? error.rpcCode
        : undefined;
    const rpcCode = typedRpcCode ?? inferRpcErrorCode(errorCode);
    const scopedName = actionName ? `${toolName} action '${actionName}'` : toolName;
    console.error(`Error in ${scopedName}:`, error);
    return {
        content: [
            {
                type: "text",
                text: `${scopedName} failed: ${message}`,
            },
        ],
        isError: true,
        errorCode,
        rpcCode,
    };
}
function inferDomainErrorCode(message) {
    if (/timed out|timeout/i.test(message))
        return "CIVIL3D.TIMEOUT";
    if (/failed to connect|connection (?:failed|closed)|plugin not running/i.test(message))
        return "CIVIL3D.UNAVAILABLE";
    if (/approval required|already exists|conflict/i.test(message))
        return "CIVIL3D.CONFLICT";
    if (/not found|does not exist/i.test(message))
        return "CIVIL3D.OBJECT_NOT_FOUND";
    if (/not registered|unknown tool/i.test(message))
        return "CIVIL3D.METHOD_NOT_FOUND";
    if (/missing required|required fields?|invalid (?:input|parameter)|validation/i.test(message))
        return "CIVIL3D.INVALID_INPUT";
    return "CIVIL3D.INTERNAL_ERROR";
}
function inferRpcErrorCode(domainCode) {
    if (domainCode === "CIVIL3D.METHOD_NOT_FOUND")
        return -32601;
    if (domainCode === "CIVIL3D.INVALID_INPUT")
        return -32602;
    if (domainCode === "CIVIL3D.UNAVAILABLE")
        return -32001;
    if (domainCode === "CIVIL3D.OBJECT_NOT_FOUND")
        return -32004;
    if (domainCode === "CIVIL3D.TIMEOUT")
        return -32008;
    if (domainCode === "CIVIL3D.CONFLICT")
        return -32009;
    if (domainCode === "CIVIL3D.CANCELLED")
        return -32010;
    return -32000;
}
async function executeExposure(definition, exposure, rawArgs, progressExtra) {
    const resolved = exposure.resolveAction(rawArgs);
    const actionName = resolved.action;
    if (!exposure.supportedActions.includes(actionName)) {
        throw new Error(`Unsupported action '${actionName}' for tool '${exposure.toolName}'. ` +
            `Supported actions: ${exposure.supportedActions.join(", ")}.`);
    }
    const actionDefinition = definition.actions[actionName];
    if (!actionDefinition) {
        throw new Error(`Action '${actionName}' is not defined for domain '${definition.domain}'.`);
    }
    const parsedArgs = actionDefinition.inputSchema.parse(resolved.args);
    const idempotencyKey = typeof rawArgs.idempotencyKey === "string"
        ? rawArgs.idempotencyKey
        : undefined;
    if (idempotencyKey && !actionDefinition.safeForRetry) {
        const error = new Error(`Action '${actionName}' does not support idempotent retries.`);
        error.code = "CIVIL3D.INVALID_INPUT";
        throw error;
    }
    const executeOnce = async () => {
        await approvalPolicy.enforce({
            toolName: exposure.toolName,
            action: actionName,
            capabilities: actionDefinition.capabilities,
            safeForRetry: actionDefinition.safeForRetry,
            requiresActiveDrawing: actionDefinition.requiresActiveDrawing,
        }, rawArgs);
        await emitProgress(progressExtra, 50, 100, `${exposure.displayName}: validated and executing '${actionName}'.`);
        const response = await actionDefinition.execute(parsedArgs);
        const validatedResponse = actionDefinition.responseSchema
            ? actionDefinition.responseSchema.parse(response)
            : response;
        const serializedResponse = JSON.stringify(validatedResponse, null, 2);
        const reportResource = maybeStoreReportResource(actionName, serializedResponse);
        return {
            content: [
                { type: "text", text: serializedResponse },
                ...(reportResource ? [{
                        type: "resource_link",
                        uri: reportResource.uri,
                        name: reportResource.name,
                        description: "Structured Civil 3D result retained for MCP resource retrieval.",
                        mimeType: "application/json",
                        size: reportResource.size,
                    }] : []),
            ],
            structuredContent: { action: actionName, result: validatedResponse },
        };
    };
    if (!idempotencyKey)
        return executeOnce();
    const signature = { ...rawArgs };
    delete signature.approvalToken;
    delete signature.idempotencyKey;
    const drawingScope = actionDefinition.requiresActiveDrawing
        ? await getActiveDrawingFingerprint()
        : "drawing-independent";
    return idempotencyStore.execute(`${exposure.toolName}:${actionName}:${drawingScope}`, idempotencyKey, signature, executeOnce);
}
async function emitProgress(extra, progress, total, message) {
    const progressToken = extra?._meta?.progressToken;
    if (progressToken === undefined || typeof extra?.sendNotification !== "function") {
        return;
    }
    try {
        await extra.sendNotification({
            method: "notifications/progress",
            params: { progressToken, progress, total, message },
        });
    }
    catch (error) {
        console.warn(`Failed to send MCP progress notification for '${message}':`, error);
    }
}
export function buildExposureOutputSchema(definition, exposure) {
    const actionNames = exposure.supportedActions;
    const actionSchema = actionNames.length > 0
        ? z.enum(actionNames)
        : z.string();
    const responseSchemas = actionNames.map((actionName) => definition.actions[actionName]?.responseSchema ?? z.unknown());
    const resultSchema = responseSchemas.length === 0
        ? z.unknown()
        : responseSchemas.length === 1
            ? responseSchemas[0]
            : z.union(responseSchemas);
    return z.object({ action: actionSchema, result: resultSchema });
}
export function buildExposureAnnotations(definition, exposure) {
    const actions = exposure.supportedActions
        .map((actionName) => definition.actions[actionName])
        .filter((action) => Boolean(action));
    const isReadOnly = actions.every((action) => action.capabilities.every((capability) => !MUTATING_CAPABILITIES.has(capability)));
    const isDestructive = actions.some((action) => hasApprovalRisk({
        toolName: exposure.toolName,
        action: action.action,
        capabilities: action.capabilities,
        safeForRetry: action.safeForRetry,
    }));
    return {
        title: exposure.displayName,
        readOnlyHint: isReadOnly,
        destructiveHint: isDestructive,
        idempotentHint: actions.length > 0 && actions.every((action) => action.safeForRetry),
        openWorldHint: false,
    };
}
export function registerDomainTools(server, definition) {
    registerSelectedDomainTools(server, definition, definition.exposures);
}
function createExposureHandler(definition, exposure) {
    return async (rawArgs, extra) => {
        const progressExtra = extra && typeof extra === "object"
            ? extra
            : undefined;
        const requestId = createRequestId();
        const startedAt = performance.now();
        return runWithRequestId(requestId, async () => {
            try {
                log.info("Tool request started", { requestId, tool: exposure.toolName });
                await emitProgress(progressExtra, 0, 100, `${exposure.displayName}: request received.`);
                const result = await executeExposure(definition, exposure, rawArgs, progressExtra);
                await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: completed.`);
                log.info("Tool request completed", {
                    requestId,
                    tool: exposure.toolName,
                    durationMs: Math.round(performance.now() - startedAt),
                });
                return { ...result, _meta: { requestId } };
            }
            catch (error) {
                const actionName = typeof rawArgs.action === "string"
                    ? String(rawArgs.action)
                    : exposure.supportedActions.length === 1
                        ? exposure.supportedActions[0]
                        : undefined;
                await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: failed.`);
                log.error("Tool request failed", {
                    requestId,
                    tool: exposure.toolName,
                    durationMs: Math.round(performance.now() - startedAt),
                    error: error instanceof Error ? error.message : String(error),
                });
                return { ...buildToolErrorResult(exposure.toolName, actionName, error), _meta: { requestId } };
            }
        }, progressExtra?.signal);
    };
}
export function captureDomainToolHandlers(definition) {
    for (const exposure of definition.exposures) {
        captureToolHandler(exposure.toolName, createExposureHandler(definition, exposure));
    }
}
export function registerSelectedDomainTools(server, definition, exposures) {
    for (const exposure of exposures) {
        server.registerTool(exposure.toolName, {
            title: exposure.displayName,
            description: exposure.description,
            inputSchema: {
                ...exposure.inputShape,
                approvalToken: z.string().min(1).optional(),
                idempotencyKey: z.string().min(1).max(128).optional(),
            },
            outputSchema: buildExposureOutputSchema(definition, exposure),
            annotations: buildExposureAnnotations(definition, exposure),
        }, createExposureHandler(definition, exposure));
    }
}
export function buildDomainToolCatalogEntries(definition) {
    return definition.exposures.map((exposure) => {
        const supportedActionDefinitions = exposure.supportedActions
            .map((actionName) => definition.actions[actionName])
            .filter((action) => Boolean(action));
        return {
            toolName: exposure.toolName,
            displayName: exposure.displayName,
            description: exposure.description,
            domain: definition.domain,
            capabilities: exposure.capabilities ?? uniqueCapabilities(supportedActionDefinitions.flatMap((action) => action.capabilities)),
            operations: exposure.operations ?? (exposure.supportedActions.length > 1
                ? exposure.supportedActions
                : undefined),
            pluginMethods: exposure.pluginMethods ?? uniqueStrings(supportedActionDefinitions.flatMap((action) => action.pluginMethods ?? [])),
            requiresActiveDrawing: exposure.requiresActiveDrawing
                ?? supportedActionDefinitions.some((action) => action.requiresActiveDrawing),
            safeForRetry: exposure.safeForRetry
                ?? supportedActionDefinitions.every((action) => action.safeForRetry),
            status: exposure.status ?? "implemented",
        };
    });
}
