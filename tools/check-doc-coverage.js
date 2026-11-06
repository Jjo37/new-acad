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

// ---- 第二段：面板配置区控件 → 使用手册必须搜得到（2026-11-06 新增）----
// 起因：v1.14.0 加了「思考深度」「测试连接」两个控件，使用手册一个字都没写，
// 而上面那张关键词表因为没人往 CHECKS 里加行，**完全没拦住**（门禁报"覆盖 13 项"全绿）。
// 所以改成自动比对：C# 里造出来的每个配置区控件，手册里必须能搜到它的标签。
// 不是替代 CHECKS，是补上"新增控件默认不会被检查"这个洞。
{
  const PALETTE = path.join(ROOT, "plugin", "AcBridge-v24", "src", "HankPalette.cs");
  const MANUAL = path.join(ROOT, "使用手册.html");
  if (fs.existsSync(PALETTE) && fs.existsSync(MANUAL)) {
    const cs = fs.readFileSync(PALETTE, "utf8");
    const manual = fs.readFileSync(MANUAL, "utf8");
    const s0 = cs.indexOf("cfgPanel = new Panel");
    const e0 = cs.indexOf("cfgChildren = extraRow != null");
    if (s0 >= 0 && e0 > s0) {
      const region = cs.slice(s0, e0);
      // 源码里有些中文是 \uXXXX 转义写的，先解码再比
      const decode = (x) => x.replace(/\\u([0-9A-Fa-f]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)));
      const labels = [...new Set([...region.matchAll(/Text\s*=\s*Loc\.T\(\s*"([^"]+)"/g)].map((m) => decode(m[1])))];
      // 豁免表：每条必须写理由。空着就是"没有豁免"。
      const CONTROL_EXEMPT = new Map([
        // 例：["某个词", "与 XX 合并在同一节说明，不单独成节"],
      ]);
      const missingCtrl = labels.filter((l) => !CONTROL_EXEMPT.has(l) && !manual.includes(l));
      if (missingCtrl.length) {
        console.log("  面板配置区控件缺文档说明（加了控件但手册没写）：");
        missingCtrl.forEach((l) => console.log("    ✗ " + l));
        console.log("    → 在使用手册 4.2 配置区补一节；确实不需要单独说明的，加进 CONTROL_EXEMPT 并写理由");
        process.exit(1);
      }
      console.log("  面板配置区 " + labels.length + " 个控件均有手册说明");
    }
  }
}
if (miss.length) {
  console.log('  文档缺覆盖（功能有、文档没有）：');
  for (const m of miss) console.log('    ✗ ' + m);
  process.exit(1);
}
console.log('  覆盖 ' + covered + ' 项用户可见功能');
