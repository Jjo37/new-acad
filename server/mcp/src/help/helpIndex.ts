import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { gzipSync, gunzipSync } from "node:zlib";
import MiniSearch from "minisearch";
import {
  discoverOfflineHelpInstallations,
  selectHelpInstallation,
} from "./helpDiscovery.js";
import {
  listHelpTopicFiles,
  parseHelpTopic,
  sourceFingerprint,
  toIndexEntry,
} from "./helpParser.js";
import type {
  HelpImageRef,
  HelpIndexCache,
  HelpIndexStatus,
  HelpInstallation,
  HelpSearchResult,
  HelpTopic,
  HelpTopicIndexEntry,
} from "./types.js";

export interface HelpSearchOptions {
  query: string;
  version?: string;
  featureArea?: string;
  topicType?: string;
  tags?: string[];
  limit?: number;
}

export type HelpIndexProgress = (
  completed: number,
  total: number,
  message: string,
) => void | Promise<void>;

export interface Civil3DHelpManagerOptions {
  installations?: HelpInstallation[];
  cacheRoot?: string;
}

interface SearchDocument extends HelpTopicIndexEntry {
  tagsText: string;
}

export class OfflineHelpIndex {
  private topics: HelpTopicIndexEntry[] = [];
  private byId = new Map<string, HelpTopicIndexEntry>();
  private byUri = new Map<string, HelpTopicIndexEntry>();
  private images = new Map<string, HelpImageRef>();
  private searchIndex?: MiniSearch<SearchDocument>;
  private status?: HelpIndexStatus;

  readonly cachePath: string;

  constructor(
    readonly installation: HelpInstallation,
    cacheRoot: string,
  ) {
    this.cachePath = path.join(cacheRoot, installation.version, "index.json.gz");
  }

  async ensureReady(
    force = false,
    progress?: HelpIndexProgress,
  ): Promise<HelpIndexStatus> {
    // Re-scan the corpus once per server process. A fresh process still verifies the
    // cache fingerprint, while repeated search/topic calls stay fast.
    if (!force && this.status) return this.status;
    const files = listHelpTopicFiles(this.installation.root);
    if (files.length === 0) {
      throw new Error(`No Civil 3D help topics were found under '${this.installation.root}'.`);
    }
    const fingerprint = sourceFingerprint(files);

    if (!force && this.loadCache(fingerprint)) return this.status!;

    const topics: HelpTopicIndexEntry[] = [];
    const seenIds = new Set<string>();
    await progress?.(0, files.length, `Indexing Civil 3D ${this.installation.version} offline help.`);
    for (let index = 0; index < files.length; index++) {
      try {
        const topic = parseHelpTopic(
          this.installation.root,
          files[index],
          this.installation.version,
        );
        if (topic && !seenIds.has(topic.id)) {
          seenIds.add(topic.id);
          topics.push(toIndexEntry(topic));
        }
      } catch {
        // A malformed Autodesk topic must not prevent the rest of the corpus from loading.
      }
      if ((index + 1) % 250 === 0 || index + 1 === files.length) {
        await progress?.(
          index + 1,
          files.length,
          `Parsed ${index + 1} of ${files.length} Civil 3D help files.`,
        );
        await new Promise<void>((resolve) => setImmediate(resolve));
      }
    }

    const indexedAt = new Date().toISOString();
    const cache: HelpIndexCache = {
      schemaVersion: 1,
      version: this.installation.version,
      root: this.installation.root,
      sourceFingerprint: fingerprint,
      indexedAt,
      topics,
    };
    this.writeCache(cache);
    this.loadTopics(topics);
    this.status = this.createStatus(cache, false);
    await progress?.(files.length, files.length, `Indexed ${topics.length} Civil 3D help topics.`);
    return this.status;
  }

  async search(
    options: HelpSearchOptions,
    progress?: HelpIndexProgress,
  ): Promise<{ confidence: "high" | "medium" | "low"; versionUsed: string; results: HelpSearchResult[] }> {
    await this.ensureReady(false, progress);
    const limit = Math.min(Math.max(options.limit ?? 8, 1), 20);
    const query = expandEngineerTerms(options.query);
    const results = this.searchIndex!.search(query, {
      boost: { title: 6, summary: 3, tagsText: 4, searchText: 1 },
      combineWith: "OR",
      prefix: true,
      fuzzy: query.length >= 8 ? 0.12 : false,
    })
      .map((result) => ({ result, topic: this.byId.get(String(result.id)) }))
      .filter((item): item is { result: typeof item.result; topic: HelpTopicIndexEntry } => Boolean(item.topic))
      .filter(({ topic }) => !options.featureArea || sameFilter(topic.featureArea, options.featureArea))
      .filter(({ topic }) => !options.topicType || sameFilter(topic.topicType, options.topicType))
      .filter(({ topic }) => !options.tags?.length || options.tags.every((tag) => topic.tags.includes(tag)))
      .slice(0, limit)
      .map(({ result, topic }) => toSearchResult(topic, result.score, options.query));
    const topScore = results[0]?.score ?? 0;
    return {
      confidence: topScore >= 12 ? "high" : topScore >= 4 ? "medium" : "low",
      versionUsed: this.installation.version,
      results,
    };
  }

  async getTopic(idOrUri: string): Promise<HelpTopic | undefined> {
    await this.ensureReady();
    const entry = this.byId.get(idOrUri) ?? this.byUri.get(idOrUri);
    if (!entry) return undefined;
    return parseHelpTopic(
      this.installation.root,
      entry.sourcePath,
      this.installation.version,
    );
  }

  async getImage(id: string): Promise<HelpImageRef | undefined> {
    await this.ensureReady();
    return this.images.get(id);
  }

  async getStatus(): Promise<HelpIndexStatus> {
    return this.ensureReady();
  }

  private loadCache(fingerprint: string): boolean {
    if (!fs.existsSync(this.cachePath)) return false;
    try {
      const cache = JSON.parse(
        gunzipSync(fs.readFileSync(this.cachePath)).toString("utf8"),
      ) as HelpIndexCache;
      if (
        cache.schemaVersion !== 1
        || cache.version !== this.installation.version
        || path.resolve(cache.root) !== path.resolve(this.installation.root)
        || cache.sourceFingerprint !== fingerprint
      ) {
        return false;
      }
      this.loadTopics(cache.topics);
      this.status = this.createStatus(cache, true);
      return true;
    } catch {
      return false;
    }
  }

  private writeCache(cache: HelpIndexCache): void {
    fs.mkdirSync(path.dirname(this.cachePath), { recursive: true });
    const temporaryPath = `${this.cachePath}.${process.pid}.tmp`;
    fs.writeFileSync(temporaryPath, gzipSync(JSON.stringify(cache)));
    fs.renameSync(temporaryPath, this.cachePath);
  }

  private loadTopics(topics: HelpTopicIndexEntry[]): void {
    this.topics = topics;
    this.byId = new Map(topics.map((topic) => [topic.id, topic]));
    this.byUri = new Map(topics.map((topic) => [topic.uri, topic]));
    this.images = new Map(
      topics.flatMap((topic) => topic.images).map((image) => [image.id, image]),
    );
    this.searchIndex = new MiniSearch<SearchDocument>({
      fields: ["title", "summary", "searchText", "tagsText"],
      storeFields: ["id"],
      idField: "id",
      searchOptions: { prefix: true },
    });
    this.searchIndex.addAll(topics.map((topic) => ({ ...topic, tagsText: topic.tags.join(" ") })));
  }

  private createStatus(cache: HelpIndexCache, loadedFromCache: boolean): HelpIndexStatus {
    return {
      version: cache.version,
      root: cache.root,
      topicCount: cache.topics.length,
      imageCount: new Set(cache.topics.flatMap((topic) => topic.images.map((image) => image.id))).size,
      cachePath: this.cachePath,
      sourceFingerprint: cache.sourceFingerprint,
      indexedAt: cache.indexedAt,
      loadedFromCache,
    };
  }
}

export class Civil3DHelpManager {
  private readonly installations: HelpInstallation[];
  private readonly cacheRoot: string;
  private readonly indexes = new Map<string, OfflineHelpIndex>();

  constructor(options: Civil3DHelpManagerOptions = {}) {
    this.installations = options.installations ?? discoverOfflineHelpInstallations();
    this.cacheRoot = options.cacheRoot
      ?? process.env.CIVIL3D_HELP_CACHE_ROOT
      ?? path.join(process.env.LOCALAPPDATA ?? os.homedir(), "Civil3DMcp", "help-index");
  }

  listInstallations(): Array<Omit<HelpInstallation, "root"> & { root?: string }> {
    const debugPaths = process.env.CIVIL3D_DEBUG_PATHS === "true";
    return this.installations.map((installation) => ({
      version: installation.version,
      language: installation.language,
      displayName: installation.displayName,
      configured: installation.configured,
      ...(debugPaths ? { root: installation.root } : {}),
    }));
  }

  async search(options: HelpSearchOptions, progress?: HelpIndexProgress) {
    return this.indexFor(options.version).search(options, progress);
  }

  async getTopic(version: string | undefined, idOrUri: string): Promise<HelpTopic | undefined> {
    return this.indexFor(version).getTopic(idOrUri);
  }

  async getImage(version: string | undefined, id: string): Promise<HelpImageRef | undefined> {
    return this.indexFor(version).getImage(id);
  }

  async status(version?: string): Promise<{
    installations: ReturnType<Civil3DHelpManager["listInstallations"]>;
    active?: Omit<HelpIndexStatus, "root" | "cachePath"> & { root?: string; cachePath?: string };
  }> {
    const installation = selectHelpInstallation(this.installations, version);
    if (!installation) return { installations: this.listInstallations() };
    const status = await this.indexFor(version).getStatus();
    const debugPaths = process.env.CIVIL3D_DEBUG_PATHS === "true";
    const { root, cachePath, ...safeStatus } = status;
    return {
      installations: this.listInstallations(),
      active: {
        ...safeStatus,
        ...(debugPaths ? { root, cachePath } : {}),
      },
    };
  }

  async reindex(version?: string, progress?: HelpIndexProgress): Promise<HelpIndexStatus> {
    return this.indexFor(version).ensureReady(true, progress);
  }

  private indexFor(version?: string): OfflineHelpIndex {
    const installation = selectHelpInstallation(this.installations, version);
    if (!installation) {
      throw new Error(
        "Civil 3D offline help was not found. Install Autodesk Offline Help or set CIVIL3D_HELP_ROOT.",
      );
    }
    if (version && installation.version !== version) {
      throw new Error(`Civil 3D ${version} offline help is not installed.`);
    }
    let index = this.indexes.get(installation.root);
    if (!index) {
      index = new OfflineHelpIndex(installation, this.cacheRoot);
      this.indexes.set(installation.root, index);
    }
    return index;
  }
}

export function selectRenderableImages(topic: HelpTopic, limit: number): HelpImageRef[] {
  const boundedLimit = Math.min(Math.max(limit, 0), 5);
  return [...topic.images]
    .filter((image) => image.mimeType.startsWith("image/") && image.size <= 2 * 1024 * 1024)
    .filter((image) => {
      const area = imagePriority(image);
      return area >= 10_000 || (area === 0 && image.size >= 2_048);
    })
    .sort((left, right) => imagePriority(right) - imagePriority(left))
    .slice(0, boundedLimit);
}

function imagePriority(image: HelpImageRef): number {
  return (image.width ?? 0) * (image.height ?? 0);
}

function toSearchResult(
  topic: HelpTopicIndexEntry,
  score: number,
  query: string,
): HelpSearchResult {
  return {
    id: topic.id,
    uri: topic.uri,
    title: topic.title,
    summary: topic.summary,
    excerpt: excerpt(topic.searchText, query),
    version: topic.version,
    featureArea: topic.featureArea,
    topicType: topic.topicType,
    tags: topic.tags,
    canonicalUrl: topic.canonicalUrl,
    imageCount: topic.images.length,
    score: Number(score.toFixed(4)),
  };
}

function excerpt(text: string, query: string): string {
  const compact = text.replace(/\s+/g, " ").trim();
  const lower = compact.toLowerCase();
  const tokens = tokenize(query);
  const first = tokens.map((token) => lower.indexOf(token)).filter((index) => index >= 0).sort((a, b) => a - b)[0] ?? 0;
  return compact.slice(Math.max(0, first - 100), Math.max(0, first - 100) + 500);
}

function expandEngineerTerms(query: string): string {
  const lower = query.toLowerCase();
  const expansions: string[] = [query];
  if (lower.includes("dirt quantities") || lower.includes("cut and fill")) {
    expansions.push("earthwork volume surface quantities");
  }
  if (lower.includes("road model")) expansions.push("corridor");
  if (lower.includes("cross sections")) expansions.push("sample lines section views");
  if (lower.includes("grading optimizer")) expansions.push("grading optimization constraints objectives zones");
  return expansions.join(" ");
}

function tokenize(value: string): string[] {
  return [...new Set(value.toLowerCase().match(/[a-z0-9][a-z0-9_-]{1,}/g) ?? [])]
    .filter((token) => !["the", "and", "for", "with", "how", "what", "why", "civil", "3d"].includes(token));
}

function sameFilter(left: string, right: string): boolean {
  const normalize = (value: string) => value.toLowerCase().replace(/[\s_-]+/g, "");
  return normalize(left) === normalize(right);
}
