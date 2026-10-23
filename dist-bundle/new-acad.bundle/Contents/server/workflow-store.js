'use strict';
// workflow-store.js -- P2.1: capture a finished task as a reviewable artifact.
//
// Design: docs/design/2026-10-21-p2-workflow-capture.md
// Scope of this file (P2.1 only): record / list / get / export (json + md) / prune.
// Replay (P2.2+) intentionally lives elsewhere so capture can ship and be tested first.
//
// Hard rules taken from the design:
//   1. fingerprints and counts only -- never the result payload (a single getEntitiesByLayer
//      can return ~900KB; 200 records would blow up the disk)
//   2. only the drawing NAME is stored, never the full path (paths carry the user name)
//   3. records are project-scoped: exchange/projects/<project>/workflows/<YYYY-MM>/<id>.json
//   4. nothing here is uploaded, indexed for RAG, or packaged

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = process.env.WORKFLOW_STORE_ROOT || path.join(__dirname, '..');   // test hook: point the store at a temp dir
const KEEP_PER_SCOPE = 200;

function sanitizeProject(name) {
  const s = String(name || '').trim();
  if (!s) return '';
  const cleaned = s.replace(/[\\/:*?"<>|.]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 64);
  return cleaned || '';
}

function scopeDir(project) {
  const p = sanitizeProject(project);
  return p
    ? path.join(ROOT, 'exchange', 'projects', p, 'workflows')
    : path.join(ROOT, 'exchange', 'workflows');
}

function newId(now) {
  const d = now || new Date();
  const p2 = (n) => String(n).padStart(2, '0');
  const stamp = d.getFullYear() + p2(d.getMonth() + 1) + p2(d.getDate()) + '-' + p2(d.getHours()) + p2(d.getMinutes()) + p2(d.getSeconds());
  return 'wf-' + stamp + '-' + crypto.randomBytes(2).toString('hex');
}

const { extractSlots } = require('./workflow-slots');

const READONLY = /^(get|list|measure|calculate|analyze|profile(Get|Check)|qc(Check)|read|summarize|trace|assert)/;

// 2026-10-21: 结果指纹 —— 捕获时算一次，回放时再算一次，比"结果是否漂移"。
// 大结果只取头尾 + 长度（确定性、便宜），用于漂移检测足够；不用于内容校验。
function fingerprint(v) {
  let s;
  try { s = typeof v === 'string' ? v : JSON.stringify(v); } catch (e) { return ''; }
  if (!s) return '';
  const buf = Buffer.from(s, 'utf8');
  if (buf.length <= 65536) return crypto.createHash('sha256').update(buf).digest('hex').slice(0, 16);
  const h = crypto.createHash('sha256');
  h.update(buf.subarray(0, 32768));
  h.update(buf.subarray(buf.length - 8192));
  h.update(String(buf.length));
  return h.digest('hex').slice(0, 16);
}

// 2026-10-22: 成功判据类方法名（verifyAssemblyGeometry / qcCheck / check* …）。
// 原来只认 assertChecks —— 一次真机装配任务里 3 次 verifyAssemblyGeometry 全被标成 act，
// 记录反而报“no verification step”，评分被凭空压低。
const VERIFY = /^(verify|check|validate|inspect|assert|qcCheck|audit)/;

// 2026-10-22: agent 侧本地工具（不经过插件/MCP），它们不是可重放的工件步骤。
// 原来一律被当成 cadCall 步骤记进 calls[]：
//   ① 回放时按插件方法去调 → -32601，任何查过知识库的任务都必然“回放失败”；
//   ② 问答轮（listMethods×2 + readKnowledge）凑够 3 步 → 被当成工作流存下来；
//   ③ 拉高步骤数、污染评分。
const LOCAL_TOOLS = new Set([
  'readKnowledge', 'searchKnowledge', 'ingestKnowledge',
  'readMemory', 'writeMemory', 'revertMemory',
  'readFile', 'writeFile', 'listDir', 'deleteFile', 'queryFile',
  'readImage', 'traceImage',
  'createPlan', 'updatePlan', 'getPlan',
]);

function isLocalTool(method) {
  return LOCAL_TOOLS.has(String(method || ''));
}

// 2026-10-22 P2.3: 结果里产出的句柄（供回放重映射）。只认句柄形态的短十六进制串，最多 64 个。
function collectHandles(result) {
  const out = [];
  const push = (v) => {
    if (typeof v === 'string' && /^[0-9A-Fa-f]{1,8}$/.test(v) && out.length < 64 && !out.includes(v)) out.push(v);
  };
  // 2026-10-22: key match is deliberately broad -- `createdHandles` (offsetEntity / breakEntity)
  // was missed by the first version, so the next step's handle could never be remapped and the
  // replay dead-ended on "needs-remap". Any key containing "handle" counts (except *Error notes).
  const walk = (node, depth) => {
    if (!node || depth > 3) return;
    if (Array.isArray(node)) { for (const v of node) walk(v, depth + 1); return; }
    if (typeof node !== 'object') return;
    for (const [k, v] of Object.entries(node)) {
      if (/handle/i.test(k) && !/error/i.test(k)) {
        if (Array.isArray(v)) v.forEach(push); else push(v);
      } else if (/^(created|entities|items|subassemblies|results|objects)$/i.test(k)) {
        walk(v, depth + 1);
      }
    }
  };
  walk(result, 0);
  return out;
}

function roleOf(method) {
  if (VERIFY.test(String(method || ''))) return 'verify';
  if (READONLY.test(String(method || ''))) return 'probe';
  return 'act';
}

// calls[] entries come from llm-agent: { method, params, blocked?, ok?, errCode?, ms?, bytes? }
function toSteps(calls) {
  const steps = [];
  let i = 0;
  for (const c of calls || []) {
    if (!c || !c.method) continue;
    if (c.blocked) { i++; steps.push({ i, method: c.method, blocked: true, role: 'probe' }); continue; }
    // collapse job polling into the step that started it
    if (/^getJob(Status|Result)$/.test(c.method) && steps.length) {
      const owner = steps[steps.length - 1];
      owner.polling = (owner.polling || 0) + 1;
      continue;
    }
    i++;
    steps.push({
      i,
      method: c.method,
      params: c.params && typeof c.params === 'object' ? compactParams(c.params) : {},
      channel: isLocalTool(c.method) ? 'local'
        : (String(c.method).startsWith('mcpCall:') ? 'mcp' : 'tcp'),
      ms: typeof c.ms === 'number' ? c.ms : null,
      bytes: typeof c.bytes === 'number' ? c.bytes : null,
      ok: c.ok !== false,
      errCode: c.errCode || '',
      fp: c.fp || '',            // 结果指纹（回放时对比）
      handlesOut: Array.isArray(c.handlesOut) ? c.handlesOut.slice(0, 8) : [],   // P2.3: 本步产出的句柄
      role: roleOf(c.method),
    });
  }
  return steps;
}

// keep params readable but bounded (arrays of handles/points can be enormous)
function compactParams(params) {
  const out = {};
  let budget = 40;
  for (const [k, v] of Object.entries(params)) {
    if (budget-- <= 0) { out['…'] = 'truncated'; break; }
    if (Array.isArray(v)) {
      out[k] = v.length > 5 ? { _array: v.length, sample: v.slice(0, 3) } : v;
    } else if (v && typeof v === 'object') {
      const keys = Object.keys(v);
      out[k] = keys.length > 8 ? { _object: keys.length, keys: keys.slice(0, 8) } : v;
    } else {
      out[k] = v;
    }
  }
  return out;
}

function scoreRecord(allSteps) {
  const steps = (allSteps || []).filter((s) => s.channel !== 'local');   // 本地工具不参与复用评分
  const acts = steps.filter((s) => s.role === 'act');
  const verify = steps.filter((s) => s.role === 'verify');
  const failed = steps.filter((s) => s.ok === false);
  const blockers = [];
  if (!acts.length) blockers.push('no action step');
  if (!verify.length) blockers.push('no verification step');
  if (failed.length) blockers.push(failed.length + ' failed step(s)');

  const handleDep = steps.filter((s) => s.params && (s.params.handle || s.params.handles)).length;
  if (handleDep) blockers.push(handleDep + ' step(s) depend on handles');

  let score = 0;
  score += acts.length && verify.length ? 0.35 : 0.1;
  score += handleDep === 0 ? 0.25 : Math.max(0, 0.25 - 0.05 * handleDep);
  score += acts.length ? 0.2 : 0;
  score += verify.length ? 0.2 : 0;
  return { score: Math.round(Math.min(1, score) * 100) / 100, blockers };
}

function record({ ask, calls, turns, cancelled, project, context }) {
  const now = new Date();
  const steps = toSteps(calls);
  // 2026-10-22: 判断“是不是工作流”只看可重放步骤。本地工具（readKnowledge 等）凑出的 3 步不是工作流。
  const replayable = steps.filter((s) => s.channel !== 'local');
  const acts = replayable.filter((s) => s.role === 'act');
  // 2026-10-22 (Bro: “工作流这边已经不更新了”): the old rule was replayable < 3 -> drop, which threw
  // away perfectly normal small tasks (listMethods + setEntitiesColor = 2 calls). Pure probe Q&A
  // stays out, but 2+ steps containing a real action are a workflow.
  if (replayable.length < 3 && !(acts.length >= 1 && replayable.length >= 2)) return null;
  // 2026-10-21: 纯只读任务也记 —— 它是最安全、最值得回放的一类（出图前检查、量算报告）。
  // 只是 kind 标清、评分里体现"没有动作步骤"，不会和写路径混在一起。

  // 2026-10-23 (Bro: the offset task worked but the record said failed): a probe that failed and
  // was then retried successfully is not a failed task. Only a failed ACTION fails the outcome.
  // Real case: getEntityInfo got `handles` instead of `handle` (-32602), the agent retried with
  // the right shape, and every offsetEntity call had already succeeded.
  const failedSteps = replayable.filter((s) => s.ok === false);
  const failed = failedSteps.length;
  const failedActs = failedSteps.filter((s) => (s.role || '') === 'act').length;
  const lastFailed = replayable.length > 0 && replayable[replayable.length - 1].ok === false;
  const totalMs = steps.reduce((a, s) => a + (s.ms || 0), 0);
  const { score, blockers } = scoreRecord(steps);

  const rec = {
    schema: 'workflow/1',
    taskId: newId(now),
    createdAt: now.toISOString(),
    project: sanitizeProject(project) || null,
    ask: String(ask || '').slice(0, 500),
    context: {
      document: (context && context.document) || '',       // name only, never the path
      entityCount: (context && context.entityCount) || null,
      pluginVersion: (context && context.pluginVersion) || '',
      provider: (context && context.provider) || '',
      model: (context && context.model) || '',
    },
    kind: acts.length ? 'write' : 'read-only',
    outcome: { ok: !cancelled && failedActs === 0 && !lastFailed, turns: turns || 0, calls: replayable.length, failed, failedActs, failedProbes: failed - failedActs, ms: totalMs, cancelled: !!cancelled, localCalls: steps.length - replayable.length },
    steps,
    // 2026-10-22 P2.4: 参数槽位（换图重跑要填的就是这些）。句柄永不入槽（那是对象绑定）。
    slots: extractSlots(replayable),
    reusability: { score, blockers },
  };

  const dir = path.join(scopeDir(project), now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0'));
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, rec.taskId + '.json'), JSON.stringify(rec, null, 2) + '\n', 'utf8');
    prune(project);
    return rec;
  } catch (e) {
    console.error('[WORKFLOW] write failed:', e.message);
    return null;
  }
}

function listFiles(project) {
  const base = scopeDir(project);
  const out = [];
  const walk = (d) => {
    let entries;
    try { entries = fs.readdirSync(d, { withFileTypes: true }); } catch (e) { return; }
    for (const e of entries) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) { if (e.name === '_trash') continue;   // 清掉的记录不再算进列表
        walk(p); continue; }
      if (e.name.endsWith('.json')) out.push(p);
    }
  };
  walk(base);
  // 2026-10-23: never sort by the file name -- the id and the YYYY-MM folder are built from
  // LOCAL time, so a user who changes timezone (travel, or a laptop set to another region)
  // would see new records sink to the bottom and prune() could delete the newest one first.
  const createdMs = new Map();
  for (const p of out) {
    let ms = 0;
    try { ms = Date.parse(JSON.parse(fs.readFileSync(p, "utf8")).createdAt) || 0; } catch (e) { ms = 0; }
    if (!ms) { try { ms = fs.statSync(p).mtimeMs; } catch (e) { ms = 0; } }
    createdMs.set(p, ms);
  }
  return out.sort((a, b) => (createdMs.get(b) - createdMs.get(a)) || (a < b ? 1 : -1));   // newest first
}

function list(project, limit) {
  const files = listFiles(project).slice(0, limit || 50);
  return files.map((f) => {
    try {
      const r = JSON.parse(fs.readFileSync(f, 'utf8'));
      // 2026-10-22 P2.4: 老记录没有 slots 字段 —— 现场算，不要求用户重新跑一遍才看到槽位。
      // 2026-10-22: 面板要在列表里看见“这条任务注入了多少字符”（Bro 报「工作流界面看不到」）。
      // 口径：注入给模型的字符数（= 每个 step 的 bytes 之和，即 formatResult 之后的文本长度），
      // 不是插件原始结果体积 —— 后者会大 10~25 倍（listMethods 实测 41307 vs 1666）。
      const steps = r.steps || [];
      let chars = 0;
      let charsPeak = 0;
      for (const s of steps) {
        if (typeof s.bytes !== 'number') continue;
        chars += s.bytes;
        if (s.bytes > charsPeak) charsPeak = s.bytes;
      }
      return {
        taskId: r.taskId, createdAt: r.createdAt, ask: r.ask,
        // steps = replayable steps (a record may also carry agent-local tools)
        steps: (r.outcome && typeof r.outcome.calls === 'number') ? r.outcome.calls : steps.length,
        slots: (Array.isArray(r.slots) ? r.slots : extractSlots(steps.filter((s) => s.channel !== 'local'))).length,
        ok: r.outcome && r.outcome.ok,
        ms: r.outcome && r.outcome.ms, score: r.reusability && r.reusability.score,
        chars, charsPeak,
      };
    } catch (e) { return null; }
  }).filter(Boolean);
}

function get(project, taskId) {
  const files = listFiles(project);
  const hit = files.find((f) => path.basename(f) === taskId + '.json');
  if (!hit) return null;
  try {
    const r = JSON.parse(fs.readFileSync(hit, 'utf8'));
    // backfill: records written before P2.4 carry no slots; compute them on read (never written back,
    // so the file stays exactly as recorded).
    if (!Array.isArray(r.slots)) r.slots = extractSlots((r.steps || []).filter((s) => s.channel !== 'local' && s.ok !== false));
    return r;
  } catch (e) { return null; }
}

// 2026-10-21: 用户要能把记录清掉。**不真删** —— 移到 _trash/（可恢复），
// 因为"清掉了"和"删没了"是两件事，记录里虽然只有骨架，但仍是用户的工作痕迹。
function purge(project, ids) {
  const want = Array.isArray(ids) && ids.length ? new Set(ids) : null;
  const base = scopeDir(project);
  const trash = path.join(base, '_trash');
  const moved = [];
  for (const file of listFiles(project)) {
    const taskId = path.basename(file, '.json');
    if (want && !want.has(taskId)) continue;
    try {
      fs.mkdirSync(trash, { recursive: true });
      const dest = path.join(trash, path.basename(file));
      fs.renameSync(file, dest);
      moved.push(taskId);
    } catch (e) {
      console.error('[WORKFLOW] purge failed for ' + taskId + ': ' + e.message);
    }
  }
  return { moved: moved.length, ids: moved, trash };
}

function prune(project) {
  const files = listFiles(project);
  for (const f of files.slice(KEEP_PER_SCOPE)) {
    try { fs.unlinkSync(f); } catch (e) { /* best-effort: a stuck file must not block recording */ }
  }
}

// ---------- export ----------
function toMarkdown(r) {
  const L = [];
  L.push('# 工作流：' + (r.ask || '(未命名)'));
  L.push('');
  L.push('- 任务号：`' + r.taskId + '`');
  L.push('- 时间：' + r.createdAt);
  L.push('- 项目：' + (r.project || '（全局）'));
  L.push('- 图纸：' + (r.context.document || '（未记录）'));
  L.push('- 结果：' + (r.outcome.ok ? '成功' : '未成功') + ' · ' + r.outcome.calls + ' 步 · ' + Math.round(r.outcome.ms / 1000) + 's' + (r.outcome.failed ? ' · 失败 ' + r.outcome.failed : ''));
  L.push('- 复用评分：' + r.reusability.score + (r.reusability.blockers.length ? '（' + r.reusability.blockers.join('；') + '）' : ''));
  L.push('');
  L.push('## 步骤');
  L.push('');
  L.push('| # | 方法 | 作用 | 耗时 | 结果 |');
  L.push('|--:|---|---|--:|---|');
  const roleZh = { probe: '探查', act: '操作', verify: '验证' };
  for (const s of r.steps) {
    if (s.blocked) { L.push('| ' + s.i + ' | `' + s.method + '` | 被拦截 | — | ✗ |'); continue; }
    if (s.collapsed) continue;
    const poll = s.polling ? '（含 ' + s.polling + ' 次轮询）' : '';
    L.push('| ' + s.i + ' | `' + s.method + '`' + poll + ' | ' + (roleZh[s.role] || s.role) + ' | ' + (s.ms != null ? s.ms + 'ms' : '—') + ' | ' + (s.ok ? '✓' : '✗ ' + (s.errCode || '')) + ' |');
  }
  L.push('');
  L.push('## 参数骨架');
  L.push('');
  for (const s of r.steps) {
    if (s.blocked || !s.params || !Object.keys(s.params).length) continue;
    L.push('**' + s.i + '. ' + s.method + '**');
    L.push('```json');
    L.push(JSON.stringify(s.params, null, 2));
    L.push('```');
  }
  L.push('');
  L.push('> 本文件由 new-acad 生成，仅含调用摘要与参数骨架（不含结果数据）。');
  return L.join('\n') + '\n';
}

function exportOne(project, taskId, opts) {
  const r = get(project, taskId);
  if (!r) return { ok: false, error: 'not found' };
  const format = (opts && opts.format) || 'both';
  const outPath = (opts && opts.path) || '';
  const base = outPath || path.join(require('os').homedir(), 'Desktop', 'new-acad-workflow-' + taskId);
  const written = [];
  try {
    if (format === 'json' || format === 'both') {
      const p = base.endsWith('.json') ? base : base + '.json';
      fs.writeFileSync(p, JSON.stringify(r, null, 2) + '\n', 'utf8');
      written.push(p);
    }
    if (format === 'md' || format === 'both') {
      const p = base.endsWith('.md') ? base : (format === 'md' && base.endsWith('.json') ? base.replace(/\.json$/, '.md') : base + '.md');
      fs.writeFileSync(p, toMarkdown(r), 'utf8');
      written.push(p);
    }
  } catch (e) {
    return { ok: false, error: e.message, written };
  }
  return { ok: true, written, format };
}

module.exports = { record, list, get, exportOne, prune, purge, toMarkdown, roleOf, compactParams, sanitizeProject, scopeDir, fingerprint, READONLY, isLocalTool, collectHandles };
