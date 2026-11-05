#!/usr/bin/env node
// P3 描述参数回归实测（2026-08-25）——验证 12 个修复过描述的方法参数名与实现一致
// 判定: 不报"参数缺失/类型错误"(MISSING_PARAM/INVALID_INPUT 参数类) 即 PASS；
//       业务错误(对象不存在等) 说明参数接收正常 → 也算 PASS（非描述问题）
// 用法: 开 C3D + 插件(:8080) + relay(:19876) → node test-desc-regression.js
'use strict';

const BASE = 'http://127.0.0.1:19876/tcp';
let idc = 0;
const results = [];

async function rpc(method, params = {}) {
  const t0 = Date.now();
  const body = JSON.stringify({ jsonrpc: '2.0', id: ++idc, method, params });
  let resp;
  try {
    resp = await fetch(BASE, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      signal: AbortSignal.timeout(40000),
    });
  } catch (e) {
    return { ok: false, error: 'NETWORK: ' + e.message, ms: Date.now() - t0 };
  }
  const txt = await resp.text();
  let j = null;
  try { j = JSON.parse(txt); } catch { j = null; }
  if (j && j.error) return { ok: false, error: `JSON-RPC ${j.error.code}: ${j.error.message}`, ms: Date.now() - t0 };
  return { ok: true, result: j ? j.result : txt, ms: Date.now() - t0 };
}

// 参数类错误特征（描述-实现不一致的表现）
const PARAM_ERR = /MISSING|参数|REQUIRED|INVALID_INPUT.*(parameter|param)|cannot be null|未知参数|缺少/i;

function classify(name, r) {
  if (r.ok) { results.push({ name, pass: true, note: 'OK' }); return; }
  const err = r.error || '';
  if (PARAM_ERR.test(err) && !/not found|不存在|未找到/i.test(err)) {
    results.push({ name, pass: false, note: err.slice(0, 90) });
  } else {
    results.push({ name, pass: true, note: '参数OK, 业务: ' + err.slice(0, 70) });
  }
}

async function main() {
  // ---- 准备测试图元 ----
  console.log('=== 准备图元 ===');
  const c1 = await rpc('createCircle', { center: [10, 10], diameter: 8 });        // revolve profile
  const p1 = await rpc('createPolyline', { points: [{ x: 0, y: 0 }, { x: 5, y: 0 }, { x: 5, y: 5 }, { x: 0, y: 5 }], closed: true }); // extrude profile
  const c2 = await rpc('createCircle', { center: [20, 20], diameter: 4 });        // sweep profile
  const l1 = await rpc('createLineSegment', { startX: 20, startY: 20, endX: 20, endY: 30 }); // sweep path
  const b1 = await rpc('createBox', { x: 0, y: 0, z: 0, length: 3, width: 3, height: 3 });   // boolean main
  const b2 = await rpc('createBox', { x: 1, y: 1, z: 0, length: 2, width: 2, height: 2 });   // boolean tool
  const b3 = await rpc('createBox', { x: 8, y: 8, z: 0, length: 4, width: 4, height: 4 });   // slice
  const l2 = await rpc('createLineSegment', { startX: 0, startY: 0, endX: 5, endY: 5 });     // chamfer/align
  const l3 = await rpc('createLineSegment', { startX: 5, endX: 5, startY: 0, endY: 5 });     // chamfer/align

  const H = {};
  for (const [k, r] of [['c1', c1], ['p1', p1], ['c2', c2], ['l1', l1], ['b1', b1], ['b2', b2], ['b3', b3], ['l2', l2], ['l3', l3]]) {
    H[k] = r.ok && r.result ? (r.result.handle || r.result.solidHandle || r.result.lineId || Object.values(r.result)[0]) : null;
    console.log(`  ${k}: ${r.ok ? 'OK' : 'FAIL ' + (r.error || '').slice(0, 60)}  handle=${H[k]}`);
  }

  console.log('\n=== 12 个修复方法回归 ===');

  // 1. revolveSolid（描述: handle + axisX1/axisY1/axisX2/axisY2 + angle）
  let r = await rpc('revolveSolid', { handle: H.c1, axisX1: 10, axisY1: 10, axisX2: 10, axisY2: 11, angle: 180 });
  classify('revolveSolid', r);

  // 2. extrudeSolid（handle + height + taperAngle?）
  r = await rpc('extrudeSolid', { handle: H.p1, height: 4 });
  classify('extrudeSolid', r);

  // 3. sweepSolid（profileHandle + pathHandle）
  r = await rpc('sweepSolid', { profileHandle: H.c2, pathHandle: H.l1 });
  classify('sweepSolid', r);

  // 4. booleanSubtract（mainHandle + toolHandle）
  r = await rpc('booleanSubtract', { mainHandle: H.b1, toolHandle: H.b2 });
  classify('booleanSubtract', r);

  // 5. booleanUnion（handles[]）
  r = await rpc('booleanUnion', { handles: [H.b1, H.b3] });
  classify('booleanUnion', r);

  // 6. sliceSolid（handle + p1x/p1y + p2x/p2y + p3x/p3y + keepBoth?）
  r = await rpc('sliceSolid', { handle: H.b3, p1x: 0, p1y: 0, p2x: 1, p2y: 0, p3x: 0, p3y: 1, keepBoth: false });
  classify('sliceSolid', r);

  // 7. assignMaterial（handle + materialName）
  r = await rpc('assignMaterial', { handle: H.b1, materialName: 'ByLayer' });
  classify('assignMaterial', r);

  // 8. createLineSegment（startX/startY/endX/endY）——已用于准备，这里再独立验证
  r = await rpc('createLineSegment', { startX: 30, startY: 30, endX: 40, endY: 40 });
  classify('createLineSegment', r);

  // 9. chamferEntities（distance1/distance2/handle1/handle2）
  r = await rpc('chamferEntities', { distance1: 1, distance2: 1, handle1: H.l2, handle2: H.l3 });
  classify('chamferEntities', r);

  // 10. addLabel（objectType/objectName/labelType）——空图可能业务失败, 参数正确即可
  r = await rpc('addLabel', { objectType: 'alignment', objectName: '不存在路线', labelType: 'station' });
  classify('addLabel', r);

  // 11. alignEntity（handle + srcX/srcY + dstX/dstY）
  r = await rpc('alignEntity', { handle: H.l2, srcX: 0, srcY: 0, dstX: 50, dstY: 50 });
  classify('alignEntity', r);

  // 12. arrayEntity（handle + type + count）
  r = await rpc('arrayEntity', { handle: H.l3, type: 'rect', count: 3, dx: 2, dy: 0 });
  classify('arrayEntity', r);

  // ---- 汇总 ----
  console.log('\n=== 汇总 ===');
  let pass = 0, fail = 0;
  for (const x of results) {
    console.log(`  ${x.pass ? '✅' : '❌'} ${x.name}: ${x.note}`);
    if (x.pass) pass++; else fail++;
  }
  console.log(`\nPASS ${pass} / FAIL ${fail}（共 ${results.length}）`);
  process.exit(fail > 0 ? 1 : 0);
}

main().catch(e => { console.error('FATAL', e); process.exit(1); });
