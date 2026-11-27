#!/usr/bin/env node
// 3D API 实测脚本（2026-08-11）——验证 loftSolid / createNurbsSurface / editNurbsControlPoints / thickenSurface
// 用法：开 C3D + NETLOAD 插件（:8080）→ relay 常驻（:19876）→ node test-3d-api.js
// 建议在空图/测试图中运行（会在模型空间创建实体，默认删除放样截面）
'use strict';

const BASE = 'http://127.0.0.1:19876/tcp';
const results = [];
let idc = 0;

async function rpc(method, params = {}) {
  const t0 = Date.now();
  const body = JSON.stringify({ jsonrpc: '2.0', id: ++idc, method, params });
  let resp;
  try {
    resp = await fetch(BASE, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      signal: AbortSignal.timeout(30000),
    });
  } catch (e) {
    return { ok: false, error: 'NETWORK: ' + e.message, ms: Date.now() - t0 };
  }
  const txt = await resp.text();
  let j = null;
  try { j = JSON.parse(txt); } catch { j = null; }
  if (j && j.error) {
    return { ok: false, error: `JSON-RPC ${j.error.code}: ${j.error.message}`, ms: Date.now() - t0 };
  }
  return { ok: true, result: j ? j.result : txt, ms: Date.now() - t0 };
}

async function step(name, fn) {
  process.stdout.write(`\n▶ ${name} ... `);
  try {
    const out = await fn();
    if (out.ok) {
      console.log(`OK (${out.ms}ms)`);
      console.log('  →', JSON.stringify(out.result));
      results.push({ name, pass: true });
    } else {
      console.log(`FAIL (${out.ms}ms)`);
      console.log('  ✗', out.error);
      results.push({ name, pass: false });
    }
    return out;
  } catch (e) {
    console.log('EXCEPTION');
    console.log('  ✗', e.message);
    results.push({ name, pass: false });
    return { ok: false };
  }
}

async function main() {
  console.log('═══ 3D API 实测 ═══');
  console.log('桥:', BASE, '| 时间:', new Date().toLocaleString('zh-CN'));

  // 0. 桥连通性 + 方法注册确认
  const probe = await rpc('listMethods', { filter: 'nurbs' });
  if (!probe.ok) {
    console.log('\n✗ 桥不通:', probe.error);
    if (String(probe.error).includes('ECONNREFUSED') || String(probe.error).includes('NETWORK'))
      console.log('  排查: ① relay 是否在跑(server/relay-launcher.js) ② C3D 插件是否已 NETLOAD(监听 :8080)');
    process.exit(1);
  }
  const methods = probe.result?.methods || probe.result || [];
  const names = Array.isArray(methods) ? methods.map(m => (typeof m === 'string' ? m : m.name || m.method)) : [];
  const need = ['loftSolid', 'createNurbsSurface', 'editNurbsControlPoints', 'thickenSurface', 'createCircle', 'move3d', 'getEntityInfo'];
  for (const n of need) console.log(`  ${names.includes(n) ? '✅' : '❌'} ${n}`);
  console.log('（listMethods 返回 NURBS 相关方法: ' + (names.filter(n => /nurbs|loft|thicken/i.test(String(n))).join(', ') || '(无)') + '）');

  // 1. loftSolid：3 个圆截面（不同高度）放样
  let solidHandle = null;
  const c1 = await rpc('createCircle', { center: [0, 0], diameter: 30 });
  console.log(`\n▶ 1.1 画圆1 (r=15, z=0) ${c1.ok ? 'OK' : 'FAIL'} (${c1.ms}ms)`);
  console.log('  →', JSON.stringify(c1.ok ? c1.result : c1.error));
  results.push({ name: '1.1 画圆1 (r=15, z=0)', pass: c1.ok });
  const h1 = c1.ok ? c1.result?.handle : null;
  const c2 = await step('1.2 画圆2 (r=10, z=0)', async () => rpc('createCircle', { center: [0, 0], diameter: 20 }));
  const c3 = await step('1.3 画圆3 (r=5, z=0)', async () => rpc('createCircle', { center: [0, 0], diameter: 10 }));
  const h2 = c2.ok ? c2.result?.handle : null;
  const h3 = c3.ok ? c3.result?.handle : null;
  if (h2) await step('1.4 圆2 抬到 z=20', async () => rpc('move3d', { handle: h2, dx: 0, dy: 0, dz: 20 }));
  if (h3) await step('1.5 圆3 抬到 z=40', async () => rpc('move3d', { handle: h3, dx: 0, dy: 0, dz: 40 }));
  if (h1 && h2 && h3) {
    const loft = await step('1.6 loftSolid 放样(3截面)', async () =>
      rpc('loftSolid', { crossSections: [h1, h2, h3] }));
    solidHandle = loft.ok ? loft.result?.solidHandle : null;
    if (solidHandle) {
      await step('1.7 验证实体', async () => rpc('getEntityInfo', { handle: solidHandle }));
    }
  } else {
    console.log('  ✗ 圆 handle 缺失，跳过放样');
    results.push({ name: '1.6 loftSolid 放样(3截面)', pass: false });
  }

  // 2. createNurbsSurface：4x4 控制点，中间隆起
  let surfHandle = null;
  const pts = [];
  for (let v = 0; v < 4; v++) {
    for (let u = 0; u < 4; u++) {
      const x = u * 10, y = v * 10;
      const z = (u === 1 || u === 2) && (v === 1 || v === 2) ? 5 : 0; // 中间 2x2 隆起
      pts.push([x, y, z]);
    }
  }
  const ns = await step('2.1 createNurbsSurface(4x4 控制点, 中间隆起)', async () =>
    rpc('createNurbsSurface', { uCount: 4, vCount: 4, points: pts }));
  surfHandle = ns.ok ? ns.result?.handle : null;
  if (surfHandle) {
    await step('2.2 验证曲面类型', async () => rpc('getEntityInfo', { handle: surfHandle }));
  }

  // 3. editNurbsControlPoints：抬一个控制点
  if (surfHandle) {
    await step('3.1 移动控制点 (1,1) 再抬 3', async () =>
      rpc('editNurbsControlPoints', { handle: surfHandle, moves: [[1, 1, 0, 0, 3]] }));
    await step('3.2 绝对设置控制点 (2,2) 到 z=8', async () =>
      rpc('editNurbsControlPoints', { handle: surfHandle, points: [[2, 2, 20, 20, 8]] }));
  } else {
    console.log('  ✗ 曲面 handle 缺失，跳过控制点编辑');
    results.push({ name: '3.1 移动控制点', pass: false });
    results.push({ name: '3.2 绝对设置控制点', pass: false });
  }

  // 4. thickenSurface：曲面加厚成实体
  if (surfHandle) {
    const th = await step('4.1 thickenSurface 加厚 2 (单侧)', async () =>
      rpc('thickenSurface', { handle: surfHandle, thickness: 2, bothSides: false }));
    const thHandle = th.ok ? th.result?.solidHandle : null;
    if (thHandle) await step('4.2 验证加厚实体', async () => rpc('getEntityInfo', { handle: thHandle }));
  } else {
    console.log('  ✗ 曲面 handle 缺失，跳过加厚');
    results.push({ name: '4.1 thickenSurface', pass: false });
  }

  // 汇总
  const pass = results.filter(r => r.pass).length;
  console.log('\n═══ 汇总 ═══');
  for (const r of results) console.log(`  ${r.pass ? '✅' : '❌'} ${r.name}`);
  console.log(`  ${pass}/${results.length} 通过`);
  console.log('\n对照检查: ① CAD 里应出现 1 个变截面放样实体(圆台状) ② 1 个隆起曲面 ③ 曲面下方 1 个薄壳实体');
}

main().catch(e => { console.error('FATAL:', e); process.exit(1); });
