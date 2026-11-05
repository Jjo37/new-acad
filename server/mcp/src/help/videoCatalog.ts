import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import MiniSearch from "minisearch";
import type { HelpVideoRef } from "./types.js";

interface RawVideoPage {
  url?: string;
  title?: string;
  video_urls?: string[];
}

interface RawVideoScrapeResult {
  status?: string;
  video_pages?: RawVideoPage[];
}

interface VideoSearchDocument extends HelpVideoRef {
  searchText: string;
}

export class AutodeskVideoCatalog {
  private readonly byId: Map<string, HelpVideoRef>;
  private readonly searchIndex: MiniSearch<VideoSearchDocument>;

  constructor(readonly videos: HelpVideoRef[]) {
    this.byId = new Map(videos.map((video) => [video.id, video]));
    this.searchIndex = new MiniSearch<VideoSearchDocument>({
      fields: ["title", "searchText"],
      storeFields: ["id"],
      idField: "id",
      searchOptions: { prefix: true },
    });
    this.searchIndex.addAll(videos.map((video) => ({
      ...video,
      searchText: normalizeVideoText(video.title),
    })));
  }

  static fromDefaultFile(): AutodeskVideoCatalog {
    const configuredPath = process.env.CIVIL3D_VIDEO_CATALOG;
    const candidates = [
      configuredPath,
      fileURLToPath(new URL("./data/autodesk_videos.json", import.meta.url)),
      path.resolve(process.cwd(), "autodesk_videos.json"),
    ].filter((candidate): candidate is string => Boolean(candidate));
    const catalogPath = candidates.find((candidate) => fs.existsSync(candidate));
    return new AutodeskVideoCatalog(catalogPath ? loadVideoFile(catalogPath) : []);
  }

  search(query: string, limit = 2): HelpVideoRef[] {
    const compactQuery = normalizeVideoText(query);
    if (!compactQuery) return [];
    const boundedLimit = Math.min(Math.max(limit, 0), 5);
    const matches = this.searchIndex.search(compactQuery, {
      boost: { title: 8, searchText: 1 },
      combineWith: "OR",
      prefix: true,
      fuzzy: compactQuery.length >= 10 ? 0.12 : false,
    });
    const minimumScore = Math.max(4, (matches[0]?.score ?? 0) * 0.5);
    return matches
      .filter((result) => result.score >= minimumScore)
      .map((result) => this.byId.get(String(result.id)))
      .filter((video): video is HelpVideoRef => Boolean(video))
      .slice(0, boundedLimit);
  }

  get(id: string): HelpVideoRef | undefined {
    return this.byId.get(id);
  }

  status(): { count: number; sourceVersions: string[] } {
    return {
      count: this.videos.length,
      sourceVersions: [...new Set(this.videos.map((video) => video.sourceVersion))].sort(),
    };
  }
}

let defaultCatalog: AutodeskVideoCatalog | undefined;

export function getAutodeskVideoCatalog(): AutodeskVideoCatalog {
  defaultCatalog ??= AutodeskVideoCatalog.fromDefaultFile();
  return defaultCatalog;
}

export function renderVideoPlayerHtml(video: HelpVideoRef): string {
  const title = escapeHtml(video.title);
  const mp4Url = escapeHtml(video.mp4Url);
  const webmSource = video.webmUrl
    ? `<source src="${escapeHtml(video.webmUrl)}" type="video/webm">`
    : "";
  const pageUrl = escapeHtml(video.pageUrl);
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; media-src https://help.autodesk.com; style-src 'unsafe-inline'; img-src data:">
<title>${title}</title><style>body{margin:0;background:#171717;color:#f4f4f4;font:14px system-ui;padding:12px}video{display:block;width:100%;max-height:70vh;background:#000;border-radius:8px}a{color:#77bdfb}p{margin:10px 0 0}</style></head>
<body><video controls preload="metadata"><source src="${mp4Url}" type="video/mp4">${webmSource}<a href="${mp4Url}">Play ${title}</a></video><p><strong>${title}</strong> · <a href="${pageUrl}">Autodesk Help</a></p></body></html>`;
}

function loadVideoFile(filePath: string): HelpVideoRef[] {
  try {
    const results = JSON.parse(fs.readFileSync(filePath, "utf8")) as RawVideoScrapeResult[];
    const videos = new Map<string, HelpVideoRef>();
    for (const result of results) {
      if (result.status && result.status !== "success") continue;
      for (const page of result.video_pages ?? []) {
        const pageUrl = trustedAutodeskUrl(page.url, ".htm");
        const mp4Url = (page.video_urls ?? [])
          .map((url) => trustedAutodeskUrl(url, ".mp4"))
          .find(Boolean);
        if (!pageUrl || !mp4Url) continue;
        const webmUrl = (page.video_urls ?? [])
          .map((url) => trustedAutodeskUrl(url, ".webm"))
          .find(Boolean);
        const title = cleanVideoTitle(page.title ?? "Autodesk Civil 3D video");
        const sourceVersion = pageUrl.match(/\/cloudhelp\/(\d{4})\//i)?.[1] ?? "unknown";
        const id = crypto.createHash("sha1").update(mp4Url).digest("hex").slice(0, 16);
        videos.set(id, {
          id,
          uri: `civil3d://help/videos/${encodeURIComponent(sourceVersion)}/${id}`,
          title,
          sourceVersion,
          pageUrl,
          mp4Url,
          webmUrl,
        });
      }
    }
    return [...videos.values()].sort((left, right) => left.title.localeCompare(right.title));
  } catch {
    return [];
  }
}

function trustedAutodeskUrl(value: string | undefined, requiredSuffix: string): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    if (url.protocol !== "https:" || url.hostname !== "help.autodesk.com") return undefined;
    if (!url.pathname.toLowerCase().endsWith(requiredSuffix)) return undefined;
    return url.href;
  } catch {
    return undefined;
  }
}

function cleanVideoTitle(value: string): string {
  return value.replace(/^video:\s*/i, "").replace(/\s+/g, " ").trim();
}

function normalizeVideoText(value: string): string {
  return cleanVideoTitle(value)
    .toLowerCase()
    .replace(/\b(how|do|does|can|could|please|civil|3d|video|tutorial|help|with|the|and|for|from|into|using|use|work)\b/g, " ")
    .replace(/[^a-z0-9]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
