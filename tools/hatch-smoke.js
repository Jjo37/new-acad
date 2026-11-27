#!/usr/bin/env node
/**
 * hatch-smoke.js — 填充能力冒烟测试（2026-09-23）
 *
 * 覆盖：createHatch（单环/多环带岛/图案/颜色）、importHatches（批量）、editHatch（改图案/比例/角度/颜色/边界）、
 *       getEntityInfo 读填充属性、measureArea 量填充面积、selectByCriteria type=HATCH、
 *       颜色修复（createPolyline / importVectorPaths 的 per-item color）
 * 结束时自动清理本次创建的图元与图层（不留垃圾）。
 *
 * 用法：node tools/hatch-smoke.js      （需 C3D 开着 + relay 在线）
 */
const { call } = require('C:/Users/Jjo37/.openclaw/workspace/toolbox/seeded/primitives/domain/cad/new-acad-relay-rpc.js');

const LAYER = 'HATCH_SMOKE';
let pass = 0, fail = 0;
const created = [];
function ok(name, cond, extra) { if (cond) { pass++; console.log('  PASS ' + name + (extra ? '  ' + extra : '')); } else { fail++; console.log('  FAIL ' + name + '  ' + (extra || '')); } }
async function post(method, params, timeout = 60000) {
  const r = await call(method, params, timeout);
  if (r.error) throw new Error(method + ': ' + JSON.stringify(r.error).slice(0, 200));
  return r.result || {};
}
async function tryPost(method, params, timeout = 60000) {
  const r = await call(method, params, timeout);
  if (r.error) return { err: JSON.stringify(r.error).slice(0, 160), result: null };
  return { err: null, result: r.result || {} };
}

(async () => {
  console.log('=== 填充能力冒烟测试 ===');
  const lm = await post('listMethods', {});
  const names = lm.methods || [];
  ok('方法已注册 createHatch/importHatches/editHatch', ['createHatch', 'importHatches', 'editHatch'].every((m) => names.includes(m)), '(共 ' + names.length + ')');

  // 启动前清理（上次失败可能留下残留）
  for (const ln of [LAYER, 'GRAD_TEST']) {
    try { const g0 = await post('getEntitiesByLayer', { layer: ln, maxCount: 500, offset: 0 }); const h0 = (g0.entities || []).map((e) => e.handle); if (h0.length) await post('deleteEntities', { handles: h0 }, 120000); await post('deleteLayer', { name: ln }); } catch (_) {}
  }
  await post('createLayer', { name: LAYER, color: 3 }).catch(() => {});
  const tri = [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 5, y: 8 }];

  // 1) 单环 SOLID + TrueColor
  const h1 = await post('createHatch', { points: tri, pattern: 'SOLID', color: [255, 0, 0], layer: LAYER });
  created.push(h1.handle);
  ok('createHatch 单环 SOLID', !!h1.handle && h1.ok === true);
  ok('  pattern=SOLID / isSolid', h1.pattern === 'SOLID' && h1.isSolid === true, JSON.stringify({ p: h1.pattern, solid: h1.isSolid, area: h1.area }));
  ok('  loopCount=1', h1.loopCount === 1);
  const info1 = await post('getEntityInfo', { handle: h1.handle });
  ok('getEntityInfo 读填充属性', info1.pattern === 'SOLID' && info1.isSolid === true && typeof info1.area === 'number' && info1.loopCount === 1, JSON.stringify({ p: info1.pattern, area: info1.area, loops: info1.loopCount, color: info1.color }));
  const area1 = await post('measureArea', { handle: h1.handle });
  ok('measureArea 支持填充（不再报错）', typeof area1.area === 'number' && Math.abs(area1.area - 40) < 0.5, 'area=' + area1.area + ' (期望 40)');

  // 2) 多环带岛（外 20x20 方块 + 内 8x8 孔）
  const outer = [{ x: 20, y: 0 }, { x: 40, y: 0 }, { x: 40, y: 20 }, { x: 20, y: 20 }];
  const inner = [{ x: 26, y: 6 }, { x: 34, y: 6 }, { x: 34, y: 14 }, { x: 26, y: 14 }];
  const h2 = await post('createHatch', { loops: [{ points: outer }, { points: inner }], pattern: 'ANSI31', scale: 1.5, angle: 45, layer: LAYER });
  created.push(h2.handle);
  ok('createHatch 多环带岛 + 图案', h2.loopCount === 2 && /ANSI31/i.test(h2.pattern), JSON.stringify({ loops: h2.loopCount, p: h2.pattern, area: h2.area }));
  ok('  带岛面积=外-内(400-64=336)', Math.abs(h2.area - 336) < 3, 'area=' + h2.area);

  // 3) 批量 importHatches（3 个，带颜色）
  const items = [0, 1, 2].map((k) => ({ points: [{ x: 50 + k * 12, y: 0 }, { x: 58 + k * 12, y: 0 }, { x: 54 + k * 12, y: 6 }], color: [0, 0, 255], layer: LAYER }));
  const batch = await post('importHatches', { items, pattern: 'SOLID', layer: LAYER }, 180000);
  ok('importHatches 批量', batch.created === 3 && batch.failed === 0, JSON.stringify(batch).slice(0, 160));

  // 4) editHatch：改图案/比例/角度/颜色/边界
  const e1 = await post('editHatch', { handle: h1.handle, pattern: 'ANSI31', scale: 2, angle: 30, color: 5 });
  ok('editHatch 改图案/比例/角度/颜色', e1.ok === true && /ANSI31/i.test(e1.pattern) && Math.abs(e1.patternScale - 2) < 1e-6 && Math.abs(e1.patternAngle - 30) < 1e-6, JSON.stringify({ p: e1.pattern, s: e1.patternScale, a: e1.patternAngle, ch: e1.changed }));
  const e2 = await post('editHatch', { handle: h2.handle, loops: [{ points: [{ x: 20, y: 0 }, { x: 36, y: 0 }, { x: 36, y: 16 }, { x: 20, y: 16 }] }] });
  ok('editHatch 整体替换边界', e2.ok === true && e2.loopCount === 1, JSON.stringify({ loops: e2.loopCount, area: e2.area }));

  // 5) 选择集：按类型选出填充
  const sel = await post('selectByCriteria', { type: 'HATCH', layer: LAYER });
  const selCount = sel.count ?? (sel.handles || []).length;
  ok('selectByCriteria type=HATCH', selCount >= 5, 'count=' + selCount);

  // 6) 颜色修复：createPolyline / importVectorPaths 的 per-item color
  const pl = await post('createPolyline', { points: [{ x: 0, y: 30 }, { x: 10, y: 30 }, { x: 10, y: 36 }], closed: 1, color: [0, 255, 0], layer: LAYER });
  created.push(pl.handle);
  const infoPl = await post('getEntityInfo', { handle: pl.handle });
  ok('createPolyline 支持 color', /Green|0,255,0|65280/i.test(String(infoPl.color)) || String(infoPl.color).includes('255'), 'color=' + infoPl.color);
  const ivp = await post('importVectorPaths', { items: [{ points: [{ x: 0, y: 40 }, { x: 8, y: 40 }], color: [255, 0, 255], layer: LAYER }], layer: LAYER });
  ok('importVectorPaths 读 item.color（此前被忽略）', (ivp.colored || 0) === 1, JSON.stringify({ created: ivp.created, colored: ivp.colored }));

  // 7) 渐变填充（2026-09-23 修：此前缺 HatchObjectType=GradientObject，静默退化成 SOLID）
  console.log('--- 渐变填充 ---');
  const g1 = await post('createHatch', { points: [{ x: 0, y: 50 }, { x: 20, y: 50 }, { x: 10, y: 66 }], gradient: 'LINEAR', gradientAngle: 45, layer: LAYER });
  created.push(g1.handle);
  ok('createHatch 渐变 LINEAR', g1.isGradient === true && !!g1.gradientName, JSON.stringify({ isGrad: g1.isGradient, name: g1.gradientName, angle: g1.gradientAngle }));
  const gInfo = await post('getEntityInfo', { handle: g1.handle });
  ok('getEntityInfo 读渐变属性', gInfo.isGradient === true && !!gInfo.gradientName && typeof gInfo.gradientAngle === 'number', JSON.stringify({ n: gInfo.gradientName, a: gInfo.gradientAngle, one: gInfo.gradientOneColor }));

  const g2 = await post('createHatch', { points: [{ x: 24, y: 50 }, { x: 44, y: 50 }, { x: 34, y: 66 }], gradient: 'CYLINDER', gradientColors: [[255, 0, 0], [0, 0, 255]], layer: LAYER });
  created.push(g2.handle);
  ok('createHatch 渐变双色', g2.isGradient === true && g2.gradientOneColor === false && Array.isArray(g2.gradientColors) && g2.gradientColors.length === 2, JSON.stringify({ one: g2.gradientOneColor, cols: g2.gradientColors }));

  const bad = await tryPost('createHatch', { points: [{ x: 48, y: 50 }, { x: 68, y: 50 }, { x: 58, y: 66 }], gradient: 'NOT_A_GRADIENT', layer: LAYER });
  ok('无效渐变名会报错（不再静默退化）', bad.err !== null, bad.err || '(没报错)');

  const e3 = await post('editHatch', { handle: g1.handle, gradient: 'SPHERICAL', gradientAngle: 30 });
  ok('editHatch 换渐变 + 角度', e3.isGradient === true && /SPHERICAL/i.test(String(e3.gradientName)) && Math.abs(e3.gradientAngle - 30) < 1e-6, JSON.stringify({ n: e3.gradientName, a: e3.gradientAngle, ch: e3.changed }));
  const e4 = await post('editHatch', { handle: g2.handle, gradient: '' });
  ok('editHatch gradient:"" 转回图案填充', e4.isGradient === false, JSON.stringify({ isGrad: e4.isGradient, ch: e4.changed }));

  // 8) 清理
  console.log('--- 清理 ---');
  const all = await post('getEntitiesByLayer', { layer: LAYER, maxCount: 500, offset: 0 });
  const handles = (all.entities || []).map((e) => e.handle);
  if (handles.length) { const d = await post('deleteEntities', { handles }, 120000); console.log('  删除图元', handles.length, JSON.stringify(d).slice(0, 80)); }
  const dl = await post('deleteLayer', { name: LAYER });
  console.log('  删除图层', JSON.stringify(dl).slice(0, 80));

  console.log('\n=== 结果：PASS ' + pass + ' / FAIL ' + fail + ' ===');
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.log('ERR', e.message); process.exit(2); });
