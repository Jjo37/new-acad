#!/usr/bin/env node
/**
 * check-provider-neutrality.js -- 配置指导与服务商默认值必须"中性" (2026-10-29)
 *
 * Why (Bro, 2026-10-29): 用户配置指导里对 DeepSeek 的态度"过于暧昧，有点商业了"，
 * 而且默认是中国的网络情况。三处同时在引导：文档夸它、安装向导第一项是它、代码里
 * `cfg.baseUrl || 'https://api.deepseek.com/v1'` 静默兜底也是它 —— 用户没配置的时候，
 * 产品会假装"配置过了"，去调一家他家根本没选的厂商。
 *
 * 判据（都是可证伪的结构断言，不抄文案）：
 *   N1 文档里不得有"夸厂商"的字眼（便宜 / 性价比 / 中文好 / cheap / 首选 / 推荐用 …）
 *   N2 面板预设必须按 id 字母序、'custom' 固定最后（顺序不得被调成"谁排第一"）
 *   N3 服务端代码不得把任何厂商地址/模型名当兜底（`cfg.baseUrl || '<厂商>'` 这类）
 *   N4 安装向导必须写明"顺序不代表推荐"，且不得有 N1 的字眼（重排的人得留住这句话）
 *
 * 为什么不去扫"哪家排第一"：字母序里 DeepSeek 本来就靠前（D）。真正的中性不是排序，
 * 是**不预选 + 不夸 + 不兜底**。N2/N4 保证排序有据可查，N1/N3 拿掉实质偏向。
 *
 * Usage: node tools/check-provider-neutrality.js [--json]
 * Exit: 0 pass, 1 violation
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const AS_JSON = process.argv.includes('--json');

// 夸厂商 / 排序暗示 / 地区假设
const PRAISE = [
  '便宜', '性价比', '性价比高', '中文好', '中文最好', '首选', '推荐用', '强烈推荐',
  '最好用', '最强', '国内直连', '低延迟', 'cheap', 'good chinese', 'best value',
  'recommended', 'we recommend', 'low latency in china',
];

const DOCS = [
  { path: '使用手册.html', required: true },
  { path: 'README.md', required: false },
  { path: 'README_EN.md', required: false },
  { path: 'tools/public-templates/README.md', required: false },
  { path: 'tools/public-templates/README_EN.md', required: false },
  { path: 'POSITIONING.md', required: false },
];

const SERVER_FILES = ['server/panel-relay.js', 'server/llm-agent.js', 'server/compact.js'];

const errors = [];
const notes = [];

function read(rel) {
  const abs = path.join(ROOT, rel);
  return fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
}

// ---------- N1: no vendor praise in user-facing docs ----------
for (const d of DOCS) {
  const text = read(d.path);
  if (text === null) {
    if (d.required) errors.push('doc missing: ' + d.path);
    continue;
  }
  const lines = text.split(/\r?\n/);
  let hits = 0;
  // Scoped to provider context: "首选" describing an image-tracing mode, or "(recommended)" on an
  // install step, is not vendor praise. Only judge a line that is actually about the model provider.
  const PROVIDER_CONTEXT = /deepseek|openai|qwen|kimi|zhipu|minimax|openrouter|xai|通义|智谱|服务商|provider/i;
  lines.forEach((line, i) => {
    if (!PROVIDER_CONTEXT.test(line)) return;
    const lower = line.toLowerCase();
    for (const word of PRAISE) {
      if (lower.includes(word.toLowerCase())) {
        errors.push(`N1 vendor praise in ${d.path}:${i + 1} ("${word}") :: ${line.trim().slice(0, 120)}`);
        hits++;
        break;
      }
    }
  });
  notes.push(`${d.path}: praise scan ${hits === 0 ? 'clean' : hits + ' hit(s)'}`);
}

// ---------- N2: presets ordered by id, custom last ----------
{
  const text = read('server/panel-relay.js');
  if (!text) errors.push('missing server/panel-relay.js');
  else {
    const m = text.match(/const LLM_PRESETS = \[([\s\S]*?)\n\];/);
    if (!m) errors.push('N2 cannot locate LLM_PRESETS array in server/panel-relay.js');
    else {
      const ids = [...m[1].matchAll(/\{\s*id:\s*'([^']+)'/g)].map((x) => x[1]);
      if (!ids.length) errors.push('N2 LLM_PRESETS has no id entries');
      else {
        const vendored = ids.filter((id) => id !== 'custom');
        const sorted = vendored.slice().sort();
        if (vendored.join(',') !== sorted.join(',')) {
          errors.push(`N2 LLM_PRESETS is not in id order. found=[${ids.join(',')}] expected=[${sorted.concat('custom').join(',')}]`);
        }
        if (ids[ids.length - 1] !== 'custom') errors.push("N2 'custom' must stay last, found last=" + ids[ids.length - 1]);
        if (vendored.join(',') === sorted.join(',')) notes.push('LLM_PRESETS: id order OK (' + ids.join(',') + ')');
      }
    }
  }
}

// ---------- N3: no vendor fallback in server code ----------
for (const rel of SERVER_FILES) {
  const text = read(rel);
  if (!text) { errors.push('missing ' + rel); continue; }
  const lines = text.split(/\r?\n/);
  lines.forEach((line, i) => {
    // `x.baseUrl || 'https://vendor...'` / `model || 'deepseek-chat'` style fallbacks
    if (/\.baseUrl\s*\|\|\s*['"]https?:\/\//.test(line)) {
      errors.push(`N3 vendor URL fallback in ${rel}:${i + 1} :: ${line.trim().slice(0, 120)}`);
    }
    if (/\.model\s*\|\|\s*['"][^'"]*deepseek/i.test(line)) {
      errors.push(`N3 vendor model fallback in ${rel}:${i + 1} :: ${line.trim().slice(0, 120)}`);
    }
  });
}
notes.push('server code: fallback scan done');

// ---------- N4: installer wizard must stay neutral + say so ----------
{
  const text = read('install.ps1');
  if (!text) errors.push('missing install.ps1');
  else {
    if (!text.includes('顺序不代表推荐')) {
      errors.push('N4 install.ps1 provider menu no longer states "顺序不代表推荐" (reordering must keep the disclaimer)');
    } else {
      notes.push('install.ps1: neutrality disclaimer present');
    }
    const lower = text.toLowerCase();
    // only judge lines that talk about a provider (same scoping rule as N1)
    text.split(/\r?\n/).forEach((line, i) => {
      if (!/deepseek|openai|qwen|kimi|zhipu|minimax|openrouter|xai|通义|智谱|服务商|provider/i.test(line)) return;
      for (const word of PRAISE) {
        if (line.toLowerCase().includes(word.toLowerCase())) errors.push('N4 vendor praise in install.ps1:' + (i + 1) + ' ("' + word + '")');
      }
    });
  }
}

if (AS_JSON) {
  console.log(JSON.stringify({ ok: errors.length === 0, errors, notes }, null, 2));
} else {
  for (const n of notes) console.log('  ok: ' + n);
  for (const e of errors) console.log('  FAIL: ' + e);
  console.log(errors.length === 0
    ? '  provider neutrality OK (' + notes.length + ' check(s))'
    : '  provider neutrality FAILED (' + errors.length + ' problem(s))');
}
process.exit(errors.length === 0 ? 0 : 1);
