import fs from "node:fs";
import { McpServer, ResourceTemplate } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult, ContentBlock } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import {
  Civil3DHelpManager,
  selectRenderableImages,
  type HelpIndexProgress,
} from "../help/helpIndex.js";
import type { HelpIndexStatus, HelpTopic, HelpVideoRef } from "../help/types.js";
import {
  getAutodeskVideoCatalog,
  renderVideoPlayerHtml,
} from "../help/videoCatalog.js";
import { captureToolHandler } from "./toolHandlerRegistry.js";

interface ProgressExtra {
  _meta?: { progressToken?: string | number };
  sendNotification?: (notification: {
    method: "notifications/progress";
    params: {
      progressToken: string | number;
      progress: number;
      total?: number;
      message?: string;
    };
  }) => Promise<void>;
}

interface HelpToolInput {
  action: "search" | "search_videos" | "get_topic" | "status" | "reindex";
  query?: string;
  id?: string;
  uri?: string;
  version?: string;
  featureArea?: string;
  topicType?: string;
  tags?: string[];
  limit?: number;
  includeImages?: boolean;
  maxImages?: number;
  includeVideos?: boolean;
  maxVideos?: number;
}

let defaultManager: Civil3DHelpManager | undefined;

export function getCivil3DHelpManager(): Civil3DHelpManager {
  defaultManager ??= new Civil3DHelpManager();
  return defaultManager;
}

export function registerHelpTool(
  server: McpServer,
  manager = getCivil3DHelpManager(),
  videoCatalog = getAutodeskVideoCatalog(),
): void {
  const handler = async (
    input: HelpToolInput,
    extra?: unknown,
  ): Promise<CallToolResult> => {
    try {
      const progress = createProgressReporter(extra as ProgressExtra | undefined);
      await progress(0, 100, `Civil 3D help: starting '${input.action}'.`);
      if (input.action === "search_videos") {
        if (!input.query?.trim()) throw invalidInput("The search_videos action requires a non-empty query.");
        const videos = videoCatalog.search(input.query, input.maxVideos ?? 3);
        const result = { query: input.query, videos: videos.map(publicVideo) };
        await progress(100, 100, `Civil 3D help: found ${videos.length} video(s).`);
        return jsonResult(input.action, result, videoContentBlocks(videos));
      }

      if (input.action === "search") {
        if (!input.query?.trim()) throw invalidInput("The search action requires a non-empty query.");
        const result = await manager.search({
          query: input.query,
          version: input.version,
          featureArea: input.featureArea,
          topicType: input.topicType,
          tags: input.tags,
          limit: input.limit,
        }, indexProgress(progress));
        const videos = input.includeVideos === false
          ? []
          : videoCatalog.search(input.query, input.maxVideos ?? 1);
        const response = { ...result, videos: videos.map(publicVideo) };
        await progress(100, 100, `Civil 3D help: found ${result.results.length} topic(s) and ${videos.length} video(s).`);
        return jsonResult(input.action, response, videoContentBlocks(videos));
      }

      if (input.action === "get_topic") {
        const idOrUri = input.uri ?? input.id;
        if (!idOrUri) throw invalidInput("The get_topic action requires id or uri.");
        const version = input.version ?? versionFromTopicUri(input.uri);
        const topic = await manager.getTopic(version, idOrUri);
        if (!topic) return toolError("CIVIL3D.OBJECT_NOT_FOUND", "Civil 3D help topic was not found.", -32004);
        const videos = input.includeVideos === false
          ? []
          : videoCatalog.search(topic.title, input.maxVideos ?? 1);
        const result = { ...topicResult(topic), videos: videos.map(publicVideo) };
        const selectedImages = selectRenderableImages(topic, input.maxImages ?? 3);
        const content: ContentBlock[] = [
          { type: "text", text: JSON.stringify(result, null, 2) },
          ...selectedImages.map<ContentBlock>((image) => ({
            type: "resource_link",
            uri: image.uri,
            name: image.caption || image.alt || `${topic.title} image`,
            description: image.caption || `Image from Autodesk Civil 3D ${topic.version} offline help.`,
            mimeType: image.mimeType,
            size: image.size,
          })),
        ];
        if (input.includeImages !== false) {
          let totalBytes = 0;
          const maxTotalBytes = Number(process.env.CIVIL3D_HELP_MAX_IMAGE_BYTES ?? 6 * 1024 * 1024);
          for (const image of selectedImages) {
            if (totalBytes + image.size > maxTotalBytes) continue;
            content.push({
              type: "image",
              data: fs.readFileSync(image.path).toString("base64"),
              mimeType: image.mimeType,
            });
            totalBytes += image.size;
          }
        }
        content.push(...videoContentBlocks(videos));
        await progress(100, 100, `Civil 3D help: loaded '${topic.title}'.`);
        return {
          content,
          structuredContent: { action: input.action, result },
        };
      }

      if (input.action === "status") {
        const result = {
          ...await manager.status(input.version),
          videoCatalog: videoCatalog.status(),
        };
        await progress(100, 100, "Civil 3D help: status ready.");
        return jsonResult(input.action, result);
      }

      if (process.env.CIVIL3D_ENABLE_HELP_REINDEX !== "true") {
        return toolError(
          "CIVIL3D.CONFLICT",
          "Civil 3D help reindex is disabled. Set CIVIL3D_ENABLE_HELP_REINDEX=true to enable it.",
          -32009,
        );
      }
      const status = await manager.reindex(input.version, indexProgress(progress));
      const result = safeIndexStatus(status);
      await progress(100, 100, `Civil 3D help: reindexed ${status.topicCount} topic(s).`);
      return jsonResult(input.action, result);
    } catch (error) {
      const code = errorCode(error);
      return toolError(code, error instanceof Error ? error.message : String(error), code === "CIVIL3D.INVALID_INPUT" ? -32602 : -32000);
    }
  };

  captureToolHandler(
    "civil3d_help",
    (args, extra) => handler(args as unknown as HelpToolInput, extra),
  );
  server.registerTool(
    "civil3d_help",
    {
      title: "Civil 3D Offline Help",
      description: "Search installed Autodesk Civil 3D help, retrieve cited topics with images and playable Autodesk videos, inspect index status, or rebuild the local index.",
      inputSchema: {
        action: z.enum(["search", "search_videos", "get_topic", "status", "reindex"]),
        query: z.string().optional(),
        id: z.string().optional(),
        uri: z.string().optional(),
        version: z.string().regex(/^\d{4}$/).optional(),
        featureArea: z.string().optional(),
        topicType: z.string().optional(),
        tags: z.array(z.string()).optional(),
        limit: z.number().int().min(1).max(20).default(8),
        includeImages: z.boolean().default(true),
        maxImages: z.number().int().min(0).max(5).default(3),
        includeVideos: z.boolean().default(true),
        maxVideos: z.number().int().min(0).max(5).default(1),
      },
      outputSchema: z.object({ action: z.string(), result: z.unknown() }),
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    handler,
  );
}

export function registerHelpResources(
  server: McpServer,
  manager = getCivil3DHelpManager(),
  videoCatalog = getAutodeskVideoCatalog(),
): void {
  server.registerResource(
    "civil3d-help-topic",
    new ResourceTemplate("civil3d://help/topics/{version}/{topicId}", { list: undefined }),
    {
      title: "Civil 3D Offline Help Topic",
      description: "Cleaned, version-matched topic from locally installed Autodesk Civil 3D offline help.",
      mimeType: "text/markdown",
    },
    async (uri, variables) => {
      const version = String(variables.version ?? "");
      const topicId = String(variables.topicId ?? "");
      const topic = await manager.getTopic(version, topicId);
      if (!topic) throw new Error("Civil 3D help topic was not found.");
      return {
        contents: [{ uri: uri.href, mimeType: "text/markdown", text: topic.markdown }],
      };
    },
  );

  server.registerResource(
    "civil3d-help-image",
    new ResourceTemplate("civil3d://help/images/{version}/{imageId}", { list: undefined }),
    {
      title: "Civil 3D Offline Help Image",
      description: "Screenshot or diagram referenced by a locally installed Autodesk Civil 3D help topic.",
      mimeType: "image/png",
    },
    async (uri, variables) => {
      const version = String(variables.version ?? "");
      const imageId = String(variables.imageId ?? "");
      const image = await manager.getImage(version, imageId);
      if (!image) throw new Error("Civil 3D help image was not found.");
      return {
        contents: [{
          uri: uri.href,
          mimeType: image.mimeType,
          blob: fs.readFileSync(image.path).toString("base64"),
        }],
      };
    },
  );

  server.registerResource(
    "civil3d-help-video",
    new ResourceTemplate("civil3d://help/videos/{version}/{videoId}", { list: undefined }),
    {
      title: "Civil 3D Autodesk Help Video",
      description: "Playable Autodesk Civil 3D help video with a direct MP4 fallback.",
      mimeType: "text/html",
    },
    async (uri, variables) => {
      const version = String(variables.version ?? "");
      const videoId = String(variables.videoId ?? "");
      const video = videoCatalog.get(videoId);
      if (!video || video.sourceVersion !== version) {
        throw new Error("Civil 3D help video was not found.");
      }
      return {
        contents: [{
          uri: uri.href,
          mimeType: "text/html",
          text: renderVideoPlayerHtml(video),
        }],
      };
    },
  );
}

function topicResult(topic: HelpTopic) {
  const debugPaths = process.env.CIVIL3D_DEBUG_PATHS === "true";
  return {
    markdown: topic.markdown,
    metadata: {
      id: topic.id,
      topicId: topic.topicId,
      uri: topic.uri,
      product: topic.product,
      version: topic.version,
      featureArea: topic.featureArea,
      topicType: topic.topicType,
      title: topic.title,
      headings: topic.headings,
      summary: topic.summary,
      tags: topic.tags,
      canonicalUrl: topic.canonicalUrl,
      contentHash: topic.contentHash,
      ...(debugPaths ? { sourcePath: topic.sourcePath } : {}),
    },
    relatedTopicIds: topic.relatedTopicIds,
    images: topic.images.map(({ path: _path, ...image }) => image),
  };
}

function jsonResult(action: string, result: unknown, additionalContent: ContentBlock[] = []) {
  return {
    content: [
      { type: "text" as const, text: JSON.stringify(result, null, 2) },
      ...additionalContent,
    ],
    structuredContent: { action, result },
  };
}

function publicVideo(video: HelpVideoRef) {
  return {
    id: video.id,
    uri: video.uri,
    title: video.title,
    sourceVersion: video.sourceVersion,
    pageUrl: video.pageUrl,
    mp4Url: video.mp4Url,
    webmUrl: video.webmUrl,
  };
}

function videoContentBlocks(videos: HelpVideoRef[]): ContentBlock[] {
  return videos.flatMap<ContentBlock>((video) => [
    {
      type: "resource",
      resource: {
        uri: video.uri,
        mimeType: "text/html",
        text: renderVideoPlayerHtml(video),
      },
    },
    {
      type: "resource_link",
      uri: video.mp4Url,
      name: `${video.title} (MP4)`,
      description: `Autodesk Civil 3D ${video.sourceVersion} help video.`,
      mimeType: "video/mp4",
    },
  ]);
}

function toolError(code: string, message: string, rpcCode: number) {
  return {
    content: [{ type: "text" as const, text: message }],
    isError: true,
    errorCode: code,
    rpcCode,
  };
}

function invalidInput(message: string): Error & { code: string } {
  const error = new Error(message) as Error & { code: string };
  error.code = "CIVIL3D.INVALID_INPUT";
  return error;
}

function errorCode(error: unknown): string {
  if (error && typeof error === "object" && "code" in error && typeof error.code === "string") return error.code;
  if (/not found|not installed/i.test(error instanceof Error ? error.message : String(error))) return "CIVIL3D.OBJECT_NOT_FOUND";
  return "CIVIL3D.INTERNAL_ERROR";
}

function createProgressReporter(extra?: ProgressExtra) {
  return async (progress: number, total: number, message: string) => {
    const progressToken = extra?._meta?.progressToken;
    if (progressToken === undefined || !extra?.sendNotification) return;
    await extra.sendNotification({
      method: "notifications/progress",
      params: { progressToken, progress, total, message },
    }).catch(() => undefined);
  };
}

function indexProgress(
  emit: ReturnType<typeof createProgressReporter>,
): HelpIndexProgress {
  return async (completed, total, message) => {
    const scaled = total > 0 ? Math.round((completed / total) * 95) : 0;
    await emit(scaled, 100, message);
  };
}

function versionFromTopicUri(uri?: string): string | undefined {
  if (!uri) return undefined;
  try {
    const segments = new URL(uri).pathname.split("/").filter(Boolean);
    return segments[0] === "topics" ? segments[1] : undefined;
  } catch {
    return undefined;
  }
}

function safeIndexStatus(status: HelpIndexStatus) {
  const { root, cachePath, ...safe } = status;
  return process.env.CIVIL3D_DEBUG_PATHS === "true"
    ? { ...safe, root, cachePath }
    : safe;
}
