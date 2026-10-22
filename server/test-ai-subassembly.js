// AI 自主生成 SAC 部件端到端回归（2026-08-17）：从需求生成 XAML（AIShoulder 模板）→ build → import → 挂装配 → 验证
// 用法: node server/test-ai-subassembly.js （需 C3D+relay 在线）
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
  // AI 从需求生成 XAML（AIShoulder 定制路肩；真实场景由 llm-agent 按 sac-xaml-guide 现场写）
  const XAML = fs.readFileSync(path.join(__dirname, '..', 'knowledge', 'sac-templates', 'AIShoulder.xaml'), 'utf8');
  const NAME = 'AIShoulder' + Date.now().toString(36);
  const ASM = 'AIShoulderAsm' + Date.now().toString(36);

  console.log('=== 1. buildSACSubassembly（AI XAML 打包）===');
  const build = await tcp('buildSACSubassembly', {
    subassemblyName: NAME, xamlContent: XAML, categoryName: 'HankAI', description: 'AI 定制路肩：水平 + 1:4 放坡'
  });
  const pkt = build.pktFilePath;
  console.log('  pkt:', pkt, '大小:', fs.statSync(pkt).size);

  console.log('=== 2. importSACSubassembly（导入）===');
  const imp = await tcp('importSACSubassembly', { subassemblyName: NAME, pktFilePath: pkt });
  console.log('  导入 handle:', imp.handle);

  console.log('=== 3. createAssembly + 挂装配 ===');
  await tcp('createAssembly', { name: ASM, insertX: 0, insertY: 0, assemblyType: 'UndividedPlanarRoad' });
  const NAME2 = NAME + 'B';
  const imp2 = await tcp('importSACSubassembly', { subassemblyName: NAME2, pktFilePath: pkt, assemblyName: ASM, insertX: 0 });
  console.log('  挂载:', imp2.attachedToAssembly);

  console.log('=== 4. getAssembly 验证（AI 部件参数）===');
  const ga = await tcp('getAssembly', { name: ASM });
  const sub = ga.subassemblies.find(x => x.name === NAME2);
  console.log('  子装配:', sub.name);
  console.log('  参数:', JSON.stringify(sub.parameters));
  if (!sub.parameters || sub.parameters.ShoulderWidth !== 3 || sub.parameters.Foreslope !== 0.25)
    throw new Error('参数未解析（ShoulderWidth=3, Foreslope=0.25 期望）');

  console.log('\n✅ AI 自主生成部件端到端通过');
  console.log('  部件:', NAME, ' 装配:', ASM);
})().catch(e => { console.error('❌', e.message); process.exit(1); });
