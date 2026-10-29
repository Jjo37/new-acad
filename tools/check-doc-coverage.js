#!/usr/bin/env node
/**
 * check-doc-coverage.js —— 文档覆盖门禁（2026-10-15）
 *
 * 防止「功能有、文档没有」：维护一张「用户可见功能 → 说明书/README 里必须出现的关键词」表，
 * 发版门禁里 grep 一遍；任一功能的全部关键词都找不到 → 退出码 1。
 *
 * 新增面向用户的功能时，往下面 CHECKS 表里加一行即可。
 * 文件不存在（如发布仓裁掉了模板）→ 跳过该项，不误报。
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');

// 功能 → { file, any:[关键词...] }（出现任一关键词即算已覆盖）
const CHECKS = [
  { name: '知识库（user-knowledge）',  file: '使用手册.html', any: ['知识库'] },
  { name: 'AI 记忆（长期记忆）',        file: '使用手册.html', any: ['长期记忆', 'agent-memory'] },
  { name: '清除配置按钮',               file: '使用手册.html', any: ['清除配置'] },
  { name: '上传文件 / 图片→CAD',        file: '使用手册.html', any: ['上传文件'] },
  { name: '项目文件夹（多工程隔离）',   file: '使用手册.html', any: ['项目文件夹'] },
  { name: '界面语言切换',               file: '使用手册.html', any: ['界面语言'] },
  { name: '批量 / 文件通道',            file: '使用手册.html', any: ['文件通道'] },
  { name: '图片描摹',                   file: '使用手册.html', any: ['描摹'] },
  // 2026-10-21: 新增功能必须先进这张表 —— 否则"发布了但用户手册没写"没有任何东西会拦。
  { name: '工具页签（调用流/方法目录）', file: '使用手册.html', any: ['工具页签', '调用流'] },
  { name: '工作流记录（捕获/导出/回放）', file: '使用手册.html', any: ['工作流记录'] },
  { name: '工作流记录（公共 README）',    file: 'tools/public-templates/README.md', any: ['workflow', '工作流'] },
  { name: '知识库（公共 README）',      file: 'tools/public-templates/README.md', any: ['知识库', 'Knowledge base'] },
  { name: 'AI 记忆（公共 README）',     file: 'tools/public-templates/README.md', any: ['记忆', 'memory'] },
];

const miss = [];
let covered = 0;
for (const c of CHECKS) {
  const f = path.join(ROOT, c.file);
  if (!fs.existsSync(f)) continue; // 文件不在 → 跳过，不误报
  let s;
  try { s = fs.readFileSync(f, 'utf8'); } catch { continue; }
  if (c.any.some((k) => s.includes(k))) covered++;
  else miss.push(c.name + '  ←  ' + c.file);
}

if (miss.length) {
  console.log('  文档缺覆盖（功能有、文档没有）：');
  for (const m of miss) console.log('    ✗ ' + m);
  process.exit(1);
}
console.log('  覆盖 ' + covered + ' 项用户可见功能');
