// AcBridge-Relay 守护启动器 v2 (2026-08-07)
// 变更: stdio 落盘 + 崩溃自动重启 + 防抖冷却
// 背景: 异机实测 relay 反复退出, 旧版 stdio:'ignore' 导致死因不可追溯, 本版根治:
//   - 子进程 stdout/stderr 重定向到 relay-launcher.log, 崩溃原因不再丢失
//   - 退出后 3s 自动重启; 连续崩溃 5 次冷却 60s, 避免死循环打爆 CPU
//   - launcher 常驻守护, 不再 unref 后立即退出
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const nodeBin = path.join(__dirname, '..', 'node.exe');
const nodeCmd = fs.existsSync(nodeBin) ? nodeBin : 'node';
const relayScript = path.join(__dirname, 'panel-relay.js');
const logFile = path.join(__dirname, 'relay-launcher.log');

const RESTART_DELAY_MS = 3000;   // 崩溃后重启等待
const CRASH_THRESHOLD = 5;       // 连续崩溃阈值
const COOLDOWN_MS = 60000;       // 冷却时长

let crashCount = 0;
let child = null;
let stopping = false;

// 2026-08-12 M8: 单实例保护——锁文件 + pid 校验, 防重复启动 EADDRINUSE 崩溃循环
const LOCK_FILE = path.join(__dirname, 'relay-launcher.lock');
function acquireLock() {
  try {
    if (fs.existsSync(LOCK_FILE)) {
      const oldPid = parseInt(fs.readFileSync(LOCK_FILE, 'utf8').trim(), 10);
      if (oldPid && !isNaN(oldPid)) {
        try { process.kill(oldPid, 0); console.log('[launcher] 已有实例运行 (PID ' + oldPid + '), 退出。'); process.exit(0); } catch (_) { /* 旧进程不存在, 可接管 */ }
      }
    }
    fs.writeFileSync(LOCK_FILE, String(process.pid), 'utf8');
    process.on('exit', () => { try { fs.unlinkSync(LOCK_FILE); } catch (_) {} });
    return true;
  } catch (_) { return true; } // 锁文件写失败不阻塞启动
}

function log(msg) {
  const line = '[' + new Date().toISOString() + '] ' + msg + '\n';
  try { fs.appendFileSync(logFile, line); } catch (_) {}
  console.log(line.trim());
}

function start() {
  if (stopping) return;
  const out = fs.openSync(logFile, 'a');
  const err = fs.openSync(logFile, 'a');
  child = spawn(nodeCmd, [relayScript], {
    cwd: __dirname,
    stdio: ['ignore', out, err],
    windowsHide: true
  });
  fs.closeSync(out);
  fs.closeSync(err);
  log('Relay started, PID: ' + child.pid);

  child.on('exit', (code, signal) => {
    child = null;
    if (stopping) {
      log('Relay exited (code=' + code + ', signal=' + signal + '), launcher shutting down');
      return;
    }
    // 2026-08-10: 正常退出（code=0，如 /restart 主动重启）不计崩溃、不累计冷却
    if (code === 0) {
      log('Relay exited cleanly (code=0), restarting in ' + RESTART_DELAY_MS + 'ms');
      setTimeout(start, RESTART_DELAY_MS);
      return;
    }
    crashCount++;
    log('Relay exited (code=' + code + ', signal=' + signal + '), crashCount=' + crashCount);
    let delay = RESTART_DELAY_MS;
    if (crashCount >= CRASH_THRESHOLD) {
      delay = COOLDOWN_MS;
      log('连续崩溃 ' + crashCount + ' 次, 冷却 ' + (COOLDOWN_MS / 1000) + 's 后继续');
      crashCount = 0;
    }
    setTimeout(start, delay);
  });
}

process.on('SIGINT', () => { stopping = true; if (child) child.kill(); });
process.on('SIGTERM', () => { stopping = true; if (child) child.kill(); });

log('Launcher v2 started');
acquireLock();
start();