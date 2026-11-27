#!/usr/bin/env node
/**
 * check-package-clean.js —— 安装包/分发包洁净检查（只读）
 *
 * 用法：node tools/check-package-clean.js <bundle 的 Contents 目录>
 *       （release-all 构建完会自动调；也可手动对已构建产物跑）
 *
 * 底线（Bro 2026-10-19 明确要求）：
 *   1) 不含任何个人属性配置：llm.apiKey / relayToken / relaySessionKey / project / knowledgeSources
 *   2) 不含用户数据：user-knowledge 只允许 _README.md；server/memory/agent-memory.md 必须是模板态
 *   3) 不含开发文档：BOOTSTRAP / DASHBOARD / ROADMAP / KNOWN-ISSUES / VERIFICATION / docs / tests
 *   4) tools/ 只允许随包工具（用户排障用），不许开发工具
 *   5) 不夹带密钥/本机路径（复用 scan-release-leaks 的规则）
 */
'use strict';
const fs = require('fs');
const path = require('path');

const C = process.argv[2];
if (!C) { console.error('usage: node tools/check-package-clean.js <bundle Contents dir>'); process.exit(2); }
if (!fs.existsSync(C)) { console.error('目录不存在: ' + C); process.exit(2); }

const errors = [];
const warns = [];
const notes = [];

// ── 1) config.json 必须是模板态 ─────────────────────────────────────────
const cfgPath = path.join(C, 'config.json');
if (!fs.existsSync(cfgPath)) errors.push('缺 config.json（用户首次运行需要它）');
else {
  let raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');
  let j = null;
  try { j = JSON.parse(raw); } catch (e) { errors.push('config.json 不是合法 JSON: ' + e.message); }
  if (j) {
    const llm = j.llm || {};
    const mustEmpty = [
      ['llm.apiKey', llm.apiKey],
      ['relayToken', j.relayToken],
      ['relaySessionKey', j.relaySessionKey],
      ['project', j.project],
    ];
    for (const [k, v] of mustEmpty) if (v) errors.push('config.json 里 ' + k + ' 非空 —— 会把个人配置发给所有用户');
    if (Array.isArray(j.knowledgeSources) && j.knowledgeSources.length) {
      errors.push('config.json 里 knowledgeSources 非空 —— 会把开发者自己的知识源路径发给用户');
    }
    // 端口/语言这种默认值是允许的
    notes.push('config 模板 ok');
  }
}

// ── 1.5) i18n 文案表必须随包（缺了所有 Loc.Tk 会显示 ⟪key⟫）──
const msgPath = path.join(C, 'i18n', 'messages.json');
if (!fs.existsSync(msgPath)) {
  errors.push('缺 i18n/messages.json（所有用户可见文案会显示成 ⟪key⟫）');
} else {
  try {
    const j = JSON.parse(fs.readFileSync(msgPath, 'utf8').replace(/^\uFEFF/, ''));
    const keys = Object.keys(j).filter((k) => !k.startsWith('_'));
    if (keys.length < 50) errors.push('i18n/messages.json 只有 ' + keys.length + ' 个 key，疑似被截断');
    const noEn = keys.filter((k) => !j[k].en);
    if (noEn.length) errors.push('i18n/messages.json 有 ' + noEn.length + ' 个 key 缺英文：' + noEn.slice(0, 5).join(', '));
    notes.push('i18n keys ' + keys.length);
  } catch (e) { errors.push('i18n/messages.json 解析失败: ' + e.message); }
}

// ── 1.6) 必需运行时模块必须随包（缺一个 relay 直接 require 失败 = 装完就废）──
const REQUIRED_SERVER_MODULES = ['knowledge-sources.js', 'i18n.js', 'llm-agent.js', 'panel-relay.js', 'cad-tools.js', 'file-tools.js'];
const missingMod = REQUIRED_SERVER_MODULES.filter((m) => !fs.existsSync(path.join(C, 'server', m)));
if (missingMod.length) errors.push('包内缺 server 模块：' + missingMod.join(', ') + '（relay 会 require 失败）');
else notes.push('server 模块 ' + REQUIRED_SERVER_MODULES.length + ' 个齐全');

// ── 2) 用户数据 ────────────────────────────────────────────────────────
const kbDir = path.join(C, 'user-knowledge');
if (fs.existsSync(kbDir)) {
  const files = fs.readdirSync(kbDir).filter((f) => f !== '_README.md');
  if (files.length) errors.push('user-knowledge 里夹带用户数据: ' + files.slice(0, 5).join(', '));
}
const mem = path.join(C, 'server', 'memory', 'agent-memory.md');
if (fs.existsSync(mem)) {
  const t = fs.readFileSync(mem, 'utf8');
  if (t.length > 3000) warns.push('agent-memory.md ' + t.length + ' 字符，像是带了个人的记忆（应只有模板）');
  if (!/（空。由 AI 在对话中逐步积累。）|empty\. accumulated by the AI/i.test(t)) {
    warns.push('agent-memory.md 不是模板态（缺少"空"标记）');
  }
}
if (fs.existsSync(path.join(C, 'exchange'))) errors.push('夹带 exchange/（运行期交换目录，含选择集/对话/日志）');

// ── 3) 开发文档 / 测试 ─────────────────────────────────────────────────
const DEV_DOCS = ['BOOTSTRAP.md', 'DASHBOARD.md', 'ROADMAP.md', 'KNOWN-ISSUES.md', 'VERIFICATION.md', 'docs', 'tests'];
for (const f of DEV_DOCS) if (fs.existsSync(path.join(C, f))) errors.push('夹带开发文档/测试: ' + f);
for (const f of ['plugin/AcBridge-v24/tests', 'plugin/AcBridge-v24/src']) {
  if (fs.existsSync(path.join(C, f))) errors.push('夹带开发目录: ' + f);
}

// ── 4) tools/ 只许随包工具 ─────────────────────────────────────────────
// 2026-10-28: smoke-plain-acad.js ships on purpose -- the plain-AutoCAD host is another machine and
// that machine must be able to produce docs/audit/host-evidence/*.json from INSIDE the installed package
// (build-dist.ps1 copies it for exactly this reason). It is not a user-facing tool, but it is a shipping one.
const TOOLS_OK = /^(switch-runtime\.ps1|port-check\.(ps1|bat)|port-config\.(ps1|bat)|restart-relay\.ps1|find-civil3d\.ps1|locale\.ps1|ocr-check\.ps1|clean-runtime\.ps1|smoke-plain-acad\.js|verify-install-hosts\.ps1|host-matrix\.js|plugin-commit\.txt|host-wiring\.ps1|manage-hosts\.ps1|supported-hosts\.json)$/;
// 2026-11-27: 加了三个随包件 —— host-wiring.ps1（install.ps1 / manage-hosts / uninstall-pre 共用的接线模块，
// 少了它安装器直接报错）、manage-hosts.ps1（使用手册 1.4b 里让用户加装/单版本卸载的入口）、
// supported-hosts.json（宿主契约，smoke-plain-acad 产 cell 时用它把产品+年份映射成宿主 id）。
// 教训：白名单落后于“实际随包清单”时，这个门禁会把正确出货判成脏包 —— 改打包清单时要同步改这里。   // 2026-11-23: verify-install-hosts 是随包的用户排障工具（只读、不写文件），install.ps1 多宿主安装后用来查「哪个宿主挂了」   // 2026-11-05: port-config 保存后调 restart-relay.ps1 重启 relay
const toolsDir = path.join(C, 'tools');
if (fs.existsSync(toolsDir)) {
  const bad = fs.readdirSync(toolsDir).filter((f) => !TOOLS_OK.test(f));
  if (bad.length) errors.push('tools/ 里混入非随包工具: ' + bad.slice(0, 8).join(', '));
  notes.push('tools 随包 ' + fs.readdirSync(toolsDir).length + ' 个');
}

// ── 5) 本机路径 / 密钥（复用既有扫描器，失败不致命）──────────────────
try {
  const { execFileSync } = require('child_process');
  const scan = path.join(__dirname, 'scan-release-leaks.js');
  if (fs.existsSync(scan)) {
    const out = execFileSync('node', [scan, C, '--ignore-build-scripts'], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
    const high = out.split(/\r?\n/).filter((l) => /\[high\]/.test(l));
    if (high.length) errors.push('泄漏扫描 high: ' + high.length + ' 条（' + high[0].trim().slice(0, 100) + '）');
    const med = out.split(/\r?\n/).filter((l) => /\[medium\]/.test(l));
    if (med.length) warns.push('泄漏扫描 medium: ' + med.length + ' 条');
  }
} catch (e) { warns.push('泄漏扫描未跑成: ' + e.message); }

if (errors.length) {
  console.error('✗ 包体洁净检查失败：' + errors.length + ' 项');
  for (const e of errors) console.error('  - ' + e);
  for (const w of warns) console.error('  ! ' + w);
  process.exit(1);
}
console.log('包体洁净 OK —— ' + notes.concat(warns).join(' · '));
