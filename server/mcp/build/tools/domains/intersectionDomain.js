import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
const GenericResponseSchema = z.object({}).passthrough();
const IntersectionListArgs = z.object({ action: z.literal("list"), siteName: z.string().optional() });
const IntersectionGetArgs = z.object({ action: z.literal("get"), name: z.string(), includeCorridorInfo: z.boolean().optional(), includeCurbReturns: z.boolean().optional() });
export const INTERSECTION_DOMAIN_DEFINITION = {
    domain: "intersection",
    actions: {
        list: { action: "list", inputSchema: IntersectionListArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listIntersections"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listIntersections", { siteName: args.siteName ?? null })) },
        get: { action: "get", inputSchema: IntersectionGetArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getIntersection"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getIntersection", { name: args.name, includeCorridorInfo: args.includeCorridorInfo ?? false, includeCurbReturns: args.includeCurbReturns ?? false })) },
    },
    exposures: [
        { toolName: "civil3d_intersection", displayName: "Civil 3D Intersection", description: "交叉口聚合工具：查询/读取交叉口", inputShape: { action: z.enum(["list", "get"]), siteName: z.string().optional(), name: z.string().optional(), includeCorridorInfo: z.boolean().optional(), includeCurbReturns: z.boolean().optional() }, supportedActions: ["list", "get"], resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }) },
        { toolName: "civil3d_intersection_list", displayName: "Civil 3D Intersection List", description: "查询交叉口列表", inputShape: { siteName: z.string().optional() }, supportedActions: ["list"], resolveAction: (rawArgs) => ({ action: "list", args: { action: "list", ...rawArgs } }) },
        { toolName: "civil3d_intersection_get", displayName: "Civil 3D Intersection Get", description: "读取交叉口信息", inputShape: { name: z.string(), includeCorridorInfo: z.boolean().optional(), includeCurbReturns: z.boolean().optional() }, supportedActions: ["get"], resolveAction: (rawArgs) => ({ action: "get", args: { action: "get", ...rawArgs } }) },
    ],
};
