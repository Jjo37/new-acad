import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerApprovalTool } from "./approvalTool.js";
import { registerManifestTools } from "./toolManifest.js";
import { registerMcpResources } from "./mcpResources.js";
import { registerHelpResources, registerHelpTool } from "./helpTool.js";

export async function registerTools(server: McpServer) {
  // Manifest-driven domains: alignment, surface, profile, corridor, section, pipe, assembly, point, grading, parcel, survey, plan_production, project, standards, qc, hydrology, quantity_takeoff, sight_distance, detention, slope_analysis, cost_estimation, geometry, drawing, coordinate_system, job, plugin, docs.
  // Add new migrated domains to toolManifest.ts, not here.
  registerManifestTools(server);
  registerApprovalTool(server);
  registerHelpTool(server);
  registerMcpResources(server);
  registerHelpResources(server);
}
