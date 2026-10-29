'use strict';
// workflow-assisted.js -- P2.3: assisted replay with handle remapping (2026-10-22, 1.11.0 line)
//
// Why this file exists: dry-run only proves read-only steps still answer the same. The value
// is replaying a *write* workflow, and that is exactly where it can destroy work. So the engine
// is split from the transport and every dangerous decision is a pure function that can be tested
// without CAD:
//
//   classify(method, params)  -> 'A' | 'B' | 'C'      (by reversibility, design §5.5)
//   planAssisted(record, opts)-> executable plan      (handle remap + per-step tier/confirm)
//   Session                    -> one approved step at a time, in order, never out of order
//
// Hard invariants (design §5.5, must stay true no matter what the caller passes):
//   1. Tier C is ALWAYS confirmed step by step. `trust` cannot batch it, any value is ignored.
//   2. Steps run strictly in record order; a failed/denied step aborts the session.
//   3. A handle that came from an earlier step of THIS workflow is remapped to the new value;
//      if a step needs one that was never produced, the session stops and reports it.
//   4. Nothing here talks to CAD directly: `call` is injected, so tests drive it with stubs.

const crypto = require('crypto');
const { READONLY, isLocalTool, collectHandles, fingerprint } = require('./workflow-store');
const { applyBindings, summarizeEffects } = require('./workflow-slots');

// Destructive / irreversible: file overwrite, deletes, bulk clean-up, arbitrary code, exports.
// Kept as an explicit list on purpose -- "looks like a write" is not enough to make it tier C.
const DESTRUCTIVE = /^(delete|erase|purge|overkill|remove|clear|drop|detach|unlink|overwrite|import|write|save|export|executeCommand|runLisp|attach|convert|replace)/i;

function classify(method) {
  const m = String(method || '');
  if (DESTRUCTIVE.test(m)) return 'C';
  if (READONLY.test(m) && !DESTRUCTIVE.test(m)) return 'A';
  return 'B';
}

// Handles are opaque strings; we only remap values we actually saw produced by this workflow.

// Handles a step *receives* (they are references, never "produced" by that step).
function paramHandles(params) {
  const out = [];
  const walk = (node, depth) => {
    if (!node || typeof node !== 'object' || depth > 4) return;
    if (Array.isArray(node)) { for (const v of node) walk(v, depth + 1); return; }
    for (const [k, v] of Object.entries(node)) {
      if (/handle/i.test(k)) {
        const list = Array.isArray(v) ? v : [v];
        for (const h of list) if (typeof h === 'string' && !out.includes(h)) out.push(h);
      } else walk(v, depth + 1);
    }
  };
  walk(params, 0);
  return out;
}

// Which params of this step point at a handle produced earlier in the same workflow?
function remapTargets(step, producedBy) {
  const hits = [];
  const walk = (node, path, depth) => {
    if (!node || depth > 4 || hits.length > 32) return;
    if (Array.isArray(node)) { node.forEach((v, i) => walk(v, path + '[' + i + ']', depth + 1)); return; }
    if (typeof node === 'object') { for (const [k, v] of Object.entries(node)) walk(v, path ? path + '.' + k : k, depth + 1); return; }
    if (typeof node !== 'string') return;
    const from = producedBy.get(node);
    if (from !== undefined && from < step.i) hits.push({ path, from, value: node });
  };
  walk(step.params || {}, '', 0);
  return hits;
}

function planAssisted(record, opts) {
  const steps = (record && record.steps) || [];
  const sameDocument = !!(opts && opts.sameDocument);
  const trust = Number((opts && opts.trust) || 0);

  const producedBy = new Map();       // old handle -> step index that produced it
  // 2026-10-22 (real record wf-20261022-165909-8f42): a READ also reports the handles it was asked
  // about, so "who produced D6A" was being answered with a later probe -> the earlier step then
  // demanded a handle that this run had not created yet (needs-remap dead end).
  // Only write-style steps produce; a handle that is also an input of the same step is not "new".
  for (const s of steps) {
    if (s.ok === false) continue;
    if (!/^(create|add|insert|copy|offset|array|mirror|break|explode)/i.test(String(s.method || ''))) continue;
    const inputs = new Set(paramHandles(s.params));
    for (const h of s.handlesOut || []) if (!inputs.has(h) && !producedBy.has(h)) producedBy.set(h, s.i);
  }

  const plan = [];
  const skipped = [];
  const blocked = [];
  for (const s of steps) {
    if (s.blocked || s.collapsed) { skipped.push({ i: s.i, method: s.method, why: 'blocked/collapsed', code: 'blocked-or-collapsed' }); continue; }
    if (s.channel === 'local' || isLocalTool(s.method)) { skipped.push({ i: s.i, method: s.method, why: 'local agent tool', code: 'local-tool' }); continue; }
    // 2026-10-22: a step that failed in the record is a defect trail, not an instruction.
    // Replaying it aborts the whole run on a known-bad call (real case: getEntityInfo with
    // `handles` instead of `handle` -> -32602, everything after it never ran).
    if (s.ok === false) { skipped.push({ i: s.i, method: s.method, why: 'failed in the record (not replayable)', code: 'failed-in-record' }); continue; }
    const tier = classify(s.method);
    const remap = remapTargets(s, producedBy);
    // A step may reference a handle that this workflow never produced: fine on the same drawing,
    // fatal on another one (the number means something else there).
    const externalHandles = [];
    const scan = (node) => {
      if (!node || typeof node !== 'object') return;
      if (Array.isArray(node)) { node.forEach(scan); return; }
      for (const [k, v] of Object.entries(node)) {
        if (/^(handle|handles|handleOut)$/i.test(k)) {
          const list = Array.isArray(v) ? v : [v];
          for (const h of list) if (typeof h === 'string' && !producedBy.has(h) && !remap.some((r) => r.value === h)) externalHandles.push(h);
        } else scan(v);
      }
    };
    scan(s.params);

    const entry = {
      i: s.i, method: s.method, tier, remap,
      externalHandles: externalHandles.slice(0, 8),
      needsConfirm: tier !== 'A',
      params: s.params || {},
      fp: s.fp || '',
      expectedHandles: (s.handlesOut || []).slice(0, 8),
    };
    if (!sameDocument && externalHandles.length) {
      entry.blocked = 'reads a handle that did not come from this workflow, and the drawing changed';
      entry.blockedCode = 'external-handle-other-drawing';
      blocked.push({ i: s.i, method: s.method, why: entry.blocked, code: entry.blockedCode });
    }
    plan.push(entry);
  }

  // Batch permission is the only thing trust can buy, and only for tier B.
  const batchable = trust >= 3 && !plan.some((p) => p.tier === 'C');
  return {
    taskId: record && record.taskId,
    document: (record && record.context && record.context.document) || '',
    sameDocument,
    trust,
    batchable,
    steps: plan,
    skipped,
    blocked,
    ok: blocked.length === 0,
  };
}

function createSession(record, opts) {
  // 2026-10-22 P2.4: bindings replace the recorded numbers before planning. Ungrounded slots
  // never fall back to the record (rule 3) -- they come back as `unbound` and block the session.
  const bindings = (opts && opts.bindings) || null;
  let steps = (record && record.steps) || [];
  let bindingReport = null;
  if (bindings && record && Array.isArray(record.slots)) {
    const applied = applyBindings(steps, record.slots, bindings);
    bindingReport = { unbound: applied.unbound, invalid: applied.invalid, values: applied.values };
    steps = applied.steps;
  }
  const boundRecord = Object.assign({}, record, { steps });
  const planResult = planAssisted(boundRecord, opts);
  const session = {
    token: crypto.randomBytes(12).toString('hex'),
    taskId: record && record.taskId,
    project: (opts && opts.project) || '',
    createdAt: Date.now(),
    plan: planResult,
    order: planResult.steps.map((s) => s.i),
    cursor: 0,
    handleMap: new Map(),        // old handle -> new handle
    results: [],
    done: false,
    aborted: null,
    bindingReport,
    slotValues: (bindingReport && bindingReport.values) || {},
  };
  return session;
}

function nextStep(session) {
  if (!session || session.done || session.aborted) return null;
  const i = session.order[session.cursor];
  if (i === undefined) return null;
  return session.plan.steps.find((s) => s.i === i) || null;
}

// Execute exactly one step. `approve` must be true for tier B/C -- the engine never assumes it
// (that is what makes "assisted" mean something).
async function runStep(session, call, opts) {
  const step = nextStep(session);
  if (!step) return { status: 'done' };
  const approve = !!(opts && opts.approve);
  const tier = step.tier;

  // Invariant 1: C is never satisfied by anything but an explicit approve for THIS step.
  if (tier !== 'A' && !approve) {
    return { status: 'awaiting-confirm', step };
  }

  const params = JSON.parse(JSON.stringify(step.params || {}));
  const applied = [];
  for (const r of step.remap) {
    const fresh = session.handleMap.get(r.value);
    if (!fresh) {
      // Dead end on purpose: the step needs an entity this run never created, so nothing after it
      // can be trusted. Abort loudly instead of letting the user press "approve" forever.
      session.aborted = 'step ' + step.i + ' (' + step.method + ') needs handle ' + r.value +
        ', which this replay never produced -- the record predates handle capture or the earlier step failed';
      return { status: 'needs-remap', step, missing: r.value, aborted: session.aborted };
    }
    setPath(params, r.path, fresh);
    applied.push({ path: r.path, from: r.value, to: fresh });
  }

  const t0 = Date.now();
  const r = await call(step.method, params);
  const ms = Date.now() - t0;
  const ok = !!(r && r.ok !== false);
  const result = ok ? r.result : null;
  const errCode = ok ? '' : String((r && r.error && (r.error.code || r.error.message)) || 'unknown');

  let fp = '';
  if (ok) {
    try { fp = fingerprint(result); } catch (e) { fp = ''; }
  }

  const newHandles = ok ? collectHandles(result) : [];
  // Map old -> new positionally: a step that created N handles hands them back in order.
  const expected = step.expectedHandles || [];
  for (let k = 0; k < newHandles.length; k++) {
    const oldH = expected[k];
    if (oldH) session.handleMap.set(oldH, newHandles[k]);
  }
  for (const h of newHandles) session.handleMap.set(h, h);

  const record = {
    i: step.i,
    method: step.method,
    tier,
    status: ok ? 'ok' : 'error',
    ms,
    errCode,
    fp,
    // 2026-10-22: drift only means something for read steps. A write returns a fresh handle, so
    // its fingerprint ALWAYS differs -- flagging that as "drift" trained the eye to ignore it.
    drift: ok && tier === 'A' && step.fp ? fp !== step.fp : false,
    handles: newHandles.slice(0, 8),
    remapped: applied,
    params,                       // the params actually sent (bound + remapped) -- effect counting needs them
    rawResult: ok ? slimResult(result) : null,
  };
  session.results.push(record);
  session.cursor++;

  if (!ok) { session.aborted = 'step ' + step.i + ' failed: ' + errCode; }
  else if (session.cursor >= session.order.length) session.done = true;

  return record;
}

function setPath(obj, path, value) {
  const parts = String(path).replace(/\[(\d+)\]/g, '.$1').split('.').filter(Boolean);
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    if (cur[parts[i]] === undefined) return;
    cur = cur[parts[i]];
  }
  if (cur && typeof cur === 'object') cur[parts[parts.length - 1]] = value;
}

// Keep only what the verification report needs; a full result can be megabytes.
function slimResult(result) {
  if (!result || typeof result !== 'object') return null;
  const out = {};
  for (const k of ['passed', 'failed', 'count', 'total', 'created', 'deleted', 'modified', 'length', 'area', 'volume', 'ok']) {
    if (typeof result[k] === 'number' || typeof result[k] === 'boolean') out[k] = result[k];
  }
  return Object.keys(out).length ? out : null;
}

function summarize(session) {
  return summarizeEffects(session && session.results, session && session.slotValues);
}

module.exports = { classify, collectHandles, planAssisted, createSession, nextStep, runStep, summarize, DESTRUCTIVE };
