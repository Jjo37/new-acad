import { withApplicationConnection } from "../utils/ConnectionManager.js";
export async function getProjectContext(selectedObjectLimit = 25) {
    const context = await withApplicationConnection(async (appClient) => appClient.sendCommand("getProjectContext", { selectedObjectLimit }));
    return {
        drawingInfo: context.drawingInfo ?? null,
        objectTypes: Array.isArray(context.objectTypes) ? context.objectTypes : [],
        selectedObjects: Array.isArray(context.selectedObjects) ? context.selectedObjects : [],
    };
}
