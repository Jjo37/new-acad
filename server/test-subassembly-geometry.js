// 子部件几何通道回归（2026-09-10 建）
// 覆盖：createCustomSubassembly（points→SAC XAML→pkt→导入，真几何）→ verifyAssemblyGeometry（探测校验）
//       → probeGeometry（单子部件非破坏几何）→ getSubassemblyGeometry（挂载链/参数）
// 用法: node server/test-subassembly-geometry.js   （需 C3D + 插件 8080 + relay 19876）
const fs = require('fs');

async function tcp(method, params) {
  const r = await fetch('http://127.0.0.1:19876/tcp', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: Date.now() + Math.random(), method, params })
  }).then(r => r.json());
  if (r.error) throw new Error(method + ': ' + JSON.stringify(r.error));
  return r.result;
}

const near = (a, b, tol = 1e-3) => a != null && Math.abs(a - b) <= tol;
let pass = 0, fail = 0;
function ok(name, cond, extra) {
  if (cond) { pass++; console.log('  ✅ ' + name + (extra ? ' — ' + extra : '')); }
  else { fail++; console.log('  ❌ ' + name + (extra ? ' — ' + extra : '')); }
}

(async () => {
  const TS = Date.now().toString(36);
  const ASM = 'GeoAsm' + TS;
  const RECT = 'GeoRect' + TS;

  // 2026-09-10 实测：C3D 停在开始页时命令上下文不可用 → 所有图纸级调用 25s 超时；
  // 且 newDrawing(DocumentManager.Add) 在 Idle 线程抛 WPF VerifyAccess 异常。
  // 改用 openDrawing(模板 .dwt, Idle 路径) 拿一张带 C3D 样式的干净图纸。
  console.log('=== 0. openDrawing（C3D 公制模板 → 干净图纸）===');
  let nd;
  try {
    nd = await tcp('openDrawing', { path: process.env.GEO_TEMPLATE || 'C:\\Users\\Jjo37\\AppData\\Local\\Autodesk\\C3D 2025\\chs\\Template\\_Autodesk Civil 3D (Metric) NCS.dwt' });
    console.log('  ' + JSON.stringify(nd).slice(0, 220));
  } catch (e) {
    console.log('  ⚠ 模板打开失败，沿用当前图纸: ' + e.message);
  }

  console.log('=== 1. createAssembly + createSubassembly（stock 子部件需显式挂，装配本身是空壳）===');
  const ca = await tcp('createAssembly', { name: ASM, insertX: 0, insertY: 0, assemblyType: 'UndividedPlanarRoad' });
  ok('装配已建', !!ca.handle, 'handle=' + ca.handle);
  const stock = await tcp('createSubassembly', { assemblyName: ASM, subassemblyType: 'BasicLane', insertX: 0, insertY: 0, side: 'Right' });
  const stockHandle = stock.subassemblies && stock.subassemblies[0] && stock.subassemblies[0].handle;
  ok('stock 子部件已挂(BasicLane)', !!stockHandle, 'handle=' + stockHandle);

  console.log('=== 2. verifyAssemblyGeometry（stock 子部件几何）===');
  const v1 = await tcp('verifyAssemblyGeometry', { assemblyName: ASM });
  console.log('  子部件数:', v1.subassemblyCount);
  ok('stock 子部件几何可读', v1.subassemblyCount > 0);
  if (v1.subassemblies && v1.subassemblies[0]) {
    const s0 = v1.subassemblies[0];
    console.log('  样例:', s0.name, 'points=' + s0.pointCount, 'links=' + s0.linkCount, 'shapes=' + s0.shapeCount,
      'w=' + s0.width, 'h=' + s0.height, 'areas=' + JSON.stringify(s0.hatchAreas));
    ok('样例子部件有几何点', s0.pointCount > 0);
  }

  console.log('=== 3. createCustomSubassembly（0.5×0.15 矩形，编程几何）===');
  const rect = await tcp('createCustomSubassembly', {
    subassemblyName: RECT,
    categoryName: 'HankCustom',
    assemblyName: ASM,
    insertX: 5,
    points: [
      { offset: 0, elevation: 0 },
      { offset: 0.5, elevation: 0 },
      { offset: 0.5, elevation: 0.15 },
      { offset: 0, elevation: 0.15 },
    ],
    links: [
      { pointIndices: [0, 1] }, { pointIndices: [1, 2] },
      { pointIndices: [2, 3] }, { pointIndices: [3, 0] },
    ],
    shapes: [{ linkIndices: [0, 1, 2, 3], code: 'Body' }],
  });
  ok('自定义子部件已建', !!rect.handle, 'handle=' + rect.handle);
  ok('已挂到装配', rect.attachedToAssembly === ASM, String(rect.attachedToAssembly));
  ok('pkt 已生成', !!rect.pktFilePath && fs.existsSync(rect.pktFilePath), rect.pktFilePath);

  console.log('=== 4. verifyAssemblyGeometry（校验自定义部件真几何）===');
  const v2 = await tcp('verifyAssemblyGeometry', { assemblyName: ASM });
  const rsub = (v2.subassemblies || []).find(s => s.name === RECT || s.handle === rect.handle);
  if (!rsub) { ok('装配里找到自定义部件', false, JSON.stringify((v2.subassemblies || []).map(s => s.name))); }
  else {
    ok('装配里找到自定义部件', true, rsub.name);
    console.log('  几何:', 'points=' + rsub.pointCount, 'links=' + rsub.linkCount, 'shapes=' + rsub.shapeCount,
      'w=' + rsub.width, 'h=' + rsub.height, 'areas=' + JSON.stringify(rsub.hatchAreas), 'pts=' + JSON.stringify(rsub.points));
    ok('几何点 4 个', rsub.pointCount === 4, String(rsub.pointCount));
    // 2026-09-10 修正后: linkCount 只数 C-ROAD-LINK 连接线（不再混入 C-ROAD-SHAP 造型边界线）
    ok('连接线 4 条', rsub.linkCount === 4, String(rsub.linkCount));
    ok('造型边界线计在 shapeLineCount', rsub.shapeLineCount === undefined || rsub.shapeLineCount >= 4, String(rsub.shapeLineCount));
    ok('结构层 1 层', rsub.shapeCount === 1, String(rsub.shapeCount));
    ok('宽 0.5', near(rsub.width, 0.5), String(rsub.width));
    ok('高 0.15', near(rsub.height, 0.15), String(rsub.height));
    ok('层面积 0.075', (rsub.hatchAreas || []).some(a => near(a, 0.075)), JSON.stringify(rsub.hatchAreas));
  }

  console.log('=== 5. probeGeometry（单子部件非破坏几何）===');
  const pg = await tcp('probeGeometry', { handle: rect.handle, maxDepth: 12 });
  // 返回结构: {sourceHandle, sourceType, itemCount, items:[{type,blockName,nested:[...]}]}
  const flat = [];
  (function walk(list) { for (const it of list || []) { flat.push(it); if (it.nested) walk(it.nested); } })(pg.items || pg.entities || pg.geometry || []);
  const cnt = t => flat.filter(i => i.type === t).length;
  console.log('  图元:', JSON.stringify(countsSummary(flat)));
  ok('probe 出几何（总数>0）', flat.length > 0, String(flat.length));
  ok('Circle ≥ 4（几何点标记）', cnt('AcDbCircle') >= 4, String(cnt('AcDbCircle')));
  // 注意: Line 含两类图层——C-ROAD-SHAP(造型边界) + C-ROAD-LINK(连接线)，故线数≥2×link 数
  ok('Line ≥ 4（边界/连接）', cnt('AcDbLine') >= 4, String(cnt('AcDbLine')));
  ok('Hatch ≥ 1（结构层）', cnt('AcDbHatch') >= 1, String(cnt('AcDbHatch')));

  console.log('=== 6. getSubassemblyGeometry（挂载链/参数）===');
  const gs = await tcp('getSubassemblyGeometry', { assemblyName: ASM });
  const list = gs.subassemblies || gs.parts || [];
  console.log('  条目:', list.length);
  ok('返回子部件列表', list.length > 0, String(list.length));
  if (list[0]) console.log('  样例:', JSON.stringify(list[0]).slice(0, 300));
  const hooked = list.find(s => s.pointIndexHookTo !== undefined);
  ok('pointIndexHookTo 哨兵值已归一化(非 -2147483648)', !list.some(s => s.pointIndexHookTo === -2147483648), JSON.stringify(list.map(s => s.pointIndexHookTo)));

  console.log('\n' + (fail === 0 ? '✅ 全部通过' : '⚠️ 有失败') + ` (pass=${pass} fail=${fail}) 装配=${ASM}`);
  process.exit(fail === 0 ? 0 : 1);
})().catch(e => { console.error('❌ ' + e.message); process.exit(1); });

function countsSummary(flat) {
  const m = {};
  for (const i of flat) m[i.type] = (m[i.type] || 0) + 1;
  return m;
}
