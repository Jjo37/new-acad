import { GENERATED_TOOL_CATALOG_ENTRIES } from "./toolManifest.js";
import { HELP_TOOL_CATALOG_ENTRY } from "./helpMetadata.js";
import type { ToolCatalogEntry } from "./toolMetadata.js";

export const TOOL_CATALOG: ToolCatalogEntry[] = [
  ...GENERATED_TOOL_CATALOG_ENTRIES,
  HELP_TOOL_CATALOG_ENTRY,
];

export function listToolCatalog() {
  return TOOL_CATALOG;
}

export function listDomains() {
  return [...new Set(TOOL_CATALOG.map((entry) => entry.domain))].sort();
}
