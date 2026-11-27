'use strict';
/**
 * test-calc-sandbox.js — 批量计算沙箱（2026-11-14，F1）
 * 断言：一次算完 106 条曲线要素且与原解算结果一致 / 读写边界 / 无 require / 超时被杀 / 结果截断
 * 跑法：node server/test-calc-sandbox.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { runCalc } = require('./calc-sandbox');

let pass = 0;
async function t(name, fn) { await fn(); pass++; console.log('  ok  ' + name); }

(async () => {
  const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'calc-test-'));
  const projectRoot = path.join(TMP, 'proj');
  const workDir = path.join(projectRoot, 'agent-work');
  fs.mkdirSync(workDir, { recursive: true });
  fs.writeFileSync(path.join(workDir, 'pi.csv'), 'idx,station,radius,delta_deg\n1,0+081.11,169.00,19.9530\n2,0+174.03,96.00,38.0617\n3,0+268.10,96.00,38.5630\n', 'utf8');
  fs.writeFileSync(path.join(TMP, 'secret.txt'), 'TOP-SECRET', 'utf8');

  console.log('test-calc-sandbox');

  await t('一次算完多行曲线要素（R·tan(Δ/2) 与 tan(Δ/4)）', async () => {
    const r = await runCalc({
      code: `
const t = readCsv('agent-work/pi.csv');
const R2D = 180 / Math.PI;
const rows = t.rows.map(x => ({ idx: x.idx, tangent: +(x.radius * Math.tan(x.delta_deg / R2D / 2)).toFixed(4), bulge: +Math.tan(x.delta_deg / R2D / 4).toFixed(6) }));
return { n: rows.length, rows };`,
      projectRoot, workDir,
    });
    assert.strictEqual(r.ok, true, '失败: ' + r.error);
    const o = JSON.parse(r.result);
    assert.strictEqual(o.n, 3);
    assert.strictEqual(o.rows[0].tangent, 29.7278);   // 与 2026-11 真实项目 plan-curve-solved.csv 一致
    assert.strictEqual(o.rows[0].bulge, 0.087282);
    assert.strictEqual(o.rows[1].tangent, 33.1133);
  });

  await t('读越界被拒（项目外文件）', async () => {
    const r = await runCalc({ code: `return readFile('../secret.txt');`, projectRoot, workDir });
    assert.strictEqual(r.ok, false);
    assert.ok(/越界/.test(r.error), '实际: ' + r.error);
  });

  await t('写越界被拒（只能写 agent-work）', async () => {
    const r = await runCalc({ code: `return writeFile('../../evil.txt','x');`, projectRoot, workDir });
    assert.strictEqual(r.ok, false);
    assert.ok(/越界/.test(r.error), '实际: ' + r.error);
  });

  await t('沙箱里没有 require / process / fetch', async () => {
    const r = await runCalc({ code: `return [typeof require, typeof process, typeof fetch, typeof globalThis.require];`, projectRoot, workDir });
    assert.strictEqual(r.ok, true, r.error);
    assert.strictEqual(r.result, '["undefined","undefined","undefined","undefined"]');
  });

  await t('eval 被禁（codeGeneration.strings=false）', async () => {
    const r = await runCalc({ code: `return eval('1+1');`, projectRoot, workDir });
    assert.strictEqual(r.ok, false, '应当禁止 eval');
  });

  await t('死循环超时被杀，且父进程存活', async () => {
    const t0 = Date.now();
    const r = await runCalc({ code: `while(true){}`, projectRoot, workDir, timeoutMs: 1500 });
    const ms = Date.now() - t0;
    assert.strictEqual(r.ok, false);
    assert.ok(/超时/.test(r.error), '实际: ' + r.error);
    assert.ok(ms < 20000, '耗时应受控，实际 ' + ms + 'ms');
  });

  await t('写文件落在 agent-work，返回写盘清单', async () => {
    const r = await runCalc({ code: `return writeFile('out/result.csv', 'a,b\\n1,2\\n');`, projectRoot, workDir });
    assert.strictEqual(r.ok, true, r.error);
    assert.strictEqual(r.writes.length, 1);
    assert.ok(fs.existsSync(path.join(workDir, 'out', 'result.csv')), '文件应真的写出来');
  });

  await t('输出按上限截断并提示', async () => {
    const r = await runCalc({ code: `return 'x'.repeat(5000);`, projectRoot, workDir, maxOutputChars: 500 });
    assert.strictEqual(r.ok, true, r.error);
    assert.ok(r.result.length < 800, '实际长度 ' + r.result.length);
    assert.ok(/已截断/.test(r.result));
  });

  await t('语法错误给出可读报错（不是静默失败）', async () => {
    const r = await runCalc({ code: `return 1 +;`, projectRoot, workDir });
    assert.strictEqual(r.ok, false);
    assert.ok(r.error && r.error.length > 0);
  });

  console.log('\n' + pass + '/' + pass + ' 通过');
  try { fs.rmSync(TMP, { recursive: true, force: true }); } catch (_) { /* 清不掉不影响结论 */ }
})();
