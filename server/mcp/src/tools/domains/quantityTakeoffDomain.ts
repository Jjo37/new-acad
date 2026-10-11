import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const GenericResponseSchema = z.object({}).passthrough();
const RegionSchema = z.array(z.object({ x: z.number(), y: z.number() }));

const QtySurfaceVolumeArgs = z.object({ action: z.literal("surface_volume"), baseSurface: z.string(), comparisonSurface: z.string(), corridorName: z.string().optional(), region: RegionSchema.optional() }).superRefine((v, ctx) => {
  if (v.region != null && v.region.length < 3) ctx.addIssue({ code: z.ZodIssueCode.custom, message: "region polygon must contain at least 3 points", path: ["region"] });
});
const QtyPipeNetworkLengthsArgs = z.object({ action: z.literal("pipe_network_lengths"), name: z.string(), groupBySize: z.boolean().optional(), groupByMaterial: z.boolean().optional() });
const QtyPressureNetworkLengthsArgs = z.object({ action: z.literal("pressure_network_lengths"), name: z.string(), groupBySize: z.boolean().optional(), groupByMaterial: z.boolean().optional() });
const QtyParcelAreasArgs = z.object({ action: z.literal("parcel_areas"), siteName: z.string().optional(), parcelNames: z.array(z.string()).optional() });
const QtyAlignmentLengthsArgs = z.object({ action: z.literal("alignment_lengths"), names: z.array(z.string()).optional(), startStation: z.number().optional(), endStation: z.number().optional() });
const QtyPointCountByGroupArgs = z.object({ action: z.literal("point_count_by_group"), groupNames: z.array(z.string()).optional() });
const QtyExportToCsvArgs = z.object({ action: z.literal("export_to_csv"), outputPath: z.string(), overwrite: z.boolean().optional(), includeCorridorVolumes: z.boolean().optional(), includeSurfaceVolumes: z.boolean().optional(), includePipeNetworks: z.boolean().optional(), includePressureNetworks: z.boolean().optional(), includeParcelAreas: z.boolean().optional(), includeAlignmentLengths: z.boolean().optional(), corridorName: z.string().optional(), baseSurface: z.string().optional(), comparisonSurface: z.string().optional() });
const QtyEarthworkSummaryArgs = z.object({ action: z.literal("earthwork_summary"), baseSurface: z.string(), designSurface: z.string(), alignmentName: z.string().optional(), startStation: z.number().optional(), endStation: z.number().optional(), stationInterval: z.number().positive().optional() });

const canonicalQuantityTakeoffInputShape = {
  action: z.enum(["surface_volume", "pipe_network_lengths", "pressure_network_lengths", "parcel_areas", "alignment_lengths", "point_count_by_group", "export_to_csv", "earthwork_summary"]),
  name: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  baseSurface: z.string().optional(),
  comparisonSurface: z.string().optional(),
  corridorName: z.string().optional(),
  region: RegionSchema.optional(),
  groupBySize: z.boolean().optional(),
  groupByMaterial: z.boolean().optional(),
  siteName: z.string().optional(),
  parcelNames: z.array(z.string()).optional(),
  names: z.array(z.string()).optional(),
  groupNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeCorridorVolumes: z.boolean().optional(),
  includeSurfaceVolumes: z.boolean().optional(),
  includePipeNetworks: z.boolean().optional(),
  includePressureNetworks: z.boolean().optional(),
  includeParcelAreas: z.boolean().optional(),
  includeAlignmentLengths: z.boolean().optional(),
  designSurface: z.string().optional(),
  stationInterval: z.number().optional(),
};

export const QUANTITY_TAKEOFF_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "quantity_takeoff",
  actions: {
    surface_volume: { action: "surface_volume", inputSchema: QtySurfaceVolumeArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtySurfaceVolume"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtySurfaceVolume", { baseSurface: args.baseSurface, comparisonSurface: args.comparisonSurface, corridorName: args.corridorName ?? null, region: args.region ?? null })) },
    pipe_network_lengths: { action: "pipe_network_lengths", inputSchema: QtyPipeNetworkLengthsArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyPipeNetworkLengths"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyPipeNetworkLengths", { name: args.name, groupBySize: args.groupBySize ?? false, groupByMaterial: args.groupByMaterial ?? false })) },
    pressure_network_lengths: { action: "pressure_network_lengths", inputSchema: QtyPressureNetworkLengthsArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyPressureNetworkLengths"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyPressureNetworkLengths", { name: args.name, groupBySize: args.groupBySize ?? false, groupByMaterial: args.groupByMaterial ?? false })) },
    parcel_areas: { action: "parcel_areas", inputSchema: QtyParcelAreasArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyParcelAreas"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyParcelAreas", { siteName: args.siteName ?? null, parcelNames: args.parcelNames ?? null })) },
    alignment_lengths: { action: "alignment_lengths", inputSchema: QtyAlignmentLengthsArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyAlignmentLengths"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyAlignmentLengths", { names: args.names ?? null, startStation: args.startStation ?? null, endStation: args.endStation ?? null })) },
    point_count_by_group: { action: "point_count_by_group", inputSchema: QtyPointCountByGroupArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyPointCountByGroup"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyPointCountByGroup", { groupNames: args.groupNames ?? null })) },
    export_to_csv: { action: "export_to_csv", inputSchema: QtyExportToCsvArgs, responseSchema: GenericResponseSchema, capabilities: ["export", "generate"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["qtyExportToCsv"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyExportToCsv", { outputPath: args.outputPath, overwrite: args.overwrite ?? false, includeCorridorVolumes: args.includeCorridorVolumes ?? false, includeSurfaceVolumes: args.includeSurfaceVolumes ?? false, includePipeNetworks: args.includePipeNetworks ?? false, includePressureNetworks: args.includePressureNetworks ?? false, includeParcelAreas: args.includeParcelAreas ?? false, includeAlignmentLengths: args.includeAlignmentLengths ?? false, corridorName: args.corridorName ?? null, baseSurface: args.baseSurface ?? null, comparisonSurface: args.comparisonSurface ?? null })) },
    earthwork_summary: { action: "earthwork_summary", inputSchema: QtyEarthworkSummaryArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "analyze", "generate"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["qtyEarthworkSummary"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("qtyEarthworkSummary", { baseSurface: args.baseSurface, designSurface: args.designSurface, alignmentName: args.alignmentName ?? null, startStation: args.startStation ?? null, endStation: args.endStation ?? null, stationInterval: args.stationInterval ?? 50 })) },
  },
  exposures: [
    { toolName: "civil3d_quantity_takeoff", displayName: "Civil 3D Quantity Takeoff", description: "工程量聚合工具：体积/长度/面积/材料清单", inputShape: canonicalQuantityTakeoffInputShape, supportedActions: ["surface_volume", "pipe_network_lengths", "pressure_network_lengths", "parcel_areas", "alignment_lengths", "point_count_by_group", "export_to_csv", "earthwork_summary"], resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }) },
    { toolName: "civil3d_qty_surface_volume", displayName: "Civil 3D Quantity Surface Volume", description: "计算曲面体积（工程量）", inputShape: { baseSurface: z.string(), comparisonSurface: z.string(), corridorName: z.string().optional(), region: RegionSchema.optional() }, supportedActions: ["surface_volume"], resolveAction: (rawArgs) => ({ action: "surface_volume", args: { action: "surface_volume", ...rawArgs } }) },
    { toolName: "civil3d_qty_pipe_network_lengths", displayName: "Civil 3D Quantity Pipe Network Lengths", description: "统计管网长度", inputShape: { name: z.string(), groupBySize: z.boolean().optional(), groupByMaterial: z.boolean().optional() }, supportedActions: ["pipe_network_lengths"], resolveAction: (rawArgs) => ({ action: "pipe_network_lengths", args: { action: "pipe_network_lengths", ...rawArgs } }) },
    { toolName: "civil3d_qty_pressure_network_lengths", displayName: "Civil 3D Quantity Pressure Network Lengths", description: "统计压力管网长度", inputShape: { name: z.string(), groupBySize: z.boolean().optional(), groupByMaterial: z.boolean().optional() }, supportedActions: ["pressure_network_lengths"], resolveAction: (rawArgs) => ({ action: "pressure_network_lengths", args: { action: "pressure_network_lengths", ...rawArgs } }) },
    { toolName: "civil3d_qty_parcel_areas", displayName: "Civil 3D Quantity Parcel Areas", description: "计算地块面积", inputShape: { siteName: z.string().optional(), parcelNames: z.array(z.string()).optional() }, supportedActions: ["parcel_areas"], resolveAction: (rawArgs) => ({ action: "parcel_areas", args: { action: "parcel_areas", ...rawArgs } }) },
    { toolName: "civil3d_qty_alignment_lengths", displayName: "Civil 3D Quantity Alignment Lengths", description: "计算路线长度（工程量）", inputShape: { names: z.array(z.string()).optional(), startStation: z.number().optional(), endStation: z.number().optional() }, supportedActions: ["alignment_lengths"], resolveAction: (rawArgs) => ({ action: "alignment_lengths", args: { action: "alignment_lengths", ...rawArgs } }) },
    { toolName: "civil3d_qty_point_count_by_group", displayName: "Civil 3D Quantity Point Count By Group", description: "统计点组内点数", inputShape: { groupNames: z.array(z.string()).optional() }, supportedActions: ["point_count_by_group"], resolveAction: (rawArgs) => ({ action: "point_count_by_group", args: { action: "point_count_by_group", ...rawArgs } }) },
    { toolName: "civil3d_qty_export_to_csv", displayName: "Civil 3D Quantity Export To CSV", description: "导出工程量 CSV {outputPath}", inputShape: { outputPath: z.string(), overwrite: z.boolean().optional(), includeCorridorVolumes: z.boolean().optional(), includeSurfaceVolumes: z.boolean().optional(), includePipeNetworks: z.boolean().optional(), includePressureNetworks: z.boolean().optional(), includeParcelAreas: z.boolean().optional(), includeAlignmentLengths: z.boolean().optional(), corridorName: z.string().optional(), baseSurface: z.string().optional(), comparisonSurface: z.string().optional() }, supportedActions: ["export_to_csv"], resolveAction: (rawArgs) => ({ action: "export_to_csv", args: { action: "export_to_csv", ...rawArgs } }) },
    { toolName: "civil3d_qty_earthwork_summary", displayName: "Civil 3D Quantity Earthwork Summary", description: "土方量汇总", inputShape: { baseSurface: z.string(), designSurface: z.string(), alignmentName: z.string().optional(), startStation: z.number().optional(), endStation: z.number().optional(), stationInterval: z.number().positive().optional() }, supportedActions: ["earthwork_summary"], resolveAction: (rawArgs) => ({ action: "earthwork_summary", args: { action: "earthwork_summary", ...rawArgs } }) },
  ],
};
