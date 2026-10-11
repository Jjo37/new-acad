// SAC 端到端回归测试（2026-08-17）：buildSACSubassembly → importSACSubassembly → createAssembly → 挂装配 → getAssembly 验证
// 用法: node server/test-sac-e2e.js   （需 C3D + 插件 8080 + relay 19876 在线）
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
  const TS = Date.now().toString(36);
  const NAME = 'AIE2E' + TS;            // 部件名（每次唯一避免同名冲突）
  const ASM = 'AIE2EAsm' + TS;          // 装配名
  const TEMPLATE = path.join(__dirname, '..', 'knowledge', 'sac-templates', 'EvenSlopeDitch.xaml');
  const xaml = fs.readFileSync(TEMPLATE, 'utf8');

  console.log('=== 1. buildSACSubassembly（XAML → .pkt）===');
  const build = await tcp('buildSACSubassembly', {
    subassemblyName: NAME, xamlContent: xaml, categoryName: 'HankAI', description: 'SAC e2e regression'
  });
  const pkt = build.pktFilePath;
  console.log('  pkt:', pkt, '存在:', fs.existsSync(pkt), '大小:', fs.statSync(pkt).size);
  if (!pkt || !fs.existsSync(pkt)) throw new Error('pkt 未生成');

  console.log('=== 2. importSACSubassembly（导入 C3D）===');
  const imp = await tcp('importSACSubassembly', { subassemblyName: NAME, pktFilePath: pkt });
  console.log('  导入 handle:', imp.handle);
  if (!imp.imported) throw new Error('导入失败');

  console.log('=== 3. createAssembly（建装配）===');
  const ca = await tcp('createAssembly', { name: ASM, insertX: 0, insertY: 0, assemblyType: 'UndividedPlanarRoad' });
  console.log('  装配 handle:', ca.handle);

  console.log('=== 4. importSACSubassembly + assemblyName（挂装配）===');
  const NAME2 = NAME + 'B';
  const imp2 = await tcp('importSACSubassembly', { subassemblyName: NAME2, pktFilePath: pkt, assemblyName: ASM, insertX: 0 });
  console.log('  挂载:', JSON.stringify(imp2));
  if (imp2.attachedToAssembly !== ASM) throw new Error('未挂到装配');

  console.log('=== 5. getAssembly 验证（部件 + 参数解析）===');
  const ga = await tcp('getAssembly', { name: ASM });
  const s = JSON.stringify(ga);
  if (!s.includes(NAME2)) throw new Error('装配里找不到子装配');
  const sub = ga.subassemblies.find(x => x.name === NAME2);
  console.log('  子装配:', sub.name, '参数:', JSON.stringify(sub.parameters));
  if (!sub.parameters || Object.keys(sub.parameters).length < 3) throw new Error('参数未解析（XAML 编译失败）');

  console.log('\n✅ SAC 端到端全部通过（XAML→pkt→导入→挂装配→参数解析）');
  console.log('  部件:', NAME, '/', NAME2, ' 装配:', ASM);
})().catch(e => { console.error('❌', e.message); process.exit(1); });
