'use strict';
// 2026-11-13: 面板工作记忆连贯性 —— 离线单测（不启动 relay、不调 LLM、不开 CAD）
// 覆盖：
//   1) llm-agent.summarizeCalls  工具调用摘要（续接用）
//   2) llm-agent.renderHistory   摘要渲染成 API 合法消息（只含 role/content）
//   3) panel-relay.slimHistoryForDisk  落盘瘦身保留 toolSummary
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const SERVER = __dirname;
let pass = 0, fail = 0;
function t(name, fn) {
  try { fn(); console.log('PASS ' + name); pass++; }
  catch (e) { console.log('FAIL ' + name + ' :: ' + e.message); fail++; }
}

const { summarizeCalls, renderHistory } = require(path.join(SERVER, 'llm-agent.js'));

// ---- 1) summarizeCalls ----
t('summarizeCalls: empty/non-array -> empty string', () => {
  assert.strictEqual(summarizeCalls([]), '');
  assert.strictEqual(summarizeCalls(null), '');
  assert.strictEqual(summarizeCalls(undefined), '');
});

t('summarizeCalls: keeps method, key params, status, handles', () => {
  const out = summarizeCalls([
    { method: 'listMethods', params: { filter: 'surface' }, ok: true },
    { method: 'addSurfaceContours', params: { handles: ['1A2B', '3C4D'], weedingDistance: 0.5 }, ok: true, handlesOut: ['9F00', '9F01', '9F02'] },
    { method: 'createTinSurface', params: { name: 'S1' }, ok: false, errCode: 'CIVIL3D.INVALID_ARG' },
    { method: 'badOne', params: {}, blocked: true },
  ]);
  assert.ok(/listMethods/.test(out), 'method name missing');
  assert.ok(/filter=surface/.test(out), 'param missing');
  assert.ok(/9F00\|9F01\|9F02/.test(out), 'handles missing');
  assert.ok(/FAIL CIVIL3D\.INVALID_ARG/.test(out), 'fail status missing');
  assert.ok(/BLOCKED/.test(out), 'blocked status missing');
  assert.ok(/^\[上一轮实际执行记录/.test(out), 'header missing');
});

t('summarizeCalls: caps at 12 calls and says so', () => {
  const many = Array.from({ length: 20 }, (_, i) => ({ method: 'm' + i, params: {}, ok: true }));
  const out = summarizeCalls(many);
  assert.ok(/1\) m8/.test(out), 'should start from the last 12 (m8)');
  assert.ok(!/m7\b/.test(out.split('\n').slice(0, 3).join(' ')), 'should drop m7');
  assert.ok(/更早 8 次调用已省略/.test(out), 'omission note missing');
});

t('summarizeCalls: hard length cap', () => {
  const big = Array.from({ length: 12 }, (_, i) => ({ method: 'veryLongMethodNameNumber' + i, params: { a: 'x'.repeat(200), b: 'y'.repeat(200) }, ok: true }));
  const out = summarizeCalls(big, 400);
  assert.ok(out.length <= 400, 'exceeded cap: ' + out.length);
  assert.ok(/已截断/.test(out), 'truncation note missing');
});

t('summarizeCalls: long param values are shortened', () => {
  const out = summarizeCalls([{ method: 'x', params: { path: 'C:/' + 'a'.repeat(120) }, ok: true }]);
  assert.ok(/…/.test(out), 'ellipsis missing');
  assert.ok(!out.includes('a'.repeat(60)), 'param not shortened');
});

// ---- 2) renderHistory ----
t('renderHistory: appends toolSummary into content', () => {
  const hist = [{ role: 'assistant', content: 'done', toolSummary: '[exec] a → ok' }];
  const out = renderHistory(hist);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].role, 'assistant');
  assert.ok(out[0].content.startsWith('done'));
  assert.ok(out[0].content.includes('[exec] a → ok'));
});

t('renderHistory: output has ONLY role/content (API-legal)', () => {
  const out = renderHistory([{ role: 'assistant', content: 'x', toolSummary: 'y', extra: 'z' }]);
  assert.deepStrictEqual(Object.keys(out[0]).sort(), ['content', 'role']);
});

t('renderHistory: does not mutate input array', () => {
  const item = { role: 'assistant', content: 'x', toolSummary: 'y' };
  const hist = [item];
  renderHistory(hist);
  assert.strictEqual(item.content, 'x', 'input mutated');
  assert.strictEqual(hist[0].toolSummary, 'y');
});

t('renderHistory: filters non user/assistant roles', () => {
  const out = renderHistory([
    { role: 'system', content: 'sys' },
    { role: 'tool', content: 'tool out' },
    { role: 'user', content: 'hi' },
    null,
  ]);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].role, 'user');
});

t('renderHistory: tolerates non-string content', () => {
  const out = renderHistory([{ role: 'user', content: { a: 1 } }]);
  assert.strictEqual(typeof out[0].content, 'string');
});

// ---- 3) slimHistoryForDisk（从 panel-relay.js 源码提取，不启动服务）----
function extractFn(src, name) {
  const start = src.indexOf('function ' + name + '(');
  if (start < 0) throw new Error('function not found: ' + name);
  const open = src.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    const ch = src[i];
    if (ch === '{') depth++;
    else if (ch === '}') {
      depth--;
      if (depth === 0) return src.slice(start, i + 1);
    }
  }
  throw new Error('unbalanced braces for ' + name);
}

const relaySrc = fs.readFileSync(path.join(SERVER, 'panel-relay.js'), 'utf8');
const slimHistoryForDisk = new Function('getLimits', extractFn(relaySrc, 'slimHistoryForDisk') + '\nreturn slimHistoryForDisk;')(() => ({ historyKeep: 60 }));   // mock 60：本文件验证纯函数行为；默认值(24)由 test-checkpoint.js 覆盖

t('slimHistoryForDisk: keeps toolSummary', () => {
  const out = slimHistoryForDisk([{ role: 'assistant', content: 'x', toolSummary: '[exec] a → ok' }]);
  assert.strictEqual(out[0].toolSummary, '[exec] a → ok');
});

t('slimHistoryForDisk: drops non user/assistant', () => {
  const out = slimHistoryForDisk([
    { role: 'system', content: 's' },
    { role: 'tool', content: 't' },
    { role: 'user', content: 'u' },
  ]);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].role, 'user');
});

t('slimHistoryForDisk: caps content 8000 / toolSummary 1500 / 60 entries', () => {
  const out = slimHistoryForDisk([
    { role: 'user', content: 'c'.repeat(9000), toolSummary: 't'.repeat(3000) },
  ]);
  assert.strictEqual(out[0].content.length, 8000);
  assert.strictEqual(out[0].toolSummary.length, 1500);

  const many = Array.from({ length: 100 }, (_, i) => ({ role: 'user', content: 'm' + i }));
  const kept = slimHistoryForDisk(many);
  assert.strictEqual(kept.length, 60);
  assert.strictEqual(kept[59].content, 'm99');
});

t('slimHistoryForDisk: no toolSummary key when absent', () => {
  const out = slimHistoryForDisk([{ role: 'user', content: 'x' }]);
  assert.ok(!('toolSummary' in out[0]));
});

t('panel-relay: clear-blocks are guarded by backup call', () => {
  const line = relaySrc.split(/\r?\n/).find(l => l.includes('consumeNewTask()'));
  assert.ok(/backupHistoryBeforeClear\(llmScope, histNow\)/.test(line), 'backup call missing: ' + line);
});

console.log('\n=== ' + pass + ' passed / ' + fail + ' failed ===');
process.exit(fail ? 1 : 0);
