// server/i18n.js —— 用户可见文案的单一入口（Node 侧）
// 2026-10-19: 与 C# 的 Loc.Tk 共用 i18n/messages.json；术语见 i18n/glossary.md。
// 约定：
//   - 用户可见文案一律走 t('panel.send')，禁止硬编码中文/英文字符串
//   - 缺 key 直接显形（返回 ⟪key⟫），开发期能立刻发现，生产期也不会静默给错字
//   - 变量用 {name} 占位
'use strict';

const fs = require('fs');
const path = require('path');

const MESSAGES_FILE = path.join(__dirname, '..', 'i18n', 'messages.json');

let table = null;
let loadErr = null;

function load(force) {
  if (table && !force) return table;
  try {
    let raw = fs.readFileSync(MESSAGES_FILE, 'utf8');
    if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
    const parsed = JSON.parse(raw);
    delete parsed._meta;
    table = parsed;
    loadErr = null;
  } catch (e) {
    loadErr = e;
    table = {};
  }
  return table;
}

function currentLang() {
  try {
    // 复用既有 locale 解析（en* / zh* 直判，其余跟随系统 ICU）
    const { resolveLocale } = require('./i18n-en.js');
    return String(resolveLocale() || 'zh').toLowerCase().startsWith('en') ? 'en' : 'zh';
  } catch (_) { return 'zh'; }
}

/**
 * @param {string} key 形如 panel.send
 * @param {object} [vars] {n: 3} → 替换 {n}
 * @param {string} [lang] 'zh' | 'en'，缺省按当前 locale
 */
function t(key, vars, lang) {
  const tb = load();
  const entry = tb[key];
  if (!entry) return '⟪' + key + '⟫';
  const L = lang || currentLang();
  let s = entry[L] || entry.zh || entry.en || ('⟪' + key + '⟫');
  if (vars) for (const [k, v] of Object.entries(vars)) s = s.split('{' + k + '}').join(String(v));
  return s;
}

function has(key) { return !!load()[key]; }
function keys() { return Object.keys(load()); }
function stats() {
  const tb = load();
  const all = Object.keys(tb);
  const missingEn = all.filter((k) => !tb[k].en);
  const missingZh = all.filter((k) => !tb[k].zh);
  return { total: all.length, missingEn, missingZh, loadError: loadErr ? loadErr.message : null, file: MESSAGES_FILE };
}

module.exports = { t, has, keys, stats, currentLang, MESSAGES_FILE };
