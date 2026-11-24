'use strict';
// 2026-11-13 L1+L2 单测：工作区权限提升（写边界/保护文件/新工具/大文件查询）
// 只在项目工作区里造临时目录，跑完清理。
const fs = require('fs');
const path = require('path');
const assert = require('assert');
const ft = require('D:/new-acad/server/file-tools.js');

let pass = 0, fail = 0;
function t(name, fn) {
  try { fn(); console.log('PASS ' + name); pass++; }
  catch (e) { console.log('FAIL ' + name + ' :: ' + e.message); fail++; }
}

const PROJ = ft.getProjectDir();
const WORK = ft.getAgentWorkDir();
const TMP = path.join(PROJ, '__test_perms__');
const OUTSIDE = path.join(path.dirname(PROJ), '__outside_probe__.txt');

function cleanup() {
  try { fs.rmSync(TMP, { recursive: true, force: true }); } catch (_) {}
  try { fs.unlinkSync(OUTSIDE); } catch (_) {}
}

// ---- 边界基础 ----
t('project dir is parent of agent-work', () => {
  assert.strictEqual(path.dirname(WORK), PROJ);
});

t('write inside project root (not just agent-work) is allowed', () => {
  const f = path.join(TMP, 'a.txt');
  const r = ft.writeFile({ path: f, content: 'hello' });
  assert.strictEqual(r.created, true);
  assert.ok(fs.existsSync(f));
});

t('write outside project is rejected', () => {
  assert.throws(() => ft.writeFile({ path: OUTSIDE, content: 'x' }), /安全红线|只允许在项目文件夹/);
});

t('runtime state files are protected (plan/checkpoint/history)', () => {
  for (const n of ['plan.json', 'checkpoint.json', 'llm-history.json', 'history-before-clear.json', 'panel-chat-2026-11-13.jsonl']) {
    assert.throws(() => ft.writeFile({ path: path.join(PROJ, n), content: 'x' }), /运行时状态文件|只能读不能改/, n + ' should be protected');
  }
});

t('delete is still project-scoped', () => {
  assert.throws(() => ft.deleteFile({ path: path.join(PROJ, 'plan.json') }), /运行时状态文件|安全红线/);
});

// ---- 新工具 ----
t('mkdir creates nested dirs inside project', () => {
  const d = path.join(TMP, 'x', 'y');
  const r = ft.mkdir({ path: d });
  assert.strictEqual(r.created, true);
  assert.ok(fs.statSync(d).isDirectory());
});

t('mkdir outside project rejected', () => {
  assert.throws(() => ft.mkdir({ path: path.join(path.dirname(PROJ), '__x__') }), /安全红线|只允许在项目文件夹/);
});

t('copyFile: any readable source -> project target', () => {
  const src = path.join(TMP, 'src.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, 'ABC', 'utf8');
  const dst = path.join(TMP, 'copy.txt');
  const r = ft.copyFile({ source: src, target: dst });
  assert.strictEqual(r.bytes, 3);
  assert.strictEqual(fs.readFileSync(dst, 'utf8'), 'ABC');
  assert.ok(fs.existsSync(src), 'copy must keep source');
});

t('copyFile: blocked extension rejected', () => {
  const src = path.join(TMP, 'a.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, 'x', 'utf8');
  assert.throws(() => ft.copyFile({ source: src, target: path.join(TMP, 'evil.ps1') }), /不允许写入/);
});

t('moveFile renames inside project', () => {
  const a = path.join(TMP, 'm1.txt');
  const b = path.join(TMP, 'm2.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(a, 'MOVE', 'utf8');
  ft.moveFile({ source: a, target: b });
  assert.ok(!fs.existsSync(a), 'source should be gone');
  assert.strictEqual(fs.readFileSync(b, 'utf8'), 'MOVE');
});

t('moveFile refuses to move from outside project', () => {
  assert.throws(() => ft.moveFile({ source: OUTSIDE, target: path.join(TMP, 'z.txt') }), /安全红线|只允许在项目文件夹/);
});

// ---- convertPoints ----
t('convertPoints PNEZD -> csv (point/order/desc)', () => {
  const src = path.join(TMP, 'pts.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, ['1,9000001.5,200001.5,1161.077,EP', '2,9000002.5,200002.5,1160.5,IP'].join('\n'), 'utf8');
  const out = path.join(TMP, 'pts.csv');
  const r = ft.convertPoints({ source: src, format: 'PNEZD', output: out });
  assert.strictEqual(r.written, 2);
  const lines = fs.readFileSync(out, 'utf8').split('\n');
  assert.strictEqual(lines[0], 'point,northing,easting,elevation,description');
  assert.ok(lines[1].startsWith('1,9000001.5,200001.5,1161.077,EP'), lines[1]);
});

t('convertPoints PENZ swaps order correctly', () => {
  const src = path.join(TMP, 'penz.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, '7,500.1,600.2,12.5\n', 'utf8');
  const out = path.join(TMP, 'penz.csv');
  ft.convertPoints({ source: src, format: 'PENZ', output: out });
  const row = fs.readFileSync(out, 'utf8').split('\n')[1].split(',');
  assert.strictEqual(row[1], '600.2', 'northing should be 2nd field of PENZ');
  assert.strictEqual(row[2], '500.1');
});

t('convertPoints swapXY + unitScale applied', () => {
  const src = path.join(TMP, 'ft.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, '100,200,10\n', 'utf8');   // NEZ = northing,easting,elevation（3 列）
  const out = path.join(TMP, 'ft.csv');
  ft.convertPoints({ source: src, format: 'NEZ', output: out, swapXY: true, unitScale: 0.3048 });
  const row = fs.readFileSync(out, 'utf8').split('\n')[1].split(',');
  assert.ok(Math.abs(Number(row[1]) - 200 * 0.3048) < 1e-6, 'northing=' + row[1]);
  assert.ok(Math.abs(Number(row[2]) - 100 * 0.3048) < 1e-6, 'easting=' + row[2]);
});

t('convertPoints skips header + malformed rows', () => {
  const src = path.join(TMP, 'hdr.txt');
  fs.mkdirSync(TMP, { recursive: true });
  fs.writeFileSync(src, 'P,N,E,Z,D\n1,10,20,30,a\nbroken\n2,11,21,31,b\n', 'utf8');
  const out = path.join(TMP, 'hdr.csv');
  const r = ft.convertPoints({ source: src, format: 'PNEZD', output: out });
  assert.strictEqual(r.written, 2);
  assert.strictEqual(r.skipped, 1);
});

t('convertPoints rejects unknown format', () => {
  assert.throws(() => ft.convertPoints({ source: path.join(TMP, 'x.txt'), format: 'NOPE' }), /不支持的 format/);
});

// ---- 大文件查询（今天真机卡死的场景）----
t('queryFile reads a 7.7MB CSV (over readMaxBytes, under queryMaxBytes)', () => {
  const f = path.join(TMP, 'big.csv');
  fs.mkdirSync(TMP, { recursive: true });
  const rows = ['station,elevation,northing,easting'];
  for (let i = 0; i < 200000; i++) rows.push((i * 0.0445).toFixed(3) + ',' + (1161 - i * 0.0017).toFixed(3) + ',' + (9000000 + i).toFixed(3) + ',' + (200000 + i).toFixed(3));
  fs.writeFileSync(f, rows.join('\n'), 'utf8');
  const mb = fs.statSync(f).size / 1024 / 1024;
  assert.ok(mb > 5, 'fixture should exceed readMaxBytes, got ' + mb.toFixed(1) + 'MB');
  const wc = ft.queryFile({ path: f, op: 'wc' });
  assert.strictEqual(wc.lines, rows.length);
  const st = ft.queryFile({ path: f, op: 'stats', column: 'elevation' });
  assert.ok(JSON.stringify(st).length > 0);
});

t('readFile still respects readMaxBytes on the same big file', () => {
  const f = path.join(TMP, 'big.csv');
  assert.ok(fs.existsSync(f), 'depends on previous fixture');
  assert.throws(() => ft.readFile({ path: f }), /文件过大/);
});

cleanup();
console.log('\n=== ' + pass + ' passed / ' + fail + ' failed ===');
process.exit(fail ? 1 : 0);
