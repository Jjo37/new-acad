'use strict';
/**
 * test-project-memory.js — 项目级记忆（v1.17 第一片，2026-11-14）
 * 覆盖：条目 id 递增 / supersede 作废但可追溯 / 中文检索 / 失败召回 / 注入内容与截断 / 持久化
 * 跑法：node server/test-project-memory.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'pm-test-'));
process.env.PROJECT_MEMORY_ROOT = TMP;
const pm = require('./project-memory');

let pass = 0;
function t(name, fn) { fn(); pass++; console.log('  ok  ' + name); }

const PROJ = 'ZZ-TEST PROJECT';
const work = path.join(TMP, 'exchange', 'projects', PROJ, 'agent-work');
fs.mkdirSync(work, { recursive: true });

console.log('test-project-memory');

t('add：id 按 kind 前缀递增，落盘可读回', () => {
  const a = pm.add(PROJ, { kind: 'fact', text: '桩号 = X − 750641.55（UTM84-51S）', source: '图纸标注', tags: ['桩号', '坐标'] });
  const b = pm.add(PROJ, { kind: 'fact', text: '地面线采样 20m 一个点', source: '用户口径', tags: ['地面线'] });
  const c = pm.add(PROJ, { kind: 'lesson', text: 'addSurfacePointsFromFile: xCol/yCol/zCol 从 0 起，文件 3 列时传 3 会跳过所有行', tags: ['addSurfacePointsFromFile', '列号'] });
  assert.strictEqual(a.id, 'F-0001');
  assert.strictEqual(b.id, 'F-0002');
  assert.strictEqual(c.id, 'L-0001');
  assert.strictEqual(pm.readEntries(PROJ).length, 3);
});

t('supersede：旧条目作废但保留，可追溯替代者', () => {
  const r = pm.supersede(PROJ, 'F-0002', { text: '地面线按数据带 20m 采样（原 100m 作废）', source: '用户口径' });
  assert.strictEqual(r.superseded, 'F-0002');
  assert.ok(/^F-\d{4}$/.test(r.replacedBy));
  const all = pm.readEntries(PROJ);
  const old = all.find((e) => e.id === 'F-0002');
  assert.strictEqual(old.supersededBy, r.replacedBy);
  assert.ok(!pm.live(PROJ).some((e) => e.id === 'F-0002'), '作废条目不进 live');
  assert.ok(pm.live(PROJ).some((e) => e.id === r.replacedBy));
});

t('search：中文关键词命中（事实与坑分开也能取）', () => {
  const hit = pm.search(PROJ, '桩号');
  assert.ok(hit.length >= 1 && hit[0].text.includes('桩号'), '实际：' + JSON.stringify(hit));
  const facts = pm.search(PROJ, '桩号', { kinds: ['fact'] });
  assert.ok(facts.every((e) => e.kind === 'fact'));
});

t('recall：按方法名召回坑表条目', () => {
  const lessons = pm.recall(PROJ, { method: 'addSurfacePointsFromFile', errCode: 'CIVIL3D.INVALID_INPUT' });
  assert.ok(lessons.length >= 1, '应召回至少一条');
  assert.ok(lessons[0].text.includes('从 0 起'), '实际：' + lessons[0].text);
  assert.ok(lessons.every((e) => e.kind === 'lesson'), '召回只给 lesson，不掺事实');
});

t('recall：无相关条目时返回空（不硬凑噪音）', () => {
  assert.deepStrictEqual(pm.recall(PROJ, { method: 'createNurbsSurface', errCode: 'CIVIL3D.API_ERROR' }), []);
});

t('buildInjection：铁规全文 + 事实标题 + 坑表计数', () => {
  fs.writeFileSync(path.join(work, 'PROJECT-RULES.md'), '# 铁规\n严格遵照图纸设计，禁止反算/简化/凑数。\n', 'utf8');
  const s = pm.buildInjection(PROJ);
  assert.ok(s.includes('项目铁规'), '应注入铁规段：' + s.slice(0, 120));
  assert.ok(s.includes('严格遵照图纸设计'), '铁规必须是正文，不是标题');
  assert.ok(s.includes('项目事实'), '应注入事实段');
  assert.ok(/坑表 \d+ 条/.test(s), '应给出坑表计数');
  assert.ok(!s.includes('从 0 起'), '坑表正文不注入（留给失败召回），实际出现了');
});

t('buildInjection：铁规超长时截断并提示全文位置', () => {
  fs.writeFileSync(path.join(work, 'PROJECT-RULES.md'), 'X'.repeat(5000), 'utf8');
  const s = pm.buildInjection(PROJ);
  assert.ok(s.length < 2600, '注入不能无限长，实际 ' + s.length);
  assert.ok(s.includes('已截断'), '应提示已截断');
});

t('没有项目 / 没有铁规时不报错、不产生空段', () => {
  assert.strictEqual(pm.buildInjection('ZZ-NO-SUCH-PROJECT'), '');
});

t('持久化：重新读盘仍能拿到条目（不靠内存态）', () => {
  const onDisk = fs.readFileSync(pm.entriesPath(PROJ), 'utf8').trim().split(/\r?\n/);
  assert.ok(onDisk.length >= 4, '实际落盘行数 ' + onDisk.length);
  assert.ok(onDisk.every((l) => { try { JSON.parse(l); return true; } catch (_) { return false; } }), '每行应是合法 JSON');
});

console.log('\n' + pass + '/' + pass + ' 通过');
try { fs.rmSync(TMP, { recursive: true, force: true }); } catch (_) { /* best-effort */ }
