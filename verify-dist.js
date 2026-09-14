// 发布包完整性自查 v3（2026-08-14）— build-dist.ps1 打包后运行
// 用法: node verify-dist.js   （在 new-acad-release 根目录，或任意位置指定参数）
// 用法: node verify-dist.js D:\some\new-acad-v1.3.0.zip   （显式指定 zip）
// 自动: 找最新 new-acad-v*.zip → 解压临时目录 → 29 项检查 → 清理
// 输出: [OK]/[FAIL] 逐项 + 汇总；有 FAIL 说明包有问题，别发出去
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execSync } = require('child_process');

const REL = 'D:\\new-acad-release';
let zip = process.argv[2];

if (!zip) {
  const zips = fs.existsSync(REL)
    ? fs.readdirSync(REL).filter(f => /^new-acad-v\d+\.\d+\.\d+\.zip$/.test(f)).map(f => path.join(REL, f))
    : [];
  if (!zips.length) { console.error('未找到 new-acad-v*.zip，请指定路径'); process.exit(1); }
  zips.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
  zip = zips[0];
}
console.log('验证包: ' + zip);

const tmp = path.join(os.tmpdir(), 'nac-verify-' + Date.now());
try {
  execSync(`powershell -NoProfile -Command "Expand-Archive -Path '${zip}' -DestinationPath '${tmp}' -Force"`, { stdio: 'pipe' });
} catch (e) { console.error('解压失败: ' + e.message); process.exit(1); }

const checks = [];
function chk(name, cond, extra) { checks.push({ name, ok: !!cond, extra: extra || '' }); }

const need = [
  'install.ps1', 'install.bat', 'uninstall.ps1', 'README.txt', 'config.json', 'node.exe',
  'server\\panel-relay.js', 'server\\llm-agent.js', 'server\\compact.js', 'server\\cad-tools.js', 'server\\file-tools.js',
  'server\\relay-launcher.js', 'server\\sacred-mcp.js', 'server\\start-relay.bat',
  'plugin\\AcBridge-v24\\Civil3DMcpPlugin.dll',
  'plugin\\AcBridge-v24\\Civil3DMcpPlugin.deps.json',
  'plugin\\AcBridge-v24\\Civil3DMcpPlugin.runtimeconfig.json',
  'tools\\find-civil3d.ps1', 'tools\\clean-runtime.ps1',
  // 2026-08-17: AI 生成 SAC 部件必需（knowledge.md 指引 AI 读 guide + 模板）
  'knowledge\\sac-xaml-guide.md',
  'knowledge\\sac-templates\\EvenSlopeDitch.xaml',
  'knowledge\\sac-templates\\CurbWallRadius.xaml',
  'knowledge\\sac-templates\\ShoulderRoundedTarget.xaml',
  'knowledge\\sac-templates\\PermeablePavement.xaml',
  'knowledge\\sac-templates\\AIShoulder.xaml',
  'knowledge\\ai-sac-subassembly-plan.md',
  'workflow-guide.md'
];
for (const f of need) chk('file: ' + f, fs.existsSync(path.join(tmp, f)));

const ins = fs.readFileSync(path.join(tmp, 'install.ps1'), 'utf8');
chk('install.ps1 FSO 模板', ins.includes('fso.GetParentFolderName') && ins.includes('relay-launcher.js'));
chk('install.ps1 无三引号模板', !ins.includes('ws.Run """'));
chk('install.ps1 无 OpenClaw 字样', !ins.includes('OpenClaw'));
chk('install.ps1 生成 Hank.lsp(NETLOAD)', ins.includes('NETLOAD') && ins.includes('$dllForward'));

const dll = fs.readFileSync(path.join(tmp, 'plugin\\AcBridge-v24\\Civil3DMcpPlugin.dll'));
chk('DLL 含 schtasks 重启修复', dll.includes(Buffer.from('schtasks', 'utf16le')), 'dll=' + dll.length + 'B');

const pr = fs.readFileSync(path.join(tmp, 'server\\panel-relay.js'), 'utf8');
chk('relay 默认 llm', pr.includes("cfg.relayMode) || 'llm'"));
chk('relay 无token提示按模式区分', pr.includes("RELAY_MODE === 'llm'"));

try {
  const cfg = JSON.parse(fs.readFileSync(path.join(tmp, 'config.json'), 'utf8'));
  chk('config relayMode=llm', cfg.relayMode === 'llm');
} catch { chk('config.json 可解析', false); }

const ne = fs.statSync(path.join(tmp, 'node.exe'));
chk('node.exe 有效 (>50MB)', ne.size > 50 * 1024 * 1024, ne.size + 'B');

const readme = fs.readFileSync(path.join(tmp, 'README.txt'), 'utf8');
chk('README 含 install 指引', /install/i.test(readme) && /右键|运行/i.test(readme));

const bat = fs.existsSync(path.join(tmp, 'install.bat')) ? fs.readFileSync(path.join(tmp, 'install.bat'), 'utf8') : '';
chk('install.bat 指向 install.ps1', bat.includes('install.ps1') && bat.includes('Bypass'));

fs.rmSync(tmp, { recursive: true, force: true });

let fail = 0;
for (const c of checks) {
  console.log((c.ok ? '[OK] ' : '[FAIL] ') + c.name + (c.extra ? '  (' + c.extra + ')' : ''));
  if (!c.ok) fail++;
}
console.log('\n' + (fail ? fail + ' FAIL — 包有问题，别发' : 'ALL PASS — 包可发') + ' (' + checks.length + ' checks)');
process.exit(fail ? 1 : 0);
