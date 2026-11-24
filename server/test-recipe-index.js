'use strict';
// test-recipe-index.js — v1.18 findRecipes 判据（不需要 CAD）
//
// 判据：
//   1) 中文意图能命中对应内置配方（地块面积 / 管网水力 / 压力管网 / 大导出 / 地形点 / 廊道 / 曲面）
//   2) 命中结果必须带 steps（真实方法名）和 notes（坑）
//   3) 英文/混合关键词同样能命中
//   4) 无关查询不瞎给（hits 为空或分数极低）
//   5) 项目里有录制工作流时，recorded 结果排在前面且带 reuse 信息
//   6) limit 生效、返回结构稳定
const fs = require('fs');
const os = require('os');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const idx = require(path.join(ROOT, 'server', 'recipe-index.js'));

let pass = 0;
let fail = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log('PASS ' + name + (detail ? '  [' + detail + ']' : '')); }
  else { fail++; console.log('FAIL ' + name + '  [' + (detail || '') + ']'); }
};

const topOf = (q) => (idx.search(null, q).hits[0] || {});

const cases = [
  ['统计地块面积并出报告', 'builtin:parcel-area-report'],
  ['地块改名换样式', 'builtin:parcel-rename'],
  ['管网水力坡度复核', 'builtin:pipe-hydraulics'],
  ['压力管网建网加管', 'builtin:pressure-network-build'],
  ['大图层导出坐标表', 'builtin:big-export'],
  ['地形点按点组导出', 'builtin:cogo-points'],
  ['廊道要素线提取', 'builtin:corridor-feature-lines'],
  ['用地形点建曲面', 'builtin:surface-build'],
];

for (const [query, expectId] of cases) {
  const top = topOf(query);
  check('中文意图命中 ' + expectId, top.id === expectId, 'query=' + query + ' got=' + top.id + ' score=' + top.score);
}

const en = idx.search(null, 'export large layer to csv with progress');
check('英文关键词命中大导出', (en.hits[0] || {}).id === 'builtin:big-export', 'got=' + (en.hits[0] || {}).id);

const hit = topOf('统计地块面积');
check('命中结果带 steps 与 notes',
  Array.isArray(hit.steps) && hit.steps.length >= 3 && hit.steps.every((s) => typeof s.method === 'string' && s.method.length > 2) && typeof hit.notes === 'string' && hit.notes.length > 5,
  'steps=' + (hit.steps || []).length);

const none = idx.search(null, 'zzz qqq unrelated gibberish 无关的内容');
check('无关查询不瞎给', none.hits.length === 0 || none.hits[0].score < 0.2, 'hits=' + none.hits.length + ' score=' + ((none.hits[0] || {}).score));

const limited = idx.search(null, '导出', { limit: 2 });
check('limit 生效', limited.hits.length <= 2, 'hits=' + limited.hits.length);

// recorded：临时项目目录里塞一条录制记录
const project = '__recipe_test_' + Date.now();
const scope = path.join(ROOT, 'exchange', 'projects', project, 'workflows');
fs.mkdirSync(scope, { recursive: true });
const taskId = 'task-recipe-test';
fs.writeFileSync(path.join(scope, taskId + '.json'), JSON.stringify({
  taskId,
  createdAt: new Date().toISOString(),
  ask: '把地块面积统计成 CSV 发给业主',
  steps: [
    { method: 'listParcels', ok: true, channel: 'cad' },
    { method: 'qtyParcelAreas', ok: true, channel: 'cad' },
    { method: 'reportParcels', ok: true, channel: 'cad' },
  ],
  outcome: { ok: true, ms: 1200, calls: 3 },
  reusability: { score: 60 },
}), 'utf8');

const withRecorded = idx.search(project, '地块面积导出 csv');
const rec = withRecorded.hits.find((h) => h.source === 'recorded');
check('项目录制的工作流能被查到', !!rec && rec.reuse && rec.reuse.taskId === taskId, rec ? ('taskId=' + rec.reuse.taskId + ' score=' + rec.score) : 'not found');
check('recorded 结果排在 builtin 前面', withRecorded.hits.length > 0 && withRecorded.hits[0].source === 'recorded',
  'first=' + (withRecorded.hits[0] || {}).source);
check('recorded 步骤带真实方法名', !!rec && rec.steps.length >= 2 && rec.steps.every((s) => s.method), rec ? ('steps=' + rec.steps.length) : '');

// 清理临时项目
try { fs.rmSync(path.join(ROOT, 'exchange', 'projects', project), { recursive: true, force: true }); } catch (_) { }

console.log('\n' + pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
