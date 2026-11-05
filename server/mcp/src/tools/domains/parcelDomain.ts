import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const GenericParcelResponseSchema = z.object({}).passthrough();

const ParcelSiteSchema = z.object({
  name: z.string().optional(),
  handle: z.string().optional(),
  parcelCount: z.number().optional(),
}).passthrough();

const ParcelSitesResponseSchema = z.object({
  sites: z.array(ParcelSiteSchema),
});

const ParcelSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  number: z.number(),
  area: z.number(),
  perimeter: z.number(),
  style: z.string(),
});

const ParcelListResponseSchema = z.object({
  siteName: z.string(),
  parcels: z.array(ParcelSummarySchema),
  units: z.object({
    area: z.string(),
    length: z.string(),
  }),
});

const ParcelDetailResponseSchema = z.object({
  siteName: z.string().optional(),
  name: z.string().optional(),
  handle: z.string().optional(),
  number: z.number().optional(),
  area: z.number().optional(),
  perimeter: z.number().optional(),
  style: z.string().optional(),
}).passthrough();

const canonicalParcelInputShape = {
  action: z.enum([
    "list_sites",
    "list",
    "get",
    "create",
    "edit",
    "lot_line_adjust",
    "report",
  ]),
  siteName: z.string().optional(),
  parcelName: z.string().optional(),
  name: z.string().optional(),
  sourceHandle: z.string().optional(),
  points: z.array(z.tuple([z.number(), z.number()])).optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
  newName: z.string().optional(),
  description: z.string().optional(),
  targetAreaSqFt: z.number().optional(),
  lotLineHandle: z.string().optional(),
  tolerance: z.number().optional(),
  parcelNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeCoordinates: z.boolean().optional(),
  units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
};

const ParcelListSitesArgsSchema = z.object({
  action: z.literal("list_sites"),
});

const ParcelListArgsSchema = z.object({
  action: z.literal("list"),
  siteName: z.string(),
});

const ParcelGetArgsSchema = z.object({
  action: z.literal("get"),
  siteName: z.string(),
  parcelName: z.string(),
});

const ParcelCreateArgsSchema = z.object({
  action: z.literal("create"),
  siteName: z.string(),
  name: z.string().optional(),
  sourceHandle: z.string().optional(),
  points: z.array(z.tuple([z.number(), z.number()])).min(3).optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
});

const ParcelEditArgsSchema = z.object({
  action: z.literal("edit"),
  siteName: z.string(),
  parcelName: z.string(),
  newName: z.string().optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
  description: z.string().optional(),
});

const ParcelLotLineAdjustArgsSchema = z.object({
  action: z.literal("lot_line_adjust"),
  siteName: z.string(),
  parcelName: z.string(),
  targetAreaSqFt: z.number().positive(),
  lotLineHandle: z.string().optional(),
  tolerance: z.number().positive().optional(),
});

const ParcelReportArgsSchema = z.object({
  action: z.literal("report"),
  siteName: z.string(),
  parcelNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeCoordinates: z.boolean().optional(),
  units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
});

export const PARCEL_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "parcel",
  actions: {
    list_sites: {
      action: "list_sites",
      inputSchema: ParcelListSitesArgsSchema,
      responseSchema: ParcelSitesResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listParcelSites"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listParcelSites", {}),
      ),
    },
    list: {
      action: "list",
      inputSchema: ParcelListArgsSchema,
      responseSchema: ParcelListResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listParcels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listParcels", { siteName: args.siteName }),
      ),
    },
    get: {
      action: "get",
      inputSchema: ParcelGetArgsSchema,
      responseSchema: ParcelDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getParcel", {
          siteName: args.siteName,
          parcelName: args.parcelName,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: ParcelCreateArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createParcel", {
          siteName: args.siteName,
          name: args.name ?? null,
          sourceHandle: args.sourceHandle ?? null,
          points: args.points ?? null,
          style: args.style ?? null,
          areaLabelStyle: args.areaLabelStyle ?? null,
        }),
      ),
    },
    edit: {
      action: "edit",
      inputSchema: ParcelEditArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("editParcel", {
          siteName: args.siteName,
          parcelName: args.parcelName,
          newName: args.newName ?? null,
          style: args.style ?? null,
          areaLabelStyle: args.areaLabelStyle ?? null,
          description: args.description ?? null,
        }),
      ),
    },
    lot_line_adjust: {
      action: "lot_line_adjust",
      inputSchema: ParcelLotLineAdjustArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["edit", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["adjustParcelLotLine"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("adjustParcelLotLine", {
          siteName: args.siteName,
          parcelName: args.parcelName,
          targetAreaSqFt: args.targetAreaSqFt,
          lotLineHandle: args.lotLineHandle ?? null,
          tolerance: args.tolerance ?? 1.0,
        }),
      ),
    },
    report: {
      action: "report",
      inputSchema: ParcelReportArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["reportParcels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("reportParcels", {
          siteName: args.siteName,
          parcelNames: args.parcelNames ?? null,
          outputPath: args.outputPath ?? null,
          overwrite: args.overwrite ?? false,
          includeCoordinates: args.includeCoordinates ?? false,
          units: args.units ?? "sqft",
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_parcel",
      displayName: "Civil 3D Parcel",
      description: "地块聚合工具：查询/读取/创建/编辑/报告",
      inputShape: canonicalParcelInputShape,
      supportedActions: ["list_sites", "list", "get", "create", "edit", "lot_line_adjust", "report"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_parcel_create",
      displayName: "Civil 3D Parcel Create",
      description: "创建地块 {siteName?, ...}",
      inputShape: {
        siteName: z.string(),
        name: z.string().optional(),
        sourceHandle: z.string().optional(),
        points: z.array(z.tuple([z.number(), z.number()])).min(3).optional(),
        style: z.string().optional(),
        areaLabelStyle: z.string().optional(),
      },
      supportedActions: ["create"],
      resolveAction: (rawArgs) => ({
        action: "create",
        args: {
          action: "create",
          siteName: rawArgs.siteName,
          name: rawArgs.name,
          sourceHandle: rawArgs.sourceHandle,
          points: rawArgs.points,
          style: rawArgs.style,
          areaLabelStyle: rawArgs.areaLabelStyle,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_edit",
      displayName: "Civil 3D Parcel Edit",
      description: "编辑地块",
      inputShape: {
        siteName: z.string(),
        parcelName: z.string(),
        newName: z.string().optional(),
        style: z.string().optional(),
        areaLabelStyle: z.string().optional(),
        description: z.string().optional(),
      },
      supportedActions: ["edit"],
      resolveAction: (rawArgs) => ({
        action: "edit",
        args: {
          action: "edit",
          siteName: rawArgs.siteName,
          parcelName: rawArgs.parcelName,
          newName: rawArgs.newName,
          style: rawArgs.style,
          areaLabelStyle: rawArgs.areaLabelStyle,
          description: rawArgs.description,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_lot_line_adjust",
      displayName: "Civil 3D Parcel Lot Line Adjust",
      description: "调整地块界线（lot line）",
      inputShape: {
        siteName: z.string(),
        parcelName: z.string(),
        targetAreaSqFt: z.number().positive(),
        lotLineHandle: z.string().optional(),
        tolerance: z.number().positive().optional(),
      },
      supportedActions: ["lot_line_adjust"],
      resolveAction: (rawArgs) => ({
        action: "lot_line_adjust",
        args: {
          action: "lot_line_adjust",
          siteName: rawArgs.siteName,
          parcelName: rawArgs.parcelName,
          targetAreaSqFt: rawArgs.targetAreaSqFt,
          lotLineHandle: rawArgs.lotLineHandle,
          tolerance: rawArgs.tolerance,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_report",
      displayName: "Civil 3D Parcel Report",
      description: "生成地块报告",
      inputShape: {
        siteName: z.string(),
        parcelNames: z.array(z.string()).optional(),
        outputPath: z.string().optional(),
        overwrite: z.boolean().optional(),
        includeCoordinates: z.boolean().optional(),
        units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
      },
      supportedActions: ["report"],
      resolveAction: (rawArgs) => ({
        action: "report",
        args: {
          action: "report",
          siteName: rawArgs.siteName,
          parcelNames: rawArgs.parcelNames,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          includeCoordinates: rawArgs.includeCoordinates,
          units: rawArgs.units,
        },
      }),
    },
  ],
};
