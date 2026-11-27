'use strict';
/**
 * calc-runner.js — 批量计算的子进程执行器（2026-11-14，v1.17 第二片 / F1）
 *
 * 只做一件事：读 stdin 上的 JSON 任务，在**受限上下文**里跑给定 JS，把结果写 stdout。
 * 父进程（calc-sandbox.js）负责超时/内存/进程隔离；本文件负责"能干什么"的边界。
 *
 * 边界（与"严格照图"同源：算数只允许用输入数据，不允许偷偷联网或读别的目录）：
 *   · 不暴露 require / process / import / 网络 API —— 进不来也出不去
 *   · 文件读写限定在 projectRoot 内；写只允许 workDir（agent-work）
 *   · 输出按字符上限截断，避免把上下文灌爆
 *   · 只用同步 API（vm 的 timeout 才管得住；异步通道一律不给）
 */

const fs = require('fs');
const path = require('path');
const vm = require('vm');

function send(obj) {
  try { process.stdout.write(JSON.stringify(obj)); } catch (_) { /* 输出失败只能退出 */ }
}

function fail(msg, extra) {
  send(Object.assign({ ok: false, error: String(msg) }, extra || {}));
  process.exitCode = 1;
}

let input = '';
process.stdin.on('data', (c) => { input += c; });
process.stdin.on('end', () => {
  let req;
  try { req = JSON.parse(input || '{}'); } catch (e) { return fail('payload 解析失败: ' + e.message); }
  const code = String(req.code || '');
  if (!code.trim()) return fail('code 为空');

  const projectRoot = path.resolve(String(req.projectRoot || '.'));
  const workDir = path.resolve(String(req.workDir || projectRoot));
  const maxOut = Number(req.maxOutputChars) > 0 ? Number(req.maxOutputChars) : 20000;
  const timeoutMs = Number(req.timeoutMs) > 0 ? Number(req.timeoutMs) : 8000;

  const inRoot = (p, root) => {
    const r = path.resolve(root);
    const t = path.resolve(p);
    return t === r || t.startsWith(r + path.sep);
  };
  const resolveRead = (p) => {
    if (!p) throw new Error('路径为空');
    const abs = path.isAbsolute(String(p)) ? path.resolve(String(p)) : path.resolve(projectRoot, String(p));
    if (!inRoot(abs, projectRoot)) throw new Error('读越界：只允许项目文件夹内的文件 —— ' + abs);
    return abs;
  };
  const resolveWrite = (p) => {
    if (!p) throw new Error('路径为空');
    const abs = path.isAbsolute(String(p)) ? path.resolve(String(p)) : path.resolve(workDir, String(p));
    if (!inRoot(abs, workDir)) throw new Error('写越界：只允许写到 agent-work —— ' + abs);
    return abs;
  };

  const logs = [];
  const writes = [];
  const pushLog = (level) => (...args) => {
    if (logs.length >= 200) return;
    logs.push('[' + level + '] ' + args.map((a) => {
      try { return typeof a === 'string' ? a : JSON.stringify(a); } catch (_) { return String(a); }
    }).join(' ').slice(0, 600));
  };

  const helpers = {
    readFile: (p, enc) => fs.readFileSync(resolveRead(p), enc || 'utf8'),
    readCsv: (p, opts) => {
      const o = opts || {};
      const text = fs.readFileSync(resolveRead(p), 'utf8').replace(/^\uFEFF/, '');
      const delim = o.delimiter || ',';
      const lines = text.split(/\r?\n/).filter((l) => l.trim().length);
      if (!lines.length) return { columns: [], rows: [] };
      const split = (l) => l.split(delim).map((s) => s.trim());
      const first = split(lines[0]);
      const hasHeader = o.header !== false;   // 默认有表头
      const columns = hasHeader ? first : first.map((_, i) => 'c' + i);
      const body = hasHeader ? lines.slice(1) : lines;
      const rows = body.map((l) => {
        const cells = split(l);
        const row = {};
        columns.forEach((c, i) => {
          const v = cells[i];
          const num = v === '' || v === undefined ? NaN : Number(v);
          row[c] = Number.isFinite(num) ? num : (v === undefined ? '' : v);
        });
        return row;
      });
      return { columns, rows };
    },
    readJson: (p) => JSON.parse(fs.readFileSync(resolveRead(p), 'utf8').replace(/^\uFEFF/, '')),
    writeFile: (p, content, enc) => {
      const abs = resolveWrite(p);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, String(content), enc || 'utf8');
      writes.push(abs);
      return { written: abs, bytes: Buffer.byteLength(String(content)) };
    },
    listDir: (p) => {
      const abs = resolveRead(p || '.');
      return fs.readdirSync(abs, { withFileTypes: true }).map((e) => e.name + (e.isDirectory() ? '/' : ''));
    },
    out: (...args) => { pushLog('out')(...args); },
  };

  const sandbox = Object.assign(Object.create(null), {
    Math, JSON, Date, Number, String, Boolean, Array, Object, Map, Set,
    parseFloat, parseInt, isNaN, isFinite, encodeURIComponent, decodeURIComponent,
    console: { log: pushLog('log'), warn: pushLog('warn'), error: pushLog('error'), info: pushLog('info') },
    out: helpers.out,
    readFile: helpers.readFile,
    readCsv: helpers.readCsv,
    readJson: helpers.readJson,
    writeFile: helpers.writeFile,
    listDir: helpers.listDir,
  });
  vm.createContext(sandbox, { name: 'calc', codeGeneration: { strings: false, wasm: false } });

  let result;
  let err = null;
  const t0 = Date.now();
  try {
    result = vm.runInContext('(function(){"use strict";\n' + code + '\n})()', sandbox, { timeout: timeoutMs, displayErrors: true });
  } catch (e) {
    err = e && e.message ? e.message : String(e);
    // vm 的原始文案是英文 "Script execution timed out after Nms"，换成人话
    if (/Script execution timed out/i.test(err)) err = '计算超时（超过 ' + timeoutMs + 'ms），已中断；请缩小批量或拆分任务';
  }
  const ms = Date.now() - t0;

  let resultText = '';
  if (!err) {
    try { resultText = typeof result === 'string' ? result : JSON.stringify(result); } catch (_) { resultText = String(result); }
    if (resultText === undefined) resultText = '';
    if (resultText.length > maxOut) {
      resultText = resultText.slice(0, maxOut) + '\n…（结果 ' + resultText.length + ' 字符，已截断到 ' + maxOut + '；要更多请写文件再用 queryFile 聚合）';
    }
  }

  send({
    ok: !err,
    error: err,
    ms,
    result: resultText,
    logs: logs.slice(0, 100),
    writes,
  });
  process.exitCode = err ? 1 : 0;
});
