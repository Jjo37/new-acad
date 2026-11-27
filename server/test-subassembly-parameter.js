// setSubassemblyParameter 回归（2026-11-16）
// 验证：参数化 SAC 部件导入后改参数 → 几何跟着变；未知参数名/非法值必须报错（不静默）
// 用法: node server/test-subassembly-parameter.js   （需 C3D + 插件 8080 + relay 19876 在线）
const fs = require('fs');
const path = require('path');

async function tcp(method, params) {
  const r = await fetch('http://127.0.0.1:19876/tcp', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: Date.now(), method, params })
  }).then(r => r.json());
  if (r.error) throw new Error(method + ': ' + JSON.stringify(r.error));
  return r.result;
}

(async () => {
  const ts = Date.now().toString(36);
  const NAME = 'ParamTest' + ts;
  const ASM = 'ParamAsm' + ts;
  const xaml = fs.readFileSync(path.join(__dirname, '..', 'knowledge', 'sac-templates', 'UDitch-param.xaml'), 'utf8');

  console.log('=== 1. build + import 参数化部件 ===');
  const build = await tcp('buildSACSubassembly', { subassemblyName: NAME, xamlContent: xaml, categoryName: 'HankTest', description: 'param regression' });
  await tcp('createAssembly', { name: ASM, insertX: 0, insertY: 0, assemblyType: 'UndividedPlanarRoad' });
  await tcp('importSACSubassembly', { subassemblyName: NAME, pktFilePath: build.pktFilePath, assemblyName: ASM, insertX: 0 });

  const areaOf = async () => {
    const v = await tcp('verifyAssemblyGeometry', { assemblyName: ASM });
    const sub = (v.subassemblies || []).find(s => s.name && s.name.startsWith(NAME));
    return sub && sub.hatchAreas ? sub.hatchAreas[sub.hatchAreas.length - 1] : null;
  };

  const base = await areaOf();
  if (Math.abs(base - 1.02) > 0.005) throw new Error('baseline area wrong: ' + base);
  console.log('  baseline 衬砌面积 =', base, 'OK');

  console.log('=== 2. 改参数 DitchW 1.4->1.6 / DitchBottomT 0.3->0.4 ===');
  const s1 = await tcp('setSubassemblyParameter', { assemblyName: ASM, subassemblyName: NAME, parameters: { DitchW: 1.6, DitchBottomT: 0.4 } });
  const applied = s1.result[0].applied;
  if (!applied.every(a => a.changed)) throw new Error('not all params reported changed: ' + JSON.stringify(applied));
  const changed = await areaOf();
  if (Math.abs(changed - 1.44) > 0.005) throw new Error('geometry did not follow the parameter: ' + changed);
  console.log('  面积变到', changed, 'OK（= 1.6×1.4 − 0.8×1.0）');

  console.log('=== 3. 改回 ===');
  await tcp('setSubassemblyParameter', { assemblyName: ASM, subassemblyName: NAME, parameters: { DitchW: 1.4, DitchBottomT: 0.3 } });
  const back = await areaOf();
  if (Math.abs(back - 1.02) > 0.005) throw new Error('did not restore: ' + back);
  console.log('  面积回到', back, 'OK');

  console.log('=== 4. 未知参数名必须报错 ===');
  let rejected = false;
  try { await tcp('setSubassemblyParameter', { assemblyName: ASM, subassemblyName: NAME, parameters: { NoSuchParam: 1 } }); }
  catch (e) { if (/not found/i.test(e.message)) rejected = true; else throw e; }
  if (!rejected) throw new Error('unknown parameter was silently ignored');
  console.log('  OK（报 not found）');

  console.log('=== 5. 非法数值必须报错 ===');
  rejected = false;
  try { await tcp('setSubassemblyParameter', { assemblyName: ASM, subassemblyName: NAME, parameters: { DitchW: 'abc' } }); }
  catch (e) { if (/not a valid/i.test(e.message)) rejected = true; else throw e; }
  if (!rejected) throw new Error('invalid value was silently accepted');
  console.log('  OK（报 not a valid）');

  console.log('\n✅ setSubassemblyParameter 全部通过（5/5）');
})().catch(e => { console.error('❌', e.message); process.exit(1); });
