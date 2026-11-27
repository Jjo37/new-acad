'use strict';
/**
 * project-memory.js — 项目级长期记忆（v1.17 第一片，2026-11-14）
 *
 * 为什么（实测缺口）：
 *   · 记忆只有全局单文件 `server/memory/agent-memory.md`，且**每轮只注入 ≤300 字的标题索引**，
 *     正文要靠 AI 主动 readMemory —— 实测整轮 24 条消息它一次都没读，等于铁规不存在。
 *   · 准入规则写着"禁止写具体任务数据"，可项目做到一半真正救命的就是这些（桩号↔坐标基准、口径、坑）。
 *     它们没地方去，只能堆进 23.7k 字符的 bim-progress.md 当散文。
 *   · writeMemory 是**覆盖式全文重写**：一次写错全丢，8k 上限还逼着它删旧条目。
 *
 * 本模块的答案：
 *   L0 铁规  —— 项目 `<agent-work>/PROJECT-RULES.md` 全文注入（改动需用户确认，AI 只提议）
 *   L1 事实  —— 本项目 `memory/entries.jsonl` 条目化（id/来源/时间/校验方式），注入标题 + 最近变更
 *   L2 坑表  —— 同一文件 kind='lesson'，**不注入**，方法失败时按方法名/错误码自动召回
 *   写入一律条目级（add/supersede），不做整文件覆盖；超量时归档而不是让 AI 删。
 */

const fs = require('fs');
const path = require('path');
const { sanitizeProjectName } = require('./project-scope');

const ROOT = process.env.PROJECT_MEMORY_ROOT || path.join(__dirname, '..');   // test hook: 单测指向临时目录
const INJECT_RULES_MAX = 1500;   // L0 注入上限（全文另可读）
const INJECT_FACTS_MAX = 6;      // L1 注入几条标题
const ENTRY_TEXT_MAX = 1200;

function projectRoot(project) {
  return path.join(ROOT, 'exchange', 'projects', sanitizeProjectName(project));
}
function memDir(project) {
  return path.join(projectRoot(project), 'memory');
}
function entriesPath(project) {
  return path.join(memDir(project), 'entries.jsonl');
}
function rulesPath(project) {
  return path.join(projectRoot(project), 'agent-work', 'PROJECT-RULES.md');
}

function readEntries(project) {
  const p = entriesPath(project);
  if (!fs.existsSync(p)) return [];
  const out = [];
  for (const line of fs.readFileSync(p, 'utf8').split(/\r?\n/)) {
    const s = line.trim();
    if (!s) continue;
    try { out.push(JSON.parse(s)); } catch (_) { /* 坏行跳过，不当整库失效 */ }
  }
  return out;
}

function writeEntries(project, arr) {
  const dir = memDir(project);
  fs.mkdirSync(dir, { recursive: true });
  const tmp = entriesPath(project) + '.tmp';
  fs.writeFileSync(tmp, arr.map((e) => JSON.stringify(e)).join('\n') + (arr.length ? '\n' : ''), 'utf8');
  fs.renameSync(tmp, entriesPath(project));
}

function nextId(arr, kind) {
  const prefix = kind === 'lesson' ? 'L' : (kind === 'rule' ? 'R' : 'F');
  let max = 0;
  for (const e of arr) {
    if (!e || typeof e.id !== 'string') continue;
    if (!e.id.startsWith(prefix + '-')) continue;
    const n = parseInt(e.id.slice(prefix.length + 1), 10);
    if (Number.isFinite(n) && n > max) max = n;
  }
  return prefix + '-' + String(max + 1).padStart(4, '0');
}

const KINDS = new Set(['fact', 'lesson', 'rule']);

/**
 * 新增一条记忆。
 * @param {{kind:'fact'|'lesson'|'rule', text:string, source?:string, tags?:string[], at?:string}} entry
 */
function add(project, entry) {
  const kind = String((entry && entry.kind) || 'fact');
  if (!KINDS.has(kind)) throw new Error('kind 必须是 fact|lesson|rule，收到 ' + kind);
  const text = String((entry && entry.text) || '').trim();
  if (!text) throw new Error('text 不能为空');
  const arr = readEntries(project);
  const rec = {
    id: nextId(arr, kind),
    kind,
    text: text.slice(0, ENTRY_TEXT_MAX),
    source: String((entry && entry.source) || 'agent').slice(0, 200),
    tags: Array.isArray(entry && entry.tags) ? entry.tags.map((t) => String(t).slice(0, 40)).slice(0, 8) : [],
    at: (entry && entry.at) || new Date().toISOString(),
    supersededBy: null,
  };
  arr.push(rec);
  writeEntries(project, arr);
  return rec;
}

/** 旧条目标作废（保留可追溯，不删）；可同时写一条新条目代替它。 */
function supersede(project, id, opts) {
  const arr = readEntries(project);
  const i = arr.findIndex((e) => e && e.id === id);
  if (i < 0) throw new Error('记忆条目不存在: ' + id);
  const old = arr[i];
  let added = null;
  if (opts && opts.text) {
    const text = String(opts.text).trim();
    if (!text) throw new Error('text 不能为空');
    added = {
      id: nextId(arr, old.kind || 'fact'),
      kind: old.kind || 'fact',
      text: text.slice(0, ENTRY_TEXT_MAX),
      source: String((opts && opts.source) || old.source || 'agent').slice(0, 200),
      tags: Array.isArray(opts && opts.tags) ? opts.tags.map((t) => String(t).slice(0, 40)).slice(0, 8) : (old.tags || []),
      at: new Date().toISOString(),
      supersededBy: null,
    };
    arr.push(added);
  }
  arr[i] = Object.assign({}, old, { supersededBy: added ? added.id : ((opts && opts.by) || 'superseded'), supersededAt: new Date().toISOString() });
  writeEntries(project, arr);
  return { superseded: id, replacedBy: added ? added.id : null };
}

function live(project) {
  return readEntries(project).filter((e) => e && !e.supersededBy);
}

function tokenize(q) {
  return String(q || '').toLowerCase().split(/[\s,，、|/]+/).filter(Boolean);
}

function scoreEntry(e, terms) {
  const hay = (e.text + ' ' + (e.tags || []).join(' ') + ' ' + e.id).toLowerCase();
  let hit = 0;
  for (const t of terms) if (hay.includes(t)) hit++;
  return hit;
}

function search(project, query, opts) {
  const terms = tokenize(query);
  const kinds = opts && Array.isArray(opts.kinds) ? new Set(opts.kinds) : null;
  const limit = (opts && opts.limit) || 8;
  const pool = live(project).filter((e) => !kinds || kinds.has(e.kind));
  if (!terms.length) return pool.slice(-limit).reverse();
  return pool
    .map((e) => [e, scoreEntry(e, terms)])
    .filter(([, s]) => s > 0)
    .sort((a, b) => b[1] - a[1] || String(b[0].at).localeCompare(String(a[0].at)))
    .slice(0, limit)
    .map(([e]) => e);
}

/**
 * 失败召回：方法名/错误码命中坑表 → 返回可直接塞进工具结果的提示行。
 * 只返回 lesson（facts 不掺进来，避免噪音）。
 */
function recall(project, f, opts) {
  const words = [];
  const method = String((f && f.method) || '');
  const code = String((f && f.errCode) || '');
  if (method) {
    words.push(method);
    // addSurfacePointsFromFile → addSurfacePoints / SurfacePoints 之类也命中
    const parts = method.replace(/Async$/, '').split(/(?=[A-Z])/);
    if (parts.length > 1) words.push(parts.slice(0, 2).join(''));
  }
  if (code) words.push(code);
  const raw = tokenize(words.join(' '));
  if (!raw.length) return [];
  const hits = search(project, raw.join(' '), { kinds: ['lesson'], limit: (opts && opts.limit) || 3 });
  // search 用的是 AND 语义前会丢；这里放宽成"任一词命中"
  let picked = hits;
  if (!picked.length) {
    const pool = live(project).filter((e) => e.kind === 'lesson');
    picked = pool
      .map((e) => [e, scoreEntry(e, raw)])
      .filter(([, s]) => s > 0)
      .sort((a, b) => b[1] - a[1])
      .slice(0, (opts && opts.limit) || 3)
      .map(([e]) => e);
  }
  return picked;
}

/** L0：读项目铁规全文（注入用，超长截断并提示全文位置）。 */
function readRules(project, max) {
  const p = rulesPath(project);
  if (!fs.existsSync(p)) return { exists: false, text: '', chars: 0, path: p };
  const s = fs.readFileSync(p, 'utf8');
  const cap = max || INJECT_RULES_MAX;
  return { exists: true, text: s.length > cap ? s.slice(0, cap) + '\n…（铁规全文 ' + s.length + ' 字符，已截断，全文见 ' + p + '）' : s, chars: s.length, path: p };
}

/**
 * 组装注入到 system 的项目记忆段（L0 全文 + L1 标题 + L2 计数）。
 * 纯函数式输出，便于单测。
 */
function buildInjection(project, opts) {
  const o = opts || {};
  const rules = readRules(project, o.rulesMax);
  const facts = live(project).filter((e) => e.kind === 'fact');
  const lessons = live(project).filter((e) => e.kind === 'lesson');
  const parts = [];
  if (rules.exists) {
    parts.push('🔒 项目铁规（用户确认；改动必须先问用户，你只能提议）：\n' + rules.text.trim());
  }
  if (facts.length) {
    const recent = facts.slice(-(o.factsMax || INJECT_FACTS_MAX)).reverse();
    parts.push('🧷 项目事实 ' + facts.length + ' 条（只列最近的；全部用 projectMemory action=list 取）：\n'
      + recent.map((e) => '- [' + e.id + '] ' + (e.text.length > 80 ? e.text.slice(0, 80) + '…' : e.text)).join('\n'));
  }
  if (lessons.length) {
    parts.push('🧯 坑表 ' + lessons.length + ' 条（调用失败时会自动召回相关条目；要主动查用 projectMemory action=search）。');
  }
  return parts.length ? '\n\n' + parts.join('\n\n') : '';
}

module.exports = {
  add, supersede, search, recall, live, readEntries, readRules, buildInjection,
  entriesPath, rulesPath, memDir, projectRoot, INJECT_RULES_MAX,
};
