'use strict';
/**
 * test-method-search.js — listMethods 检索行为（2026-11-14）
 * 覆盖：中文描述搜索 / 名称搜索 / 多词 AND / 分页 / 0 命中给候选 / 缓存不被子集污染
 * 跑法：node server/test-method-search.js
 */
const assert = require('assert');
const { formatResult, setMethodsCache, isMethodsCacheFresh } = require('./cad-tools');

let pass = 0;
function t(name, fn) { fn(); pass++; console.log('  ok  ' + name); }

const METHODS = [
  'setSuperelevation', 'getSuperelevation', 'checkSuperelevationDesign',
  'addSurfacePoints', 'addSurfaceContours', 'getSurface', 'listSurfaces',
  'createAssembly', 'createSubassembly', 'addCorridorRegion',
];
const DESCRIPTIONS = {
  'setSuperelevation': '设置超高 {alignmentName, stations:[], slopes:[]}——按桩号施加超高（弯道横坡过渡）',
  'getSuperelevation': '读取超高 {alignmentName}',
  'addSurfacePoints': '向曲面添加高程点 {name, points:[{x,y,z}]}',
  'createAssembly': '创建装配 {name, insertX, insertY, assemblyType}',
  'createSubassembly': '创建部件（横断面组成单元）{assemblyName, subassemblyType}',
};
const resp = { result: { count: METHODS.length, methods: METHODS, descriptions: DESCRIPTIONS } };
const call = (params) => formatResult(resp, 'listMethods', params);

console.log('test-method-search');

t('中文描述能被搜到（"超高" → setSuperelevation/getSuperelevation）', () => {
  const out = call({ filter: '超高' });
  assert.ok(out.includes('setSuperelevation'), '应命中 setSuperelevation，实际：' + out.slice(0, 200));
  assert.ok(out.includes('getSuperelevation'));
  // 该词只存在于描述里：checkSuperelevationDesign 名称含 superelev 但描述缺失，故不应命中
  assert.ok(out.includes('找到 2 个匹配'), '中文命中数应为 2，实际：' + out.slice(0, 80));
});

t('英文名称搜索照旧可用', () => {
  const out = call({ filter: 'superelev' });
  assert.ok(out.includes('setSuperelevation'));
});

t('多关键词是 AND（"surface 高程点" 只命中 addSurfacePoints）', () => {
  const out = call({ filter: 'surface 高程点' });
  assert.ok(out.includes('addSurfacePoints'));
  assert.ok(!out.includes('addSurfaceContours'), '不应命中不含"高程点"的 addSurfaceContours');
});

t('0 命中时给最接近的候选并明确禁止猜方法名', () => {
  const out = call({ filter: 'superelevaton' });   // 拼错
  assert.ok(/最接近/.test(out), '应给候选，实际：' + out.slice(0, 200));
  assert.ok(out.includes('MISSING'));
});

t('分页：limit 生效且给出下一段 offset', () => {
  const many = [];
  const d = {};
  for (let i = 0; i < 40; i++) { many.push('testMethod' + String(i).padStart(2, '0')); d[many[i]] = '测试方法 ' + i; }
  const r2 = { result: { count: many.length, methods: many, descriptions: d } };
  const p1 = formatResult(r2, 'listMethods', { filter: 'testMethod', limit: 10 });
  assert.strictEqual((p1.match(/testMethod\d\d/g) || []).length, 10, '首页应为 10 条');
  assert.ok(p1.includes('offset=10'), '首页应提示 offset=10，实际：' + p1.slice(0, 200));
  const p2 = formatResult(r2, 'listMethods', { filter: 'testMethod', limit: 10, offset: 30 });
  assert.strictEqual((p2.match(/testMethod\d\d/g) || []).length, 10, '尾页应为 10 条');
  assert.ok(!p2.includes('offset=40'), '已到末页不应再提示 offset');
});

t('不带 filter 时仍给分组摘要 + 总数', () => {
  const out = call({});
  assert.ok(out.includes('方法总数: ' + METHODS.length), '实际：' + out.slice(0, 120));
  assert.ok(out.includes('按功能分组'));
});

t('带 filter 的调用不会把方法缓存写成子集', () => {
  setMethodsCache(METHODS);
  assert.ok(isMethodsCacheFresh(), '缓存应先可用');
  call({ filter: '超高' });
  assert.ok(isMethodsCacheFresh(), '过滤调用后缓存仍应可用');
});

console.log('\n' + pass + '/' + pass + ' 通过');
