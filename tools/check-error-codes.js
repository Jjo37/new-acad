#!/usr/bin/env node
/**
 * check-error-codes.js -- 域码与数字码必须走同一张表 (2026-10-29)
 *
 * Why: `CIVIL3D.CIVIL_NOT_INSTALLED` 曾经在 JIT 缺口分支里硬编码 `-32603`，而 `-32603` 是
 * JSON-RPC 的 "internal error"（真崩溃也用这个号），同一条域码在 `CivilSupport.Require()` 路径
 * 却得 `-32000`。靠数字码判读的客户端因此分不清"缺 Civil 3D"和"插件崩了"。
 *
 * 更难看的是：这张映射表早就加好了，只是调用点没跟着改 —— 单测只测映射函数就永远是绿的。
 * 所以本检查不看映射表本身，而是**扫调用点**：凡是以字面数字调 SerializeError 的地方，
 * 都必须等于映射表里该域码的值（没有映射的域码按默认值 -32000 算）。
 *
 * Usage: node tools/check-error-codes.js [--json]
 * Exit: 0 pass, 1 violation
 */
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const SRC = path.join(ROOT, 'plugin', 'AcBridge-v24', 'src');
const PROTO = path.join(SRC, 'JsonRpcProtocol.cs');
const AS_JSON = process.argv.includes('--json');

const errors = [];
const notes = [];

// ---------- 1. parse the mapping table ----------
const proto = fs.readFileSync(PROTO, 'utf8');
const switchBody = proto.slice(proto.indexOf('NumericErrorCode(string domainCode)'));
const map = new Map();
let fallback = null;
for (const m of switchBody.matchAll(/^\s*(.+?)\s*=>\s*(-?\d+)\s*,/gm)) {
  const pattern = m[1].trim();
  const value = Number(m[2]);
  if (pattern === '_') { fallback = value; continue; }
  for (const code of pattern.split(/\s+or\s+/)) {
    const cleaned = code.trim().replace(/^"|"$/g, '');
    if (/^CIVIL3D\./.test(cleaned)) map.set(cleaned, value);
  }
}
if (!map.size) errors.push('cannot parse NumericErrorCode table (no CIVIL3D.* arms found)');
if (fallback === null) errors.push('NumericErrorCode has no default arm');
notes.push('mapping entries: ' + map.size + ', fallback ' + fallback);

// ---------- 2. scan call sites ----------
const files = fs.readdirSync(SRC).filter((f) => f.endsWith('.cs') && f !== 'JsonRpcProtocol.cs');
let sites = 0;
for (const f of files) {
  const lines = fs.readFileSync(path.join(SRC, f), 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    // SerializeError(id, <literal>, "CODE", ...)
    const m = /SerializeError\(\s*[^,]+,\s*(-?\d+)\s*,\s*"([A-Z0-9_.]+)"/.exec(line);
    if (!m) return;
    sites++;
    const numeric = Number(m[1]);
    const code = m[2];
    const expected = map.has(code) ? map.get(code) : fallback;
    if (numeric !== expected) {
      errors.push(
        f + ':' + (i + 1) + ': ' + code + ' is sent as ' + numeric +
        ' but the table says ' + expected + '  (hardcoded numbers drift; call NumericErrorCode)'
      );
    }
    // -32603 is reserved for a genuine internal error
    if (numeric === -32603 && code !== 'CIVIL3D.INTERNAL_ERROR') {
      errors.push(f + ':' + (i + 1) + ': -32603 is the JSON-RPC internal-error code but is used for ' + code);
    }
  });
}
if (!sites) errors.push('no SerializeError(..., <numeric>, "<domain code>") call sites found -- parser may be stale');
notes.push('call sites checked: ' + sites);

if (AS_JSON) {
  console.log(JSON.stringify({ ok: errors.length === 0, errors, notes }, null, 2));
} else {
  for (const n of notes) console.log('  ok: ' + n);
  for (const e of errors) console.log('  FAIL: ' + e);
  console.log(errors.length === 0
    ? '  error codes consistent (' + sites + ' call site(s))'
    : '  error codes INCONSISTENT (' + errors.length + ' problem(s))');
}
process.exit(errors.length === 0 ? 0 : 1);
