import { createHash, randomUUID } from "node:crypto";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
export class ApprovalRequiredError extends Error {
}
export class ApprovalValidationError extends Error {
}
const APPROVAL_TOKEN_TTL_MS = 5 * 60 * 1000;
const NO_ACTIVE_DRAWING_FINGERPRINT = "no-active-drawing";
const MUTATING_CAPABILITIES = new Set([
    "create",
    "edit",
    "delete",
    "manage",
    "import",
    "export",
    "execute", // 2026-08-14 P1-2: execute_command 等纳入审批
]);
const EXPLICIT_APPROVAL_ACTION = /(?:^|_)(?:delete|remove|import|export|save|new|undo|redo|overwrite|replace|publish|promote|sync|fix|remediate|execute|write|attach)(?:_|$)/i; // 2026-08-14 P1-2: execute/write/attach 纳入审批
function stableJson(value) {
    if (Array.isArray(value)) {
        return `[${value.map((item) => stableJson(item)).join(",")}]`;
    }
    if (value && typeof value === "object") {
        const object = value;
        return `{${Object.keys(object)
            .sort()
            .map((key) => `${JSON.stringify(key)}:${stableJson(object[key])}`)
            .join(",")}}`;
    }
    return JSON.stringify(value);
}
function hashParameters(parameters) {
    const normalized = { ...parameters };
    delete normalized.approvalToken;
    return createHash("sha256").update(stableJson(normalized)).digest("hex");
}
export function isApprovalRequired(target) {
    if (process.env.CIVIL3D_APPROVAL_MODE === "disabled") {
        return false;
    }
    return hasApprovalRisk(target);
}
export function hasApprovalRisk(target) {
    const mutatesState = target.capabilities.some((capability) => MUTATING_CAPABILITIES.has(capability));
    return (target.capabilities.some((capability) => ["delete", "import", "export"].includes(capability)) ||
        EXPLICIT_APPROVAL_ACTION.test(target.action) ||
        (!target.safeForRetry && mutatesState));
}
export async function getActiveDrawingFingerprint() {
    const drawingInfo = await withApplicationConnection((client) => client.sendCommand("getDrawingInfo", {}));
    return createHash("sha256").update(stableJson(drawingInfo)).digest("hex");
}
function isNoActiveDrawingError(error) {
    const code = error && typeof error === "object" && "code" in error
        ? String(error.code)
        : "";
    const message = error instanceof Error ? error.message : String(error);
    return code === "CIVIL3D.NO_DRAWING" || code === "CIVIL3D.NO_ACTIVE_DRAWING" ||
        /no active (?:civil 3d )?drawing|no active (?:civil 3d )?document/i.test(message);
}
export class ApprovalPolicyService {
    drawingFingerprint;
    now;
    grants = new Map();
    constructor(drawingFingerprint = getActiveDrawingFingerprint, now = () => Date.now()) {
        this.drawingFingerprint = drawingFingerprint;
        this.now = now;
    }
    async fingerprintFor(target) {
        try {
            return await this.drawingFingerprint();
        }
        catch (error) {
            if (target.requiresActiveDrawing === false && isNoActiveDrawingError(error)) {
                return NO_ACTIVE_DRAWING_FINGERPRINT;
            }
            throw error;
        }
    }
    async requestApproval(target, parameters, ttlMs = APPROVAL_TOKEN_TTL_MS) {
        if (!isApprovalRequired(target)) {
            throw new ApprovalValidationError(`'${target.toolName}' action '${target.action}' does not require approval. Execute it directly.`);
        }
        const drawingFingerprint = await this.fingerprintFor(target);
        const expiresAt = this.now() + ttlMs;
        const approvalToken = randomUUID();
        this.grants.set(approvalToken, {
            toolName: target.toolName,
            action: target.action,
            parametersHash: hashParameters(parameters),
            drawingFingerprint,
            expiresAt,
        });
        return {
            approvalToken,
            expiresAt: new Date(expiresAt).toISOString(),
            drawingFingerprint,
        };
    }
    async enforce(target, parameters) {
        if (!isApprovalRequired(target)) {
            return;
        }
        const approvalToken = typeof parameters.approvalToken === "string" ? parameters.approvalToken : undefined;
        if (!approvalToken) {
            throw new ApprovalRequiredError(`Approval required for '${target.toolName}' action '${target.action}'. ` +
                "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken.");
        }
        const grant = this.grants.get(approvalToken);
        if (!grant) {
            throw new ApprovalValidationError("Approval token is missing, expired, or has already been used.");
        }
        this.grants.delete(approvalToken);
        if (grant.expiresAt <= this.now()) {
            throw new ApprovalValidationError("Approval token has expired. Request a new approval.");
        }
        if (grant.toolName !== target.toolName ||
            grant.action !== target.action ||
            grant.parametersHash !== hashParameters(parameters)) {
            throw new ApprovalValidationError("Approval token does not match this tool action and its parameters.");
        }
        const activeDrawingFingerprint = await this.fingerprintFor(target);
        if (grant.drawingFingerprint !== activeDrawingFingerprint) {
            throw new ApprovalValidationError("The active drawing changed after approval. Request approval for the current drawing.");
        }
    }
}
export const approvalPolicy = new ApprovalPolicyService();
