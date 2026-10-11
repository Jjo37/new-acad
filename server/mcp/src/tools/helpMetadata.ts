import type { ToolCatalogEntry } from "./toolMetadata.js";

export const HELP_TOOL_CATALOG_ENTRY: ToolCatalogEntry = {
  toolName: "civil3d_help",
  displayName: "Civil 3D Offline Help",
  description: "Search installed Autodesk Civil 3D offline help and retrieve cited topics with renderable images.",
  domain: "help",
  capabilities: ["query"],
  operations: ["search", "search_videos", "get_topic", "status", "reindex"],
  requiresActiveDrawing: false,
  safeForRetry: true,
  status: "implemented",
};
