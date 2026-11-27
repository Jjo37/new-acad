'use strict';
/**
 * project-scope.js — 项目名 → 目录名的唯一真相（2026-11-14）
 *
 * 为什么要有这个文件（实测事故）：
 *   同一台机器上曾有三套命名规则：
 *     · panel-relay.js / file-tools.js：只替换非法字符，不截断
 *     · workflow-store.js：连 '.' 一起替换，且 slice(0,64)
 *   本机项目名长 100 字符（"...TO LOLOTOE TOWN STA. 0+000 – 8+900 (8.9 KM)"），
 *   于是工作流记录被切进 "...BOBONARO TO LOLO" 这个幽灵目录，
 *   而 agent-work / llm-history / dwg 都在完整名字的目录里 —— 断点续接会找错地方。
 *
 * 规则：
 *   1. 只替换 Windows 非法字符，不替换 '.'（图纸项目名里 "STA. 0+000" 的点是名字的一部分）。
 *   2. 目录名上限 120 字符；超长时截断**必须带哈希后缀**，否则不同的长项目名会撞同一个目录。
 *   3. 空名回落 'unnamed'；结尾的点/空格去掉（Windows 会静默吞掉，导致同一名字两个目录）。
 */

const MAX_DIR_NAME = 120;

function shortHash(s) {
  // FNV-1a 32bit → base36（8 位内），只用于区分被截断的长名字
  let h = 0x811c9dc5;
  const str = String(s == null ? '' : s);
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(36).padStart(7, '0');
}

function sanitizeProjectName(name) {
  let s = String(name == null ? '' : name)
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_')
    .trim();
  s = s.replace(/[. ]+$/, '');
  if (!s) return 'unnamed';
  if (s.length <= MAX_DIR_NAME) return s;
  const hash = shortHash(s);
  return s.slice(0, MAX_DIR_NAME - hash.length - 1) + '~' + hash;
}

module.exports = { sanitizeProjectName, shortHash, MAX_DIR_NAME };
