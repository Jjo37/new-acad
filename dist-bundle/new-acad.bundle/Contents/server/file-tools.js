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

// 读/写大小上限（防撑爆磁盘/内存）
const MAX_READ_BYTES = 5 * 1024 * 1024;    // 读 5MB
const MAX_IMAGE_B64 = 1.5 * 1024 * 1024;   // 2026-09-15: 图片 base64 上限(字符) ≈ 1.1MB 原图
const MAX_WRITE_BYTES = 2 * 1024 * 1024;   // 写 2MB

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
  } catch (_) {}
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

// ---------- readFile ----------
function readFile(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveReadPath(rawPath, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);
  const stat = fs.statSync(p);
  if (stat.size > MAX_READ_BYTES) throw new Error('文件过大（>' + (MAX_READ_BYTES / 1024 / 1024) + 'MB），拒绝读取');
  const content = fs.readFileSync(p, 'utf8');
  let lines = 1;
  for (let i = 0; i < content.length; i++) if (content.charCodeAt(i) === 10) lines++;
  return {
    path: p,
    size: stat.size,
    lines,
    content,
    hint: '内容将发送给 AI 模型服务商处理。文件共 ' + lines + ' 行。'
  };
}

// ---------- readImage ----------
const IMAGE_EXT = { '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.bmp': 'image/bmp', '.gif': 'image/gif', '.webp': 'image/webp' };
function readImage(args) {
  const rawPath = String(args.path || '').trim();
  if (!rawPath) throw new Error('参数 path 必填');
  const p = resolveReadPath(rawPath, true);
  if (fs.statSync(p).isDirectory()) throw new Error('这是目录，不是文件: ' + p);
  const ext = path.extname(p).toLowerCase();
  const mime = IMAGE_EXT[ext];
  if (!mime) throw new Error('仅支持图片文件（png/jpg/jpeg/bmp/gif/webp）: ' + ext);
  const stat = fs.statSync(p);
  if (stat.size > MAX_READ_BYTES) throw new Error('图片过大（>' + (MAX_READ_BYTES / 1024 / 1024) + 'MB），拒绝读取');
  const base64 = fs.readFileSync(p).toString('base64');
  // 2026-09-15: 单图编码体积硬约束——图片是请求体里最大的一块，必须限额（>1.5MB base64 ≈ 1.1MB 原图）
  // 原因: 旧实现只限原文件 5MB → 6.7MB base64 直接塞进请求体（超窗口/超时）。给用户明确指引优于抛模糊错误
  if (base64.length > MAX_IMAGE_B64) {
    throw new Error('图片过大（编码后 ' + (base64.length / 1024 / 1024).toFixed(1) + 'MB，上限 ' + (MAX_IMAGE_B64 / 1024 / 1024).toFixed(1) + 'MB）。请先压缩或裁剪后再读取（如缩小截图范围、转 JPG、或用画图另存为 JPEG）');
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
  if (Buffer.byteLength(content, 'utf8') > MAX_WRITE_BYTES) {
    throw new Error('内容过大（>' + (MAX_WRITE_BYTES / 1024 / 1024) + 'MB），拒绝写入');
  }
  const ext = path.extname(rawPath).toLowerCase();
  if (BLOCKED_EXT.has(ext)) {
    throw new Error('安全红线: 不允许写入 ' + ext + ' 类型文件（可执行/脚本文件禁写）');
  }
  const p = resolveWritePath(rawPath);
  const existed = fs.existsSync(p);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, content, 'utf8');
  return { path: p, created: !existed, overwritten: existed, bytes: Buffer.byteLength(content, 'utf8') };
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

  const MODES = ['flat', 'arc', 'centerline', 'outline', 'posterize'];
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
  };
  const ext = path.extname(p).toLowerCase();
  // PNG/BMP 本引擎自带解码；其他格式（jpg/gif/tif/webp…）走插件 exportImageGray（GDI+ 全格式）
  const nativeOk = ext === '.png' || ext === '.bmp';
  if (!nativeOk) opts.needPluginGray = true;

  const finish = (r) => {
    const realMode = (r.stats && r.stats.mode) || mode;
    const realLayer = args.layer ? String(args.layer) : ('TRACE_' + String(realMode).toUpperCase());
    for (const it of r.items) it.layer = realLayer;
    const base = path.basename(p).replace(/\.[^.]+$/, '').slice(0, 40).replace(/[^\w.\u4e00-\u9fa5-]/g, '_');
    const outPath = resolveWritePath(path.join(getAgentWorkDir(), 'trace-' + base + '-' + realMode + '.json'));
    fs.mkdirSync(path.dirname(outPath), { recursive: true });
    fs.writeFileSync(outPath, JSON.stringify(r), 'utf8');
    const res = {
      ok: true, image: p, mode: realMode, layer: realLayer, json: outPath, size: fs.statSync(outPath).size,
      stats: r.stats,
      autoMode: (r.stats && r.stats.autoMode) || autoMode || undefined, imageKind: kindInfo || undefined,
    };
    if (args.draw === true || args.draw === 1) return drawItems(r.items, realLayer, res);
    res.note = '已生成矢量路径 JSON（未画入 CAD）。要落地请再调 traceImage 同参数 + draw:true。';
    return res;
  };

  if (!opts.needPluginGray) return finish(engine.traceImage(opts));

  // 非 PNG/BMP：先让插件解码成灰度（deflate+base64）
  return (async () => {
    const cadCall = require('./cad-tools.js').cadCall;
    const gr = await cadCall('exportImageGray', { path: p, maxSide: num(args.maxSide, 1400) }, 60000);
    const meta = gr && gr.result ? gr.result : gr;
    if (!meta || !meta.gray) throw new Error('插件 exportImageGray 未返回灰度数据：' + JSON.stringify(gr).slice(0, 160));
    const buf = zlib.inflateRawSync(Buffer.from(meta.gray, 'base64'));
    if (buf.length < meta.width * meta.height) throw new Error('灰度数据长度不符（' + buf.length + ' < ' + (meta.width * meta.height) + '）');
    const grayData = new Uint8Array(buf.buffer, buf.byteOffset, meta.width * meta.height);
    if (mode === 'posterize') throw new Error('posterize 模式需要彩色信息，请先把图片转成 PNG 再用；或换 arc/centerline/outline');
    return finish(engine.traceImage({ ...opts, input: null, gray: { w: meta.width, h: meta.height, data: grayData } }));
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
async function drawItems(items, layer, res) {
  const cadCall = require('./cad-tools.js').cadCall;
  res.drawn = { layer, ok: 0, fail: 0, samples: [], batched: false };
  try { await cadCall('createLayer', { name: layer, color: 3 }, 20000); } catch (_) { /* 图层已存在 */ }

  if (await hasBatchImport()) {
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
        else { res.drawn.fail += chunk.length; if (res.drawn.samples.length < 3) res.drawn.samples.push(JSON.stringify((rr && rr.error) || rr).slice(0, 160)); }
      } catch (e) {
        res.drawn.fail += chunk.length;
        if (res.drawn.samples.length < 3) res.drawn.samples.push(String(e.message).slice(0, 160));
      }
    }
    res.drawn.batched = true;
  } else {
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      const params = { points: it.points.map((q) => ({ x: q[0], y: q[1] })), closed: !!it.closed, layer };
      if (it.bulges && it.bulges.length) params.bulges = it.bulges;
      try {
        const rr = await cadCall('createPolyline', params, 30000);
        if (rr && rr.result && !rr.error) res.drawn.ok++;
        else { res.drawn.fail++; if (res.drawn.samples.length < 3) res.drawn.samples.push(JSON.stringify((rr && rr.error) || rr).slice(0, 160)); }
      } catch (e) {
        res.drawn.fail++;
        if (res.drawn.samples.length < 3) res.drawn.samples.push(String(e.message).slice(0, 160));
      }
    }
  }
  res.note = '已画入当前图纸：图层 ' + layer + '，成功 ' + res.drawn.ok + ' / 失败 ' + res.drawn.fail +
    (res.drawn.batched ? '（批量 importVectorPaths）' : '（逐条 createPolyline）') + '。请提醒用户 ZOOM→E 查看。';
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
      description: '读取本地文件内容（文本/CSV/JSON 等，格式不限）。支持任意本地路径（桌面/D盘/用户给的文件夹内）。⚠️ 内容将发送给 AI 模型服务商处理；敏感文件（config.json/.env/密钥/证书/.git 等）会被拒绝。用于读取用户数据/配置/报告。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '文件路径（绝对路径）' }
        },
        required: ['path']
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
      description: '在 AI 工作区 {{WORKDIR}} 内创建/覆盖文件（文本/CSV/JSON/报告，格式不限）。⚠️ 只能写工作区：用户文件/其他位置一律拒绝（安全红线）。工作区内已存在的文件可覆盖更新。用于生成中间数据/报告。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '文件路径（必须位于 {{WORKDIR}} 内，如 {{WORKDIR}}/report.csv）' },
          content: { type: 'string', description: '文件内容（文本）' }
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
      description: '读取图片文件（png/jpg/jpeg/bmp/gif/webp）并让模型查看——用于用户要求看图/提取图中文字。图片会附加到消息中（需模型支持视觉；不支持时 API 报错，如实告知即可，不要反复重试）。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '图片文件路径（绝对路径）' }
        },
        required: ['path']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'traceImage',
      description: '把位图（PNG/BMP/JPEG/GIF/TIFF）自动描摹成 CAD 矢量线条——用户贴图要求"画进 CAD/描成线条/照这个画"时用。mode 选择：flat=色块区域(★扁平/纯色插画：卡通、logo、矢量风壁纸——线最少效果最好) / arc=圆弧拟合(线稿、手绘、照片风格) / centerline=骨架细线(要能继续编辑) / outline=描边(保笔画粗细) / posterize=全色块(含背景)。不传 mode 会按图片类型自动判定：扁平/纯色→flat；白底线稿/扫描件且含大块实心墨→outline（外轮廓更接近原画，实测优于中心线）；其余→arc。默认只生成路径 JSON；要画进图纸**直接带 draw:true**——工具会自己建图层、一次画完并回报成功/失败数（不要自己循环 createPolyline）。非 PNG/BMP 会自动走插件 exportImageGray 解码。mask 参数（默认 auto）控制掩膜来源：auto 会在“明亮纸张背景 + 墨迹比例适中”的图（墨线稿/扫描件/白底手绘）上自动改用墨迹阈值，其余（照片/彩图）走边缘检测；可直接传 ink（强制墨迹阈值，适合白底线稿/扫描件）或 dog（强制边缘检测）。',
      parameters: {
        type: 'object',
        properties: {
          image: { type: 'string', description: '图片绝对路径（PNG/BMP/JPEG/GIF/TIFF）' },
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
          arcize: { type: 'boolean', description: 'flat：是否做圆弧拟合（默认 true）' }
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

module.exports = { readFile, writeFile, deleteFile, listDir, readImage, traceImage, FILE_TOOLS, getFileTools, getAgentWorkDir };
