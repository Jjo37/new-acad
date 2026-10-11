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

const p = spawn(nodeCmd, [INDEX], {
  cwd: path.join(MCP_DIR, 'mcp'),
  env: { ...process.env, CIVIL3D_HOST: '127.0.0.1', CIVIL3D_ENABLE_HELP_REINDEX: 'true' },  // 2026-08-07: 启用 civil3d_help/docs（默认禁用）
  stdio: ['pipe', 'pipe', 'pipe']
});
p.on('error', (e) => { console.error('[MCP] 启动失败: ' + e.message + '（node.exe 缺失或路径错误？）'); }); // 2026-08-14 P2-3

p.stdout.on('data', d => process.stdout.write(d));
p.stderr.on('data', d => process.stderr.write(d));

p.on('exit', code => {
  console.log('Sacred MCP exited with code', code);
  process.exit(code);
});
