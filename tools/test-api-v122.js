// test-api-v122.js — 2026-08-03 API 化 13 个方法的 C3D 在线实测
// 用法: node tools/test-api-v122.js
// 走 relay /tcp 桥（禁止直接打 8080）
const BASE = 'http://127.0.0.1:19876/tcp';
let id = 0;
const results = [];

async function call(method, params) {
  const res = await fetch(BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: ++id, method, params }),
  });
  const data = await res.json();
  if (data.error) throw new Error(JSON.stringify(data.error));
  return data.result;
}

async function test(name, fn) {
  try {
    const r = await fn();
    console.log(`PASS ${name}: ${JSON.stringify(r)}`);
    results.push({ name, ok: true, result: r });
  } catch (e) {
    console.log(`FAIL ${name}: ${e.message}`);
    results.push({ name, ok: false, error: e.message });
  }
}

async function main() {
  // ---- 准备：画测试实体 ----
  const rectA = await call('createPolyline', { points: [{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 50 }, { x: 0, y: 50 }], closed: 1 });
  const rectAHandle = rectA.handle;
  const circ = await call('createCircle', { center: [250, 0], diameter: 50 });
  const circHandle = circ.handle;
  const rectB = await call('createPolyline', { points: [{ x: 0, y: -100 }, { x: 40, y: -100 }, { x: 40, y: -80 }, { x: 0, y: -80 }], closed: 1 });
  const rectBHandle = rectB.handle;
  const path = await call('createLineSegment', { startX: 20, startY: -90, startZ: 0, endX: 20, endY: -90, endZ: 50 });
  const pathHandle = path.lineId;
  const box1 = await call('createBox', { x: 0, y: 200, z: 0, length: 50, width: 50, height: 50 });
  const box2 = await call('createBox', { x: 30, y: 230, z: 0, length: 50, width: 50, height: 50 });
  const box3 = await call('createBox', { x: 0, y: 320, z: 0, length: 50, width: 50, height: 50 });
  const box4 = await call('createBox', { x: 30, y: 350, z: 0, length: 30, width: 30, height: 30 });
  const box5 = await call('createBox', { x: 0, y: 450, z: 0, length: 50, width: 50, height: 50 });
  const box6 = await call('createBox', { x: 200, y: 450, z: 0, length: 50, width: 50, height: 50 });
  const box7 = await call('createBox', { x: 0, y: 550, z: 0, length: 20, width: 20, height: 20 });
  const line1 = await call('createLineSegment', { startX: 0, startY: 650, startZ: 0, endX: 50, endY: 650, endZ: 0 });
  const line2 = await call('createLineSegment', { startX: 50, startY: 650, startZ: 0, endX: 100, endY: 650, endZ: 0 });

  // ---- 1. extrudeSolid（闭合多段线 → 实体，验证原曲线被删）----
  await test('extrudeSolid', async () => call('extrudeSolid', { handle: rectAHandle, height: 30, taperAngle: 0 }));

  // ---- 2. revolveSolid（圆绕轴 180°）----
  await test('revolveSolid', async () => call('revolveSolid', { handle: circHandle, axisX1: 250, axisY1: 100, axisX2: 251, axisY2: 100, angle: 180 }));

  // ---- 3. sweepSolid（闭合 profile 沿路径）----
  await test('sweepSolid', async () => call('sweepSolid', { profileHandle: rectBHandle, pathHandle: pathHandle }));

  // ---- 4. booleanUnion（重叠两 box）----
  await test('booleanUnion', async () => call('booleanUnion', { handles: [box1.handle, box2.handle] }));

  // ---- 5. booleanSubtract（box3 - box4）----
  await test('booleanSubtract', async () => call('booleanSubtract', { mainHandle: box3.handle, toolHandle: box4.handle }));

  // ---- 6. sliceSolid keepBoth（box5 切两半）----
  await test('sliceSolid keepBoth', async () => call('sliceSolid', { handle: box5.handle, p1x: 0, p1y: 450, p2x: 50, p2y: 450, p3x: 0, p3y: 500, keepBoth: true }));

  // ---- 7. interferenceCheck（重叠 true / 分离 false）----
  await test('interferenceCheck 重叠', async () => call('interferenceCheck', { handle1: box6.handle, handle2: box6.handle }));
  await test('interferenceCheck 分离', async () => call('interferenceCheck', { handle1: box6.handle, handle2: box7.handle }));

  // ---- 8. array3d rect（box7 阵列 2行3列）----
  await test('array3d rect', async () => call('array3d', { handle: box7.handle, type: 'rect', count: 6, rows: 2, cols: 3, levels: 1, dx: 60, dy: 60, dz: 0 }));

  // ---- 9. assignMaterial（box6 赋 Global 材质）----
  await test('assignMaterial', async () => call('assignMaterial', { handle: box6.handle, materialName: 'Global' }));

  // ---- 10. joinEntities（两条相接 Polyline）----
  const jp1 = await call('createPolyline', { points: [{ x: 0, y: 650 }, { x: 50, y: 650 }], closed: 0 });
  const jp2 = await call('createPolyline', { points: [{ x: 50, y: 650 }, { x: 100, y: 650 }], closed: 0 });
  await test('joinEntities', async () => call('joinEntities', { handles: [jp1.handle, jp2.handle] }));

  // ---- 11. createBlock（box7 → 块）----
  await test('createBlock', async () => call('createBlock', { name: 'TESTBLK03', handles: [box7.handle], x: 0, y: 550 }));

  // ---- 12. writeBlock（box6 写 dwg）----
  await test('writeBlock', async () => call('writeBlock', { handle: box6.handle, filePath: 'D:\\new-acad\\exchange\\_out\\test-wblock.dwg' }));

  // ---- 13. attachXref（引用刚写的 dwg）----
  await test('attachXref', async () => call('attachXref', { filePath: 'D:\\new-acad\\exchange\\_out\\test-wblock.dwg', x: 300, y: 550, scale: 1, rotation: 0 }));

  // ---- 汇总 ----
  const ok = results.filter(r => r.ok).length;
  console.log(`\n===== 汇总: ${ok}/${results.length} 通过 =====`);
  for (const r of results.filter(r => !r.ok)) console.log(`  FAIL ${r.name}: ${r.error}`);
}

main().catch(e => { console.error('FATAL:', e.message); process.exit(1); });
