'use strict';
// trust-model.js -- P2.5: 模块识别 + 信任计分（服务端）
//
// 契约来源：tools/test-trust-model.js（2026-10-23 先写测试后写实现，16 断言）＋
//          docs/design/2026-10-21-p2-workflow-capture.md §5.5。
// 铁律（Bro 2026-10-21）：**变快只能来自减少需要确认的步骤，不能来自降低确认标准。**
//
// 四条不变量（改这个文件前先读）：
//   1. C 档（不可逆）trust 恒为 0，任何配置/参数都打不开；tier 判在 trust 之前。
//   2. trust 绑 (project, module)，换项目从 0 开始。
//   3. 结果漂移 / 任一步失败 / 断言失败 / 取消后重放 → 归零（"快但结果变了"不是成功）。
//   4. 每次变动留痕（at/project/module/delta/before/after/reason），可读、可手动清零。
//
// 会话口径（Bro 2026-11-02 选 B）：trust=5 的"本会话不再询问"绑 **CAD 会话**
//   —— cadSessionId 变了（关 CAD 再开）就失效；relay 自己重启不影响。
//   调用方暂时没传 cadSessionId 时退化成"绑 relay 本次启动"（更保守，不会跨重启放宽）。
//
// 判据单一真相：docs/audit/destructive-methods.json（方法级），本文件不写死方法名。

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const RULES_PATH = process.env.TRUST_RULES_PATH || path.join(__dirname, '..', 'docs', 'audit', 'destructive-methods.json');
const ROOT = process.env.WORKFLOW_STORE_ROOT || path.join(__dirname, '..');
// relay 本次启动的标识：没拿到 cadSessionId 时的退化口径
const BOOT_ID = 'boot-' + crypto.randomBytes(3).toString('hex');

let rulesCache = null;
function loadRules() {
  if (rulesCache) return rulesCache;
  const j = JSON.parse(fs.readFileSync(RULES_PATH, 'utf8'));
  rulesCache = {
    tierA: new RegExp(j.tierA, 'i'),
    always: (j.tierC && j.tierC.always || []).map((r) => ({ re: new RegExp(r.pattern, 'i'), why: r.why || '' })),
    whenTrue: (j.tierC && j.tierC.whenTrue || []).map((r) => ({ re: new RegExp(r.pattern, 'i'), param: r.param, why: r.why || '' })),
    def: j.default || 'B',
  };
  return rulesCache;
}

const truthy = (v) => v === true || v === 'true' || v === 1 || v === '1';

/**
 * 一步的档位。params === undefined 表示调用方不知道参数 → 参数相关的规则按 C 保守处理。
 * 判据顺序固定：参数级 C → 名字级 C → A → 默认 B（默认绝不给 A）。
 */
function tierOf(method, params) {
  const rules = loadRules();
  const m = String(method || '');
  const known = params !== undefined && params !== null;
  const p = known ? params : {};

  for (const r of rules.whenTrue) {
    if (!r.re.test(m)) continue;
    if (!known) return 'C';                       // 不知道参数 → 不敢假设它没覆盖
    if (truthy(p[r.param])) return 'C';
  }
  for (const r of rules.always) if (r.re.test(m)) return 'C';
  if (rules.tierA.test(m)) return 'A';
  return rules.def;
}

/** 信任分 → 确认方式。C 档不看这个函数（tier 先判）。 */
function confirmMode(trust) {
  const t = Math.max(0, Math.min(5, Number(trust) | 0));
  if (t >= 5) return 'optional';
  if (t >= 3) return 'merged';
  return 'step';
}

// ---------------------------------------------------------------------------
// 模块识别：一组可复用步骤 = 方法名序列（忽略参数）
// 参数会变（句柄、坐标），方法序列才稳定；参数进模块键会让同一件事被拆成无数模块。
// ---------------------------------------------------------------------------
const MODULE_LIB = {
  createLineSegment: '画线', createPolyline: '画多段线', createCircle: '画圆', createCogoPoints: '建点',
  setEntitiesColor: '改颜色', createLayer: '建图层', offsetEntity: '偏移', copyEntity: '复制', moveEntity: '移动',
  getEntitiesByLayer: '读图层图元', getEntityInfo: '读图元信息', listLayers: '读图层表',
  assertChecks: '批量断言', verifyAssemblyGeometry: '校验装配', exportSelectionToFile: '导出选择集',
  createSurface: '建曲面', createAlignment: '建路线', createProfileFromSurface: '纵断面取面', createCorridor: '建廊道',
  createPipeNetwork: '建管网', importHatches: '批量填充', measureArea: '量面积', listMethods: '查方法表',
};

/**
 * 方法序列 → 稳定的模块 id（忽略参数）。
 * 返回 { id, names, label }：id 用于账本键，label 给面板显示（中文动作词拼接）。
 */
function moduleIdOf(steps) {
  const names = (steps || [])
    .filter((s) => s && s.method && s.channel !== 'local')
    .map((s) => String(s.method));
  if (!names.length) return null;
  const hash = crypto.createHash('sha1').update(names.join('>')).digest('hex').slice(0, 8);
  const label = names.slice(0, 3).map((n) => MODULE_LIB[n] || n).join(' → ') + (names.length > 3 ? ' → …' : '');
  return { id: 'mod-' + hash, names, label };
}

/** verify 角色步骤 → 断言计数（passed/failed）。没有 verify 步骤 = 0/0，不造假。 */
function assertionsFromSteps(steps) {
  let passed = 0, failed = 0;
  for (const s of steps || []) {
    if (!s || s.role !== 'verify') continue;
    if (s.ok === true) passed++;
    else if (s.ok === false) failed++;
  }
  return { passed, failed };
}

/**
 * 漂移：2026-11-03 Bro 拍板 A —— **只有 verify 步骤**算断言，只读探针的结果变化不算漂移。
 * 探针（getEntitiesByLayer 之类）本来就是看一眼，图一变指纹就对不上；拿它当否决权会让
 * 带探针的记录永远攒不到合并确认（实测 7 个模块全 0，3 条原因就是 result drift）。
 * 口径与回放引擎 server/workflow-assisted.js 的每步 drift 判定一致（门禁锁了这两处）。
 */
function driftOf(recordedSteps, replayedSteps) {
  const by = new Map((replayedSteps || []).map((s) => [s && s.method, s]));
  for (const old of recordedSteps || []) {
    if (!old || old.role !== 'verify') continue;
    if (!old.fp) continue;
    const now = by.get(old.method);
    if (now && now.fp && now.fp !== old.fp) return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// 账本：持久化到 exchange/projects/<project>/trust.json（无项目落 exchange/trust.json）
// 结构：{ version, projects: { <project>: { modules: { <module>: { trust, trace[] } } } },
//         waivers: [ { project, module, session, at } ] }
// ---------------------------------------------------------------------------
function ledgerPath(project) {
  const p = String(project || '').replace(/[\\/:*?"<>|.]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 64);
  return p
    ? path.join(ROOT, 'exchange', 'projects', p, 'trust.json')
    : path.join(ROOT, 'exchange', 'trust.json');
}

const EMPTY = () => ({ version: 1, projects: {}, waivers: [] });

function createLedger(opts) {
  const o = opts || {};
  const now = o.now || (() => new Date().toISOString());
  const files = new Map();     // project -> state（懒加载）
  const sessionId = o.sessionId || BOOT_ID;

  function load(project) {
    const key = project || '';
    if (files.has(key)) return files.get(key);
    const fp = o.file ? o.file : ledgerPath(project);
    let st = EMPTY();
    try {
      if (fs.existsSync(fp)) {
        const j = JSON.parse(fs.readFileSync(fp, 'utf8'));
        if (j && typeof j === 'object') st = Object.assign(EMPTY(), j);
      }
    } catch (_) { st = EMPTY(); }
    if (!st.projects) st.projects = {};
    if (!Array.isArray(st.waivers)) st.waivers = [];
    const entry = { file: fp, state: st };
    files.set(key, entry);
    return entry;
  }

  function save(project) {
    const e = load(project);
    try {
      fs.mkdirSync(path.dirname(e.file), { recursive: true });
      fs.writeFileSync(e.file, JSON.stringify(e.state, null, 2), 'utf8');
    } catch (err) {
      console.error('[TRUST] save failed: ' + err.message);
    }
  }

  function rec(project, module) {
    const e = load(project);
    const p = String(project || '');
    if (!e.state.projects[p]) e.state.projects[p] = { modules: {} };
    const mods = e.state.projects[p].modules;
    if (!mods[module]) mods[module] = { trust: 0, trace: [] };
    return mods[module];
  }

  const get = (project, module) => rec(project, module).trust;

  function note(project, module, delta, reason, at) {
    const r = rec(project, module);
    const before = r.trust;
    r.trust = Math.max(0, Math.min(5, before + delta));
    r.trace.push({ at: at || now(), project: String(project || ''), module, delta, before, after: r.trust, reason });
    if (r.trace.length > 200) r.trace.splice(0, r.trace.length - 200);
    save(project);
    return r.trust;
  }

  /**
   * 一次回放的结果入账。
   * outcome: { steps:[{method, params, ok}], assertions:{passed,failed}, drift:bool, cancelled:bool }
   * 顺序要紧：C 档先判（INV1），然后失败→取消→漂移→才算成功。
   */
  function recordReplay(project, module, outcome, at) {
    const steps = (outcome && outcome.steps) || [];
    const hasC = steps.some((s) => tierOf(s && s.method, s && s.params) === 'C');
    if (hasC) return note(project, module, 0, 'tier-C step present: trust cannot move', at);

    const a = (outcome && outcome.assertions) || { passed: 0, failed: 0 };
    const anyFailed = steps.some((s) => s && s.ok === false) || (a.failed > 0);
    if (anyFailed) return note(project, module, -get(project, module), 'a step or assertion failed -> reset', at);
    if (outcome && outcome.cancelled) return note(project, module, -get(project, module), 'user cancelled then replayed -> reset', at);
    if (outcome && outcome.drift) return note(project, module, -get(project, module), 'result drift (assertions disagree) -> reset', at);
    return note(project, module, +1, 'all tier-B steps and all assertions passed', at);
  }

  const trace = (project, module) => rec(project, module).trace.slice();
  const clear = (project, module, at) => note(project, module, -get(project, module), 'cleared by user', at);

  // ---- 会话豁免（选 B：绑 CAD 会话；没有 cadSessionId 时退化绑 relay 启动） ----
  function setWaiver(project, module, cadSessionId) {
    const e = load(project);
    const p = String(project || '');
    const sess = cadSessionId || sessionId;
    e.state.waivers = e.state.waivers.filter((w) => !(w.project === p && w.module === module));
    e.state.waivers.push({ project: p, module, session: sess, at: now() });
    save(project);
    return true;
  }

  function isWaived(project, module, cadSessionId) {
    const e = load(project);
    const p = String(project || '');
    const sess = cadSessionId || sessionId;
    const hit = e.state.waivers.find((w) => w.project === p && w.module === module && w.session === sess);
    return !!(hit && get(project, module) >= 5);   // 分数掉了，豁免自动作废
  }

  function clearWaiver(project, module) {
    const e = load(project);
    const p = String(project || '');
    const before = e.state.waivers.length;
    e.state.waivers = e.state.waivers.filter((w) => !(w.project === p && w.module === module));
    save(project);
    return before !== e.state.waivers.length;
  }

  /** 面板用：某项目下所有模块的信任总览 */
  function overview(project, cadSessionId) {
    const e = load(project);
    const p = String(project || '');
    const mods = (e.state.projects[p] && e.state.projects[p].modules) || {};
    return Object.entries(mods).map(([module, v]) => ({
      module,
      trust: v.trust,
      confirmMode: confirmMode(v.trust),
      waived: isWaived(project, module, cadSessionId),
      lastReason: v.trace.length ? v.trace[v.trace.length - 1].reason : '',
      changes: v.trace.length,
    })).sort((x, y) => y.trust - x.trust);
  }

  return { get, recordReplay, trace, clear, setWaiver, isWaived, clearWaiver, overview, _paths: () => [...files.values()].map((v) => v.file) };
}

module.exports = {
  tierOf, confirmMode, moduleIdOf, assertionsFromSteps, driftOf,
  createLedger, ledgerPath, loadRules, BOOT_ID,
};
