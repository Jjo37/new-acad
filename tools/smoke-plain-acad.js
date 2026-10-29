#!/usr/bin/env node
// smoke-plain-acad.js — 纯 AutoCAD 宿主的端到端冒烟（2026-10-27）
//
// 在"没有 Civil 3D"的机器上跑这一条就够了（10 秒）：
//   1 面板连接 + 宿主识别（civilAvailable=false）
//   2 面板方法目录真的加载（palette.catalogLoaded + rows ≥ 400）
//   3 listMethods ≥ 400 且含 createCircle / createPolyline
//   4 新建图纸
//   5 画圆（真的落图，返回 handle）
//   6 列图层
//   7 Civil 3D 专有方法 → CIVIL3D.CIVIL_NOT_INSTALLED（不是 INTERNAL_ERROR）
//
// 在 Civil 3D 宿主上会自动 SKIP（并提示改用 release-gate --with-cad）。
// 前置：CAD 已启动、插件在线（:8080 / relay :19876）。
// 用法：node tools/smoke-plain-acad.js [--force]   （--force 在 C3D 上也跑，仅做连通性冒烟）

const net = require('net');
const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const os = require('os');

const ROOT = path.join(__dirname, '..');
const CFG = (() => { try { return JSON.parse(fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8').replace(/^\uFEFF/, '')); } catch { return {}; } })();
const BRIDGE_PORT = CFG.bridgePort || 8080;
const FORCE = process.argv.includes('--force');
// --evidence <file>: write a machine-readable proof file so the release pipeline (which runs on
// a different machine) can require "plain AutoCAD was actually verified for THIS dll".
const EVIDENCE = (() => {
  const i = process.argv.indexOf('--evidence');
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : '';
})();

const results = [];
let lastHealth = null;
function record(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log((ok === 'skip' ? 'SKIP | ' : ok ? 'PASS | ' : 'FAIL | ') + name + (detail ? '  (' + detail + ')' : ''));
}

function rpc(method, params = {}, timeoutMs = 30000) {
  return new Promise((resolve) => {
    const s = net.connect(BRIDGE_PORT, '127.0.0.1');
    let b = '';
    let done = false;
    const fin = (v) => { if (!done) { done = true; try { s.destroy(); } catch { } resolve(v); } };
    s.setEncoding('utf8');
    s.on('connect', () => s.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) + '\n'));
    s.on('data', (d) => { b += d; });
    s.on('close', () => { try { const r = JSON.parse(b); fin(r.error ? { ok: false, error: r.error } : { ok: true, result: r.result }); } catch { fin({ ok: false, error: { message: 'bad json' } }); } });
    s.on('error', (e) => fin({ ok: false, error: { message: e.message } }));
    setTimeout(() => fin({ ok: false, error: { message: 'timeout' } }), timeoutMs);
  });
}

// The domain error code lives in error.data.code; error.code is the numeric JSON-RPC code
// (-32603 = JSON-RPC "internal error", the same number a genuine crash carries).
// 2026-10-29: the first version read error.code first, so it always saw the numeric value and
// reported a FAIL on the plain-AutoCAD machine that was really a false negative of this script.
const GENERIC_INTERNAL_MESSAGE = 'The Civil 3D plugin encountered an unexpected error.';
function classifyCivilGap(resp) {
  if (!resp || resp.ok) return { gap: false, domain: '', numeric: NaN, msg: resp && resp.ok ? '(call succeeded)' : '(no response)' };
  const err = resp.error || {};
  const domain = String((err.data && err.data.code) || '');
  const numeric = Number(err.code);
  const msg = String(err.message || '');
  // A capability gap must be labelled with its own domain code AND carry a real explanation;
  // falling through to the generic internal-error text would mean the JIT-gap path did not run.
  const gap = domain === 'CIVIL3D.CIVIL_NOT_INSTALLED' && !!msg && msg !== GENERIC_INTERNAL_MESSAGE;
  return { gap, domain, numeric, msg };
}

function selfTest() {
  const cases = [
    ['jit gap (-32603 + domain code)', { ok: false, error: { code: -32603, message: '这个方法是 Civil 3D 专有的。', data: { code: 'CIVIL3D.CIVIL_NOT_INSTALLED' } } }, true],
    ['require() gap (-32000 + domain code)', { ok: false, error: { code: -32000, message: '这个方法是 Civil 3D 专有的。', data: { code: 'CIVIL3D.CIVIL_NOT_INSTALLED' } } }, true],
    ['real crash (generic internal message)', { ok: false, error: { code: -32603, message: GENERIC_INTERNAL_MESSAGE, data: { code: 'CIVIL3D.INTERNAL_ERROR' } } }, false],
    ['domain code but no message', { ok: false, error: { code: -32603, message: '', data: { code: 'CIVIL3D.CIVIL_NOT_INSTALLED' } } }, false],
    ['call succeeded', { ok: true, result: {} }, false],
    ['no response', { ok: false, error: { message: 'timeout' } }, false],
  ];
  let bad = 0;
  for (const [name, resp, want] of cases) {
    const got = classifyCivilGap(resp).gap;
    const ok = got === want;
    if (!ok) bad++;
    console.log((ok ? 'PASS' : 'FAIL') + '  ' + name + '  -> ' + got + ' (want ' + want + ')');
  }
  console.log(bad === 0 ? 'ALL PASS (' + cases.length + ')' : 'FAILED: ' + bad + '/' + cases.length);
  process.exit(bad === 0 ? 0 : 1);
}

if (process.argv.includes('--selftest')) selfTest();

(async () => {
  console.log('=== 纯 AutoCAD 冒烟 (bridge :' + BRIDGE_PORT + ') ===');

  const health = await rpc('getCivil3DHealth', {});
  if (!health.ok) { record('插件在线', false, health.error.message); return finish(); }

  const h = health.result;
  lastHealth = h;
  record('插件在线', true, 'buildStamp=' + h.buildStamp);

  // A Civil 3D host makes this smoke meaningless: skip cleanly instead of failing the
  // host-identification line (that was a false FAIL in the first draft of this script).
  if (h.civilAvailable === true && !FORCE) {
    record('纯 AutoCAD 冒烟', 'skip',
      '当前是 Civil 3D 宿主（civilAvailable=true）-> 用 node tools/release-gate.js --with-cad；要强制跑加 --force');
    return finish();
  }

  record('宿主识别（纯 CAD：civilAvailable=false）', h.civilAvailable === false,
    'civilAvailable=' + h.civilAvailable + ' detail=' + String(h.civilApiDetail).slice(0, 60));

  // The expectation is whatever the backend advertises right now (no hardcoded count).
  const lm0 = await rpc('listMethods', {});
  const expectedCount = (lm0.ok && lm0.result && Array.isArray(lm0.result.methods)) ? lm0.result.methods.length : 0;
  const pal = h.palette || null;
  record('面板方法目录已加载', !!(pal && pal.catalogLoaded && expectedCount > 0 && pal.rows >= expectedCount),
    pal ? ('catalogLoaded=' + pal.catalogLoaded + ' rows=' + pal.rows + ' / expected ' + expectedCount) : 'health 无 palette 字段');

  const methods = (lm0.ok && lm0.result && lm0.result.methods) || [];
  const needed = ['createCircle', 'createPolyline', 'getLayers'];
  const missing = needed.filter((m) => !methods.includes(m));
  record('方法清单完整', methods.length === expectedCount && expectedCount > 0 && missing.length === 0,
    methods.length + ' 条' + (missing.length ? '，缺 ' + missing.join(',') : ''));

  const nd = await rpc('newDrawing', {}, 60000);
  record('新建图纸', nd.ok, nd.ok ? String((nd.result && nd.result.drawingName) || '') : nd.error.message);

  const circle = await rpc('createCircle', { center: [25, 25, 0], diameter: 10 }, 60000);
  record('画圆（真的落图）', circle.ok && !!(circle.result && circle.result.handle),
    circle.ok ? 'handle=' + (circle.result && circle.result.handle) : JSON.stringify(circle.error).slice(0, 80));

  const layers = await rpc('getLayers', {});
  record('列图层', layers.ok && Array.isArray(layers.result && layers.result.layers),
    layers.ok ? String((layers.result.layers || []).length) + ' 个图层' : layers.error.message);

  const surfaces = await rpc('listSurfaces', {});
  const gap = classifyCivilGap(surfaces);
  record('Civil 3D 方法明确报 CIVIL_NOT_INSTALLED', gap.gap,
    gap.gap
      ? 'domain=' + gap.domain + ' numeric=' + gap.numeric + ' msg=' + gap.msg.slice(0, 40)
      : (surfaces.ok ? '居然成功了 —— 纯 CAD 上不应如此' : 'domain=' + (gap.domain || '(none)') + ' numeric=' + gap.numeric + ' msg=' + gap.msg.slice(0, 60)));

  return finish();
})();

function pluginDllPath() {
  const root = process.env.CIVIL3D_ROOT || path.join(__dirname, '..');
  return path.join(root, 'plugin', 'AcBridge-v24', 'Civil3DMcpPlugin.dll');
}

function writeEvidence(extra) {
  if (!EVIDENCE) return;
  const dll = pluginDllPath();
  let sha = '';
  try { sha = crypto.createHash('sha256').update(fs.readFileSync(dll)).digest('hex'); } catch { sha = ''; }
  const payload = Object.assign({
    schema: 'new-acad.host-evidence.v1',
    generatedAt: new Date().toISOString(),
    machine: os.hostname(),
    os: os.platform() + ' ' + os.release(),
    pluginDll: dll,
    pluginDllSha256: sha,
    checks: results
  }, extra || {});
  try {
    fs.mkdirSync(path.dirname(path.resolve(EVIDENCE)), { recursive: true });
    fs.writeFileSync(EVIDENCE, JSON.stringify(payload, null, 2) + '\n', 'utf8');
    console.log('[evidence] ' + EVIDENCE + ' (dllSha256=' + (sha ? sha.slice(0, 16) + '…' : 'unknown') + ')');
  } catch (e) {
    console.error('[evidence] write failed: ' + e.message);
  }
}

function finish() {
  const failed = results.filter((r) => r.ok === false).length;
  const skipped = results.filter((r) => r.ok === 'skip').length;
  const passed = results.filter((r) => r.ok === true).length;
  console.log('\n合计: ' + passed + ' passed, ' + failed + ' failed, ' + skipped + ' skipped');
  const isPlainHost = lastHealth ? lastHealth.civilAvailable === false : null;
  writeEvidence({
    passed: failed === 0 && skipped === 0,
    skipped: skipped > 0,
    civilAvailable: lastHealth ? lastHealth.civilAvailable : null,
    buildStamp: lastHealth ? lastHealth.buildStamp : null,
    pluginVersion: lastHealth ? lastHealth.pluginVersion : null,
    hostProduct: lastHealth ? lastHealth.hostProduct : null,
    palette: lastHealth ? (lastHealth.palette || null) : null,
    note: isPlainHost === false ? 'ran on a Civil 3D host — NOT valid evidence for plain AutoCAD' : undefined
  });
  process.exitCode = failed === 0 ? 0 : 1;
}
