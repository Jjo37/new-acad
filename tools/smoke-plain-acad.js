#!/usr/bin/env node
// smoke-plain-acad.js -- 宿主端到端冒烟（2026-10-27 首版；2026-11-27 扩成两种宿主通吃）
//
// 在【装好的包里】跑这一条就能自证这个宿主能用（不需要 dev 仓、不需要 release-gate）：
//   纯 AutoCAD（civilAvailable=false）：
//     1 插件在线  2 面板方法目录已加载  3 方法清单完整  4 新建图纸  5 画圆（真的落图）
//     6 列图层    7 Civil 3D 专有方法明确报 CIVIL3D.CIVIL_NOT_INSTALLED（能力边界诚实）
//   Civil 3D（civilAvailable=true）：
//     1-6 同上，7 之后再跑真几何往返 —— 建曲面 → 按"自己知道答案"的点核对统计（点数/最小/最大高程）
//     → 删曲面 → 回读确认真没了（写操作闭环）
//
// 为什么要对已知输入核对：方法自己报的数字不能当判据，只断言"返回了数字"等于没验。
//
// 前置：CAD 已启动、插件在线（:8080）
// 用法：node tools/smoke-plain-acad.js [--evidence <cell.json>] [--selftest]
//   --evidence  写出宿主矩阵 cell（带产品/年份/运行时/插件提交），发布门禁按它认"这台宿主验过"
//   --selftest  只跑错误分类判定器的离线自测（不连 CAD）
// 注：--force 已不需要（本脚本自己按 civilAvailable 分派），传了也无害。

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
    let timer = null;
    // 2026-11-27: 旧版谁都不 clearTimeout，3 分钟的兜底计时器把事件循环续住 ——
    // 12 项全 PASS 之后进程还要赖着不退（异机上看就是「跑完卡住」）。清掉 + unref。
    const fin = (v) => { if (!done) { done = true; if (timer) clearTimeout(timer); try { s.destroy(); } catch { } resolve(v); } };
    s.setEncoding('utf8');
    s.on('connect', () => s.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) + '\n'));
    s.on('data', (d) => { b += d; });
    s.on('close', () => { try { const r = JSON.parse(b); fin(r.error ? { ok: false, error: r.error } : { ok: true, result: r.result }); } catch { fin({ ok: false, error: { message: 'bad json' } }); } });
    s.on('error', (e) => fin({ ok: false, error: { message: e.message } }));
    timer = setTimeout(() => fin({ ok: false, error: { message: 'timeout' } }), timeoutMs);
    if (timer.unref) timer.unref();
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

// 期望值判定单独成函数：离线自测要能证明「这条判据真的会变红」。
// 只断言"返回了数字"等于没验 —— 判据必须对得上我们自己喂进去的那组点。
function judgeSurfaceStats(result, want) {
  if (!result) return false;
  return result.numberOfPoints === want.count
    && Math.abs(Number(result.minimumElevation) - want.min) < 1e-6
    && Math.abs(Number(result.maximumElevation) - want.max) < 1e-6;
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
  const statsCases = [
    ['统计与输入一致', { numberOfPoints: 9, minimumElevation: 0, maximumElevation: 4 }, { count: 9, min: 0, max: 4 }, true],
    ['点数不符（裁掉一个点）', { numberOfPoints: 8, minimumElevation: 0, maximumElevation: 4 }, { count: 9, min: 0, max: 4 }, false],
    ['最大高程偏 0.5', { numberOfPoints: 9, minimumElevation: 0, maximumElevation: 4.5 }, { count: 9, min: 0, max: 4 }, false],
    ['字段缺失（空曲面）', { numberOfPoints: 0, minimumElevation: null, maximumElevation: null }, { count: 9, min: 0, max: 4 }, false],
    ['结果为空对象', {}, { count: 9, min: 0, max: 4 }, false],
  ];
  let bad = 0;
  for (const [name, result, want, expect] of statsCases) {
    const got = judgeSurfaceStats(result, want);
    const ok = got === expect;
    if (!ok) bad++;
    console.log((ok ? 'PASS' : 'FAIL') + '  ' + name + '  -> ' + got + ' (want ' + expect + ')');
  }
  const total = cases.length + statsCases.length;
  for (const [name, resp, want] of cases) {
    const got = classifyCivilGap(resp).gap;
    const ok = got === want;
    if (!ok) bad++;
    console.log((ok ? 'PASS' : 'FAIL') + '  ' + name + '  -> ' + got + ' (want ' + want + ')');
  }
  console.log(bad === 0 ? 'ALL PASS (' + total + ')' : 'FAILED: ' + bad + '/' + total);
  process.exit(bad === 0 ? 0 : 1);
}

if (process.argv.includes('--selftest')) selfTest();

(async () => {
  console.log('=== 宿主冒烟 (bridge :' + BRIDGE_PORT + ') ===');

  const health = await rpc('getCivil3DHealth', {});
  if (!health.ok) { record('插件在线', false, health.error.message); return finish(); }

  const h = health.result;
  lastHealth = h;
  record('插件在线', true, 'buildStamp=' + h.buildStamp);

  const isCivilHost = h.civilAvailable === true;
  console.log('宿主: ' + String(h.hostProduct || '?') + ' / ACADVER ' + String(h.civil3dVersion || '?') + ' / civilAvailable=' + h.civilAvailable);

  record(isCivilHost ? '宿主识别（Civil 3D：civilAvailable=true）' : '宿主识别（纯 CAD：civilAvailable=false）',
    isCivilHost ? h.civilAvailable === true : h.civilAvailable === false,
    'civilAvailable=' + h.civilAvailable + ' detail=' + String(h.civilApiDetail || '').slice(0, 60));

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

  if (isCivilHost) {
    await civilSurfaceRoundTrip();
  } else {
    const surfaces = await rpc('listSurfaces', {});
    const gap = classifyCivilGap(surfaces);
    record('Civil 3D 方法明确报 CIVIL_NOT_INSTALLED', gap.gap,
      gap.gap
        ? 'domain=' + gap.domain + ' numeric=' + gap.numeric + ' msg=' + gap.msg.slice(0, 40)
        : (surfaces.ok ? '居然成功了 —— 纯 CAD 上不应如此' : 'domain=' + (gap.domain || '(none)') + ' numeric=' + gap.numeric + ' msg=' + gap.msg.slice(0, 60)));
  }

  return finish();
})();

// Civil 3D 真几何往返：建曲面 -> 加一组"自己知道答案"的点 -> 核对统计 -> 删掉 -> 回读确认。
// 期望值来自我们喂进去的点（点数 / 最小高程 / 最大高程），不是方法自己报的数。
async function civilSurfaceRoundTrip() {
  const name = 'newacad-hostsmoke-' + Date.now().toString(36);
  const GRID = 3;
  const STEP = 10;
  const pts = [];
  for (let i = 0; i < GRID; i++) {
    for (let j = 0; j < GRID; j++) pts.push({ x: i * STEP, y: j * STEP, z: i + j });
  }
  const wantMin = 0;
  const wantMax = (GRID - 1) * 2;
  const wantCount = GRID * GRID;

  const created = await rpc('createSurface', { name: name }, 60000);
  record('C3D 建曲面', created.ok && !!(created.result && created.result.created === true),
    created.ok
      ? 'name=' + (created.result && created.result.name) + ' handle=' + (created.result && created.result.handle)
      : JSON.stringify(created.error).slice(0, 120));
  if (!created.ok) return;

  const added = await rpc('addSurfacePoints', { name: name, points: pts }, 180000);
  record('C3D 加曲面点', added.ok && !!(added.result && added.result.pointsAdded === pts.length),
    added.ok
      ? 'pointsAdded=' + (added.result && added.result.pointsAdded) + ' / 传入 ' + pts.length
      : JSON.stringify(added.error).slice(0, 120));

  const st = await rpc('getSurfaceStatistics', { name: name }, 120000);
  const r = (st.ok && st.result) || {};
  const okStats = st.ok && judgeSurfaceStats(r, { count: wantCount, min: wantMin, max: wantMax });
  record('C3D 曲面统计与输入一致', okStats,
    st.ok
      ? 'points=' + r.numberOfPoints + '/' + wantCount + ' min=' + r.minimumElevation + '/' + wantMin
        + ' max=' + r.maximumElevation + '/' + wantMax + ' triangles=' + r.numberOfTriangles
      : JSON.stringify(st.error).slice(0, 120));

  const del = await rpc('deleteSurface', { name: name }, 60000);
  record('C3D 删曲面（写操作闭环）', del.ok && !!(del.result && del.result.deleted === true),
    del.ok ? 'deleted=' + (del.result && del.result.deleted) : JSON.stringify(del.error).slice(0, 120));

  const after = await rpc('listSurfaces', {});
  const list = (after.ok && after.result && after.result.surfaces) || [];
  const still = list.filter((s) => s && s.name === name);
  record('C3D 曲面已消失（回读校验）', after.ok && still.length === 0,
    after.ok ? ('剩余同名 ' + still.length + ' 个 / 图中共 ' + list.length + ' 个曲面') : after.error.message);
}

function pluginDllPath() {
  const root = process.env.CIVIL3D_ROOT || path.join(__dirname, '..');
  return path.join(root, 'plugin', 'AcBridge-v24', 'Civil3DMcpPlugin.dll');
}

function writeEvidence(extra) {
  if (!EVIDENCE) return;
  // 2026-11-23 P35: 证据升级成宿主矩阵 cell（v2）——带宿主产品/年份/运行时/插件提交，
  // 门禁（tools/host-matrix.js）按这些字段判断「这一格本版验过没有」。
  const M = require('./host-matrix');
  const dll = pluginDllPath();
  let sha = '';
  try { sha = crypto.createHash('sha256').update(fs.readFileSync(dll)).digest('hex'); } catch { sha = ''; }
  const hostRaw = (extra && extra.hostProduct) || '';
  const product = M.productFromHealth({ hostProduct: hostRaw, civilAvailable: extra && extra.civilAvailable !== undefined ? extra.civilAvailable : null });
  // 2026-11-24: hostProduct 经常只有产品名（纯 AutoCAD 就回 "AutoCAD"），年份得从 ACADVER 取。
  // health.civil3dVersion = (getvar "ACADVER")，如 "26.0s (LMS Tech)" -> 2027。取不到才退回产品串。
  const yearsByAcadver = M.yearsFromAcadver(extra && extra.acadver);
  const years = yearsByAcadver.length ? yearsByAcadver : M.yearsFromProduct(hostRaw);
  let cellId = '';
  try {
    const contract = M.loadContract(path.join(__dirname, '..'));
    // 同产品有多条年份条目：必须按年份挑，否则 2027 的机器会记到 autocad-2025 名下。
    const host = contract.hosts.find((h) => h.product === product && (!years.length || (Array.isArray(h.years) && h.years.some((y) => years.includes(y)))))
      || contract.hosts.find((h) => h.product === product);
    if (host) cellId = host.id;
  } catch { /* 契约读不到就留空，门禁会点名 */ }
  const failedChecks = results.filter((r) => r.ok === false).length;
  const payload = Object.assign({
    schema: M.SCHEMA_CELL,
    id: cellId,
    product,
    productRaw: hostRaw,
    years,
    runtime: (extra && extra.runtime) || '',
    // 装好的包里没有 .git → 退回 buildStamp 里的完整提交号（否则异机产出的 cell 永远不认）
    // 提交号优先级：包内 tools/plugin-commit.txt（打包时写入）→ git（dev 仓）→ buildStamp
    commit: (() => {
      try {
        const f = path.join(__dirname, 'plugin-commit.txt');
        if (fs.existsSync(f)) return fs.readFileSync(f, 'utf8').trim();
      } catch { }
      return M.pluginCommit(path.join(__dirname, '..')) || M.commitFromBuildStamp(extra && extra.buildStamp);
    })(),
    hostVersion: (extra && extra.buildStamp) || '',
    generatedAt: new Date().toISOString(),
    machine: os.hostname(),
    os: os.platform() + ' ' + os.release(),
    pluginDll: dll,
    pluginDllSha256: sha,
    source: 'smoke-plain-acad.js',
    result: failedChecks === 0 ? 'pass' : 'fail',
    checks: results,
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
    acadver: lastHealth ? lastHealth.civil3dVersion : null,
    runtime: lastHealth ? (lastHealth.runtime || '') : '',
    palette: lastHealth ? (lastHealth.palette || null) : null,
    note: isPlainHost === false ? 'ran on a Civil 3D host — NOT valid evidence for plain AutoCAD' : undefined
  });
  process.exitCode = failed === 0 ? 0 : 1;
}
