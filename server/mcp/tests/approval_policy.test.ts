import { describe, expect, it } from "vitest";
import {
  ApprovalPolicyService,
  ApprovalRequiredError,
  ApprovalValidationError,
  isApprovalRequired,
} from "../src/tools/approvalPolicy.js";

const saveTarget = {
  toolName: "civil3d_drawing",
  action: "save",
  capabilities: ["edit", "manage"] as const,
  safeForRetry: false,
};

describe("approval policy", () => {
  it("requires approval for non-retryable drawing mutations but not safe queries", () => {
    expect(isApprovalRequired(saveTarget)).toBe(true);
    expect(isApprovalRequired({
      toolName: "civil3d_get_drawing_info",
      action: "info",
      capabilities: ["query", "inspect"],
      safeForRetry: true,
    })).toBe(false);
  });

  it("binds a single-use token to exact parameters and drawing identity", async () => {
    let drawing = "drawing-a";
    let now = 10_000;
    const policy = new ApprovalPolicyService(async () => drawing, () => now);
    const parameters = { action: "save", saveAs: "C:/work/design.dwg" };
    const receipt = await policy.requestApproval(saveTarget, parameters, 30_000);

    await expect(policy.enforce(saveTarget, { ...parameters, approvalToken: receipt.approvalToken })).resolves.toBeUndefined();
    await expect(policy.enforce(saveTarget, { ...parameters, approvalToken: receipt.approvalToken }))
      .rejects.toBeInstanceOf(ApprovalValidationError);

    const changedParameters = await policy.requestApproval(saveTarget, parameters, 30_000);
    await expect(policy.enforce(saveTarget, {
      action: "save",
      saveAs: "C:/work/other.dwg",
      approvalToken: changedParameters.approvalToken,
    })).rejects.toBeInstanceOf(ApprovalValidationError);

    const changedDrawing = await policy.requestApproval(saveTarget, parameters, 30_000);
    drawing = "drawing-b";
    await expect(policy.enforce(saveTarget, { ...parameters, approvalToken: changedDrawing.approvalToken }))
      .rejects.toBeInstanceOf(ApprovalValidationError);

    now += 31_000;
    const expired = await policy.requestApproval(saveTarget, parameters, 30_000);
    now += 31_000;
    await expect(policy.enforce(saveTarget, { ...parameters, approvalToken: expired.approvalToken }))
      .rejects.toBeInstanceOf(ApprovalValidationError);
  });

  it("rejects protected execution that has no approval token", async () => {
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    await expect(policy.enforce(saveTarget, { action: "save" })).rejects.toBeInstanceOf(ApprovalRequiredError);
  });

  it("permits approval for drawing-independent creation when no drawing is open", async () => {
    const noDrawing = Object.assign(new Error("No active drawing is open in Civil 3D."), {
      code: "CIVIL3D.NO_DRAWING",
    });
    const policy = new ApprovalPolicyService(async () => { throw noDrawing; });
    const target = {
      toolName: "civil3d_drawing",
      action: "new",
      capabilities: ["create", "manage"] as const,
      safeForRetry: false,
      requiresActiveDrawing: false,
    };
    const parameters = { action: "new", templatePath: "C:/templates/civil.dwt" };
    const receipt = await policy.requestApproval(target, parameters);

    expect(receipt.drawingFingerprint).toBe("no-active-drawing");
    await expect(policy.enforce(target, { ...parameters, approvalToken: receipt.approvalToken }))
      .resolves.toBeUndefined();
  });
});
