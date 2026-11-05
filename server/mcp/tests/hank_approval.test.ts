import { describe, expect, it } from "vitest";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { MIGRATED_DOMAIN_DEFINITIONS } from "../src/tools/toolManifest.js";
import type { DomainToolDefinition } from "../src/tools/domainRuntime.js";
import type { ToolCapability } from "../src/tools/toolMetadata.js";

// 合法能力枚举（与 toolMetadata.ts 的 ToolCapability 对齐）。
// 回归防护：曾出现 hankDomain 用非法值 "modify"，被 @ts-nocheck 掩盖，
// 导致改制图工具被判为“非变更”从而绕过审批门禁。
const LEGAL_CAPABILITIES: ReadonlySet<string> = new Set<ToolCapability>([
  "query", "create", "edit", "delete", "analyze", "manage",
  "generate", "register", "inspect", "export", "import", "execute",
]);

const hank = MIGRATED_DOMAIN_DEFINITIONS.find(
  (definition) => (definition as DomainToolDefinition).domain === "hank",
) as DomainToolDefinition | undefined;

function risk(actionName: string): boolean {
  const definition = hank as DomainToolDefinition;
  const action = definition.actions[actionName];
  return hasApprovalRisk({
    toolName: `hank_${actionName}`,
    action: actionName,
    capabilities: action.capabilities,
    safeForRetry: action.safeForRetry,
  });
}

describe("hank domain approval coverage", () => {
  it("registers the hank domain", () => {
    expect(hank).toBeDefined();
  });

  it("uses only legal ToolCapability values across all migrated domains", () => {
    const offenders: string[] = [];
    for (const definition of MIGRATED_DOMAIN_DEFINITIONS as DomainToolDefinition[]) {
      for (const [actionName, action] of Object.entries(definition.actions)) {
        for (const capability of action.capabilities) {
          if (!LEGAL_CAPABILITIES.has(capability)) {
            offenders.push(`${definition.domain}.${actionName}: ${String(capability)}`);
          }
        }
      }
    }
    expect(offenders).toEqual([]);
  });

  it("requires approval for hank drawing-mutating tools", () => {
    const mutating = [
      "move_entity", "rotate_entity", "scale_entity", "copy_entity", "mirror_entity",
      "offset_entity", "break_entity", "trim_entity", "extend_entity", "stretch_entity",
      "array_entity", "align_entity", "explode_entity", "overkill_entities",
      "layer_off", "layer_freeze", "layer_lock", "layer_isolate", "layer_match",
      "boolean_union", "boolean_subtract", "move_3d", "convert_to_solid", "assign_material",
      "set_entity_color", "edit_text", "delete_entity", "execute_command",
    ];
    for (const actionName of mutating) {
      expect(risk(actionName), `${actionName} should require approval`).toBe(true);
    }
  });

  it("does not require approval for hank read-only tools", () => {
    const readOnly = ["get_selection", "get_entity_info", "get_layers", "select_by_criteria", "measure_dist", "measure_area"];
    for (const actionName of readOnly) {
      expect(risk(actionName), `${actionName} should stay approval-free`).toBe(false);
    }
  });
});
