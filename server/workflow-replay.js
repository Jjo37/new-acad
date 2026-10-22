'use strict';
// workflow-replay.js -- P2.2: dry-run replay (read-only steps only).
//
// What it proves: the recorded parameters still work against the current drawing, and the
// results come back the same (fingerprints match). It NEVER executes a step with side effects:
// anything that is not a read-only method is reported as "skipped (needs assisted mode)".
//
// Why start here: P2.3 (assisted replay with handle remapping) is where the danger lives.
// Dry-run is the cheap, safe half that also validates the record format itself.

const net = require('net');
const { READONLY, fingerprint, isLocalTool } = require('./workflow-store');

// extra safety net: even if a method starts with get/list/..., these must never run unattended
const DENY = /(executeCommand|runLisp|writeFile|writeTextFile|saveDrawing|export|delete|purge|overkill|import|create|set[A-Z])/;

function isReadOnly(method) {
  const m = String(method || '');
  if (DENY.test(m)) return false;
  return READONLY.test(m);
}

function tcpCall(method, params, opts) {
  const port = (opts && opts.port) || 8080;
  const timeout = (opts && opts.timeoutMs) || 30000;
  return new Promise((resolve) => {
    const c = net.connect(port, '127.0.0.1');
    let buf = '';
    let done = false;
    const fin = (v) => { if (done) return; done = true; try { c.destroy(); } catch (e) { /* socket already gone */ } resolve(v); };
    c.on('connect', () => c.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method, params: params || {} }) + '\n'));
    c.on('data', (d) => {
      buf += d;
      try {
        const j = JSON.parse(buf.split('\n')[0]);
        fin(j.error ? { ok: false, error: j.error } : { ok: true, result: j.result });
      } catch (e) { /* partial line */ }
    });
    c.on('error', (e) => fin({ ok: false, error: { message: e.message } }));
    setTimeout(() => fin({ ok: false, error: { code: 'TIMEOUT', message: 'timeout ' + timeout + 'ms' } }), timeout);
  });
}

function plan(record) {
  const steps = (record && record.steps) || [];
  const runnable = [];
  const skipped = [];
  for (const s of steps) {
    if (s.blocked || s.collapsed) { skipped.push({ i: s.i, method: s.method, why: 'blocked/collapsed' }); continue; }
    // 2026-10-22: agent 本地工具不是插件方法，调了只会 -32601；旧记录没有 channel 字段，也按方法名兜住
    if (s.channel === 'local' || isLocalTool(s.method)) { skipped.push({ i: s.i, method: s.method, why: 'local agent tool — nothing to replay' }); continue; }
    if (!isReadOnly(s.method)) { skipped.push({ i: s.i, method: s.method, why: 'side effects — needs assisted mode' }); continue; }
    runnable.push(s);
  }
  return { runnable, skipped };
}

async function dryRun(record, opts) {
  const call = (opts && opts.call) || ((m, p) => tcpCall(m, p, opts));
  const { runnable, skipped } = plan(record);
  const steps = [];
  let matched = 0, mismatched = 0, errors = 0, noFp = 0;

  for (const s of runnable) {
    const t0 = Date.now();
    const r = await call(s.method, s.params || {});
    const ms = Date.now() - t0;
    if (!r || r.ok === false) {
      errors++;
      steps.push({ i: s.i, method: s.method, status: 'error', ms, error: (r && r.error && (r.error.code || r.error.message)) || 'unknown' });
      continue;
    }
    const fp = fingerprint(r.result);
    if (!s.fp) { noFp++; steps.push({ i: s.i, method: s.method, status: 'no-fingerprint', ms, fp }); continue; }
    if (fp === s.fp) { matched++; steps.push({ i: s.i, method: s.method, status: 'match', ms, fp }); }
    else { mismatched++; steps.push({ i: s.i, method: s.method, status: 'mismatch', ms, fp, expected: s.fp }); }
  }

  const ok = errors === 0 && mismatched === 0;
  return {
    ok,
    mode: 'dry-run',
    taskId: record && record.taskId,
    ran: runnable.length,
    skipped: skipped.length,
    matched,
    mismatched,
    errors,
    noFingerprint: noFp,
    steps,
    skippedSteps: skipped.slice(0, 20),
  };
}

module.exports = { plan, dryRun, tcpCall, isReadOnly };
