// 全量扫描 v2: 方法描述参数 vs 实现参数（大括号配对切方法体，消除误报）
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

// 2. descriptions
const descMap = {};
const descRe = /\["([a-zA-Z0-9]+)"\]\s*=\s*"([^"]*)"/g;
while ((m = descRe.exec(dispatcher))) if (descMap[m[1]] === undefined) descMap[m[1]] = m[2];

// 3. 大括号配对提取方法体
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

function getMethodParams(cls, method) {
  const file = Object.keys(allSrc).find(f => allSrc[f].includes(`public static Task<object?> ${method}`));
  if (!file) return { file: null, req: [], opt: [] };
  const body = getMethodBody(allSrc[file], `public static Task<object?> ${method}`);
  if (!body) return { file, req: [], opt: [] };
  const req = [], opt = [];
  const re = /Get(Required|Optional)(String|Double|Int|Bool|StringArray|Parameter)\(parameters,\s*"([a-zA-Z0-9_]+)"/g;
  let p;
  while ((p = re.exec(body))) (p[1] === 'Required' ? req : opt).push(p[3]);
  const arrRe = /GetParameter\(parameters,\s*"([a-zA-Z0-9_]+)"\)\s*as JsonArray/g;
  while ((p = arrRe.exec(body))) req.push(p[3]);
  return { file, req: [...new Set(req)], opt: [...new Set(opt)] };
}

// 4. 对比
const severe = [], fake = [], fuzzy = [], noimpl = [];
let checked = 0;
for (const [name, desc] of Object.entries(descMap)) {
  const map = switchMap[name];
  if (!map) continue;
  checked++;
  const { file, req, opt } = getMethodParams(map.cls, map.method);
  if (!file) { noimpl.push(`${name}: ${map.cls}.${map.method} 未找到`); continue; }
  const allParams = [...req, ...opt];
  const brace = desc.match(/\{([^}]*)\}/);
  // 去嵌套结构 [..] 再解析（center:[x,y] → center; points:[{x,y,z}] → points）
  const cleanBrace = brace ? brace[1].replace(/\[[^\]]*\]/g, '') : '';
  const descParams = cleanBrace ? cleanBrace.split(/[,，]/).map(s => s.trim().replace(/\?.*$/, '').replace(/^[a-zA-Z0-9]+:/, '').replace(/[>\(（].*$/, '').replace(/\|.*$/, '').trim()).filter(Boolean) : [];
  if (desc.includes('...')) fuzzy.push(`${name}: (${desc.slice(0, 55)})`);
  const missing = req.filter(r => r && !desc.includes(r));
  if (missing.length) severe.push(`${name}: 实现必需 ${missing.join('/')} 描述缺 (${desc.slice(0, 55)})`);
  const fakeList = descParams.filter(d => d && !allParams.includes(d) && !['handle', 'handles', 'name', 'points', 'path', 'type', 'x', 'y', 'z', 'angle'].includes(d)
    && !/[\u4e00-\u9fff]/.test(d) && !/^\d/.test(d) && !/^(csv|pnezd|penz|xyzd|xyz|rect|polar|json|dxf|dwg|txt)$/i.test(d));
  if (fakeList.length) fake.push(`${name}: 描述写 ${fakeList.join('/')} 实现无 (${desc.slice(0, 55)})`);
}

console.log(`=== v2 扫描: ${checked} 个方法 ===`);
console.log(`严重(实现必需-描述缺): ${severe.length}`);
console.log(`描述有实现无: ${fake.length}`);
console.log(`描述含...: ${fuzzy.length}`);
console.log(`无实现: ${noimpl.length}`);
console.log('\n--- 严重 ---');
severe.forEach(s => console.log('  ' + s));
console.log('\n--- 描述有实现无 ---');
fake.forEach(s => console.log('  ' + s));
console.log('\n--- 描述模糊含... ---');
fuzzy.forEach(s => console.log('  ' + s));
console.log('\n--- 其他(无实现) ---');
noimpl.forEach(s => console.log('  ' + s));

// P4 防回归: --fail-on-severe 时严重数 > 0 退出码 1（build-plugin.ps1 接入）
if (process.argv.includes('--fail-on-severe')) {
  if (severe.length > 0) {
    console.error(`[FAIL] 描述参数一致性: ${severe.length} 个严重问题（实现必需参数描述缺/写错），请修复后重新编译`);
    process.exit(1);
  }
  console.log(`[OK] 描述参数一致性: 严重 0（${checked} 方法扫描通过）`);
}
