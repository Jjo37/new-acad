import { withApplicationConnection } from "../utils/ConnectionManager.js";

export interface ProjectContext {
  drawingInfo: unknown | null;
  objectTypes: string[];
  selectedObjects: unknown[];
}

export async function getProjectContext(selectedObjectLimit = 25): Promise<ProjectContext> {
  const context = await withApplicationConnection(async (appClient) =>
    appClient.sendCommand("getProjectContext", { selectedObjectLimit }),
  ) as Partial<ProjectContext>;

  return {
    drawingInfo: context.drawingInfo ?? null,
    objectTypes: Array.isArray(context.objectTypes) ? context.objectTypes : [],
    selectedObjects: Array.isArray(context.selectedObjects) ? context.selectedObjects : [],
  };
}
