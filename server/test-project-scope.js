'use strict';
/**
 * test-project-scope.js — 项目目录命名单一真相（2026-11-14）
 * 事故：workflow-store 把 100 字符项目名 slice(0,64) 成 "...TO LOLO"，
 *       工作流记录落到幽灵目录，与 agent-work/dwg 分离，断点续接会找错地方。
 * 跑法：node server/test-project-scope.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { sanitizeProjectName, MAX_DIR_NAME } = require('./project-scope');

let pass = 0;
function t(name, fn) { fn(); pass++; console.log('  ok  ' + name); }

const REAL = 'ROAD REHABILITATION OF MUNICIPIO ROADS JCT A-16 BOBONARO TO LOLOTOE TOWN STA. 0+000 \u2013 8+900 (8.9 KM)';

console.log('test-project-scope');

t('真实项目名（100 字符）原样保留，不再截断', () => {
  const out = sanitizeProjectName(REAL);
  assert.strictEqual(out, REAL, '应保持原样，实际：' + out);
  assert.strictEqual(out.length, 100);
});

t('超长名截断后带哈希：长度受控且不同名字不撞目录', () => {
  const a = sanitizeProjectName('X'.repeat(300));
  const b = sanitizeProjectName('X'.repeat(300) + 'Y');
  assert.ok(a.length <= MAX_DIR_NAME, '长度应 <= ' + MAX_DIR_NAME + '，实际 ' + a.length);
  assert.notStrictEqual(a, b, '不同超长名必须得到不同目录名');
  assert.ok(/~[0-9a-z]{7}$/.test(a), '截断必须带哈希后缀，实际：' + a.slice(-12));
});

t('非法字符替换、结尾点/空格去掉、空名回落', () => {
  assert.strictEqual(sanitizeProjectName('a/b:c*d?e"f<g>h|i'), 'a_b_c_d_e_f_g_h_i');
  assert.strictEqual(sanitizeProjectName('abc.  '), 'abc');
  assert.strictEqual(sanitizeProjectName('   '), 'unnamed');
  assert.strictEqual(sanitizeProjectName(null), 'unnamed');
});

t('保留名字里的点（"STA. 0+000" 是名字的一部分）', () => {
  assert.ok(sanitizeProjectName('STA. 0+000 – 8+900').includes('STA. 0+000'));
});

t('三处入口都走同一个实现（防回退）', () => {
  const root = path.join(__dirname);
  const files = ['workflow-store.js', 'panel-relay.js', 'file-tools.js'];
  for (const f of files) {
    const src = fs.readFileSync(path.join(root, f), 'utf8');
    assert.ok(/require\(['"]\.\/project-scope['"]\)/.test(src), f + ' 应 require ./project-scope');
    // 只查代码行（跳过注释：注释里会引用旧的 slice(0,64) 作为历史说明）
    const code = src.split(/\r?\n/).filter((l) => !/^\s*(\/\/|\*|\/\*)/.test(l)).join('\n');
    assert.ok(!/\.slice\(0,\s*64\)/.test(code), f + ' 不应再出现私有截断 slice(0,64)');
  }
});

console.log('\n' + pass + '/' + pass + ' 通过');
