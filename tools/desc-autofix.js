// P1 描述参数补全 - 生成补丁（保守版: 只重建 {xxx} 参数段, 不碰描述其他文字）
// 用法: node tools/desc-autofix.js          → 生成补丁 JSON 到 tools/desc-autofix-patch.json + 分类统计
//       node tools/desc-autofix.js --apply   → 应用 AUTO 类补丁到 CommandDispatcher.cs
// 质量规则:
//   1. 参数名与实现 GetRequired*/GetOptional* 一致
//   2. 别名映射: 描述旧名被实现名包含(如 material ⊂ materialName) → 用实现名
//   3. 数组参数 → name:[] ; 可选 → name?
//   4. ... 删除; 不新增/不删除描述的非 {xxx} 文字
//   5. C 类(无法自动安全映射) → REVIEW 清单人工
const fs = require('fs');
const path = require('path');
const SRC = 'D:/new-acad/plugin/AcBridge-v24/src';

const dispatcher = fs.readFileSync(path.join(SRC, 'CommandDispatcher.cs'), 'utf8');
const srcFiles = fs.readdirSync(SRC).filter(f => f.endsWith('.cs'));
const allSrc = {};
for (const f of srcFiles) allSrc[f] = fs.readFileSync(path.join(SRC, f), 'utf8');

// 1. switch 映射
const switchMap = {};
const switchRe = /"([a-zA-Z0-9]+)"\s*=>\s*([A-Za-z0-9]+)\.([A-Za-z0-9]+Async)\(parameters\)/g;
let m;
while ((m = switchRe.exec(dispatcher))) switchMap[m[1]] = { cls: m[2], method: m[3] };

// 2. descriptions（支持转义引号 \"）
const descMap = {};
const descRe = /\["([a-zA-Z0-9]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"/g;
while ((m = descRe.exec(dispatcher))) if (descMap[m[1]] === undefined) descMap[m[1]] = m[2];

// 3. 方法体 + 参数
function getMethodBody(src, signature) {
  const idx = src.indexOf(signature);
  if (idx < 0) return null;
  const open = src.indexOf('{', idx);
  if (open < 0) return null;
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}') { depth--; if (depth === 0) return src.slice(idx, i + 1); }
  }
  return src.slice(idx);
}
function getParams(cls, method) {
  const file = Object.keys(allSrc).find(f => allSrc[f].includes(`public static Task<object?> ${method}`));
  if (!file) return null;
  const body = getMethodBody(allSrc[file], `public static Task<object?> ${method}`);
  if (!body) return null;
  const req = [], opt = [], arr = [];
  const re = /Get(Required|Optional)(String|Double|Int|Bool|StringArray|Parameter)\(parameters,\s*"([a-zA-Z0-9_]+)"/g;
  let p;
  while ((p = re.exec(body))) {
    const name = p[3];
    const isArr = p[1] === 'Optional' && p[2] === 'StringArray';
    if (isArr) arr.push(name);
    else (p[1] === 'Required' ? req : opt).push(name);
  }
  const arrRe = /GetParameter\(parameters,\s*"([a-zA-Z0-9_]+)"\)\s*as JsonArray/g;
  while ((p = arrRe.exec(body))) arr.push(p[1]); // p[1]=参数名组（p[3] 是 undefined 的坑）
  return { req: [...new Set(req.filter(Boolean))], opt: [...new Set(opt.filter(Boolean))], arr: [...new Set(arr.filter(Boolean))] };
}

// 4. 解析描述 {xxx} tokens
function parseTokens(desc) {
  const brace = desc.match(/\{([^}]*)\}/);
  if (!brace) return { tokens: [], hasBrace: false };
  return {
    hasBrace: true,
    tokens: brace[1].split(/[,，]/).map(s => s.trim()).filter(Boolean)
  };
}

// 5. 生成新 {xxx}
function buildNewTokens(tokens, params) {
  const { req, opt, arr } = params;
  const implAll = [...req, ...opt, ...arr];
  // 按 impl 分类标后缀: 数组→:[] / 可选→? / 必需→''
  const suffix = (name) => arr.includes(name) ? ':[]' : (opt.includes(name) ? '?' : '');
  const out = [];
  const used = new Set();
  const mapAlias = (tokName) => {
    // 别名映射: 实现名包含 token 名, 或 token 名包含实现名(短名) → 用实现名
    if (implAll.includes(tokName)) return tokName;
    const hits = implAll.filter(n => n && n.includes(tokName) && tokName.length >= 3);
    if (hits.length === 1) return hits[0];
    return null;
  };
  // 原 token 保序保留（映射别名 + 按 impl 重标 ?/:[]，枚举提示保留）
  for (const t of tokens) {
    if (t === '...') continue;
    const base = t.split('?')[0].split(':')[0].trim();
    if (!base) continue;
    const mapped = mapAlias(base);
    const final = mapped || base;
    if (used.has(final)) continue;
    used.add(final);
    const colon = t.includes(':') ? t.slice(t.indexOf(':')) : '';
    // 枚举提示(非:[]格式)保留原样; 否则按 impl 分类
    const suffixPart = colon && colon !== ':[]' ? colon : suffix(final);
    out.push(final + suffixPart);
  }
  // 补缺失实现参数（必需→name, 数组→name:[], 可选→name?）
  for (const r of req) if (!used.has(r)) { out.push(r); used.add(r); }
  for (const a of arr) if (!used.has(a)) { out.push(a + ':[]'); used.add(a); }
  for (const o of opt) if (!used.has(o)) { out.push(o + '?'); used.add(o); }
  return out;
}

// 6. 生成补丁
const patches = [];
let autoCount = 0, reviewCount = 0, extracted = 0, noImpl = 0;
for (const [name, desc] of Object.entries(descMap)) {
  const map = switchMap[name];
  if (!map) continue;
  const params = getParams(map.cls, map.method);
  if (!params) { noImpl++; continue; }
  extracted++;
  const { tokens, hasBrace } = parseTokens(desc);
  const newTokens = buildNewTokens(tokens, params);
  const oldBrace = hasBrace ? (desc.match(/\{([^}]*)\}/)[0]) : '';
  const newBrace = '{' + newTokens.join(', ') + '}';
  if (oldBrace === newBrace) continue; // 无需改
  // 判断是否安全: 嵌套结构(含 [ 或 { 除 :[] 数组标记) / 残留符号 / 无法映射词 → REVIEW
  const rawBrace = hasBrace ? (desc.match(/\{[^}]*\}/)[0]) : '';
  const nested = /[\[{]/.test(rawBrace.slice(1, -1).replace(/:\[\]/g, '')); // 去外层{}后还有 [ 或 { → 嵌套
  const messy = tokens.some(t => /[\]{}]/.test(t)); // 拆分残留
  const implAll = [...params.req, ...params.opt, ...params.arr];
  const unmapped = tokens.filter(t => {
    if (t === '...') return false;
    const base = t.split('?')[0].split(':')[0].trim();
    return base && !implAll.includes(base) && !implAll.some(n => n && n.includes(base) && base.length >= 3);
  }).map(t => t.split('?')[0].split(':')[0].trim()).filter(Boolean);
  const hasQuote = /[^\\]"/.test(newBrace); // 裸引号(转义引号场景) → 人工
  const category = (nested || messy || unmapped.length > 0 || hasQuote) ? 'REVIEW' : 'AUTO';
  if (category === 'AUTO') autoCount++; else reviewCount++;
  patches.push({
    method: name, category, oldDesc: desc,
    oldBrace, newBrace,
    newDesc: hasBrace ? desc.replace(/\{[^}]*\}/, newBrace) : desc + ' ' + newBrace,
    unmapped
  });
}

// 输出
const outPath = 'D:/new-acad/tools/desc-autofix-patch.json';
fs.writeFileSync(outPath, JSON.stringify(patches, null, 2));
console.log(`=== 补丁生成: 共 ${patches.length} 条 (AUTO ${autoCount} / REVIEW ${reviewCount}) ===`);
console.log(`参数提取: ${extracted}/${Object.keys(descMap).length} (无实现 ${noImpl})`);
console.log(`补丁文件: ${outPath}`);

// --apply: 只应用 AUTO 类补丁到 CommandDispatcher.cs
if (process.argv.includes('--apply')) {
  const dp = 'D:/new-acad/plugin/AcBridge-v24/src/CommandDispatcher.cs';
  let cs = fs.readFileSync(dp, 'utf8');
  let applied = 0, failed = 0;
  for (const p of patches) {
    if (p.category !== 'AUTO') continue;
    const needle = `["${p.method}"] = "${p.oldDesc}"`;
    const repl = `["${p.method}"] = "${p.newDesc}"`;
    if (cs.includes(needle)) { cs = cs.replace(needle, repl); applied++; }
    else { failed++; console.log('FAIL 未匹配:', p.method); }
  }
  fs.writeFileSync(dp, cs);
  console.log(`--- apply: 应用 ${applied} 条, 失败 ${failed} ---`);
  // 验证: 重新扫描
  console.log('请重跑 node tools/desc-autofix.js 验证');
}

// REVIEW 清单（仅在非 apply 模式完整输出）
if (!process.argv.includes('--apply')) {
  console.log('\n--- REVIEW 清单（需人工看）---');
  patches.filter(p => p.category === 'REVIEW').forEach(p => {
    console.log(`  ${p.method}: 原 ${p.oldBrace} → 新 ${p.newBrace}  [未映射词: ${p.unmapped.join('/')}]`);
  });
  console.log('\n--- AUTO 抽样前 15 条 ---');
  patches.filter(p => p.category === 'AUTO').slice(0, 15).forEach(p => {
    console.log(`  ${p.method}: ${p.oldDesc.slice(0, 45)} → ${p.newBrace}`);
  });
}
