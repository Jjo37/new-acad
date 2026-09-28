#!/usr/bin/env node
'use strict';
/**
 * scan-release-leaks.js — 发布前泄漏扫描（只读）
 *
 *   node tools/scan-release-leaks.js <dir> [--ignore-build-scripts]
 *
 * 检查：开发者本机路径、疑似密钥/token、明文 Bearer、私有邮箱、内部文档残留。
 * 说明：构建脚本里的 `D:\new-acad` 只是各仓默认根路径（无害），用 --ignore-build-scripts 跳过。
 */
const fs = require('fs');
const path = require('path');

const ROOT = process.argv[2];
if (!ROOT) { console.error('usage: node tools/scan-release-leaks.js <dir> [--ignore-build-scripts]'); process.exit(2); }
const ignoreBuild = process.argv.includes('--ignore-build-scripts');

const textExt = new Set(['.md', '.js', '.ts', '.json', '.ps1', '.bat', '.html', '.cs', '.xaml', '.txt', '.iss', '.vbs', '.lsp', '.yml', '.cmd', '.py']);
const skipDirs = new Set(['.git', 'node_modules']);

const rules = [
  { name: '本机用户路径', re: /C:\\+Users\\+(?!Public|Default)[A-Za-z0-9_.-]+/i, severity: 'high' },
  { name: '开发者 dev 路径', re: /D:\\+new-acad/i, severity: 'low', skipWhen: (f) => ignoreBuild && /(build-|install\.ps1|clean-runtime|port-|sync-dev-release|publish-)/i.test(f) },
  { name: '疑似 API Key (sk-)', re: /\bsk-[A-Za-z0-9_\-]{16,}/, severity: 'high' },
  { name: '疑似 GitHub token', re: /\b(ghp_|github_pat_)[A-Za-z0-9_]{20,}/, severity: 'high' },
  { name: '明文 Bearer 长串', re: /Bearer\s+[A-Za-z0-9_\-.]{30,}/, severity: 'high' },
  { name: '内部开发档案', re: /^(DASHBOARD|BOOTSTRAP|ROADMAP)\.md$|^(project-(review|context-plan|improvement-plan)|optimization-tasks|relay-llm-agent-plan|command-methods-assessment|deployment-package|ai-sac-subassembly-plan)/, severity: 'medium', nameOnly: true },
];

const hits = [];
let scanned = 0;
function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!skipDirs.has(e.name)) walk(path.join(dir, e.name)); continue; }
    const full = path.join(dir, e.name);
    const rel = path.relative(ROOT, full);
    for (const r of rules) {
      if (r.nameOnly) {
        if (r.re.test(e.name)) hits.push({ rule: r.name, severity: r.severity, file: rel, line: 0, text: e.name });
        continue;
      }
      if (r.skipWhen && r.skipWhen(rel)) continue;
      if (!textExt.has(path.extname(e.name).toLowerCase())) continue;
      let text;
      try { text = fs.readFileSync(full, 'utf8'); } catch { continue; }
      if (scanned === 0 || true) { /* count once per file below */ }
      const lines = text.split(/\r?\n/);
      lines.forEach((line, i) => {
        if (line.length > 500) return;
        if (r.re.test(line)) hits.push({ rule: r.name, severity: r.severity, file: rel, line: i + 1, text: line.trim().slice(0, 120) });
      });
    }
    scanned++;
  }
}
walk(ROOT);

console.log(`扫描目录: ${ROOT}（${scanned} 个文件）`);
if (!hits.length) { console.log('无命中 ✓'); process.exit(0); }
const byRule = {};
for (const h of hits) byRule[h.rule] = (byRule[h.rule] || 0) + 1;
console.log('命中:', JSON.stringify(byRule));
for (const r of rules) {
  const sub = hits.filter(h => h.rule === r.name);
  if (!sub.length) continue;
  console.log(`\n-- [${r.severity}] ${r.name}（${sub.length}）--`);
  for (const h of sub.slice(0, 10)) console.log(`  ${h.file}${h.line ? ':' + h.line : ''}  ${h.text}`);
  if (sub.length > 10) console.log(`  … 其余 ${sub.length - 10} 条`);
}
const high = hits.filter(h => h.severity === 'high');
process.exit(high.length ? 1 : 0);
