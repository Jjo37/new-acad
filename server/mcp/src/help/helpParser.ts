import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import * as cheerio from "cheerio";
import type { AnyNode } from "domhandler";
import { assertPathInsideHelpRoot } from "./helpDiscovery.js";
import type {
  HelpImageRef,
  HelpTopic,
  HelpTopicIndexEntry,
  HelpTopicType,
} from "./types.js";

const EMPTY_BODY_MINIMUM = 80;
const DECORATIVE_IMAGE_NAMES = new Set([
  "ac.menuaro.gif",
  "cclink.png",
]);

export function listHelpTopicFiles(root: string): string[] {
  const safeRoot = path.resolve(root);
  const directories = fs.readdirSync(safeRoot, { withFileTypes: true });
  const wrappedDirectories = directories
    .filter((entry) => entry.isDirectory() && /^wrapped-files/i.test(entry.name))
    .map((entry) => path.join(safeRoot, entry.name));
  const sourceDirectories = wrappedDirectories.length > 0
    ? wrappedDirectories
    : directories
      .filter((entry) => entry.isDirectory() && /^files/i.test(entry.name))
      .map((entry) => path.join(safeRoot, entry.name));
  const suffix = wrappedDirectories.length > 0 ? ".htm.js" : ".htm";
  const files: string[] = [];

  for (const directory of sourceDirectories) {
    const stack = [directory];
    while (stack.length > 0) {
      const current = stack.pop()!;
      for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
        const fullPath = path.join(current, entry.name);
        if (entry.isDirectory()) stack.push(fullPath);
        else if (entry.name.toLowerCase().endsWith(suffix)) files.push(fullPath);
      }
    }
  }

  return files.sort((left, right) => left.localeCompare(right));
}

export function sourceFingerprint(files: string[]): string {
  let latestModified = 0;
  let totalBytes = 0;
  for (const file of files) {
    const stat = fs.statSync(file);
    latestModified = Math.max(latestModified, Math.trunc(stat.mtimeMs));
    totalBytes += stat.size;
  }
  return `${files.length}:${latestModified}:${totalBytes}`;
}

export function extractWrappedTopicHtml(jsText: string): string | undefined {
  const assignment = jsText.indexOf("var topic");
  if (assignment < 0) return undefined;
  const equals = jsText.indexOf("=", assignment);
  if (equals < 0) return undefined;
  let index = equals + 1;
  while (/\s/.test(jsText[index] ?? "")) index++;
  const quote = jsText[index];
  if (quote !== '"' && quote !== "'") return undefined;

  let escaped = false;
  let literal = quote;
  for (index += 1; index < jsText.length; index++) {
    const character = jsText[index];
    literal += character;
    if (escaped) {
      escaped = false;
      continue;
    }
    if (character === "\\") {
      escaped = true;
      continue;
    }
    if (character === quote) break;
  }
  return decodeJsStringLiteral(literal);
}

export function parseHelpTopic(
  root: string,
  filePath: string,
  configuredVersion?: string,
): HelpTopic | undefined {
  const safePath = assertPathInsideHelpRoot(root, filePath);
  const raw = fs.readFileSync(safePath, "utf8");
  const html = safePath.toLowerCase().endsWith(".htm.js")
    ? extractWrappedTopicHtml(raw)
    : raw;
  if (!html) return undefined;

  const $ = cheerio.load(html);
  $("script, style, noscript, .footer-block, .footer-license-block").remove();
  const title = cleanText($("h1").first().text() || $("title").first().text());
  const contentRoot = $(".body_content, #body-content").first().length
    ? $(".body_content, #body-content").first()
    : $("body").first();
  const plainBody = cleanText(contentRoot.text());
  if (!title || plainBody.length < EMPTY_BODY_MINIMUM) return undefined;

  const topicId = meta($, "topicid") ?? guidFromPath(safePath) ?? stableHash(safePath, 20);
  const version = configuredVersion ?? meta($, "release") ?? detectVersion(html) ?? "unknown";
  const productCode = meta($, "product") ?? "CIV3D";
  const headings = $("h1, h2, h3")
    .toArray()
    .map((node) => cleanText($(node).text()))
    .filter(Boolean)
    .slice(0, 12);
  const images = extractImages($, contentRoot, root, safePath, version);
  const markdown = renderMarkdown($, contentRoot, images, safePath);
  const summary = meta($, "description") ?? firstSentence(plainBody) ?? title;
  const featureArea = inferFeatureArea(`${title}\n${headings.join("\n")}\n${plainBody}`);
  const topicType = inferTopicType(meta($, "topic-type") ?? "", title, plainBody);
  const relatedTopicIds = extractRelatedTopicIds($);
  const tags = inferTags(`${title}\n${plainBody}`, featureArea, topicType);
  const contentHash = `sha256:${crypto.createHash("sha256").update(markdown).digest("hex")}`;
  const id = stableHash(`${version}:${topicId}`, 16);
  const uri = `civil3d://help/topics/${encodeURIComponent(version)}/${id}`;
  const product = productCode.toUpperCase() === "CIV3D" ? "Civil 3D" : productCode;

  return {
    id,
    topicId,
    uri,
    title,
    summary,
    version,
    product,
    featureArea,
    topicType,
    headings: headings.length > 0 ? headings : [title],
    tags,
    relatedTopicIds,
    images,
    sourcePath: safePath,
    canonicalUrl: topicId.toUpperCase().startsWith("GUID-")
      ? `https://help.autodesk.com/view/CIV3D/${version}/ENG/?guid=${topicId}`
      : undefined,
    contentHash,
    markdown,
    searchText: cleanText(`${title}\n${summary}\n${headings.join("\n")}\n${plainBody}\n${tags.join(" ")}`).slice(0, 24000),
  };
}

export function toIndexEntry(topic: HelpTopic): HelpTopicIndexEntry {
  const { markdown: _markdown, ...entry } = topic;
  return entry;
}

function decodeJsStringLiteral(literal: string): string | undefined {
  if (literal.length < 2) return undefined;
  let output = "";
  for (let index = 1; index < literal.length - 1; index++) {
    const character = literal[index];
    if (character !== "\\") {
      output += character;
      continue;
    }
    const next = literal[++index];
    if (next === undefined) break;
    if (next === "r") output += "\r";
    else if (next === "n") output += "\n";
    else if (next === "t") output += "\t";
    else if (next === "b") output += "\b";
    else if (next === "f") output += "\f";
    else if (next === "v") output += "\v";
    else if (next === "0") output += "\0";
    else if (next === "x") {
      const hex = literal.slice(index + 1, index + 3);
      if (/^[\da-f]{2}$/i.test(hex)) {
        output += String.fromCharCode(Number.parseInt(hex, 16));
        index += 2;
      } else output += next;
    } else if (next === "u") {
      const hex = literal.slice(index + 1, index + 5);
      if (/^[\da-f]{4}$/i.test(hex)) {
        output += String.fromCharCode(Number.parseInt(hex, 16));
        index += 4;
      } else output += next;
    } else output += next;
  }
  return output;
}

function extractImages(
  $: cheerio.CheerioAPI,
  rootElement: cheerio.Cheerio<AnyNode>,
  helpRoot: string,
  sourcePath: string,
  version: string,
): HelpImageRef[] {
  const images = new Map<string, HelpImageRef>();
  rootElement.find("img[src]").each((_, node) => {
    const element = $(node);
    const source = element.attr("src");
    if (!source || /^(?:https?:|data:)/i.test(source)) return;
    if (element.hasClass("adsk-glyph-arrow") || element.closest(".uifinderbtn").length > 0) return;
    const assetPath = path.resolve(path.dirname(sourcePath), source);
    const fileName = path.basename(assetPath).toLowerCase();
    if (DECORATIVE_IMAGE_NAMES.has(fileName)) return;

    let safeAssetPath: string;
    try {
      safeAssetPath = assertPathInsideHelpRoot(helpRoot, assetPath);
    } catch {
      return;
    }
    if (!fs.existsSync(safeAssetPath) || !fs.statSync(safeAssetPath).isFile()) return;

    const relativePath = path.relative(helpRoot, safeAssetPath).replace(/\\/g, "/");
    const id = stableHash(relativePath.toLowerCase(), 16);
    if (images.has(id)) return;
    const size = fs.statSync(safeAssetPath).size;
    const dimensions = readImageDimensions(safeAssetPath);
    const alt = cleanText(element.attr("alt") || element.attr("title") || "");
    const nearbyCaption = cleanText(
      element.closest("p, div, td").next("p, div").first().text(),
    );
    const caption = /^(?:figure|image|illustration)\b/i.test(nearbyCaption)
      ? nearbyCaption.slice(0, 240)
      : alt;
    images.set(id, {
      id,
      uri: `civil3d://help/images/${encodeURIComponent(version)}/${id}`,
      path: safeAssetPath,
      alt,
      caption,
      mimeType: mimeTypeFor(safeAssetPath),
      width: dimensions?.width,
      height: dimensions?.height,
      size,
    });
  });
  return [...images.values()];
}

function renderMarkdown(
  $: cheerio.CheerioAPI,
  rootElement: cheerio.Cheerio<AnyNode>,
  images: HelpImageRef[],
  sourcePath: string,
): string {
  const imageByPath = new Map(images.map((image) => [path.normalize(image.path), image]));
  const lines: string[] = [];
  rootElement.children().each((_, node) => renderNode($, node, lines, imageByPath, sourcePath));
  return lines.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

function renderNode(
  $: cheerio.CheerioAPI,
  node: AnyNode,
  lines: string[],
  imageByPath: Map<string, HelpImageRef>,
  sourcePath: string,
): void {
  const element = $(node);
  const tag = String((node as { tagName?: string }).tagName ?? "").toLowerCase();
  if (!tag) return;

  if (/^h[1-6]$/.test(tag)) {
    const text = cleanText(element.text());
    if (text) lines.push(`${"#".repeat(Math.min(Number(tag.slice(1)), 6))} ${text}`, "");
    return;
  }
  if (tag === "p") {
    const text = cleanText(element.clone().find("img").remove().end().text());
    if (text) lines.push(text, "");
    element.find("img[src]").each((_, image) => renderImage($, image, lines, imageByPath, sourcePath));
    return;
  }
  if (tag === "ol" || tag === "ul") {
    element.children("li").each((index, item) => {
      const text = cleanText($(item).clone().find("img").remove().end().text());
      if (text) lines.push(`${tag === "ol" ? `${index + 1}.` : "-"} ${text}`);
      $(item).find("img[src]").each((_, image) => renderImage($, image, lines, imageByPath, sourcePath));
    });
    lines.push("");
    return;
  }
  if (tag === "table") {
    const rows = element.find("tr").toArray().map((row) =>
      $(row).find("th, td").toArray().map((cell) => cleanText($(cell).text()))
    ).filter((row) => row.some(Boolean));
    for (const row of rows) lines.push(`| ${row.join(" | ")} |`);
    if (rows.length > 0) lines.push("");
    element.find("img[src]").each((_, image) => renderImage($, image, lines, imageByPath, sourcePath));
    return;
  }
  if (tag === "img") {
    renderImage($, node, lines, imageByPath, sourcePath);
    return;
  }
  if (tag === "br") {
    lines.push("");
    return;
  }
  element.children().each((_, child) => renderNode($, child, lines, imageByPath, sourcePath));
}

function renderImage(
  $: cheerio.CheerioAPI,
  node: AnyNode,
  lines: string[],
  imageByPath: Map<string, HelpImageRef>,
  sourcePath: string,
): void {
  const source = $(node).attr("src");
  if (!source) return;
  const resolved = path.normalize(path.resolve(path.dirname(sourcePath), source));
  const image = imageByPath.get(resolved);
  if (!image) return;
  lines.push(`![${image.caption || image.alt || "Civil 3D help image"}](${image.uri})`, "");
}

function readImageDimensions(filePath: string): { width: number; height: number } | undefined {
  const buffer = Buffer.alloc(32);
  const descriptor = fs.openSync(filePath, "r");
  try {
    const bytesRead = fs.readSync(descriptor, buffer, 0, buffer.length, 0);
    if (bytesRead >= 24 && buffer.toString("ascii", 1, 4) === "PNG") {
      return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
    }
    if (bytesRead >= 10 && ["GIF87a", "GIF89a"].includes(buffer.toString("ascii", 0, 6))) {
      return { width: buffer.readUInt16LE(6), height: buffer.readUInt16LE(8) };
    }
  } finally {
    fs.closeSync(descriptor);
  }
  return undefined;
}

function mimeTypeFor(filePath: string): string {
  const extension = path.extname(filePath).toLowerCase();
  if (extension === ".png") return "image/png";
  if (extension === ".gif") return "image/gif";
  if (extension === ".jpg" || extension === ".jpeg") return "image/jpeg";
  if (extension === ".svg") return "image/svg+xml";
  return "application/octet-stream";
}

function meta($: cheerio.CheerioAPI, name: string): string | undefined {
  const value = $(`meta[name="${name}"]`).first().attr("content");
  const cleaned = cleanText(value ?? "");
  return cleaned || undefined;
}

function guidFromPath(filePath: string): string | undefined {
  return path.basename(filePath).match(/(GUID-[A-F0-9-]+)/i)?.[1]?.toUpperCase();
}

function detectVersion(html: string): string | undefined {
  return html.match(/Civil 3D\s+(\d{4})/i)?.[1]
    ?? html.match(/name=["']release["'][^>]+content=["'](\d{4})/i)?.[1];
}

function inferFeatureArea(text: string): string {
  const lower = text.toLowerCase();
  const areas: Array<[string, string[]]> = [
    ["Grading", ["grading", "grading object", "grading criteria"]],
    ["Feature Lines", ["feature line", "elevation editor"]],
    ["Surfaces", ["surface", "tin", "contour", "breakline"]],
    ["Corridors", ["corridor", "baseline", "region", "target"]],
    ["Alignments", ["alignment", "station", "offset"]],
    ["Profiles", ["profile", "vertical curve", "pvi"]],
    ["Pipe Networks", ["pipe network", "structure"]],
    ["Pressure Networks", ["pressure network"]],
    ["Parcels", ["parcel"]],
    ["Survey", ["survey"]],
    ["Data Shortcuts", ["data shortcut"]],
    ["Plan Production", ["plan production", "view frame", "sheet"]],
    ["Labels", ["label", "annotation"]],
  ];
  let best = { name: "General", count: 0 };
  for (const [name, terms] of areas) {
    const count = terms.reduce((sum, term) => sum + occurrences(lower, term), 0);
    if (count > best.count) best = { name, count };
  }
  return best.name;
}

function inferTopicType(raw: string, title: string, body: string): HelpTopicType {
  const text = `${raw} ${title} ${body.slice(0, 600)}`.toLowerCase();
  if (text.includes("troubleshoot") || /^why\b/i.test(title)) return "Troubleshooting";
  if (text.includes("dialog")) return "Dialog Reference";
  if (text.includes("command reference")) return "Command Reference";
  if (text.includes("tutorial")) return "Tutorial";
  if (raw.toLowerCase().includes("task") || /^to\s/i.test(title)) return "Procedure";
  if (raw.toLowerCase().includes("reference")) return "Reference";
  return "Concept";
}

function inferTags(text: string, featureArea: string, topicType: HelpTopicType): string[] {
  const lower = text.toLowerCase();
  const tags = new Set<string>([
    `feature:${slug(featureArea)}`,
    `doc:${slug(topicType)}`,
    "audience:civil_engineer",
  ]);
  const objects: Array<[string, string[]]> = [
    ["surface", ["surface"]],
    ["grading_object", ["grading object", "grading group"]],
    ["feature_line", ["feature line"]],
    ["alignment", ["alignment"]],
    ["profile", ["profile"]],
    ["corridor", ["corridor"]],
    ["pipe_network", ["pipe network"]],
    ["data_shortcut", ["data shortcut"]],
  ];
  for (const [name, terms] of objects) {
    if (terms.some((term) => lower.includes(term))) tags.add(`object:${name}`);
  }
  if (/\b(create|add|build|new)\b/.test(lower)) tags.add("task:create");
  if (/\b(edit|modify|change)\b/.test(lower)) tags.add("task:edit");
  if (/\b(troubleshoot|fix|not updating|wrong|missing)\b/.test(lower)) tags.add("task:troubleshoot");
  return [...tags].sort();
}

function extractRelatedTopicIds($: cheerio.CheerioAPI): string[] {
  const ids = new Set<string>();
  $("a[href]").each((_, node) => {
    const guid = ($(node).attr("href") ?? "").match(/GUID-[A-F0-9-]+/i)?.[0];
    if (guid) ids.add(guid.toUpperCase());
  });
  return [...ids].slice(0, 80);
}

function firstSentence(text: string): string | undefined {
  return cleanText(text.match(/^(.{30,280}?[.!?])(?:\s|$)/)?.[1] ?? text.slice(0, 240)) || undefined;
}

function stableHash(value: string, length: number): string {
  return crypto.createHash("sha1").update(value).digest("hex").slice(0, length);
}

function slug(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
}

function occurrences(text: string, term: string): number {
  return text.split(term).length - 1;
}

export function cleanText(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}
