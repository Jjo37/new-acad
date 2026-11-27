// knowledge-sources.js — 用户知识源（多源、只读引用、不搬文件）
// 2026-10-19: 由单一 user-knowledge/ 目录升级为「可配置多源」。
//
// 设计铁律
//   1) 源目录**只读**：本模块对源目录只做 readdir/stat/readFile，任何写操作只落在 CACHE_ROOT
//   2) 派生数据（文本缓存 + 索引 + manifest）统一放 user-knowledge/.cache/，源目录不留痕
//   3) 安全：只收本地绝对路径；拒绝设备路径/UNC/系统目录/敏感文件名
//   4) 增量：按 mtime+size 判断，未变文件直接复用缓存
//
// 配置（config.json）
//   "knowledgeSources": ["D:\\规范库", { "path": "E:\\国标", "label": "国标", "enabled": true }]
//   缺省/为空 → 仅使用默认目录 user-knowledge/
'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = path.join(__dirname, '..');
const DEFAULT_DIR = path.join(ROOT, 'user-knowledge');
// 2026-10-19: 下面这些路径可由 configure({ defaultDir }) 重定位（自测用临时目录，避免污染真实缓存）
let KB_DIR = DEFAULT_DIR;
let CACHE_ROOT = path.join(KB_DIR, '.cache');
let INDEX_FILE = path.join(KB_DIR, '_index.md');
let MANIFEST_FILE = path.join(CACHE_ROOT, '_manifest.json');

const TEXT_EXT = new Set(['.md', '.txt', '.csv', '.json', '.log', '.tsv']);
const OFFICE_EXT = { '.docx': 'readDocx', '.doc': 'readDoc', '.xlsx': 'readXlsx', '.xls': 'readXls', '.pptx': 'readPptx', '.pdf': 'readPdf' };

const LIMITS = {
  maxFilesPerSource: 2000,   // 扫描上限（防超大目录卡死）
  maxIndexPerSource: 50,     // 每个源进索引的文件条数
  indexMaxChars: 3000,       // 索引总字符（注入 system prompt 用）
  maxFileBytes: 20 * 1024 * 1024,
  maxDepth: 8,
};

// 依赖注入（避免循环 require：llm-agent 提供 cadCall；ocr 由本模块直接 require）
let deps = { cadCall: null };
let cfgOverride = null;
function configure(d) {
  deps = Object.assign(deps, d || {});
  if (d && d.defaultDir) {
    KB_DIR = d.defaultDir;
    CACHE_ROOT = path.join(KB_DIR, '.cache');
    INDEX_FILE = path.join(KB_DIR, '_index.md');
    MANIFEST_FILE = path.join(CACHE_ROOT, '_manifest.json');
  }
  if (d && 'cfg' in d) cfgOverride = d.cfg;
}

// ── 安全 ────────────────────────────────────────────────────────────────
const SENSITIVE_RE = new RegExp(
  '(^|[\\\\/])(config\\.json|appsettings\\.json|web\\.config|\\.npmrc|\\.piprc|secrets\\.json|\\.env(\\.[a-z0-9_]+)?)($|[\\\\/])' +
  '|\\.(pem|key|pfx|p12|p8|crt)$' +
  '|(^|[\\\\/])id_(rsa|dsa|ecdsa|ed25519)($|[\\\\/])' +
  '|(^|[\\\\/])\\.(git|svn|hg)($|[\\\\/])' +
  '|(^|[\\\\/])(\\.aws|gcloud|azure|credentials|credentials\\.json|\\.kube)($|[\\\\/])',
  'i'
);

function norm(p) { return path.resolve(String(p || '')); }

/** 路径是否允许作为知识源（返回 {ok, reason, path}） */
function validateSourcePath(raw) {
  const p = String(raw || '').trim();
  if (!p) return { ok: false, reason: '空路径' };
  if (/^[\\\\/]{2}[?.][\\\\/]/.test(p)) return { ok: false, reason: '设备路径不支持' };
  if (/^[\\\\/]{2}/.test(p)) return { ok: false, reason: 'UNC 网络路径不支持（请先映射为本地盘）' };
  if (!path.isAbsolute(p)) return { ok: false, reason: '必须是绝对路径' };
  const abs = norm(p);

  const windir = norm(process.env.windir || 'C:\\Windows');
  const sys32 = path.join(windir, 'System32');
  const pf = norm(process.env.ProgramFiles || 'C:\\Program Files');
  const pf86 = norm(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)');
  const lower = abs.toLowerCase().replace(/\\+$/, '');
  for (const [dir, label] of [[windir, 'Windows 目录'], [sys32, 'System32'], [pf, 'Program Files'], [pf86, 'Program Files (x86)']]) {
    const d = dir.toLowerCase().replace(/\\+$/, '');
    if (lower === d || lower.startsWith(d + '\\')) return { ok: false, reason: label + '不可作为知识源' };
  }
  return { ok: true, path: abs };
}

function assertInsideAny(fileFull, roots) {
  const f = norm(fileFull).toLowerCase();
  return roots.some((r) => {
    const b = norm(r).toLowerCase().replace(/\\+$/, '');
    return f === b || f.startsWith(b + '\\');
  });
}

// ── 配置解析 ─────────────────────────────────────────────────────────────
function readConfig() {
  try {
    let raw = fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8');
    if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
    return JSON.parse(raw);
  } catch (_) { return {}; }
}

/**
 * 返回启用的知识源列表：[{ id, path, label, isDefault }]
 * 配置缺省 → 单个默认源 user-knowledge/
 */
function getSources(cfg) {
  const conf = cfg || cfgOverride || readConfig();
  const list = [];
  const raw = conf.knowledgeSources;
  const push = (entry) => {
    const obj = typeof entry === 'string' ? { path: entry } : (entry || {});
    if (obj.enabled === false) return;
    const v = validateSourcePath(obj.path);
    if (!v.ok) { list.push({ id: 'invalid:' + String(obj.path), path: String(obj.path || ''), label: obj.label || String(obj.path || ''), invalid: v.reason }); return; }
    if (list.some((x) => x.path && x.path.toLowerCase() === v.path.toLowerCase())) return;   // 去重
    list.push({
      id: crypto.createHash('sha1').update(v.path.toLowerCase()).digest('hex').slice(0, 12),
      path: v.path,
      label: String(obj.label || path.basename(v.path) || v.path),
      isDefault: false,
    });
  };
  if (Array.isArray(raw)) raw.forEach(push);
  else if (typeof raw === 'string' && raw.trim()) push(raw);

  // 配了可用源就不再塞默认源（默认源只在“没配置/配置全无效”时补位）
  const defaults = list.filter((x) => !x.invalid);
  if (!defaults.length) {
    const v = validateSourcePath(KB_DIR);
    if (v.ok) list.unshift({ id: 'default', path: v.path, label: 'user-knowledge', isDefault: true });
    else list.unshift({ id: 'default', path: KB_DIR, label: 'user-knowledge', isDefault: true });
  }
  return list;
}

// ── 枚举 ────────────────────────────────────────────────────────────────
function listFiles(src, stats) {
  const out = [];
  const bump = (k) => { if (stats) stats[k] = (stats[k] || 0) + 1; };
  const walk = (dir, rel, depth) => {
    if (depth > LIMITS.maxDepth || out.length >= LIMITS.maxFilesPerSource) return;
    let ents;
    try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (_) { return; }
    for (const e of ents) {
      if (out.length >= LIMITS.maxFilesPerSource) return;
      if (e.name.startsWith('_') || e.name.startsWith('.')) { bump('hidden'); continue; }   // _index.md / .cache / 隐藏文件
      const full = path.join(dir, e.name);
      const r = rel ? rel + '/' + e.name : e.name;
      let st;
      try { st = fs.lstatSync(full); } catch (_) { continue; }
      if (st.isSymbolicLink()) continue;                                  // 不跟符号链接（防环/越界）
      if (st.isDirectory()) { walk(full, r, depth + 1); continue; }
      if (!st.isFile()) { bump('other'); continue; }
      if (SENSITIVE_RE.test(full)) { bump('sensitive'); continue; }       // 敏感文件名一律跳过
      const ext = path.extname(e.name).toLowerCase();
      if (!TEXT_EXT.has(ext) && !OFFICE_EXT[ext]) { bump('unsupported'); continue; }
      if (st.size > LIMITS.maxFileBytes) { bump('tooLarge'); continue; }
      out.push({ full, rel: r, ext, size: st.size, mtimeMs: Math.floor(st.mtimeMs) });
    }
  };
  walk(src, '', 0);
  return out;
}

// ── 缓存 ────────────────────────────────────────────────────────────────
function cacheDirFor(srcId) {
  const d = path.join(CACHE_ROOT, srcId);
  fs.mkdirSync(d, { recursive: true });
  return d;
}

function cachePathFor(srcId, rel) {
  return path.join(cacheDirFor(srcId), encodeURIComponent(rel) + '.txt');
}

function readManifest() {
  try { return JSON.parse(fs.readFileSync(MANIFEST_FILE, 'utf8')); } catch (_) { return {}; }
}
function writeManifest(m) {
  try {
    fs.mkdirSync(CACHE_ROOT, { recursive: true });
    fs.writeFileSync(MANIFEST_FILE, JSON.stringify(m, null, 1), 'utf8');
  } catch (_) { /* manifest 失败不影响主流程 */ }
}

// ── 文本抽取 ────────────────────────────────────────────────────────────
async function extractText(file, signal) {
  if (TEXT_EXT.has(file.ext)) {
    return { text: fs.readFileSync(file.full, 'utf8'), ocr: false };
  }
  const cmd = OFFICE_EXT[file.ext];
  if (!cmd) throw new Error('不支持的扩展名 ' + file.ext);
  if (!deps.cadCall) throw new Error('CAD 通道不可用（office/pdf 抽取需要插件在线）');
  let text = '';
  let thin = false;
  let err = '';
  try {
    const pr = await deps.cadCall(cmd, { path: file.full, maxChars: 200000, maxResultBytes: 0 }, 60000, signal);
    if (pr && pr.error) {
      if (file.ext === '.pdf') thin = true; else err = pr.error.message || pr.error.code;
    } else {
      const res = (pr && pr.result) || {};
      text = (typeof res.content === 'string') ? res.content : JSON.stringify(res || '');
      if (file.ext === '.pdf') {
        const pages = Number(res.pages) || 0;
        thin = text.trim().length < 60 || (pages > 0 && text.trim().length / pages < 30);
      }
    }
  } catch (e) { err = e.message; }

  if (file.ext === '.pdf' && thin) {
    try {
      const { ocrFile } = require('./ocr.js');
      const oc = await ocrFile(file.full, { maxPages: 60 });
      // 2026-10-19: 不能早返回——要落到下面的 thin 守卫（18 字符的图形 PDF 曾因此混进索引）
      if (oc.ok && oc.chars > text.trim().length) { text = oc.text; usedOcr = true; err = ''; }
      if (!oc.ok) err = '无文本层，OCR 失败 ' + oc.error + (oc.hint ? ('（' + oc.hint + '）') : '');
      else err = '';
    } catch (e2) { err = 'OCR 异常 ' + e2.message; }
  }
  if (err && !text.trim()) throw new Error(err);
  // 2026-10-19: 图形类 PDF（CAD 导出）常常只抽到个位数/十几个字符 —— 别塞进索引冒充"已入库"
  if ((text || '').trim().length < 20) {
    throw new Error('几乎无文本（' + (text || '').trim().length + ' 字符）——疑似图形/扫描件，建议走图纸解析或 OCR');
  }
  return { text, ocr: false };
}

function headLines(text) {
  return text.split('\n')
    .filter((l) => /^\s*(#{1,4}\s|第[一二三四五六七八九十百0-9]+[章节条]|[0-9]+(\.[0-9]+)*[、.\s])/.test(l))
    .map((l) => l.trim().slice(0, 120))
    .slice(0, 8);
}

// ── 摄取（主入口）────────────────────────────────────────────────────────
async function ingestAll(signal) {
  const sources = getSources();
  const manifest = readManifest();
  const report = { sources: [], files: 0, reused: 0, extracted: 0, skipped: 0, errors: [] };
  const entries = [];

  for (const src of sources) {
    if (src.invalid) { report.sources.push({ label: src.label, path: src.path, ok: false, error: src.invalid }); report.errors.push(src.path + ': ' + src.invalid); continue; }
    const stat = { label: src.label, path: src.path, ok: true, files: 0, reused: 0, extracted: 0, errors: [] };
    if (!fs.existsSync(src.path)) { stat.ok = false; stat.error = '目录不存在'; report.sources.push(stat); report.errors.push(src.path + ': 目录不存在'); continue; }

    const skipStats = {};
    const files = listFiles(src.path, skipStats);
    stat.files = files.length;
    stat.skipped = skipStats;
    const mKey = manifest[src.id] = manifest[src.id] || {};

    for (const f of files) {
      const key = f.rel;
      const prev = mKey[key];
      const cp = cachePathFor(src.id, f.rel);
      const unchanged = prev && prev.mtimeMs === f.mtimeMs && prev.size === f.size && fs.existsSync(cp);
      let text = '';
      if (unchanged) {
        try {
          text = fs.readFileSync(cp, 'utf8');
          if (text.trim().length < 20 && (f.ext === '.pdf' || OFFICE_EXT[f.ext])) {
            // 2026-10-19: 缓存里是"几乎无文本"的垃圾（图形 PDF）→ 作废，重新抽取走 thin 守卫
            try { fs.unlinkSync(cp); } catch (_) { /* 删不掉就算了 */ }
            text = '';
          } else { stat.reused++; report.reused++; }
        } catch (_) { text = ''; }
      }
      if (!text) {
        if (signal && signal.aborted) throw new Error('任务已取消');
        try {
          const ex = await extractText(f, signal);
          text = ex.text || '';
          fs.writeFileSync(cp, text, 'utf8');
          mKey[key] = { mtimeMs: f.mtimeMs, size: f.size, ocr: !!ex.ocr, at: Date.now() };
          stat.extracted++; report.extracted++;
        } catch (e) {
          stat.errors.push(f.rel + ': ' + e.message);
          report.errors.push(src.label + '/' + f.rel + ': ' + e.message);
          continue;
        }
      }
      report.files++;
      entries.push({ source: src.label, path: src.path, rel: f.rel, ext: f.ext, bytes: Buffer.byteLength(text, 'utf8'), heads: headLines(text) });
    }
    report.sources.push(stat);
  }
  writeManifest(manifest);

  // 索引（分源、限量）
  const lines = ['# 用户知识库索引（自动生成，勿手改）', '',
    '> 多源只读引用：源目录不进副本，只有派生缓存。检索用 searchKnowledge；取详情用 readFile。', ''];
  const perSource = {};
  for (const e of entries) {
    perSource[e.source] = perSource[e.source] || [];
    if (perSource[e.source].length < LIMITS.maxIndexPerSource) perSource[e.source].push(e);
  }
  let chars = lines.join('\n').length;
  for (const src of report.sources) {
    const items = perSource[src.label] || [];
    const sk = src.skipped || {};
    const skTotal = Object.values(sk).reduce((x, y) => x + y, 0);
    const skText = skTotal ? '，跳过 ' + skTotal + '（' + Object.keys(sk).map(k => ({ tooLarge: '超限', unsupported: '类型不支持', sensitive: '敏感', hidden: '隐藏', other: '其它' }[k] || k) + ' ' + sk[k]).join('、') + '）' : '';
    // 2026-10-19: 这里要写"入库"数（items），不是"扫到几个支持类型"——否则会显示 2 篇可解析却只列 1 条
    const header = '## ' + src.label + '  (' + src.path + ', ' + items.length + ' 篇入库' + skText + (src.ok ? '' : '，不可用') + ')';
    if (chars + header.length > LIMITS.indexMaxChars) break;
    lines.push(header); chars += header.length;
    for (const e of items) {
      const row = '- ' + e.rel + '  [' + e.ext + ', ' + e.bytes + 'B]' + (e.heads.length ? '：' + e.heads[0] : '');
      if (chars + row.length > LIMITS.indexMaxChars) { lines.push('- …（索引已截断，用 searchKnowledge 检索）'); chars = Infinity; break; }
      lines.push(row); chars += row.length;
    }
    lines.push('');
  }
  const indexText = lines.join('\n');
  try {
    fs.mkdirSync(KB_DIR, { recursive: true });
    fs.writeFileSync(INDEX_FILE, indexText, 'utf8');
  } catch (e) { throw new Error('写索引失败: ' + e.message); }

  report.entries = entries.length;
  report.indexChars = indexText.length;
  return report;
}

// ── 读取 ────────────────────────────────────────────────────────────────
function indexText(limit) {
  try {
    if (!fs.existsSync(INDEX_FILE)) return '';
    return fs.readFileSync(INDEX_FILE, 'utf8').slice(0, limit || 1500);
  } catch (_) { return ''; }
}

function sourcesSummary() {
  return getSources().map((s) => ({ label: s.label, path: s.path, isDefault: !!s.isDefault, invalid: s.invalid || null }));
}

// ── 检索 ────────────────────────────────────────────────────────────────
function search(args) {
  const q = String(args.query || '').trim();
  if (!q) throw new Error('query 必填');
  let re;
  try { re = new RegExp(q, 'i'); } catch (_) { re = new RegExp(q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'); }
  const maxM = Math.max(1, parseInt(args.maxMatches, 10) || 100);
  const sources = getSources().filter((s) => !s.invalid);
  const docs = [];
  for (const src of sources) {
    const cd = path.join(CACHE_ROOT, src.id);
    let hasCache = false;
    try {
      if (fs.existsSync(cd)) {
        for (const n of fs.readdirSync(cd)) {
          if (!n.endsWith('.txt')) continue;
          try { docs.push({ source: src.label, rel: decodeURIComponent(n.replace(/\.txt$/, '')), text: fs.readFileSync(path.join(cd, n), 'utf8') }); hasCache = true; } catch (_) { /* 跳过不可读缓存 */ }
        }
      }
    } catch (_) { /* 缓存目录不可读 */ }
    if (!hasCache) {
      // 没摄取过：文本类直读兜底（仍然只读）
      for (const f of listFiles(src.path)) {
        if (!TEXT_EXT.has(f.ext)) continue;
        try { docs.push({ source: src.label, rel: f.rel, text: fs.readFileSync(f.full, 'utf8') }); } catch (_) { /* 跳过 */ }
      }
    }
  }
  const hits = [];
  for (const d of docs) {
    const lines = d.text.split('\n');
    for (let i = 0; i < lines.length && hits.length < maxM; i++) {
      if (re.test(lines[i])) hits.push({ source: d.source, file: d.rel, line: i + 1, text: lines[i].trim().slice(0, 200) });
    }
    if (hits.length >= maxM) break;
  }
  return { query: q, sources: sources.map((s) => s.label), matchCount: hits.length, capped: hits.length >= maxM, hits };
}

module.exports = {
  configure,
  getSources,
  sourcesSummary,
  listFiles,
  ingestAll,
  indexText,
  search,
  validateSourcePath,
  LIMITS,
  DEFAULT_DIR,
  get CACHE_ROOT() { return CACHE_ROOT; },
  get INDEX_FILE() { return INDEX_FILE; },
  get KB_DIR() { return KB_DIR; },
};
