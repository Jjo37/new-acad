'use strict';
/**
 * test-block-policy.js — 失败封锁策略（2026-11-14）
 * 背景：旧规则"同方法错 2 次就整任务物理封锁"把可修的参数错也封死，
 *       实测导致 addSurfacePointsFromFile 只能用 100m 采样代替 20m（质量事故）。
 * 跑法：node server/test-block-policy.js
 */
const assert = require('assert');
const { FailTracker, classify, paramsFingerprint } = require('./block-policy');

let pass = 0;
function t(name, fn) { fn(); pass++; console.log('  ok  ' + name); }

console.log('test-block-policy');

t('错误分类：参数类 / 桥接类 / 其他', () => {
  assert.strictEqual(classify('-32602'), 'param');
  assert.strictEqual(classify('CIVIL3D.INVALID_INPUT'), 'param');
  assert.strictEqual(classify('BRIDGE_EXCEPTION'), 'transient');
  assert.strictEqual(classify('-32000'), 'transient');
  assert.strictEqual(classify('CIVIL3D.OBJECT_NOT_FOUND'), 'other');
  assert.strictEqual(classify(''), 'other');
});

t('参数错永远不封锁（这是本次事故的根因）', () => {
  const tr = new FailTracker();
  for (let i = 0; i < 6; i++) {
    const v = tr.note({ method: 'addSurfacePointsFromFile', errCode: 'CIVIL3D.INVALID_INPUT', params: { zCol: 3 + i } });
    assert.strictEqual(v.block, false, '第 ' + (i + 1) + ' 次参数错不应封锁');
  }
  assert.strictEqual(tr.isBlocked('addSurfacePointsFromFile'), false);
});

t('参数错重复 3 次以上给"停止盲猜"提示', () => {
  const tr = new FailTracker();
  tr.note({ method: 'm', errCode: '-32602', params: { a: 1 } });
  tr.note({ method: 'm', errCode: '-32602', params: { a: 2 } });
  const v = tr.note({ method: 'm', errCode: '-32602', params: { a: 3 } });
  assert.ok(/停止盲猜/.test(v.note), '实际：' + v.note);
  assert.strictEqual(v.block, false);
});

t('同一异常 + 同一参数重复 2 次 → 封锁（真打转）', () => {
  const tr = new FailTracker();
  const p = { name: 'S', filePath: 'x.csv' };
  const v1 = tr.note({ method: 'createSurface', errCode: 'CIVIL3D.API_ERROR', params: p });
  assert.strictEqual(v1.block, false);
  const v2 = tr.note({ method: 'createSurface', errCode: 'CIVIL3D.API_ERROR', params: p });
  assert.strictEqual(v2.block, true);
  assert.ok(tr.isBlocked('createSurface'));
  assert.ok(/已被封锁/.test(tr.blockedMessage('createSurface')));
});

t('同码换参数仍不稳：4 次才封锁', () => {
  const tr = new FailTracker();
  const codes = ['CIVIL3D.API_ERROR'];
  for (let i = 0; i < 3; i++) {
    const v = tr.note({ method: 'x', errCode: codes[0], params: { i } });
    assert.strictEqual(v.block, false, '第 ' + (i + 1) + ' 次不应封锁');
  }
  const v4 = tr.note({ method: 'x', errCode: codes[0], params: { i: 3 } });
  assert.strictEqual(v4.block, true);
});

t('桥接/超时类：首次不封锁（可重试一次），同参重复 2 次才封', () => {
  const tr = new FailTracker();
  const v1 = tr.note({ method: 'getDrawingInfo', errCode: 'BRIDGE_EXCEPTION', params: {} });
  const v2 = tr.note({ method: 'getDrawingInfo', errCode: 'BRIDGE_EXCEPTION', params: {} });
  assert.strictEqual(v1.block, false);
  assert.strictEqual(v2.block, true, '同异常同参重复 2 次仍应封锁（避免无限重试）');
  assert.ok(/不封锁/.test(v1.note), '首次应明确告知可重试，实际：' + v1.note);
});

t('reset 可解封（给 AI/用户一条出路）', () => {
  const tr = new FailTracker();
  tr.note({ method: 'm', errCode: 'CIVIL3D.API_ERROR', params: { a: 1 } });
  tr.note({ method: 'm', errCode: 'CIVIL3D.API_ERROR', params: { a: 1 } });
  assert.ok(tr.isBlocked('m'));
  tr.reset('m');
  assert.strictEqual(tr.isBlocked('m'), false);
});

t('参数指纹：键序无关、超长值截断', () => {
  assert.strictEqual(paramsFingerprint({ a: 1, b: 2 }), paramsFingerprint({ b: 2, a: 1 }));
  assert.notStrictEqual(paramsFingerprint({ a: 1 }), paramsFingerprint({ a: 2 }));
  assert.ok(paramsFingerprint({ big: 'x'.repeat(500) }).length <= 400);
  assert.strictEqual(paramsFingerprint(null), '');
});

t('不同方法互不牵连', () => {
  const tr = new FailTracker();
  tr.note({ method: 'a', errCode: 'CIVIL3D.API_ERROR', params: { i: 1 } });
  tr.note({ method: 'a', errCode: 'CIVIL3D.API_ERROR', params: { i: 1 } });
  assert.ok(tr.isBlocked('a'));
  assert.strictEqual(tr.isBlocked('b'), false);
});

console.log('\n' + pass + '/' + pass + ' 通过');
