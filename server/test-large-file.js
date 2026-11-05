// test-large-file.js — 2026-10-13 P0 回归测试
// 覆盖: readFile 分页/outline  ·  queryFile 本地聚合  ·  工具结果外溢(spill)
//   （替代旧的"4000 字静默截断"：大结果落盘 + 句柄，数据零丢失）
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');

const { readFile, queryFile, writeFile, getAgentWorkDir, getLimits } = require('./file-tools.js');
const { spillIfLarge, spillLargestToolMsg } = require('./llm-agent.js');

let fail = 0;
function check(name, cond, detail) {
  console.log((cond ? '  PASS ' : '  FAIL ') + name + (detail ? ' — ' + detail : ''));
  if (!cond) fail++;
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'large-file-'));
const bigTxt = path.join(tmp, 'big.txt');
const LINES = 500;   // fixture size: declared once, asserted everywhere
const lines = [];
for (let i = 1; i <= LINES; i++) lines.push('line ' + i + ' ' + 'x'.repeat(40));
fs.writeFileSync(bigTxt, lines.join('\n'), 'utf8');

// ---------- 1. readFile 分页 ----------
const p1 = readFile({ path: bigTxt, offset: 1, limit: 10 });
check('分页 from/to', p1.from === 1 && p1.to === 10, 'from=' + p1.from + ' to=' + p1.to);
check('分页 hasMore/nextOffset', p1.hasMore === true && p1.nextOffset === 11, 'hasMore=' + p1.hasMore + ' next=' + p1.nextOffset);
check('totalLines 正确', p1.totalLines === LINES, 'totalLines=' + p1.totalLines);

let all = '', off = 1, guard = 0;
while (off && guard++ < 1000) { const pg = readFile({ path: bigTxt, offset: off, limit: 37 }); all += (all ? '\n' : '') + pg.content; off = pg.nextOffset; }
check('跨页拼接 == 原文件', all === lines.join('\n'), 'len ' + all.length + ' vs ' + lines.join('\n').length);

const p2 = readFile({ path: bigTxt, offset: 1, limit: 0, maxChars: 100 });
check('maxChars 生效', p2.content.length <= 100 && p2.truncatedByChars === true, 'len=' + p2.content.length);

const p3 = readFile({ path: bigTxt, outline: true });
check('outline 不回全文', p3.outline === true && !p3.content && p3.totalLines === LINES, 'head=' + (p3.head || '').length + ' chars');

// ---------- 2. queryFile 本地聚合 ----------
const csv = path.join(tmp, 'data.csv');
fs.writeFileSync(csv, ['name,qty', 'a,3', 'b,5', 'a,2', 'c,10', 'b,1'].join('\n'), 'utf8');
const gb = queryFile({ path: csv, op: 'groupby', by: 'name' });
const aGrp = (gb.groups || []).find(g => g.key === 'a');
check('groupby count', aGrp && aGrp.count === 2, JSON.stringify(gb.groups));
const st = queryFile({ path: csv, op: 'stats', column: 'qty' });
check('stats sum/min/max', st.sum === 21 && st.min === 1 && st.max === 10, JSON.stringify(st));
const gp = queryFile({ path: csv, op: 'grep', pattern: 'a' });
check('grep 命中', gp.matchCount >= 2, 'matchCount=' + gp.matchCount);
const wc = queryFile({ path: bigTxt, op: 'wc' });
check('wc 行数', wc.lines === 500, 'lines=' + wc.lines);

const js = path.join(tmp, 'obj.json');
fs.writeFileSync(js, JSON.stringify({ items: [{ price: 1 }, { price: 2 }], total: 2 }), 'utf8');
const jq = queryFile({ path: js, op: 'json', jsonPath: '$.items' });
check('json 取数组', jq.type === 'array' && jq.length === 2, JSON.stringify(jq));

// ---------- 3. 工具结果外溢(spill) ----------
const small = 'x'.repeat(100);
check('小结果不外溢', spillIfLarge('readFile', small) === small);

const big = JSON.stringify({ content: 'y'.repeat(getLimits().spillBytes + 500) });
const spilled = spillIfLarge('readFile', big);
let obj = null; try { obj = JSON.parse(spilled); } catch (_) {}
check('外溢为句柄(结构正确)', !!obj && obj.spilled === true && typeof obj.path === 'string' && typeof obj.preview === 'string' && obj.preview.length <= 1200, spilled.slice(0, 90));
const back = obj ? fs.readFileSync(obj.path, 'utf8') : '';
check('外溢文件内容完整(可回读)', back === big, 'back.len=' + back.length);
check('句柄体远小于原文', spilled.length < big.length / 3, spilled.length + ' < ' + big.length);

// ---------- 4. writeFile 追加（大输出分段写；仅能写 AI 工作区） ----------
const wf = path.join(getAgentWorkDir(), '_test_append_' + Date.now() + '.txt');
writeFile({ path: wf, content: 'AAA\n' });
writeFile({ path: wf, content: 'BBB\n', append: true });
writeFile({ path: wf, content: 'CCC\n', append: true });
check('writeFile append 拼接', fs.readFileSync(wf, 'utf8') === 'AAA\nBBB\nCCC\n', JSON.stringify(fs.readFileSync(wf, 'utf8')));
try { fs.unlinkSync(wf); } catch (_) {}

// ---------- 5. 触顶抢救 spillLargestToolMsg ----------
const msgs = [
  { role: 'system', content: 'S' },
  { role: 'user', content: 'u' },
  { role: 'tool', tool_call_id: 't1', content: 'z'.repeat(20000) },
  { role: 'tool', tool_call_id: 't2', content: 'small' },
];
const did = spillLargestToolMsg(msgs, () => {});
const t1obj = (() => { try { return JSON.parse(msgs[2].content); } catch (_) { return null; } })();
check('触顶抢救：最大 tool 结果被外溢', did === true && !!t1obj && t1obj.spilled === true, 'did=' + did);
check('触顶抢救：小结果保留', msgs[3].content === 'small');

// ---------- 清理 ----------
try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (_) {}
if (obj && obj.path) { try { fs.unlinkSync(obj.path); } catch (_) {} }
if (t1obj && t1obj.path) { try { fs.unlinkSync(t1obj.path); } catch (_) {} }

console.log(fail === 0 ? '\nALL PASS' : '\n' + fail + ' FAILED');
process.exit(fail === 0 ? 0 : 1);
