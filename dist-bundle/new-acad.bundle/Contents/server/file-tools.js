// file-tools.js — 内置 agent 本地文件读写工具（安全边界版 v3）
// 2026-08-13 重构（Bro 拍板方案）:
//   - readFile/readImage/listDir: 读全放开（任意本地路径），仅敏感黑名单拦截
//   - writeFile/deleteFile: 只允许 <项目根>\exchange\agent-work\ 内（AI 自己的工作区）——可新建/覆盖/删除
//   - 目录外写入/修改/删除 → 拒绝（08-06 越权教训的物理兜底：AI 改不了用户任何文件）
//   - 扩展名黑名单: 禁写可执行/脚本文件
// 2026-08-06 初版: readFile 限 exchange/ + writeFile 只新建 + 无删除（v2 于 08-07 加敏感黑名单）

'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

// 禁写的扩展名（防止被诱导写入可执行/脚本文件）
const BLOCKED_EXT = new Set([
  '.exe', '.bat', '.cmd', '.ps1', '.dll', '.scr', '.vbs', '.js', '.mjs', '.cjs',
  '.sh', '.py', '.com', '.msi', '.reg', '.wsf', '.jar',
]);

// 读/写/上下文大小上限 —— 2026-10-13: 改为 config.json `limits.*` 可配（缺省用下列默认值）
const LIMIT_DEFAULTS = {
  readMaxBytes: 5 * 1024 * 1024,     // readFile/readImage 原始文件上限
  imageMaxB64: 1.5 * 1024 * 1024,    // readImage 编码后 base64 字符上限(≈1.1MB 原图)
  writeMaxBytes: 2 * 1024 * 1024,    // writeFile 单次内容上限
  readMaxChars: 16000,               // readFile 分页默认单次字符额度
  readMaxCharsCap: 32000,            // readFile 分页硬上限
  spillBytes: 8000,                  // 工具结果外溢阈值（字符）
  spillPreview: 1200,                // 外溢预览字符数
  ctxMaxBytes: 250000,               // 上下文硬上限（llm-agent 工具循环）
  historyKeep: 24,                   // 对话历史滚动窗口（条）
};
function loadRawConfig() {
  try {
    const cfgPath = path.join(__dirname, '..', 'config.json');
    if (fs.existsSync(cfgPath)) return JSON.parse(fs.readFileSync(cfgPath, 'utf8')) || {};
  } catch (e) { console.error('[CFG] 读取 config.json 失败（限额回落默认）: ' + e.message); }
  return {};
}
// 读配置限额：config.json 顶层 `limits` 对象内数值字段覆盖默认值（非法/缺失回落默认）
function getLimits() {
  const raw = loadRawConfig();
  const l = (raw.limits && typeof raw.limits === 'object') ? raw.limits : {};
  const out = Object.assign({}, LIMIT_DEFAULTS);
  for (const k of Object.keys(LIMIT_DEFAULTS)) { const v = Number(l[k]); if (Number.isFinite(v) && v >= 0) out[k] = v; }
  return out;
}
// 读/写大小上限（防撑爆磁盘/内存）

// 敏感路径黑名单（2026-08-13 增强，与插件 FileBoundary 对齐）：
// L1 配置/密钥/凭据/版本库；L2 系统敏感区（SAM/影子密码/浏览器登录态）
const SENSITIVE_FILE = new RegExp(
  '(^|[\\\\/])(config\\.json|appsettings\\.json|web\\.config|\\.npmrc|\\.piprc|secrets\\.json|\\.env(\\.[a-z0-9_]+)?)([\\\\/]|$)' +
  '|\\.(pem|key|pfx|p12|p8|crt)$' +
  '|(^|[\\\\/])id_(rsa|dsa|ecdsa|ed25519)([\\\\/]|$)' +
  '|(^|[\\\\/])\\.(git|svn|hg)([\\\\/]|$)' +
  '|(^|[\\\\/])(\\.aws|gcloud|azure|credentials|credentials\\.json)([\\\\/]|$)' +
  '|(^|[\\\\/])\\.kube([\\\\/]|$)' +
  // 2026-08-14 P1-5: 关键词按文件名段+词边界匹配（防 author-notes/token-map 误伤）; 允许 _ 后缀 + 办公扩展名
  '|(^|[\\\\/])(token|secret|password|passwd|shadow|api[_-]?key|apikey|auth|credentials)(?:_[a-z0-9]+)?(\\.(json|ya?ml|txt|ini|conf|cfg|key|pem|csv|xlsx?|docx?))?([\\\\/]|$)' +
  '|(^|[\\\\/])system32[\\\\/]config([\\\\/]|$)|shadow$|^passwd$' +
  '|(^|[\\\\/])(Cookies|Login Data|Local State)([\\\\/]|$)',
  'i');

// ---------- AI 工作区（唯一可写/可删目录；2026-08-17: 按项目分目录） ----------
function getAgentWorkDir() {
  const projectRoot = path.resolve(__dirname, '..');
  // 读 config.json 的 project 字段（用户显式声明的项目名，空=全局）
  let project = '';
  try {
    const cfgPath = path.join(projectRoot, 'config.json');
    if (fs.existsSync(cfgPath)) {
      const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
      if (cfg && typeof cfg.project === 'string') project = cfg.project.trim();
    }
  } catch (e) { console.error('[CFG] 读取 config.json 项目字段失败（回落全局工作区）: ' + e.message); }   // 2026-10-10 B7: 不再静默
  let dir;
  if (project) {
    const safe = String(project).replace(/[\\/:*?"<>|]/g, '_').trim() || 'unnamed';
    dir = path.join(projectRoot, 'exchange', 'projects', safe, 'agent-work');
  } else {
    dir = path.join(projectRoot, 'exchange', 'agent-work');
  }
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function isWithin(dir, target) {
  const rel = path.relative(dir, target);
  return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
}

// ---------- 路径解析 ----------
// 读: 全盘放开，仅敏感黑名单拦截
function resolveReadPath(rawPath, needExisting) {
  const p = path.resolve(rawPath);
  if (SENSITIVE_FILE.test(p)) {
    throw new Error('安全红线: 禁止访问敏感文件（配置/密钥/凭据/版本库类）: ' + p);
  }
  if (needExisting && !fs.existsSync(p)) {
    throw new Error('文件不存在: ' + p + '（请确认路径正确）');
  }
  return p;
}

// 写/删: 仅限 AI 工作区（物理隔离，用户文件不可动）
function resolveWritePath(rawPath) {
  const p = path.resolve(rawPath);
  const work = getAgentWorkDir();
  if (!isWithin(work, p)) {
    throw new Error('安全红线: 只允许在 AI 工作区 {{WORKDIR}} 内创建/修改/删除文件，不能操作其他位置: ' + p +
      '（用户文件请走插件导出通道，或让用户指定保存位置）');
  }
  return p;
}

// ---------- readFile（2026-10-13 P0: 分页 + outline；大文件不再被静默截断） ----------

function readFile(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveReadPath(rawPath, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);
  const stat = fs.statSync(p);
  if (stat.size > getLimits().readMaxBytes) throw new Error('文件过大（>' + (getLimits().readMaxBytes / 1024 / 1024) + 'MB），拒绝读取');
  const raw = fs.readFileSync(p, 'utf8');
  const allLines = raw === '' ? [] : raw.split('\n');
  const totalLines = allLines.length;

  // outline: 只回结构预览（大文件先看结构，再决定读哪段 / 是否 queryFile）
  if (args.outline === true || args.outline === 1) {
    const headN = Math.min(totalLines, 50);
    const out = {
      path: p, size: stat.size, totalLines, outline: true,
      head: allLines.slice(0, headN).join('\n').slice(0, getLimits().readMaxChars),
      hint: '结构预览（前 ' + headN + ' 行）。读正文请用 offset/limit 分页，或 queryFile 聚合。'
    };
    const first = (allLines[0] || '').trim();
    if (first.includes(',')) out.detected = { kind: 'csv', columns: first.split(',').length, header: first.slice(0, 300) };
    else if (/^[\[{]/.test(raw.trim().slice(0, 1)) && raw.length < 2 * 1024 * 1024) {
      try {
        const j = JSON.parse(raw);
        out.detected = Array.isArray(j)
          ? { kind: 'json', topType: 'array', length: j.length }
          : { kind: 'json', topType: 'object', keys: Object.keys(j).slice(0, 50) };
      } catch (_) { /* 非整体 JSON（可能是 NDJSON/JSONL） */ }
    }
    return out;
  }

  const offset = Math.max(1, parseInt(args.offset, 10) || 1);
  const limit = Math.max(0, parseInt(args.limit, 10) || 0);
  let maxChars = parseInt(args.maxChars, 10) || getLimits().readMaxChars;
  maxChars = Math.min(Math.max(maxChars, 1), getLimits().readMaxCharsCap);

  const start = Math.min(offset, totalLines + 1);
  const end = limit > 0 ? Math.min(totalLines, start + limit - 1) : totalLines;
  let body = start <= totalLines ? allLines.slice(start - 1, end).join('\n') : '';
  let truncatedByChars = false;
  if (body.length > maxChars) { body = body.slice(0, maxChars); truncatedByChars = true; }
  const got = body.length ? body.split('\n').length : 0;
  const to = start <= totalLines ? Math.min(end, start + got - 1) : start - 1;
  const hasMore = to < totalLines;
  return {
    path: p, size: stat.size, totalLines,
    from: start, to, hasMore, nextOffset: hasMore ? to + 1 : null,
    content: body, truncatedByChars,
    hint: start <= totalLines
      ? ('内容将发送给 AI 模型服务商处理。本次 ' + start + '-' + to + ' 行（共 ' + totalLines + ' 行）。' +
         (hasMore ? '继续读请传 offset=' + (to + 1) + '（可加 limit/maxChars）。' : '已到文件末尾。'))
      : ('offset ' + offset + ' 超出文件行数（共 ' + totalLines + ' 行）。')
  };
}

// ---------- queryFile（2026-10-13 P0: 本地聚合/结构化读 —— 大文件的“正路径”） ----------
//   原文不进上下文，只进结论。op: head|tail|sample|wc|grep|groupby|stats|json
function queryFile(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const op = String(args.op || 'wc').toLowerCase();
  const p = resolveReadPath(rawPath, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);
  const stat = fs.statSync(p);
  if (stat.size > getLimits().readMaxBytes) throw new Error('文件过大（>' + (getLimits().readMaxBytes / 1024 / 1024) + 'MB），拒绝读取');
  const raw = fs.readFileSync(p, 'utf8');
  const lines = raw === '' ? [] : raw.split('\n');
  const cap = Math.min(Math.max(parseInt(args.maxChars, 10) || getLimits().readMaxChars, 1), getLimits().readMaxCharsCap);
  const cut = (s) => (typeof s === 'string' && s.length > cap) ? s.slice(0, cap) + '\n…（已截断，缩小范围/加参数再查）' : s;
  const asRows = () => {
    const delim = String(args.delimiter || ',');
    const rows = lines.filter(l => l.trim() !== '').map(l => l.split(delim));
    const header = args.header === false ? [] : (rows.shift() || []);
    return { header, rows };
  };
  const colIdx = (header, c) => {
    if (c == null) return 0;
    if (/^\d+$/.test(String(c))) return parseInt(c, 10);
    const i = header.indexOf(String(c));
    return i >= 0 ? i : 0;
  };

  if (op === 'head') return { path: p, op, totalLines: lines.length, content: cut(lines.slice(0, Math.max(1, parseInt(args.limit, 10) || 20)).join('\n')) };
  if (op === 'tail') return { path: p, op, totalLines: lines.length, content: cut(lines.slice(-Math.max(1, parseInt(args.limit, 10) || 20)).join('\n')) };
  if (op === 'sample') {
    const n = Math.max(1, parseInt(args.limit, 10) || 10);
    const step = Math.max(1, Math.floor(lines.length / n));
    const out = [];
    for (let i = 0; i < lines.length && out.length < n; i += step) out.push(lines[i]);
    return { path: p, op, totalLines: lines.length, content: cut(out.join('\n')) };
  }
  if (op === 'wc') return { path: p, op, lines: lines.length, nonEmptyLines: lines.filter(l => l.trim() !== '').length, chars: raw.length, bytes: Buffer.byteLength(raw, 'utf8') };
  if (op === 'grep') {
    const pattern = String(args.pattern || args.query || '');
    if (!pattern) throw new Error('grep 需要 pattern 参数');
    let re;
    try { re = new RegExp(pattern, args.ignoreCase === false ? '' : 'i'); } catch (e) { throw new Error('正则无效: ' + e.message); }
    const maxM = Math.max(1, parseInt(args.maxMatches, 10) || 100);
    const hits = [];
    for (let i = 0; i < lines.length && hits.length < maxM; i++) if (re.test(lines[i])) hits.push((i + 1) + ': ' + lines[i]);
    return { path: p, op, pattern, totalLines: lines.length, matchCount: hits.length, capped: hits.length >= maxM, content: cut(hits.join('\n')) };
  }
  if (op === 'groupby') {
    const { header, rows } = asRows();
    const ci = colIdx(header, args.by != null ? args.by : args.column);
    const agg = args.agg ? String(args.agg).toLowerCase() : 'count';
    const vci = colIdx(header, args.value != null ? args.value : args.by);
    const g = new Map();
    for (const r of rows) {
      const k = String(r[ci] == null ? '' : r[ci]).trim();
      let cur = g.get(k);
      if (!cur) { cur = { key: k, count: 0, sum: 0, nums: 0 }; g.set(k, cur); }
      cur.count++;
      if (agg !== 'count') { const v = Number(r[vci]); if (Number.isFinite(v)) { cur.sum += v; cur.nums++; } }
    }
    const arr = [...g.values()].map(x => agg === 'count' ? { key: x.key, count: x.count }
      : agg === 'sum' ? { key: x.key, count: x.count, sum: Math.round(x.sum * 1e6) / 1e6 }
      : { key: x.key, count: x.count, avg: x.nums ? Math.round((x.sum / x.nums) * 1e6) / 1e6 : null });
    arr.sort((a, b) => b.count - a.count);
    return { path: p, op, by: header[ci] != null ? header[ci] : ci, agg, groupCount: arr.length, groups: arr.slice(0, Math.max(1, parseInt(args.limit, 10) || 50)) };
  }
  if (op === 'stats') {
    const { header, rows } = asRows();
    const ci = colIdx(header, args.column);
    let n = 0, sum = 0, mn = Infinity, mx = -Infinity;
    for (const r of rows) { const v = Number(r[ci]); if (Number.isFinite(v)) { n++; sum += v; if (v < mn) mn = v; if (v > mx) mx = v; } }
    return { path: p, op, column: header[ci] != null ? header[ci] : ci, rows: rows.length, numeric: n,
      min: n ? mn : null, max: n ? mx : null, sum: n ? Math.round(sum * 1e6) / 1e6 : null, avg: n ? Math.round((sum / n) * 1e6) / 1e6 : null };
  }
  if (op === 'json') {
    let j;
    try { j = JSON.parse(raw); } catch (e) { throw new Error('JSON 解析失败: ' + e.message); }
    const jp = args.jsonPath || args.key;
    let val = j;
    if (jp) for (const seg of String(jp).replace(/^\$?\.?/, '').split('.')) { if (seg === '') continue; if (val == null) break; val = val[seg]; }
    if (val == null) return { path: p, op, jsonPath: jp || null, value: null };
    if (Array.isArray(val)) return { path: p, op, jsonPath: jp || null, type: 'array', length: val.length, preview: cut(JSON.stringify(val.slice(0, 20))) };
    if (typeof val === 'object') return { path: p, op, jsonPath: jp || null, type: 'object', keys: Object.keys(val).slice(0, 100) };
    return { path: p, op, jsonPath: jp || null, value: val };
  }
  throw new Error('未知 op：' + op + '（可用 head/tail/sample/wc/grep/groupby/stats/json）');
}

// ---------- readImage ----------
const IMAGE_EXT = { '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.bmp': 'image/bmp', '.gif': 'image/gif', '.webp': 'image/webp', '.tif': 'image/tiff', '.tiff': 'image/tiff' };   // 2026-10-10 D-048: 补 tif/tiff
function readImage(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveReadPath(rawPath, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);
  const ext = path.extname(p).toLowerCase();
  const mime = IMAGE_EXT[ext];
  if (!mime) throw new Error('仅支持图片文件（png/jpg/jpeg/bmp/gif/webp）: ' + ext);
  const stat = fs.statSync(p);
  const L = getLimits();
  if (stat.size > L.readMaxBytes) throw new Error('图片过大（>' + (L.readMaxBytes / 1024 / 1024) + 'MB），拒绝读取');
  const base64 = fs.readFileSync(p).toString('base64');
  // 2026-09-15: 单图编码体积硬约束（图片是请求体最大的一块）。
  // 2026-10-13 P2: 超限**不再硬拒、不自动降损**——返回“需用户决策”结构，由 AI 把选项交给用户（有损决策归用户）。
  if (base64.length > L.imageMaxB64) {
    if (args.maxQuality === true || args.maxQuality === 1) {
      throw new Error('图片过大（编码后 ' + (base64.length / 1024 / 1024).toFixed(1) + 'MB，上限 ' + (L.imageMaxB64 / 1024 / 1024).toFixed(1) + 'MB）。已按 maxQuality 原样拒绝（不压缩）。');
    }
    return {
      needsDecision: true,
      path: p,
      size: stat.size,
      encodedBytes: base64.length,
      limitBytes: L.imageMaxB64,
      options: [
        { id: 'resize', how: 'readImage({path, resize:{maxSide:1600}})', desc: '压缩后查看（有损、保整体）' },
        { id: 'crop', how: 'readImage({path, crop:{x,y,w,h}})', desc: '只看局部高清（保原分辨率）' },
        { id: 'manual', desc: '用户自己压小/裁剪后再给路径' }
      ],
      note: '图片编码后 ' + (base64.length / 1024 / 1024).toFixed(1) + 'MB，超过上限 ' + (L.imageMaxB64 / 1024 / 1024).toFixed(1) + 'MB。请把选项（压缩看 / 局部高清 / 用户自处理）交用户决定；勿自动压缩——有损决策归用户。'
    };
  }
  return { path: p, size: stat.size, mime, base64 };
}

// ---------- listDir（2026-08-13 新增：AI 看文件夹有什么） ----------
function listDir(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveReadPath(rawPath, true);
  if (!fs.statSync(p).isDirectory()) throw new Error('这是文件，不是目录: ' + p);
  const entries = fs.readdirSync(p, { withFileTypes: true });
  const dirs = [], files = [];
  for (const e of entries) {
    const full = path.join(p, e.name);
    // 敏感条目过滤（列了也读不了，且可能诱导）
    if (SENSITIVE_FILE.test(full)) continue;
    if (e.isDirectory()) dirs.push({ name: e.name, dir: true });
    else if (e.isFile()) files.push({ name: e.name, size: fs.statSync(full).size, dir: false });
  }
  return { path: p, dirs, files, count: dirs.length + files.length };
}

// ---------- writeFile（仅 AI 工作区，可新建/覆盖） ----------
function writeFile(args) {
  const rawPath = String(args.path || '').trim();
  const content = String(args.content ?? '');
  if (!rawPath) throw new Error('参数 path 必填');
  if (content.length === 0 && !args.content) throw new Error('参数 content 必填（可为空字符串，但需显式传入）');
  if (Buffer.byteLength(content, 'utf8') > getLimits().writeMaxBytes) {
    throw new Error('内容过大（>' + (getLimits().writeMaxBytes / 1024 / 1024) + 'MB），拒绝写入');
  }
  const ext = path.extname(rawPath).toLowerCase();
  if (BLOCKED_EXT.has(ext)) {
    throw new Error('安全红线: 不允许写入 ' + ext + ' 类型文件（可执行/脚本文件禁写）');
  }
  const p = resolveWritePath(rawPath);
  const existed = fs.existsSync(p);
  const append = args.append === true || args.append === 1 || String(args.mode || '').toLowerCase() === 'append';
  fs.mkdirSync(path.dirname(p), { recursive: true });
  if (append) fs.appendFileSync(p, content, 'utf8');
  else fs.writeFileSync(p, content, 'utf8');
  return { path: p, created: !existed, overwritten: existed && !append, appended: append, bytes: Buffer.byteLength(content, 'utf8') };
}

// ---------- deleteFile（2026-08-13 新增：仅 AI 工作区） ----------
function deleteFile(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveWritePath(rawPath);
  if (!fs.existsSync(p)) throw new Error('文件不存在: ' + p);
  fs.unlinkSync(p);
  return { path: p, deleted: true };
}

// ---------- traceImage（2026-09-22 新增：位图 → CAD 矢量线条） ----------
// 纯 Node 实现（server/trace-image.js），不依赖 Python/外部工具。
// draw=true 时自己循环调插件 createPolyline 落地（无需模型传大数据、无需插件新增方法）。
function num(v, d) { const n = Number(v); return Number.isFinite(n) ? n : d; }

function traceImage(args) {
  const raw = String(args.image || args.path || '').trim();
  if (!raw) throw new Error('参数 image 必填（图片绝对路径）');
  const p = resolveReadPath(raw, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);

  const MODES = ['flat', 'arc', 'centerline', 'outline', 'posterize', 'line', 'tone'];
  const engine = require('./trace-image.js');
  // 2026-09-22: 未指定 mode 时自动判定——扁平/纯色插画（卡通、logo、矢量壁纸）必须走 flat，
  // 否则 DoG+骨架管线会把色块边缘碎成一堆小圈（实测：同一张 AT 壁纸 flat 6 条 vs arc 83 条）
  let autoMode = null, kindInfo = null;
  if (!args.mode) {
    try { const _im = engine.loadImage(p); kindInfo = engine.detectImageKind(_im); autoMode = kindInfo.kind === 'flat' ? 'flat' : 'auto'; }
    catch (_) { autoMode = 'auto'; }
  }
  const mode = String(args.mode || autoMode || 'arc').toLowerCase();
  if (!MODES.includes(mode) && mode !== 'auto') throw new Error('mode 必须是 auto|' + MODES.join('|') + '（收到 ' + mode + '）');

  const layer = args.layer ? String(args.layer) : ('TRACE_' + (mode === 'auto' ? 'TMP' : mode.toUpperCase()));
  const opts = {
    input: p, mode,
    simplify: num(args.simplify, 1.2), arcThreshold: num(args.arcThreshold, 0.02),
    minLength: (args.minLength == null ? null : num(args.minLength, 12)), bridge: num(args.bridge, 6),
    scaleTo: num(args.scaleTo, 100), offsetX: num(args.offsetX, 0), offsetY: num(args.offsetY, 0),
    layer, posterizeLevels: num(args.levels, 3), speck: num(args.speck, 12), close: num(args.close, 1),
    // flat 模式参数（2026-09-22）
    flatLevels: num(args.levels, 4), flatMinArea: num(args.minArea, 0.05), flatMinColor: num(args.minColor, 1),
    keepBackground: args.keepBackground === true || args.keepBackground === 1,
    arcize: args.arcize === false ? false : true, maxWorkSide: num(args.maxSide, 1600),
    // 2026-09-23: 掩膜来源 —— auto=墨线稿/扫描件自动走墨迹阈值,照片/彩图走 DoG 边缘
    maskMode: ['auto', 'dog', 'ink'].includes(String(args.mask || 'auto').toLowerCase()) ? String(args.mask || 'auto').toLowerCase() : 'auto',
    inkGamma: num(args.inkGamma, 1),   // ink 掩膜提亮指数（默认 1=不变；0.5=更干净但浅排线更狠）
    toneLevels: num(args.levels, 4), toneMinArea: num(args.minArea, 0.15),   // 2026-10-04: tone 档分层数/每层最小面积(%)
    toneContrast: num(args.contrast, 1), toneGamma: num(args.gamma, 1),   // 2026-10-10 D-050: 明暗对比(线性拉伸) / 伽马（k-means 前作用）
    sauvolaWindow: (args.window == null ? null : num(args.window, 0)), sauvolaK: (args.k == null ? undefined : num(args.k, 0.2))
  };
  // 2026-10-04: 可选增强开关（仅显式传参时注入，line 档在引擎侧走默认）
  if (args.adaptive !== undefined) opts.adaptive = (args.adaptive === true || args.adaptive === 1);
  if (args.autotune !== undefined) opts.autotune = (args.autotune === true || args.autotune === 1);
  if (args.denoise !== undefined) opts.denoise = (args.denoise === true || args.denoise === 1);
  const ext = path.extname(p).toLowerCase();
  // PNG/BMP 本引擎自带解码；其他格式（jpg/gif/tif/webp…）走插件 exportImageGray（GDI+ 全格式）
  const nativeOk = ext === '.png' || ext === '.bmp';
  if (!nativeOk) opts.needPluginGray = true;

  const finish = (r) => {
    const realMode = (r.stats && r.stats.mode) || mode;
    const realLayer = args.layer ? String(args.layer) : ('TRACE_' + String(realMode).toUpperCase());
    for (const it of r.items) { if (!it.layer || it.layer === layer) it.layer = realLayer; }   // 2026-10-04: 保留 tone 的逐色图层
    const base = path.basename(p).replace(/\.[^.]+$/, '').slice(0, 40).replace(/[^\w.\u4e00-\u9fa5-]/g, '_');
    const outPath = resolveWritePath(path.join(getAgentWorkDir(), 'trace-' + base + '-' + realMode + '.json'));
    fs.mkdirSync(path.dirname(outPath), { recursive: true });
    fs.writeFileSync(outPath, JSON.stringify(r), 'utf8');
    const res = {
      ok: true, image: p, mode: realMode, layer: realLayer, json: outPath, size: fs.statSync(outPath).size,
      stats: r.stats,
      autoMode: (r.stats && r.stats.autoMode) || autoMode || undefined, imageKind: kindInfo || undefined,
    };
    // 2026-10-04: tone 档默认『轮廓+填充』（可用 fill:false 关掉）
    const toneFill = String(realMode).toLowerCase() === 'tone' && args.fill !== false && args.fill !== 0;
    const fillWant = args.fill === true || args.fill === 1 || toneFill;
    if (args.draw === true || args.draw === 1) return drawItems(r.items, realLayer, res, { fill: fillWant, pattern: args.fillPattern });
    res.note = '已生成矢量路径 JSON（未画入 CAD）。要落地请再调 traceImage 同参数 + draw:true。';
    return res;
  };

  if (!opts.needPluginGray) {
    // 2026-10-10 D-047: 本引擎解码失败（隔行 PNG / 16bit 等）→ 自动回退插件解码
    try { return finish(engine.traceImage(opts)); }
    catch (e) {
      console.error('[TRACE] 本引擎解码失败，回退插件解码：' + e.message);
      opts.needPluginGray = true;
    }
  }

  // 非 PNG/BMP（或本引擎解不了）：让插件解码；**同时取彩色**（2026-10-10 D-046，flat/region/posterize/tone 需真实颜色）
  return (async () => {
    const cadCall = require('./cad-tools.js').cadCall;
    // 2026-10-04: 必须带 fullResult:true —— 插件 C2 大结果降级（默认阈值 64KB）会把 gray base64 当"最长字符串"截断
    // → inflate 报"数据不完整"，非 PNG/BMP（JPEG/GIF/TIFF）描摹一律失败。fullResult 是插件设计的逃生门。
    const gr = await cadCall('exportImageGray', { path: p, maxSide: num(args.maxSide, 1400), withColor: true, fullResult: true }, 60000);
    const meta = gr && gr.result ? gr.result : gr;
    if (!meta || !meta.gray) throw new Error('插件 exportImageGray 未返回灰度数据：' + JSON.stringify(gr).slice(0, 160));
    if (meta._summarized) throw new Error('灰度数据被插件大结果降级截断（_summarized）——调用需带 fullResult:true');
    const buf = zlib.inflateRawSync(Buffer.from(meta.gray, 'base64'));
    if (buf.length < meta.width * meta.height) throw new Error('灰度数据长度不符（' + buf.length + ' < ' + (meta.width * meta.height) + '）');
    const grayData = new Uint8Array(buf.buffer, buf.byteOffset, meta.width * meta.height);
    let rgbIn = null;
    if (meta.rgb) {   // 2026-10-10 D-046: 插件回彩色 → flat 不再"JPG 变灰"
      try {
        const rbuf = zlib.inflateRawSync(Buffer.from(meta.rgb, 'base64'));
        if (rbuf.length >= meta.width * meta.height * 3) rgbIn = { w: meta.width, h: meta.height, data: new Uint8Array(rbuf.buffer, rbuf.byteOffset, meta.width * meta.height * 3) };
      } catch (e) { console.error('[TRACE] 彩色数据读取失败（退回灰度）：' + e.message); }
    }
    return finish(engine.traceImage({ ...opts, input: null, gray: { w: meta.width, h: meta.height, data: grayData }, rgb: rgbIn }));
  })();
}

// 批量落地能力探测（老版本插件无 importVectorPaths 时自动回退逐条）
let _hasBatch = null;
async function hasBatchImport() {
  if (_hasBatch !== null) return _hasBatch;
  try {
    const cadCall = require('./cad-tools.js').cadCall;
    const r = await cadCall('listMethods', {}, 20000);
    const arr = (r && r.result && (r.result.methods || r.result.names)) || [];
    _hasBatch = Array.isArray(arr) && arr.includes('importVectorPaths');
  } catch (_) { _hasBatch = false; }
  return _hasBatch;
}

// 落地：优先批量一次事务；否则逐条 createPolyline
// 2026-10-04: 整块 INTERNAL_ERROR 时逐项重试，跳过坏 loop
async function fillOneByOne(closed, i, CH, res, layer, fillOpts) {
  const cadCall = require('./cad-tools.js').cadCall;
  for (const it of closed.slice(i, i + CH)) {
    try {
      const rr = await cadCall('importHatches', { items: [{ loops: [{ points: it.points }], color: it.color, layer: it.layer || layer, pattern: (fillOpts && fillOpts.pattern) || undefined }], layer }, 60000);
      const m = rr && rr.result ? rr.result : null;
      if (m && m.created) res.drawn.ok += m.created; else { res.drawn.fail++; if (res.drawn.samples.length < 3) res.drawn.samples.push('bad loop @' + it.layer); }
    } catch (e) { res.drawn.fail++; if (res.drawn.samples.length < 3) res.drawn.samples.push('bad loop: ' + String(e.message).slice(0, 80)); }
  }
}
async function drawItems(items, layer, res, fillOpts) {
  const cadCall = require('./cad-tools.js').cadCall;
  const useFill = !!(fillOpts && (fillOpts.fill === true || fillOpts.fill === 1));
  res.drawn = { layer, ok: 0, fail: 0, samples: [], batched: false, mode: useFill ? 'hatch' : 'polyline' };
  // 2026-10-10: 语义澄清 —— mode='hatch' 时 ok=**填充对象数**(hatch，≈图层数)，loops=**轮廓块数**；
  //             mode='polyline' 时 ok=paths=**多段线数**。AI 报数请用 loops/paths，别把 ok 当块数（旧 bug：82 条报成 16）。
  try { await cadCall('createLayer', { name: layer, color: 3 }, 20000); } catch (_) {
    /* best-effort: the layer usually already exists; real plugin/transport problems surface in the hatch/polyline step below */
  }

  const closed = items.filter((it) => it.closed && Array.isArray(it.points) && it.points.length >= 3);   // 2026-10-10: 上提至函数级（原在 fill 分支内，批量回退处引用会 ReferenceError 被静默吞）
  if (useFill) {
    res.drawn.loops = closed.length;   // 2026-10-10: 轮廓块数（真实"块数"）
    res.drawn.layers = new Set(closed.map((it) => it.layer || layer)).size;
    // 2026-10-04: 按图层聚合为『多层环』填充（每色一层、含孔洞岛）——避免逐环 hatch 把孔洞填死、互相遮挡
    const byLayer = new Map();
    for (const it of closed) { const ln = it.layer || layer; let g = byLayer.get(ln); if (!g) { g = { color: it.color, loops: [] }; byLayer.set(ln, g); } g.loops.push(it.points); }
    const layerOrder = [...byLayer.keys()].sort((a, b) => Number((b.match(/(\d+)$/) || [0, 0])[1]) - Number((a.match(/(\d+)$/) || [0, 0])[1]));   // 浅→深
    for (const ln of layerOrder) {
      const g = byLayer.get(ln);
      try { await cadCall('createLayer', { name: ln, color: 7 }, 20000); } catch (_) {
      /* best-effort: layer likely exists already; importHatches failures are collected below */
    }
      let ok = false;
      try {
        const rr = await cadCall('importHatches', { items: [{ loops: g.loops.map((p) => ({ points: p })), color: g.color, layer: ln, pattern: fillOpts.pattern || undefined }], layer: ln }, 180000);
        const m = rr && rr.result ? rr.result : null;
        if (m) { res.drawn.ok += m.created || 0; res.drawn.fail += m.failed || 0; ok = true; }
      } catch (e) { /* 整层失败 → 逐环回退 */ if (res.drawn.samples.length < 10) res.drawn.samples.push('layer ' + ln + ' hatch failed: ' + String(e.message).slice(0, 120)); }
      if (!ok) {
        for (const p of g.loops) {
          let done = false;
          try { const rr2 = await cadCall('importHatches', { items: [{ loops: [{ points: p }], color: g.color, layer: ln }], layer: ln }, 60000); const m2 = rr2 && rr2.result ? rr2.result : null; if (m2 && m2.created) { res.drawn.ok += m2.created; done = true; } } catch (e2) {
              /* by design: the polyline fallback below handles this loop; failures are counted */
            }
          if (!done) {
            // 2026-10-04: hatch 拒不收的小环 → 改画闭合多段线（不填）
            /* 2026-10-04: 极小环不画多段线（Bro: 画蛇添足）→ 直接跳过 */ res.drawn.fail += 1;
            if (res.drawn.samples.length < 10) res.drawn.samples.push('loop dropped (' + p.length + ' pts) on ' + ln);
          }
        }
      }
    }
    res.drawn.batched = true;
    res.drawn.skipped = items.length - closed.length;   // 非闭合/点数不足被跳过者
  } else if (await hasBatchImport()) {
    const CH = 200;
    for (let i = 0; i < items.length; i += CH) {
      const chunk = items.slice(i, i + CH).map((it) => ({
        points: it.points, bulges: it.bulges, closed: !!it.closed, layer: it.layer || layer,
      }));
      try {
        const rr = await cadCall('importVectorPaths', { items: chunk, layer }, 120000);
        const m = rr && rr.result ? rr.result : null;
        if (m) {
          res.drawn.ok += m.created || 0; res.drawn.fail += m.failed || 0;
          // 2026-09-22: 插件对“不存在的图层”会静默回落到当前层（0）——这里显式提醒
          if (Array.isArray(m.layers) && m.layers.length && !m.layers.some((x) => String(x).toLowerCase() === String(layer).toLowerCase())) {
            res.drawn.layerMismatch = m.layers;
          }
        }
        else { await fillOneByOne(closed, i, CH, res, layer, fillOpts); }
      } catch (e) {
        res.drawn.fail += chunk.length;
        if (res.drawn.samples.length < 10) res.drawn.samples.push(String(e.message).slice(0, 120));
      }
    }
    res.drawn.batched = true;
    res.drawn.paths = items.length;   // 2026-10-10: 多段线模式下的图形数
  } else {
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      const params = { points: it.points.map((q) => ({ x: q[0], y: q[1] })), closed: !!it.closed, layer };
      if (it.bulges && it.bulges.length) params.bulges = it.bulges;
      try {
        const rr = await cadCall('createPolyline', params, 30000);
        if (rr && rr.result && !rr.error) res.drawn.ok++;
        else { res.drawn.fail++; if (res.drawn.samples.length < 10) res.drawn.samples.push(JSON.stringify((rr && rr.error) || rr).slice(0, 160)); }
      } catch (e) {
        res.drawn.fail++;
        if (res.drawn.samples.length < 10) res.drawn.samples.push(String(e.message).slice(0, 160));
      }
    }
    res.drawn.paths = items.length;
  }
  const countTxt = useFill
    ? ('轮廓块 ' + (res.drawn.loops || 0) + '，填充对象 ' + res.drawn.ok + '，失败环 ' + res.drawn.fail + '（报告块数请用 drawn.loops）')
    : ('图形 ' + (res.drawn.paths || items.length) + '，成功 ' + res.drawn.ok + '，失败 ' + res.drawn.fail);
  res.note = '已画入当前图纸：图层 ' + layer + '，' + countTxt +
    (useFill ? '（批量 importHatches 填充）' : res.drawn.batched ? '（批量 importVectorPaths）' : '（逐条 createPolyline）') + '。请提醒用户 ZOOM→E 查看。' + (res.drawn.samples.length ? (' 失败样本: ' + res.drawn.samples.slice(0, 3).join(' | ')) : '');
  if (res.drawn.layerMismatch) {
    res.note += ' 注意：实际落在图层 ' + res.drawn.layerMismatch.join('/') + '（目标图层可能不存在，先调 createLayer 建层再画）。';
  }
  return res;
}

// ---------- 工具定义（OpenAI 兼容） ----------
const FILE_TOOLS = [
  {
    type: 'function',
    function: {
      name: 'readFile',
      description: '读取本地文件内容（文本/CSV/JSON/日志等，格式不限）。支持任意本地路径（桌面/D盘/用户给的文件夹内）。⚠️ 内容将发送给 AI 模型服务商处理；敏感文件（config.json/.env/密钥/证书/.git 等）会被拒绝。**大文件必须分页读**：用 offset（起始行,1 起）+ limit（行数）+ maxChars（字符额度,默认16000）分页；返回 from/to/hasMore/nextOffset，续读传 offset=nextOffset。**只想先看结构**用 outline:true（总行数/前50行/CSV列头/JSON顶层键）。**要统计/筛选/只看结论**（求和分组/grep/取键）优先用 queryFile——原文不进上下文。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '文件路径（绝对路径）' },
          offset: { type: 'number', description: '起始行（1 起，默认 1）' },
          limit: { type: 'number', description: '读取行数（0/省略=到文件尾，仍受 maxChars 约束）' },
          maxChars: { type: 'number', description: '本次返回字符上限（默认 16000，上限 32000）' },
          outline: { type: 'boolean', description: 'true=只回结构预览（总行数/前50行/CSV列头/JSON顶层键），不返回全文' }
        },
        required: ['path']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'queryFile',
      description: '在本地“结构化读/聚合”一个文件，只把结论给模型——大文件（CSV/TSV/JSON/日志）的首选，避免把原文灌进上下文。op：head/tail/sample（取样）| wc（行数/字符数）| grep（pattern 正则，返回“行号:内容”；ignoreCase:false 关忽略大小写）| groupby（CSV 分组；by=分组列, agg=count|sum|avg, value=数值列, limit）| stats（CSV 某列 min/max/sum/avg；column=列名或列号）| json（jsonPath 如 $.a.b 或 key，取键/取值）。CSV 默认首行为表头（header:false 关）、分隔符默认逗号（delimiter）。返回体受 maxChars（默认16000）约束。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '文件绝对路径' },
          op: { type: 'string', description: 'head|tail|sample|wc|grep|groupby|stats|json（默认 wc）' },
          pattern: { type: 'string', description: 'grep 的正则（默认不分大小写）' },
          by: { type: 'string', description: 'groupby：分组列（表头名或列号）' },
          value: { type: 'string', description: 'groupby + sum/avg：数值列（表头名或列号）' },
          agg: { type: 'string', description: 'groupby 聚合：count|sum|avg（默认 count）' },
          column: { type: 'string', description: 'stats：统计列（表头名或列号）' },
          jsonPath: { type: 'string', description: 'json：取值路径，如 $.a.b 或 a.b' },
          delimiter: { type: 'string', description: 'CSV 分隔符（默认逗号）' },
          header: { type: 'boolean', description: 'CSV 首行是否表头（默认 true）' },
          limit: { type: 'number', description: 'head/tail/sample/groupby 返回条数' },
          maxChars: { type: 'number', description: '返回字符上限（默认 16000）' }
        },
        required: ['path', 'op']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'listDir',
      description: '列出目录内容（子目录+文件名/大小，不含内容）——用户给文件夹路径时先调这个看里面有什么，再针对性读文件。敏感条目（.git/凭据类）自动过滤。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '目录路径（绝对路径）' }
        },
        required: ['path']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'writeFile',
      description: '在 AI 工作区 {{WORKDIR}} 内创建/覆盖文件（文本/CSV/JSON/报告，格式不限）。⚠️ 只能写工作区：用户文件/其他位置一律拒绝（安全红线）。工作区内已存在的文件可覆盖更新。**大输出分段写**：传 append:true 追加到文件末尾（多次调用拼成完整文件，避免单次内容过大）。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '文件路径（必须位于 {{WORKDIR}} 内，如 {{WORKDIR}}/report.csv）' },
          content: { type: 'string', description: '文件内容（文本）' },
          append: { type: 'boolean', description: 'true=追加到文件末尾（大输出分多次写）；默认 false=覆盖' }
        },
        required: ['path', 'content']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'deleteFile',
      description: '删除 AI 工作区 {{WORKDIR}} 内的文件（仅限自己生成的文件，其他位置一律拒绝）。用于清理本次工作产生的临时/废弃文件。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '要删除的文件路径（必须位于 {{WORKDIR}} 内）' }
        },
        required: ['path']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'readImage',
      description: '读取图片文件（png/jpg/jpeg/bmp/gif/webp）并让模型查看——用于用户要求看图/提取图中文字。图片会附加到消息中（需模型支持视觉；不支持时 API 报错，如实告知即可，不要反复重试）。**超限（编码后 > limits.imageMaxB64，默认 1.5MB）时不自动压缩**，返回 `needsDecision` + 选项，请把选项告诉用户由其决定：压缩看（readImage 带 `resize:{maxSide:1600}`）/ 局部高清（带 `crop:{x,y,w,h}`）/ 用户自处理。resize/crop 走插件 GDI+ 是有损/局部操作，**只在用户选了才调**；`maxQuality:true` = 原样硬拒。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '图片文件路径（绝对路径）' },
          resize: { type: 'object', description: '压缩查看（有损）：{maxSide:1600} 等比缩到长边≤maxSide' },
          crop: { type: 'object', description: '局部高清：{x,y,w,h} 只取该区域（原分辨率）' },
          quality: { type: 'number', description: 'JPEG 质量 10-100（默认 85，仅 resize/crop 时生效）' },
          format: { type: 'string', description: 'jpeg|png（默认 jpeg，仅 resize/crop 时生效）' },
          maxQuality: { type: 'boolean', description: 'true=超限直接拒绝，不做任何处理（保持原图）' }
        },
        required: ['path']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'traceImage',
      description: '把位图（PNG/BMP/JPEG/GIF/TIFF/WEBP）自动描摹成 CAD 矢量线条——用户贴图要求"画进 CAD/描成线条/照这个画"时用。mode 选择：flat=色块区域(★扁平/纯色插画：卡通、logo、矢量风壁纸——线最少效果最好) / arc=圆弧拟合(线稿、手绘、照片风格) / centerline=骨架细线(要能继续编辑) / outline=描边(保笔画粗细) / posterize=全色块(含背景)。不传 mode 会按图片类型自动判定：扁平/纯色→flat；白底线稿/扫描件且含大块实心墨→outline（外轮廓更接近原画，实测优于中心线）；其余→arc。默认只生成路径 JSON；要画进图纸**直接带 draw:true**——工具会自己建图层、一次画完并回报成功/失败数（不要自己循环 createPolyline）。非 PNG/BMP 会自动走插件 exportImageGray 解码。fill:true 时改为把每条闭合路径**填充**落地（走 importHatches；色块图/扁平插画用，可用 fillPattern 指定图案名如 ANSI31，默认 SOLID 实体填充）。mask 参数（默认 auto）控制掩膜来源：auto 会在“明亮纸张背景 + 墨迹比例适中”的图（墨线稿/扫描件/白底手绘）上自动改用墨迹阈值，其余（照片/彩图）走边缘检测；可直接传 ink（强制墨迹阈值，适合白底线稿/扫描件）或 dog（强制边缘检测）。另有两档增强：line=线稿增强（中值降噪 + Sauvola 局部自适应阈值，适合脏扫描/光照不均的线稿）；tone=明暗分层（灰度 k-means 分层 → 每层轮廓，draw 时默认按层填色填充，fill:false 可关；适合连续调/照片/灰度图）。autotune:true 会用「覆盖率/精度」自动挑最优管线与参数（推荐，略慢）。**两种输出形态都保留、由用户选**：要"描成线"→ 线稿多段线（line/arc/outline/centerline）；要"色块/明暗分层"→ tone（分层平涂：每个色阶一个 CAD 图层 + 填充，可逐层开关）。不确定时问用户要哪种。tone/flat 的 levels 建议 2–12（上限 32，超出无收益——实测档数越高小区域越碎）；tone 的 minArea 是细节地板（默认 0.15%，调小→填充更满但块数更多）；明暗对比用 contrast（默认 1，>1 更强）/ gamma（<1 提亮）。**非 PNG/BMP 现也回传彩色**（flat/posterize 不再变灰；插件 withColor）。落地后回读：drawn.loops=轮廓块数、drawn.paths=多段线数、drawn.ok=图元对象数（填充模式下≈图层数，**别拿它当块数**）。',
      parameters: {
        type: 'object',
        properties: {
          image: { type: 'string', description: '图片绝对路径（PNG/BMP/JPEG/GIF/TIFF/WEBP）' },
          mode: { type: 'string', description: 'flat(扁平/纯色插画首选) | arc(线稿/手绘) | centerline | outline | posterize；不传=按图自动判定' },
          draw: { type: 'boolean', description: 'true=直接画入当前图纸（默认 false 只生成 JSON）' },
          layer: { type: 'string', description: '目标图层名（默认 TRACE_<MODE>）' },
          scaleTo: { type: 'number', description: '缩放到该宽度（图形单位，默认 100）' },
          offsetX: { type: 'number', description: 'X 偏移（默认 0）' },
          offsetY: { type: 'number', description: 'Y 偏移（默认 0）' },
          simplify: { type: 'number', description: 'RDP 容差 px（默认 1.2）' },
          minLength: { type: 'number', description: '丢弃短于该长度（默认 12）' },
          bridge: { type: 'number', description: '端点桥接距离 px（默认 6，0=关）' },
          arcThreshold: { type: 'number', description: '小于该曲率不弧化（默认 0.02）' },
          minArea: { type: 'number', description: 'flat：色块最小面积占比%（默认 0.05）' },
          minColor: { type: 'number', description: 'flat：视为独立颜色的最小占比%（默认 1；抗锯齿过渡色会并入最近主色）' },
          keepBackground: { type: 'boolean', description: 'flat：是否连背景色块一起画（默认 false）' },
          arcize: { type: 'boolean', description: 'flat：是否做圆弧拟合（默认 true）' },
          mask: { type: 'string', description: '掩膜来源 auto|ink|dog（默认 auto：线稿/扫描走墨迹阈值，照片走边缘检测）' },
          levels: { type: 'number', description: 'flat/tone/posterize 分层数（默认 4/4/3，建议 2-12）' },
          fill: { type: 'boolean', description: 'true=闭合路径填充落地（走 importHatches；tone 档默认填）' },
          fillPattern: { type: 'string', description: '填充图案名（默认 SOLID，如 ANSI31）' },
          contrast: { type: 'number', description: 'tone：明暗线性拉伸（默认 1，>1 更强）' },
          gamma: { type: 'number', description: 'tone：伽马（默认 1，<1 提亮）' },
          autotune: { type: 'boolean', description: 'true=按覆盖率/精度自动挑最优管线与参数（推荐，略慢）' },
          adaptive: { type: 'boolean', description: 'line 档：Sauvola 局部自适应阈值增强' },
          denoise: { type: 'boolean', description: '中值降噪开关' },
          maxSide: { type: 'number', description: '解码长边上限 px（默认 1600）' },
          window: { type: 'number', description: 'Sauvola 窗口大小 px' },
          k: { type: 'number', description: 'Sauvola 系数 k（默认 0.2）' },
          speck: { type: 'number', description: '去噪点：小于该面积(px)的斑块过滤（默认 12）' },
          close: { type: 'number', description: '闭运算核大小（默认 1）' },
          inkGamma: { type: 'number', description: 'ink 掩膜提亮指数（默认 1；0.5 更干净但浅排线更狠）' },
        },
        required: ['image']
      }
    }
  }
];

// 2026-08-19: 工作区路径动态化——工具描述里 {{WORKDIR}} 占位符在调用时替换为真实工作区（项目模式 = projects/<名>/agent-work）
function getFileTools() {
  const work = getAgentWorkDir();
  return FILE_TOOLS.map(t => {
    const copy = JSON.parse(JSON.stringify(t));
    if (copy.function && copy.function.description) copy.function.description = copy.function.description.replaceAll('{{WORKDIR}}', work);
    const props = copy.function && copy.function.parameters && copy.function.parameters.properties;
    if (props) for (const k of Object.keys(props)) {
      if (props[k] && props[k].description) props[k].description = props[k].description.replaceAll('{{WORKDIR}}', work);
    }
    return copy;
  });
}

module.exports = { readFile, writeFile, deleteFile, listDir, readImage, traceImage, queryFile, FILE_TOOLS, getFileTools, getAgentWorkDir, getLimits };
