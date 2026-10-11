import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericSectionResponseSchema = z.object({}).passthrough();

const SampleLineGroupSchema = z.object({
  name: z.string(),
  handle: z.string(),
  sampleLineCount: z.number(),
  stations: z.array(z.number()),
});

const SectionListSampleLinesResponseSchema = z.object({
  sampleLineGroups: z.array(SampleLineGroupSchema),
});

const SectionDataResponseSchema = z.object({
  station: z.number(),
  surfaces: z.array(z.object({
    surfaceName: z.string(),
    offsets: z.array(z.number()),
    elevations: z.array(z.number()),
  })),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
  }),
});

// ─── Per-action input schemas ─────────────────────────────────────────────────

const SectionListSampleLinesArgsSchema = z.object({
  action: z.literal("list_sample_lines"),
  alignmentName: z.string(),
});

const SectionGetDataArgsSchema = z.object({
  action: z.literal("get_section_data"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  station: z.number(),
});

const SectionCreateSampleLinesArgsSchema = z.object({
  action: z.literal("create_sample_lines"),
  alignmentName: z.string(),
  groupName: z.string(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number(),
  rightWidth: z.number(),
  surfaces: z.array(z.string()),
}).superRefine((value, ctx) => {
  if (!value.stations && value.interval === undefined) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Either stations or interval is required.",
      path: ["stations"],
    });
  }
});

const SectionViewCreateArgsSchema = z.object({
  action: z.literal("view_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.tuple([z.number(), z.number()]),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().nonnegative().optional(),
  rightOffset: z.number().nonnegative().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

const SectionViewListArgsSchema = z.object({
  action: z.literal("view_list"),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
});

const SectionViewUpdateStyleArgsSchema = z.object({
  action: z.literal("view_update_style"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  applyToAll: z.boolean().optional(),
});

const SectionViewGroupCreateArgsSchema = z.object({
  action: z.literal("view_group_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.tuple([z.number(), z.number()]),
  style: z.string().optional(),
  plotStyle: z.string().optional(),
});

const SectionViewExportArgsSchema = z.object({
  action: z.literal("view_export"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

// ─── Canonical input shape ────────────────────────────────────────────────────

const canonicalSectionInputShape = {
  action: z.enum([
    "list_sample_lines",
    "get_section_data",
    "create_sample_lines",
    "view_create",
    "view_list",
    "view_update_style",
    "view_group_create",
    "view_export",
  ]),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
  station: z.number().optional(),
  groupName: z.string().optional(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number().optional(),
  rightWidth: z.number().optional(),
  surfaces: z.array(z.string()).optional(),
  insertionPoint: z.tuple([z.number(), z.number()]).optional(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().nonnegative().optional(),
  rightOffset: z.number().nonnegative().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
  applyToAll: z.boolean().optional(),
  plotStyle: z.string().optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
};

// ─── Domain definition ────────────────────────────────────────────────────────

export const SECTION_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "section",
  actions: {
    list_sample_lines: {
      action: "list_sample_lines",
      inputSchema: SectionListSampleLinesArgsSchema,
      responseSchema: SectionListSampleLinesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSampleLineGroups"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSampleLineGroups", {
          alignmentName: args.alignmentName,
        }),
      ),
    },
    get_section_data: {
      action: "get_section_data",
      inputSchema: SectionGetDataArgsSchema,
      responseSchema: SectionDataResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          station: args.station,
        }),
      ),
    },
    create_sample_lines: {
      action: "create_sample_lines",
      inputSchema: SectionCreateSampleLinesArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSampleLines"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSampleLines", {
          alignmentName: args.alignmentName,
          groupName: args.groupName,
          stations: args.stations,
          interval: args.interval,
          leftWidth: args.leftWidth,
          rightWidth: args.rightWidth,
          surfaces: args.surfaces,
        }),
      ),
    },
    view_create: {
      action: "view_create",
      inputSchema: SectionViewCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViews", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            bandSetStyle: args.bandSetStyle ?? null,
            leftOffset: args.leftOffset ?? null,
            rightOffset: args.rightOffset ?? null,
            stationStart: args.stationStart ?? null,
            stationEnd: args.stationEnd ?? null,
          });
        },
      ),
    },
    view_list: {
      action: "view_list",
      inputSchema: SectionViewListArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSectionViews", {
          alignmentName: args.alignmentName ?? null,
          sampleLineGroupName: args.sampleLineGroupName ?? null,
        }),
      ),
    },
    view_update_style: {
      action: "view_update_style",
      inputSchema: SectionViewUpdateStyleArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updateSectionViewStyles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updateSectionViewStyles", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          style: args.style ?? null,
          bandSetStyle: args.bandSetStyle ?? null,
          applyToAll: args.applyToAll ?? true,
        }),
      ),
    },
    view_group_create: {
      action: "view_group_create",
      inputSchema: SectionViewGroupCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViewGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViewGroup", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            plotStyle: args.plotStyle ?? null,
          });
        },
      ),
    },
    view_export: {
      action: "view_export",
      inputSchema: SectionViewExportArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          includeElevations: args.includeElevations ?? true,
          stationStart: args.stationStart ?? null,
          stationEnd: args.stationEnd ?? null,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_section",
      displayName: "Civil 3D Section",
      description: "横断面聚合工具：查询/采样线/断面图",
      inputShape: canonicalSectionInputShape,
      supportedActions: [
        "list_sample_lines",
        "get_section_data",
        "create_sample_lines",
        "view_create",
        "view_list",
        "view_update_style",
        "view_group_create",
        "view_export",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_section_view_create",
      displayName: "Civil 3D Section View Create",
      description: "创建横断面图 {sampleLineGroup, ...}",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.tuple([z.number(), z.number()]),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        leftOffset: z.number().nonnegative().optional(),
        rightOffset: z.number().nonnegative().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_create"],
      resolveAction: (rawArgs) => ({
        action: "view_create",
        args: {
          action: "view_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          leftOffset: rawArgs.leftOffset,
          rightOffset: rawArgs.rightOffset,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_list",
      displayName: "Civil 3D Section View List",
      description: "查询横断面图列表",
      inputShape: {
        alignmentName: z.string().optional(),
        sampleLineGroupName: z.string().optional(),
      },
      supportedActions: ["view_list"],
      resolveAction: (rawArgs) => ({
        action: "view_list",
        args: {
          action: "view_list",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_update_style",
      displayName: "Civil 3D Section View Update Style",
      description: "更新横断面图样式",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        applyToAll: z.boolean().optional(),
      },
      supportedActions: ["view_update_style"],
      resolveAction: (rawArgs) => ({
        action: "view_update_style",
        args: {
          action: "view_update_style",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          applyToAll: rawArgs.applyToAll,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_group_create",
      displayName: "Civil 3D Section View Group Create",
      description: "创建横断面图组",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.tuple([z.number(), z.number()]),
        style: z.string().optional(),
        plotStyle: z.string().optional(),
      },
      supportedActions: ["view_group_create"],
      resolveAction: (rawArgs) => ({
        action: "view_group_create",
        args: {
          action: "view_group_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          plotStyle: rawArgs.plotStyle,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_export",
      displayName: "Civil 3D Section View Export",
      description: "导出横断面数据（CSV）{sampleLineGroup, outputPath, ...}",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        outputPath: z.string(),
        overwrite: z.boolean().optional(),
        includeElevations: z.boolean().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_export"],
      resolveAction: (rawArgs) => ({
        action: "view_export",
        args: {
          action: "view_export",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          includeElevations: rawArgs.includeElevations,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
  ],
};
