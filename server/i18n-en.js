'use strict';
// 2026-10-14 i18n-B: English tool-schema descriptions (LLM-facing). Used when locale = en.
// Paths are like "readImage" or "readImage.maxSide" (tool name, optional dot-separated params).
const TOOL_EN = {
  "cadCall": "Invoke a C3D plugin TCP method (JSON-RPC). First call `listMethods` to get the method list, then pick a method; never guess method names. Method execution results are returned to the AI.",
  "cadCall.method": "Plugin method name, e.g. `listMethods` / `getEntityInfo` / `createCircle`. When unsure, call `listMethods` first.",
  "cadCall.params": "Method parameter object (identical to JSON-RPC params). Pass {} when there are no parameters.",
  "mcpCall": "Call high-level MCP tools (POST :3000/execute). Use ONLY for: composite/multi-step wizard types (civil3d_workflow_* / civil3d_quantity_takeoff / civil3d_orchestrate), help/documentation/standards checks (civil3d_docs / civil3d_help / civil3d_standards_lookup), and methods with complex, error-prone parameters. For atomic operations always use cadCall (TCP). The MCP backend shares the same origin as TCP (both are the plugin :8080); a method that is broken in the plugin is equally broken via MCP.",
  "mcpCall.tool": "MCP tool name (e.g. civil3d_quantity_takeoff / civil3d_docs)",
  "mcpCall.parameters": "Tool parameter object",
  "readFile": "Read local file contents (text/CSV/JSON/logs, etc., any format). Supports any local path (Desktop/D drive/any folder the user provides). ⚠️ Content is sent to the AI model provider for processing; sensitive files (config.json/.env/keys/certificates/.git, etc.) are rejected. **Large files must be read page by page**: use offset (start line, 1-based) + limit (number of lines) + maxChars (character budget, default 16000) to paginate; returns from/to/hasMore/nextOffset; to continue reading pass offset=nextOffset. **To just see the structure first** use outline:true (total line count / first 50 lines / CSV column headers / JSON top-level keys). **For stats/filtering/just the conclusion** (sum & group / grep / extract keys) prefer queryFile — the raw text does not enter the context.",
  "readFile.path": "File path (absolute path)",
  "readFile.offset": "Start line (1-based, default 1)",
  "readFile.limit": "Number of lines to read (0/omitted = to end of file, still bounded by maxChars)",
  "readFile.maxChars": "Character cap for this return (default 16000, max 32000)",
  "readFile.outline": "true = return only a structure preview (total line count / first 50 lines / CSV column headers / JSON top-level keys), no full text",
  "queryFile": "Structurally 'read/aggregate' a file locally, giving the model only the conclusion — the preferred choice for large files (CSV/TSV/JSON/logs), avoiding dumping raw text into the context. op: head/tail/sample (sampling) | wc (line count/character count) | grep (pattern regex, returns \"lineNumber:content\"; ignoreCase:false disables case-insensitivity) | groupby (CSV grouping; by=group column, agg=count|sum|avg, value=numeric column, limit) | stats (min/max/sum/avg of a given CSV column; column=column name or index) | json (jsonPath such as $.a.b or key, to extract keys/values). For CSV the first row is the header by default (header:false disables), delimiter defaults to comma (delimiter). The response body is bounded by maxChars (default 16000).",
  "queryFile.path": "Absolute file path",
  "queryFile.op": "head|tail|sample|wc|grep|groupby|stats|json (default wc)",
  "queryFile.pattern": "grep regex (case-insensitive by default)",
  "queryFile.by": "groupby: group column (header name or column index)",
  "queryFile.value": "groupby + sum/avg: numeric column (header name or column index)",
  "queryFile.agg": "groupby aggregation: count|sum|avg (default count)",
  "queryFile.column": "stats: column to analyze (header name or column index)",
  "queryFile.jsonPath": "json: value path, e.g. $.a.b or a.b",
  "queryFile.delimiter": "CSV delimiter (default comma)",
  "queryFile.header": "Whether the CSV's first row is a header (default true)",
  "queryFile.limit": "Number of entries returned by head/tail/sample/groupby",
  "queryFile.maxChars": "Character cap for the response (default 16000)",
  "listDir": "List directory contents (subdirectories + file names/sizes, without contents) — when the user gives a folder path, call this first to see what's inside, then read files as needed. Sensitive entries (.git/credential-type) are automatically filtered out.",
  "listDir.path": "Directory path (absolute path)",
  "writeFile": "Create/overwrite a file inside the AI workspace {{WORKDIR}} (text/CSV/JSON/reports, any format). ⚠️ Can only write to the workspace: user files/other locations are always rejected (security red line). Existing files inside the workspace may be overwritten/updated. **Write large output in chunks**: pass append:true to append to the end of the file (multiple calls assemble the complete file, avoiding an oversized single write).",
  "writeFile.path": "File path (must be inside {{WORKDIR}}, e.g. {{WORKDIR}}/report.csv)",
  "writeFile.content": "File content (text)",
  "writeFile.append": "true = append to the end of the file (write large output in multiple passes); default false = overwrite",
  "deleteFile": "Delete a file inside the AI workspace {{WORKDIR}} (only files you generated yourself; other locations are always rejected). Used to clean up temporary/discarded files produced during this work.",
  "deleteFile.path": "Path of the file to delete (must be inside {{WORKDIR}})",
  "readImage": "Read an image file (png/jpg/jpeg/bmp/gif/webp) and let the model view it — used when the user asks to look at an image / extract text from an image. The image is attached to the message (requires the model to support vision; if unsupported the API errors — just report it honestly, do not retry repeatedly). **When over the limit (encoded > limits.imageMaxB64, default 1.5MB) it is NOT auto-compressed**; it returns `needsDecision` + options. Tell the user the options and let them decide: compressed view (readImage with `resize:{maxSide:1600}`) / local high-res (with `crop:{x,y,w,h}`) / user handles it themselves. resize/crop go through the plugin GDI+ and are lossy/partial operations — **only call them when the user has chosen**; `maxQuality:true` = hard reject as-is.",
  "readImage.path": "Image file path (absolute path)",
  "readImage.resize": "Compressed view (lossy): {maxSide:1600} scales proportionally so the long side ≤ maxSide",
  "readImage.crop": "Local high-res: {x,y,w,h} takes only that region (original resolution)",
  "readImage.quality": "JPEG quality 10-100 (default 85, applies only with resize/crop)",
  "readImage.format": "jpeg|png (default jpeg, applies only with resize/crop)",
  "readImage.maxQuality": "true = reject outright when over the limit, without any processing (keep the original image)",
  "traceImage": "Automatically trace bitmaps (PNG/BMP/JPEG/GIF/TIFF/WEBP) into CAD vector lines — use when the user pastes an image asking to \"draw it into CAD / trace it into lines / draw it like this\". mode selection: flat=color-block regions (★ for flat/solid-color illustrations: cartoons, logos, vector-style wallpapers — fewest lines, best result) / arc=arc fitting (line art, hand-drawn, photo style) / centerline=skeleton thin lines (must remain editable) / outline=stroke outline (preserves stroke weight) / posterize=all color blocks (including background). If mode is omitted it auto-detects by image type: flat/solid-color→flat; white-background line art/scan containing large solid ink areas→outline (the outer contour is closer to the original; tested better than centerline); everything else→arc. By default only the path JSON is generated; to draw into the drawing **include draw:true directly** — the tool creates the layer itself, completes the drawing in one pass, and reports the success/failure counts (do NOT loop createPolyline yourself). Non-PNG/BMP is automatically decoded via the plugin exportImageGray. With fill:true, each closed path is instead **filled** on landing (goes through importHatches; for color-block images/flat illustrations — fillPattern can name a pattern such as ANSI31, default SOLID solid fill). The mask parameter (default auto) controls the mask source: auto switches to ink threshold on images with \"bright paper background + moderate ink ratio\" (ink line art/scans/white-background hand drawings) and uses edge detection for the rest (photos/color images); you can pass ink directly (forces ink threshold, suitable for white-background line art/scans) or dog (forces edge detection). There are also two enhancement tiers: line=line-art enhancement (median denoise + Sauvola local adaptive threshold, suitable for dirty scans/unevenly lit line art); tone=tone separation (grayscale k-means layering → contour per layer; when drawing, fill by layer color by default, fill:false disables; suitable for continuous-tone/photos/grayscale images). autotune:true uses \"coverage/accuracy\" to automatically pick the optimal pipeline and parameters (recommended, slightly slower). **Both output forms are retained and the user chooses**: for \"trace into lines\"→ line-art polylines (line/arc/outline/centerline); for \"color blocks/tone separation\"→ tone (layered flat fill: one CAD layer + fill per tonal level, individually toggleable). When unsure, ask the user which one they want. levels for tone/flat is recommended 2–12 (max 32; beyond that there is no benefit — tested: the higher the count, the more fragmented the small regions become); minArea for tone is the detail floor (default 0.15%; lower → fuller fill but more blocks); for shading contrast use contrast (default 1, >1 stronger) / gamma (<1 brightens). **Non-PNG/BMP now also returns color** (flat/posterize no longer grayscale; plugin withColor). After landing it reports back: drawn.loops=contour block count, drawn.paths=polyline count, drawn.ok=entity object count (≈ layer count in fill mode, **don't treat it as the block count**).",
  "traceImage.image": "Absolute image path (PNG/BMP/JPEG/GIF/TIFF/WEBP)",
  "traceImage.mode": "flat (preferred for flat/solid-color illustrations) | arc (line art/hand-drawn) | centerline | outline | posterize; omitted = auto-detect by image",
  "traceImage.draw": "true = draw directly into the current drawing (default false generates JSON only)",
  "traceImage.layer": "Target layer name (default TRACE_<MODE>)",
  "traceImage.scaleTo": "Scale to this width (drawing units, default 100)",
  "traceImage.offsetX": "X offset (default 0)",
  "traceImage.offsetY": "Y offset (default 0)",
  "traceImage.simplify": "RDP tolerance in px (default 1.2)",
  "traceImage.minLength": "Discard anything shorter than this length (default 12)",
  "traceImage.bridge": "Endpoint bridging distance in px (default 6, 0=off)",
  "traceImage.arcThreshold": "Curvature below this is not arc-ized (default 0.02)",
  "traceImage.minArea": "flat: minimum color-block area percentage % (default 0.05)",
  "traceImage.minColor": "flat: minimum percentage % to count as a distinct color (default 1; anti-aliasing transition colors are merged into the nearest dominant color)",
  "traceImage.keepBackground": "flat: whether to also draw the background color block (default false)",
  "traceImage.arcize": "flat: whether to perform arc fitting (default true)",
  "traceImage.mask": "Mask source: auto|ink|dog (default auto: ink threshold for line art/scans, edge detection for photos)",
  "traceImage.levels": "flat/tone/posterize level count (defaults 4/4/3; 2-12 recommended)",
  "traceImage.fill": "true = fill closed paths on landing (via importHatches; tone fills by default)",
  "traceImage.fillPattern": "Hatch pattern name (default SOLID, e.g. ANSI31)",
  "traceImage.contrast": "tone: tonal contrast stretch (default 1, >1 stronger)",
  "traceImage.gamma": "tone: gamma (default 1, <1 brightens)",
  "traceImage.autotune": "true = auto-pick the best pipeline/params by coverage/accuracy (recommended, slower)",
  "traceImage.adaptive": "line tier: Sauvola local adaptive threshold enhancement",
  "traceImage.denoise": "median denoise on/off",
  "traceImage.maxSide": "Decode long-side cap in px (default 1600)",
  "traceImage.window": "Sauvola window size px",
  "traceImage.k": "Sauvola k coefficient (default 0.2)",
  "traceImage.speck": "Despeckle: filter blobs smaller than this area in px (default 12)",
  "traceImage.close": "Closing kernel size (default 1)",
  "traceImage.inkGamma": "ink mask brightening exponent (default 1; 0.5 cleaner but harsher on light hatching)",
  "writeMemory": "Write important information into long-term memory (overwrite-style update of the full text). Admission principles: only write rule-type (red lines/specs/standards), user habits (preferences/style/common settings), and tool methods (usage/workflows/lessons); do NOT write concrete task data/drawings/one-off context. Before writing, call readMemory to see the existing content first.",
  "writeMemory.content": "Full memory text (markdown), sectioned as user preferences/rule specs/tool methods, self-contained and readable on its own",
  "readMemory": "Read the full long-term memory. Memory is **injected only as a per-section index** (there is an index in the system prompt); use this tool to fetch a specific section or the whole content when needed; before writing you should also read it once to merge old entries.",
  "revertMemory": "Revert long-term memory to the **previous version** (the .bak automatically backed up before writing). Use when the user rejects the memory you just wrote, or when you find a mistake. After reverting, report a summary of the restored content to the user.",
  "readKnowledge": "Read the full built-in knowledge base (precondition checkpoints + cautions; method lookup relies on listMethods filter, and high-frequency methods carry their own descriptions). For complex tasks (quantity takeoff/surfaces/batch modification/export, etc.) call this tool first to verify preconditions before deciding which methods to call.",
  "ingestKnowledge": "Organize the \"user knowledge base\": scan the sources listed in config.json → knowledgeSources (multi-source, **read-only reference**; defaults to user-knowledge/), across md/txt/csv/json/log/docx/doc/xlsx/xls/pptx/pdf, extract text → write cache → rebuild the index _index.md. **Source folders are never written to or copied**; derived cache lives in user-knowledge/.cache/. A scanned PDF without a text layer goes through Windows OCR automatically (needs the language pack). Call this when the user says \"organize the knowledge base\" or \"add this folder to the knowledge base\".",
  "searchKnowledge": "Cross-file search (grep) in the \"user knowledge base\", returning file:line + snippet — when the user asks \"what does spec/clause XX require\", search first; after a hit use readFile to get more context from that file (for office files use readDocx/readXlsx/readPptx).",
  "searchKnowledge.query": "Keyword or regex snippet (case-insensitive by default)",
  "searchKnowledge.maxMatches": "Maximum number of matches returned (default 100)",
  "createPlan": "Create a structured plan at the start of a complex task (overwrites the old plan). For multi-step tasks (≥3 dependent steps) it is recommended to create the plan before executing; update the status with updatePlan after each step completes.",
  "createPlan.goal": "Task goal (the user's own words or a one-sentence summary)",
  "createPlan.steps": "Ordered steps (≤10 steps, one sentence each)",
  "updatePlan": "Update a plan step's status: index=which step (0-based), status=doing/done/failed, note is an optional supplementary comment",
  "getPlan": "View the current plan status (which step is done / remaining steps). Call when resuming after hitting a limit or when unsure of progress.",
};

function readLocale() {
  try {
    const p = require('path').join(__dirname, '..', 'config.json');
    if (require('fs').existsSync(p)) {
      let raw = require('fs').readFileSync(p, 'utf8');
      if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
      const j = JSON.parse(raw);
      return String(j.locale || '').trim().toLowerCase();
    }
  } catch (_) { /* unreadable config -> fall through to auto */ }
  return '';
}
// 2026-10-16: 统一 locale 解析（auto / 空 → 跟随系统 UI 语言）。
//   背景：llm-agent 原先直接拿 config 原始值判断，`auto` 既不是 en 也不是 zh
//   → 语言注记为空、系统提示却走中文 → 打招呼语言随缘（Bro 报障）。
function resolveLocale() {
  const l = readLocale();
  if (l.startsWith('en')) return 'en';
  if (l.startsWith('zh')) return 'zh';
  try { return String(Intl.DateTimeFormat().resolvedOptions().locale || '').toLowerCase().startsWith('zh') ? 'zh' : 'en'; }
  catch (_) { return 'zh'; }
}
function isEnglish() { return resolveLocale() === 'en'; }
function applyDesc(node, path, workDir) {
  if (!node || typeof node !== 'object') return;
  if (typeof node.description === 'string' && TOOL_EN[path] !== undefined) {
    node.description = workDir ? TOOL_EN[path].replaceAll('{{WORKDIR}}', workDir) : TOOL_EN[path];
  }
  const props = node.properties || (node.parameters && node.parameters.properties);
  if (props) for (const k of Object.keys(props)) applyDesc(props[k], path + '.' + k, workDir);
}
// Returns a localized COPY of the OpenAI-style tool array when locale=en; otherwise the original array.
function localizeToolSchema(tools) {
  if (!isEnglish()) return tools;
  let workDir = '';
  try { workDir = require('./file-tools.js').getAgentWorkDir(); } catch (_) { workDir = ''; }
  return tools.map((t) => {
    const c = structuredClone(t);
    const fn = c.function || c;
    applyDesc(fn, fn.name || '?', workDir);
    return c;
  });
}

module.exports = { TOOL_EN, isEnglish, resolveLocale, localizeToolSchema };
