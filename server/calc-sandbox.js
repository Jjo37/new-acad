'use strict';
/**
 * calc-sandbox.js — 父进程侧：把一段 JS 丢给 calc-runner 子进程跑，拿回结果（2026-11-14 / F1）
 *
 * 为什么要有这个东西：
 *   2026-11-14 实测一轮真实项目：1312 次工具调用里 **873 次（66.5%）是单发三角**
 *   （cogoDirectionDistance 521 / cogoCurveSolve 213 / cogoInverse 139，平均 3–4 ms）。
 *   每次都要过一个完整 LLM 轮次 —— 工具只花了 20 分钟，轮次和上下文才是真成本。
 *   106 个 PI 的曲线要素本该一次算完。
 *
 * 隔离方式（三层，缺一不可）：
 *   1. 子进程 + Node 权限模型：--permission --allow-fs-read=<项目> --allow-fs-write=<agent-work>
 *   2. 沙箱上下文：不暴露 require/process/网络 API，codeGeneration.strings=false（挡 eval 逃逸）
 *   3. 硬上限：超时（默认 10s，最大 30s）+ 堆上限 256MB + 输出字符上限
 */

const { spawn } = require('child_process');
const path = require('path');

const RUNNER = path.join(__dirname, 'calc-runner.js');
const DEFAULT_TIMEOUT = 10000;
const MAX_TIMEOUT = 30000;
const MAX_OUTPUT = 20000;
const HEAP_MB = 256;
const HARD_KILL_MS = 45000;

// Node 22+ 权限模型；老版本没有该 flag 时自动回落（回落时仍保留沙箱上下文与超时）
let permissionSupported = null;
function supportsPermissionModel() {
  if (permissionSupported !== null) return permissionSupported;
  try {
    const r = require('child_process').spawnSync(process.execPath, ['--permission', '-e', '0'], { timeout: 5000, encoding: 'utf8' });
    permissionSupported = r.status === 0;
  } catch (_) {
    permissionSupported = false;
  }
  return permissionSupported;
}

/**
 * @param {{code:string, projectRoot:string, workDir:string, timeoutMs?:number, maxOutputChars?:number}} job
 * @returns {Promise<{ok:boolean, result:string, logs:string[], writes:string[], ms:number, error:string|null, softLimit?:string}>}
 */
function runCalc(job) {
  const timeoutMs = Math.min(Math.max(Number(job.timeoutMs) || DEFAULT_TIMEOUT, 1000), MAX_TIMEOUT);
  const payload = JSON.stringify({
    code: String(job.code || ''),
    projectRoot: job.projectRoot,
    workDir: job.workDir,
    timeoutMs: timeoutMs,
    maxOutputChars: Number(job.maxOutputChars) > 0 ? Math.min(Number(job.maxOutputChars), 200000) : 10000,
  });

  const args = ['--max-old-space-size=' + HEAP_MB];
  let softLimit = '';
  if (supportsPermissionModel()) {
    args.push('--permission',
      '--allow-fs-read=' + path.resolve(job.projectRoot),
      '--allow-fs-read=' + RUNNER,
      '--allow-fs-write=' + path.resolve(job.workDir));
  } else {
    softLimit = 'permission-model-unavailable';
  }
  args.push(RUNNER);

  return new Promise((resolve) => {
    const child = spawn(process.execPath, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let out = '';
    let errOut = '';
    let killedByTimer = false;
    const t0 = Date.now();
    const timer = setTimeout(() => { killedByTimer = true; try { child.kill('SIGKILL'); } catch (_) { /* 已退出 */ } }, timeoutMs + 3000);
    const hardTimer = setTimeout(() => { try { child.kill('SIGKILL'); } catch (_) { /* 已退出 */ } }, HARD_KILL_MS);

    child.stdout.on('data', (c) => { if (out.length < 2_000_000) out += c; });
    child.stderr.on('data', (c) => { if (errOut.length < 20000) errOut += c; });
    child.on('error', (e) => {
      clearTimeout(timer); clearTimeout(hardTimer);
      resolve({ ok: false, result: '', logs: [], writes: [], ms: Date.now() - t0, error: '启动沙箱失败: ' + e.message, softLimit });
    });
    child.on('close', (code) => {
      clearTimeout(timer); clearTimeout(hardTimer);
      const ms = Date.now() - t0;
      if (killedByTimer) {
        return resolve({ ok: false, result: '', logs: [], writes: [], ms, error: '计算超时（>' + timeoutMs + 'ms）已被杀掉；请缩小批量或拆分', softLimit });
      }
      let parsed = null;
      try { parsed = JSON.parse(out); } catch (_) { /* 下面统一报 */ }
      if (!parsed) {
        return resolve({ ok: false, result: '', logs: [], writes: [], ms, error: '沙箱无有效输出（exit ' + code + '）: ' + (errOut || out).slice(0, 400), softLimit });
      }
      resolve({ ok: !!parsed.ok, result: String(parsed.result || ''), logs: Array.isArray(parsed.logs) ? parsed.logs : [], writes: Array.isArray(parsed.writes) ? parsed.writes : [], ms, error: parsed.error || null, softLimit });
    });

    child.stdin.end(payload);
  });
}

const CALC_TOOL = {
  type: 'function',
  function: {
    name: 'calc',
    description: '本地批量计算沙箱（不碰 CAD、不联网、无 require）。需要"成批算数"时用它，别用 cadCall 一个一个算：三角函数、曲线要素、坐标换算、桩号链、CSV 统计、批量格式换算、生成点/多段线坐标表。'
      + '\n可用 API（都是同步的）：'
      + '\n· readCsv(path[,{delimiter,header}]) → **{columns:[\'列名\'...], rows:[{列名:值}...]}**（默认逗号 + 有表头；值是数字就自动转 Number）'
      + '\n· readFile(path) → 字符串；readJson(path) → 对象；listDir(path) → [名字]'
      + '\n· writeFile(path, content) → 只能写 agent-work 下（返回 {written,bytes}）'
      + '\n· out(x) / console.log(x) → 进日志；代码末尾 return 的值就是结果'
      + '\n路径一律相对「项目根」写（如 agent-work/plan-PI-table.csv）。'
      + '\n用法示例（照这个写）：'
      + '\n  const t = readCsv("agent-work/x.csv");'
      + '\n  const R2D = 180/Math.PI;'
      + '  const rows = t.rows.map(r => ({ idx: r.idx, T: +(r.radius*Math.tan(r.delta_deg/R2D/2)).toFixed(4) }));'
      + '\n  return { n: rows.length, rows: rows.slice(0,3) };'
      + '\n⚠️ return 的值必须是普通对象/数组（Map/Set 会被 JSON 序列化成 {}，实测踩过）；writeFile 的相对路径就是 agent-work 内，不要再套一层 agent-work/。'
      + '\n结果超 1 万字符会截断，大结果请 writeFile 后用 queryFile 聚合。默认超时 10s。',
    parameters: {
      type: 'object',
      properties: {
        code: { type: 'string', description: '要执行的 JS 源码（同步代码；末尾 return 的值就是结果；不能 require/联网）' },
        timeoutMs: { type: 'number', description: '超时毫秒（默认 10000，最大 30000）' },
        maxOutputChars: { type: 'number', description: '结果字符上限（默认 10000）' },
      },
      required: ['code'],
    },
  },
};

module.exports = { runCalc, CALC_TOOL, supportsPermissionModel };
