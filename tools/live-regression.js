// new-acad 真机回归套件 —— 需要在开着的 Civil 3D 上跑（插件在线 :8080）
// 用法: node tools/live-regression.js          # 全量
//       node tools/live-regression.js --readonly   # 只跑只读扫描（不动图纸）
//
// 设计：CORE 断言失败 → 退出码 1；BROAD 只读扫描失败 → 只 WARN。
// 写路径会**自建临时图层 + 图元，跑完/异常都强制清理**（绝不残留）。
const net = require('net');
const fs = require('fs');
const path = require('path');

const BRIDGE_PORT = (() => { try { return JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'config.json'), 'utf8').replace(/^\uFEFF/, '')).bridgePort || 8080; } catch { return 8080; } })();
const READONLY = process.argv.includes('--readonly');
const TS = Date.now();
const LAYER = 'NAC_LIVE_REG_' + TS;

function tcp(method, params, timeout) {
  return new Promise((resolve) => {
    const c = new net.Socket(); const id = TS % 100000 + Math.floor(Math.random() * 1000);
    let done = false; const fin = (v) => { if (!done) { done = true; c.destroy(); resolve(v); } };
    c.connect(BRIDGE_PORT, '127.0.0.1', () => c.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'));
    let b = '';
    c.on('data', (d) => { b += d.toString(); });
    c.on('close', () => { try { const r = JSON.parse(b); fin(r.error ? { ok: false, error: r.error.message || String(r.error) } : { ok: true, result: r.result }); } catch (e) { fin({ ok: false, error: 'BAD_JSON: ' + String(b).slice(0, 80) }); } });
    c.on('error', (e) => fin({ ok: false, error: e.message }));
    setTimeout(() => fin({ ok: false, error: 'TIMEOUT' }), timeout || 30000);
  });
}

let pass = 0, fail = 0, warn = 0;
function ok(name, detail) { pass++; console.log('  PASS  ' + name + (detail ? '  (' + detail + ')' : '')); }
function bad(name, why) { fail++; console.log('  FAIL  ' + name + '  ' + why); }
function soft(name, why) { warn++; console.log('  warn  ' + name + '  ' + why); }

async function core(name, fn) {
  try { const d = await fn(); ok(name, d); } catch (e) { bad(name, e.message || String(e)); }
}

async function main() {
  console.log('\n=== new-acad 真机回归 ===  port :' + BRIDGE_PORT + (READONLY ? '  [readonly]' : ''));
  const h = await tcp('getCivil3DHealth', {}, 8000);
  if (!h.ok || !h.result || h.result.connected === false) {
    console.log('  ✗ 插件不在线（:' + BRIDGE_PORT + '）—— 请开 Civil 3D 并 NETLOAD 插件后重试。');
    process.exit(2);
  }
  ok('插件在线 (getCivil3DHealth)');

  // ── 1) 只读扫描：CORE（硬失败）──
  const CORE = ['getDrawingInfo', 'getDrawingSettings', 'listMethods', 'getLayers', 'getSelection'];
  console.log('\n-- 只读 CORE --');
  for (const m of CORE) {
    await core('CORE ' + m, async () => {
      const r = await tcp(m, {}, 20000);
      if (!r.ok) throw new Error(r.error);
      return '';
    });
  }

  // ── 2) 只读扫描：BROAD（软失败，仅 WARN）──
  const BROAD = ['getCoordinateSystemInfo', 'getDrawingOverview', 'getProjectContext', 'listCivilObjectTypes', 'listSurfaces', 'listAlignments', 'listProfiles', 'listAssemblies', 'listStockSubassemblies', 'listCorridors', 'listIntersections', 'listParcels', 'listParcelSites', 'listPipeNetworks', 'listPressureNetworks', 'listGradingGroups', 'listGradings', 'listFeatureLines', 'listDataShortcuts', 'listSurveyDatabases', 'listSurveyFigures', 'listSheetSets', 'listLabelStyles', 'listStyles', 'listCatchmentGroups', 'listCatchments', 'listSampleLineGroups', 'listSectionViews', 'listPointGroups', 'listCogoPoints', 'listGradingCriteria', 'listTcMethods', 'listHydrologyCapabilities', 'listSsaCapabilities', 'listPipePartsCatalog', 'getVisualStyle', 'summarizeDrawing'];
  console.log('\n-- 只读 BROAD（软失败=WARN，可能需参数）--');
  for (const m of BROAD) {
    const r = await tcp(m, {}, 20000);
    if (r.ok) pass++; else soft('BROAD ' + m, r.error);
  }

  if (READONLY) { return finish(); }

  // ── 3) 写路径回归（建→断言→改→删），带强制清理 ──
  console.log('\n-- 写路径（临时图层 ' + LAYER + '，跑完强制清理）--');
  const made = [];
  const cleanup = async () => {
    try { if (made.length) await tcp('deleteEntities', { handles: made }, 30000); } catch (_) {}
    try { await tcp('deleteLayer', { name: LAYER }, 20000); } catch (_) {}
  };
  try {
    await core('createLayer', async () => {
      const r = await tcp('createLayer', { name: LAYER }, 20000);
      if (!r.ok) throw new Error(r.error);
      return LAYER;
    });

    let ch = null, ph = null;
    await core('createCircle + 归入临时层', async () => {
      const r = await tcp('createCircle', { center: [0, 0], diameter: 100 }, 30000);
      if (!r.ok || !r.result || !r.result.handle) throw new Error(r.error || 'no handle');
      ch = r.result.handle; made.push(ch);
      const sl = await tcp('setEntityLayer', { handles: [ch], layer: LAYER }, 20000);
      if (!sl.ok) throw new Error('setEntityLayer: ' + sl.error);
      return 'handle=' + ch;
    });
    await core('createPolyline + 归入临时层', async () => {
      const r = await tcp('createPolyline', { points: [{ x: 0, y: 1000 }, { x: 500, y: 1000 }, { x: 500, y: 1300 }] }, 30000);
      if (!r.ok || !r.result || !r.result.handle) throw new Error(r.error || 'no handle');
      ph = r.result.handle; made.push(ph);
      const sl = await tcp('setEntityLayer', { handles: [ph], layer: LAYER }, 20000);
      if (!sl.ok) throw new Error('setEntityLayer: ' + sl.error);
      return 'handle=' + ph;
    });

    // assertChecks：存在 + 计数 + 半径 + 顶点数
    await core('assertChecks（存在/计数/半径/顶点数）', async () => {
      const checks = [
        { type: 'entityExists', handle: ch },
        { type: 'entityExists', handle: ph },
        { type: 'entityCount', layer: LAYER, expected: 2, op: 'ge' },
        { type: 'value', handle: ch, field: 'radius', expected: 50, tolerance: 0.01 },
        { type: 'value', handle: ph, field: 'vertexCount', expected: 3, tolerance: 0 },
      ];
      const r = await tcp('assertChecks', { checks }, 30000);
      if (!r.ok) throw new Error(r.error);
      const res = r.result || {};
      if (res.failed > 0) throw new Error('assertChecks failed=' + res.failed + ' 明细:' + JSON.stringify((res.results || []).filter(x => !x.pass)));
      return res.passed + '/' + res.total;
    });

    await core('setEntityColor(红) + getEntityInfo 回读', async () => {
      const r = await tcp('setEntityColor', { handle: ch, color: 1 }, 20000);
      if (!r.ok || r.result.color !== 1) throw new Error(r.error || ('setter color=' + (r.result && r.result.color)));
      const info = await tcp('getEntityInfo', { handle: ch }, 20000);
      if (!info.ok) throw new Error(info.error);
      // 稳定字段：colorIndex（ACI）。color 是本地化名（zh「红」）仅作展示
      const c = String(info.result.color ?? '');
      if (info.result.colorIndex !== 1) throw new Error('colorIndex=' + info.result.colorIndex + '（期望 1）; color=' + c);
      return 'colorIndex=1' + (c ? ' / color=' + c.slice(0, 16) : '');
    });

    await core('moveEntity + 仍存在', async () => {
      const r = await tcp('moveEntity', { handle: ph, dx: 100, dy: 0, dz: 0 }, 20000);
      if (!r.ok) throw new Error(r.error);
      const a = await tcp('assertChecks', { checks: [{ type: 'entityExists', handle: ph }] }, 20000);
      if (!a.ok || (a.result && a.result.passed < 1)) throw new Error('移动后不存在');
      return 'ok';
    });

    // 清理 + 验证清空
    await cleanup();
    made.length = 0;
    await core('清理后 临时层图元=0', async () => {
      const a = await tcp('assertChecks', { checks: [{ type: 'entityCount', layer: LAYER, expected: 0, op: 'le' }] }, 20000);
      if (!a.ok) throw new Error(a.error);
      if (a.result && a.result.failed > 0) throw new Error('仍有残留: ' + JSON.stringify(a.result.results));
      return '0';
    });
  } catch (e) { /* core 已记录 */ }
  finally { await cleanup(); }

  finish();
}

function finish() {
  console.log('\n=== 结果：' + pass + ' passed, ' + fail + ' failed' + (warn ? ', ' + warn + ' warned' : '') + ' ===');
  process.exit(fail > 0 ? 1 : 0);
}

main().catch((e) => { console.log('\nCRASH: ' + e.message); process.exit(2); });
