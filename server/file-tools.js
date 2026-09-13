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

// 禁写的扩展名（防止被诱导写入可执行/脚本文件）
const BLOCKED_EXT = new Set([
  '.exe', '.bat', '.cmd', '.ps1', '.dll', '.scr', '.vbs', '.js', '.mjs', '.cjs',
  '.sh', '.py', '.com', '.msi', '.reg', '.wsf', '.jar',
]);

// 读/写大小上限（防撑爆磁盘/内存）
const MAX_READ_BYTES = 5 * 1024 * 1024;    // 读 5MB
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

module.exports = { readFile, writeFile, deleteFile, listDir, readImage, FILE_TOOLS, getFileTools, getAgentWorkDir };
