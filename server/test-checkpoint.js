'use strict';
// 2026-11-13: 检查点落盘 + historyKeep 对齐 —— 离线单测（不启动 relay / 不开 CAD / 不调 LLM）
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const SERVER = __dirname;
let pass = 0, fail = 0;
function t(name, fn) {
  try { fn(); console.log('PASS ' + name); pass++; }
  catch (e) { console.log('FAIL ' + name + ' :: ' + e.message); fail++; }
}

const A = require(path.join(SERVER, 'llm-agent.js'));
const { checkpointFileFor, loadCheckpoint, saveCheckpoint, clearCheckpoint, persistCompactionCheckpoint } = A;

const PROJ = '__test_cp_' + process.pid;
const HOUR = 3600 * 1000;

// ---- 1) 路径推导 ----
t('checkpointFileFor: sanitizes illegal chars, under projects/', () => {
  const f = checkpointFileFor('a/b:c*d?e"f<g>h|i');
  assert.ok(/checkpoint\.json$/.test(f), 'filename');
  assert.ok(/[\\/]projects[\\/]/.test(f), 'should live under projects/');
  const base = path.basename(path.dirname(f));
  assert.ok(!/[\\/:*?"<>|]/.test(base), 'illegal chars left in dir name: ' + base);
});

t('checkpointFileFor: empty project -> global file', () => {
  const f = checkpointFileFor('');
  assert.ok(!/[\\/]projects[\\/]/.test(f), 'should not be under projects/: ' + f);
  assert.ok(/checkpoint\.json$/.test(f));
});

// ---- 2) 往返 ----
t('save -> load roundtrip keeps summary + meta', () => {
  const ok = saveCheckpoint(PROJ, '【上下文检查点】\n## 任务目标\n- 测试目标', { goal: 'g1', turns: 7, bytes: 1234 });
  assert.strictEqual(ok, true);
  const j = loadCheckpoint(PROJ);
  assert.ok(j, 'should load');
  assert.ok(/测试目标/.test(j.summary), 'summary text');
  assert.strictEqual(j.goal, 'g1');
  assert.strictEqual(j.turns, 7);
  assert.ok(Number.isFinite(Date.parse(j.at)), 'at is ISO');
});

t('load: expired (>24h) returns null', () => {
  const j = loadCheckpoint(PROJ);
  assert.ok(j, 'precondition');
  const future = Date.parse(j.at) + 25 * HOUR;
  assert.strictEqual(loadCheckpoint(PROJ, future), null, 'should expire');
  const near = Date.parse(j.at) + 23 * HOUR;
  assert.ok(loadCheckpoint(PROJ, near), 'should still be valid at 23h');
});

t('load: corrupt file returns null (no throw)', () => {
  const f = checkpointFileFor(PROJ);
  fs.writeFileSync(f, '{not json', 'utf8');
  assert.strictEqual(loadCheckpoint(PROJ), null);
});

t('load: missing summary returns null', () => {
  const f = checkpointFileFor(PROJ);
  fs.writeFileSync(f, JSON.stringify({ at: new Date().toISOString() }), 'utf8');
  assert.strictEqual(loadCheckpoint(PROJ), null);
});

// ---- 3) 清理 ----
t('clear removes file; load -> null', () => {
  saveCheckpoint(PROJ, 'x');
  assert.ok(fs.existsSync(checkpointFileFor(PROJ)));
  clearCheckpoint(PROJ);
  assert.strictEqual(fs.existsSync(checkpointFileFor(PROJ)), false);
  assert.strictEqual(loadCheckpoint(PROJ), null);
});

t('clear on non-existent path is safe', () => {
  assert.doesNotThrow(() => clearCheckpoint('__never_exists_' + Date.now()));
});

// ---- 4) 压缩后落盘钩子 ----
t('persistCompactionCheckpoint: writes when checkpoint message present', () => {
  clearCheckpoint(PROJ);
  const newMsgs = [
    { role: 'system', content: 'sys' },
    { role: 'user', content: '【上下文检查点】以下为早期对话的自动摘要\n## 关键上下文\n- 保留这条' },
    { role: 'user', content: 'tail' },
  ];
  const ok = persistCompactionCheckpoint(PROJ, newMsgs, 9, 999, { goal: '目标X' });
  assert.strictEqual(ok, true);
  const j = loadCheckpoint(PROJ);
  assert.ok(/保留这条/.test(j.summary));
  assert.strictEqual(j.turns, 9);
  assert.strictEqual(j.goal, '目标X');
});

t('persistCompactionCheckpoint: ignores non-checkpoint arrays', () => {
  clearCheckpoint(PROJ);
  const notCp = [{ role: 'system', content: 's' }, { role: 'user', content: '普通历史' }];
  assert.strictEqual(persistCompactionCheckpoint(PROJ, notCp, 1, 1, null), false);
  assert.strictEqual(fs.existsSync(checkpointFileFor(PROJ)), false, 'should not write');
  assert.strictEqual(persistCompactionCheckpoint(PROJ, null, 1, 1, null), false);
  assert.strictEqual(persistCompactionCheckpoint(PROJ, [{ role: 'system' }], 1, 1, null), false);
});

// ---- 5) historyKeep 对齐（从 panel-relay 源码提取纯函数）----
function extractFn(src, name) {
  const start = src.indexOf('function ' + name + '(');
  if (start < 0) throw new Error('not found: ' + name);
  const open = src.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}') { depth--; if (depth === 0) return src.slice(start, i + 1); }
  }
  throw new Error('unbalanced: ' + name);
}
const relaySrc = fs.readFileSync(path.join(SERVER, 'panel-relay.js'), 'utf8');
const slim = new Function('getLimits', extractFn(relaySrc, 'slimHistoryForDisk') + '\nreturn slimHistoryForDisk;')(() => ({ historyKeep: 24 }));

t('slimHistoryForDisk: honours keep param (== memory window)', () => {
  const arr = Array.from({ length: 100 }, (_, i) => ({ role: 'user', content: 'm' + i }));
  assert.strictEqual(slim(arr, 5).length, 5);
  assert.strictEqual(slim(arr, 5)[4].content, 'm99');
  assert.strictEqual(slim(arr, 24).length, 24);
});

t('slimHistoryForDisk: falls back to getLimits().historyKeep, not hardcoded 60', () => {
  const arr = Array.from({ length: 100 }, (_, i) => ({ role: 'user', content: 'm' + i }));
  assert.strictEqual(slim(arr).length, 24, 'default should come from limits (24), got ' + slim(arr).length);
  assert.strictEqual(slim(arr, 0).length, 24, '0 is invalid -> fallback');
  assert.strictEqual(slim(arr, -3).length, 24, 'negative -> fallback');
});

t('panel-relay: saveHistory passes configured keep', () => {
  const line = relaySrc.split(/\r?\n/).find(l => l.includes('slimHistoryForDisk(llmHistories.get(key)'));
  assert.ok(/getLimits\(\)\.historyKeep/.test(line), 'keep not from config: ' + line);
  assert.ok(!/slice\(-60\)/.test(extractFn(relaySrc, 'slimHistoryForDisk')), 'hardcoded 60 still present');
});

// 静态接线断言：改了函数但没接上调用点 = 白做
t('wiring: sysPrompt injects cpSection', () => {
  const agentSrc = fs.readFileSync(path.join(SERVER, 'llm-agent.js'), 'utf8');
  assert.ok(/planToString\(activePlan\) : ''\) \+ cpSection \+ \(planNote \|\| ''\)/.test(agentSrc), 'cpSection not injected into sysPrompt');
  assert.ok(/const lastCheckpoint = loadCheckpoint\(projectName\)/.test(agentSrc), 'checkpoint not loaded in handleMessage');
  assert.ok(/const cpSection = lastCheckpoint/.test(agentSrc), 'cpSection not built');
});

t('wiring: both compaction sites persist the checkpoint', () => {
  const agentSrc = fs.readFileSync(path.join(SERVER, 'llm-agent.js'), 'utf8');
  const hits = (agentSrc.match(/persistCompactionCheckpoint\(projectName, newMsgs/g) || []).length;
  assert.strictEqual(hits, 2, 'expected 2 call sites (55% trigger + ceiling rescue), got ' + hits);
});

t('wiring: task-boundary clears checkpoint', () => {
  const agentSrc = fs.readFileSync(path.join(SERVER, 'llm-agent.js'), 'utf8');
  assert.ok(/clearActivePlan\(project\)[\s\S]{0,220}clearCheckpoint\(project\)/.test(agentSrc), 'clearActivePlan does not clear checkpoint');
  assert.ok(/pendingNewTask = true; clearCheckpoint\(projectName\);/.test(agentSrc), 'new-task boundary does not clear checkpoint');
});

// 清理测试项目目录
try {
  const dir = path.dirname(checkpointFileFor(PROJ));
  if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
} catch (_) { /* ignore */ }

console.log('\n=== ' + pass + ' passed / ' + fail + ' failed ===');
process.exit(fail ? 1 : 0);
