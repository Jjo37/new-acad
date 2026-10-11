import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const HealthResponseSchema = z.object({
  connected: z.boolean(),
  civil3dVersion: z.string().optional(),
  pluginVersion: z.string().optional(),
  drawingLoaded: z.boolean(),
  operationInProgress: z.boolean(),
  currentOperation: z.string().nullable(),
  queueDepth: z.number(),
  queueCapacity: z.number(),
  currentOperationStartedAtUnixMs: z.number().nullable(),
  currentRequestId: z.string().nullable(),
  currentOperationDurationMs: z.number().nullable(),
  memoryUsageMb: z.number(),
  logFilePath: z.string(),
  fileLoggingHealthy: z.boolean(),
  fileLoggingError: z.string().nullable(),
  jobs: z.object({
    total: z.number(),
    running: z.number(),
    completed: z.number(),
    failed: z.number(),
    cancelled: z.number(),
    capacity: z.number(),
    terminalRetentionMinutes: z.number(),
  }),
});

export const PLUGIN_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plugin",
  actions: {
    health: {
      action: "health",
      inputSchema: z.object({ action: z.literal("health") }),
      responseSchema: HealthResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["getCivil3DHealth"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCivil3DHealth", {}),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_health",
      displayName: "Civil 3D Health",
      description: "检查插件/图纸状态（在线、卡顿、文档信息）",
      inputShape: {},
      supportedActions: ["health"],
      resolveAction: () => ({ action: "health", args: { action: "health" } }),
    },
  ],
};
