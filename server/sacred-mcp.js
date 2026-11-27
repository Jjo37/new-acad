// Sacred MCP Server 启动器
// MCP 源码已内嵌在 server/mcp/build/ 目录
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const MCP_DIR = path.resolve(__dirname);
const INDEX = path.join(MCP_DIR, 'mcp', 'build', 'index.js');

// P1-7 修复: 优先用包内 node.exe（便携版），兑现"无需安装 Node"承诺
const nodeBin = path.join(__dirname, '..', 'node.exe');
const nodeCmd = fs.existsSync(nodeBin) ? nodeBin : 'node';

// 2026-11-05: 端口必须跟着 config.json 的 mcpPort 走。
// 以前这里不传 MCP_HTTP_PORT，httpBridge 默认 3000 —— 用户把 mcpPort 改成 3002 后，
// 面板探测 3002 说「未启动」，MCP 其实在 3000（或因为 3000 被占根本没绑上），
// 而 C3D 每次懒启动又拉起一对新进程 -> 僵进程越堆越多。
function resolveMcpPort() {
  const envPort = parseInt(process.env.MCP_HTTP_PORT || '', 10);
  if (envPort > 0 && envPort < 65536) return envPort;
  try {
    const cfg = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'config.json'), 'utf8'));
    const p2 = parseInt(cfg.mcpPort, 10);
    if (p2 > 0 && p2 < 65536) return p2;
  } catch (_) {
    /* config.json 读不到就用默认端口；面板诊断会如实显示实际监听端口 */
  }
  return 3000;
}
const mcpPort = resolveMcpPort();
const p = spawn(nodeCmd, [INDEX], {
  cwd: path.join(MCP_DIR, 'mcp'),
  env: { ...process.env, MCP_HTTP_PORT: String(mcpPort), CIVIL3D_HOST: '127.0.0.1', CIVIL3D_ENABLE_HELP_REINDEX: 'true' },  // 2026-11-05: MCP_HTTP_PORT 跟随 config.mcpPort
  stdio: ['pipe', 'pipe', 'pipe']
});
p.on('error', (e) => { console.error('[MCP] 启动失败: ' + e.message + '（node.exe 缺失或路径错误？）'); }); // 2026-08-14 P2-3

p.stdout.on('data', d => process.stdout.write(d));
p.stderr.on('data', d => process.stderr.write(d));

p.on('exit', code => {
  console.log('Sacred MCP exited with code', code);
  process.exit(code);
});
