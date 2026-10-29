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

    // ── 3D 新能力（2026-10-29）：螺旋线 / 抽壳 / 任意平面剖切 / 3D 轴旋转 ──
    let helixH = null;
    await core('createHelix（真 Helix 曲线，量高度）', async () => {
      const r = await tcp('createHelix', { center: [0, 0, 2000], baseRadius: 50, height: 200, turns: 4 }, 30000);
      if (!r.ok || !r.result || !r.result.handle) throw new Error(r.error || 'no handle');
      helixH = r.result.handle; made.push(helixH);
      await tcp('setEntityLayer', { handles: [helixH], layer: LAYER }, 20000);
      // 实测高度必须对得上 —— 防“建了个退化曲线但返回了 handle”
      const mh = Number(r.result.measuredHeight);
      if (!(Math.abs(mh - 200) <= 0.5)) throw new Error('measuredHeight=' + mh + '（期望 200）');
      if (String(r.result.type) !== 'Helix') throw new Error('type=' + r.result.type);
      return 'handle=' + helixH + ' h=' + mh + ' turns=' + r.result.turns;
    });

    let cylH = null;
    await core('extrudeSolid（造一个抽壳/剖切靶子）', async () => {
      const c = await tcp('createCircle', { center: [0, 5000], diameter: 100 }, 30000);
      if (!c.ok || !c.result || !c.result.handle) throw new Error('createCircle: ' + c.error);
      // extrude 会删掉 profile，所以 profile handle 不进 made（否则删已删对象）
      const e = await tcp('extrudeSolid', { handle: c.result.handle, height: 200 }, 60000);
      if (!e.ok || !e.result || !e.result.solidHandle) throw new Error(e.error || 'no solidHandle');
      cylH = e.result.solidHandle; made.push(cylH);
      await tcp('setEntityLayer', { handles: [cylH], layer: LAYER }, 20000);
      return 'solid=' + cylH;
    });

    await core('listSolidFaces + shellSolid（抽壳）', async () => {
      if (!cylH) throw new Error('没有靶子实体');
      const lf = await tcp('listSolidFaces', { handle: cylH }, 30000);
      if (!lf.ok) throw new Error('listSolidFaces: ' + lf.error);
      const faces = lf.result.faces || [];
      const n = Number(lf.result.faceCount);
      if (!(n >= 3)) throw new Error('faceCount=' + n + '（拉伸圆至少 3 个面）');
      for (let k = 0; k < faces.length; k++) {
        if (faces[k].index !== k + 1) throw new Error('面 id 不是 1..N 递增：第 ' + (k + 1) + ' 项 index=' + faces[k].index);
      }
      // 有几何就用“最高面”，没几何就退用最后一个 id（两者都必须能开壳）
      const withZ = faces.filter((f) => typeof f.maxZ === 'number');
      const target = withZ.length
        ? withZ.reduce((a, b) => (b.maxZ > a.maxZ ? b : a)).index
        : faces[faces.length - 1].index;
      const r = await tcp('shellSolid', { handle: cylH, thickness: 5, removeFaces: [target] }, 60000);
      if (!r.ok) throw new Error(r.error);
      if (Number(r.result.removedFaces) !== 1) throw new Error('removedFaces=' + r.result.removedFaces);
      return 'faces=' + n + ' opened=' + target + (withZ.length ? ' (by geometry)' : ' (no geometry, last id)');
    });

    await core('sliceSolid（任意 3D 平面 + keepBoth）', async () => {
      if (!cylH) throw new Error('没有靶子实体');
      // 三个点都不在 z=0 同一平面上 —— 旧实现把 z 钉成 0，永远只能切 XY 面，这个断言就是那道防线
      const r = await tcp('sliceSolid', {
        handle: cylH,
        p1x: -200, p1y: 5000, p1z: 0,
        p2x: 200, p2y: 5000, p2z: 50,
        p3x: 0, p3y: 5200, p3z: -50,
        keepBoth: true,
      }, 60000);
      if (!r.ok) throw new Error(r.error);
      const n = r.result.normal || [];
      if (!(Math.abs(n[0]) + Math.abs(n[1]) + Math.abs(n[2]) > 0)) throw new Error('normal 缺失');
      if (Math.abs(Number(n[2]) ?? 0) > 0.99 && Math.abs(Number(n[0]) ?? 0) < 1e-6 && Math.abs(Number(n[1]) ?? 0) < 1e-6) {
        throw new Error('法向仍是 (0,0,1) —— 平面被钉回 XY 了: ' + JSON.stringify(n));
      }
      if (!r.result.createdHandle) throw new Error('keepBoth 但没返回 createdHandle');
      made.push(r.result.createdHandle);
      return 'normal=' + n.map((x) => Math.round(x * 1000) / 1000).join(',');
    });

    await core('revolveSolid（3D 轴，axisZ 生效）', async () => {
      // 几何前提：轴必须与 profile 共面。所以先把圆绕 X 轴翻 90° 变成竖直面，再用竖直轴。
      const c = await tcp('createCircle', { center: [1000, 8000], diameter: 100 }, 30000);
      if (!c.ok || !c.result || !c.result.handle) throw new Error('createCircle: ' + c.error);
      const rot = await tcp('rotate3d', { handle: c.result.handle, ax: 900, ay: 8000, az: 0, bx: 1100, by: 8000, bz: 0, angle: 90 }, 30000);
      if (!rot.ok) throw new Error('rotate3d: ' + rot.error);
      const r = await tcp('revolveSolid', {
        handle: c.result.handle,
        axisX1: 1300, axisY1: 8000, axisZ1: 0,
        axisX2: 1300, axisY2: 8000, axisZ2: 300,
        angle: 270,
      }, 60000);
      if (!r.ok || !r.result || !r.result.solidHandle) throw new Error(r.error || 'no solidHandle');
      made.push(r.result.solidHandle);
      await tcp('setEntityLayer', { handles: [r.result.solidHandle], layer: LAYER }, 20000);
      return 'solid=' + r.result.solidHandle;
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
