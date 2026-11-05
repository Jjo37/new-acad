#!/usr/bin/env node
/**
 * check-known-issues.js —— 已知问题台账门禁（2026-10-15）
 *
 * 直击批评：「已知问题清单四个版本没动」。规则：
 *   1. KNOWN-ISSUES.md 存在，且含 A 设计边界 / B 待办 / C 已解决 三段。
 *   2. B（待办）表每行都有「目标版本」与「状态」。
 *   3. 最近两个版本的发布说明里，「已知问题」段落**不能逐字相同**（要么解掉、要么说明）。
 *
 * 退出码：0 通过；1 不合格。
 */
const fs = require('fs');
const path = require('path');
const ROOT = path.join(__dirname, '..');
const TPL = path.join(ROOT, 'tools', 'public-templates');

function fail(msg) { console.log('  ✗ ' + msg); process.exitCode = 1; }

// 1. 台账存在 + 结构
const ledgerPath = path.join(ROOT, 'KNOWN-ISSUES.md');
if (!fs.existsSync(ledgerPath)) {
  console.log('  ✗ 缺少 KNOWN-ISSUES.md（已知问题台账，唯一来源）');
  process.exit(1);
}
const ledger = fs.readFileSync(ledgerPath, 'utf8');
for (const [tag, re] of [['A 设计边界', /##\s*A\./], ['B 待办', /##\s*B\./], ['C 已修复', /##\s*C\./], ['D 已定案', /##\s*D\./]]) {
  if (!re.test(ledger)) fail('KNOWN-ISSUES.md 缺少「' + tag + '」段');
}

// 2. B 表每行（| 项 | 目标 | 状态 | ...）目标与状态非空
const secB = (ledger.split(/##\s*B\./)[1] || '').split(/##\s*C\./)[0];
const rows = secB.split('\n').filter(l => /^\s*\|/.test(l) && !/^\s*\|[\s\-:|]+\|\s*$/.test(l));
let bCount = 0;
for (const r of rows) {
  const cells = r.split('|').map(s => s.trim());
  if (cells.length < 5) continue;
  if (/项|目标/.test(cells[1])) continue; // 表头
  bCount++;
  if (!cells[2] || cells[2] === '—' || cells[2] === '-') fail('B 表条目缺「目标」：' + cells[1]);
  if (!cells[3]) fail('B 表条目缺「状态」：' + cells[1]);
}

// 3. 最近两版发布说明的「已知问题」段落不得逐字相同
function knownBlock(file) {
  if (!fs.existsSync(file)) return null;
  const l = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  const i = l.findIndex(x => x.startsWith('### 已知问题'));
  if (i < 0) return null;
  const out = [];
  for (let k = i + 1; k < l.length && !l[k].startsWith('###'); k++) out.push(l[k].trim());
  return out.join('\n').trim();
}
const notes = fs.readdirSync(TPL)
  .map(f => ({ f, m: f.match(/^release-notes-(\d+)\.(\d+)\.(\d+)(?:-[\w.]+)?\.md$/) }))
  .filter(x => x.m)
  .map(x => ({ file: path.join(TPL, x.f), v: [+x.m[1], +x.m[2], +x.m[3]] }))
  .sort((a, b) => (b.v[0] - a.v[0]) || (b.v[1] - a.v[1]) || (b.v[2] - a.v[2]));

if (notes.length >= 2) {
  const cur = knownBlock(notes[0].file), prev = knownBlock(notes[1].file);
  if (cur && prev && cur === prev) {
    fail('最近两版发布说明的「已知问题」逐字相同 —— 请解掉/更新，或在台账 C 表记录本版已解决；'
      + '若本版确实无变化，于该段加一行「本版已知问题未变动：<原因>」。');
  }
}

if (process.exitCode) { console.log('  → 已知问题台账不合格（见上）'); process.exit(1); }
console.log('  台账 OK（B 待办 ' + bCount + ' 项，最近两版清单有差异）');
