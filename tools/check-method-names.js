// 对比 CommandDispatcher 注册名 vs 反射方法名（listMethods 一致性检查）
const fs = require('fs');
const path = require('path');
const srcDir = path.join(__dirname, '..', 'plugin', 'AcBridge-v24', 'src');

const disp = fs.readFileSync(path.join(srcDir, 'CommandDispatcher.cs'), 'utf8');
const regs = [...disp.matchAll(/"([a-zA-Z0-9]+)"\s*=>/g)].map(m => m[1]);

const methods = []; // {file, name(反射名)}
for (const f of fs.readdirSync(srcDir).filter(f => f.endsWith('.cs'))) {
  const s = fs.readFileSync(path.join(srcDir, f), 'utf8');
  for (const m of s.matchAll(/public static Task<object\?> (\w+)Async\(/g)) {
    const n = m[1];
    const rn = n[0].toLowerCase() + n.slice(1);
    methods.push({ file: f, name: rn });
  }
}

const reflNames = new Set(methods.map(m => m.name));
const regSet = new Set(regs);
const missing = regs.filter(r => !reflNames.has(r));   // 注册名 ≠ 反射名（需前缀修正）
const extra = [...reflNames].filter(n => !regSet.has(n)); // 反射名未注册（listMethods 误报）

console.log('注册名总数:', regs.length, ' 反射名总数:', reflNames.size, ' 去重反射名:', methods.length);
console.log('\n=== 注册名不在反射名集合(注册名带前缀/别名) ===');
for (const m of missing) {
  const hits = methods.filter(x => regs.indexOf(m) !== -1 && false);
  console.log(' ', m);
}
console.log('\n=== 反射名未注册(listMethods 会误报为可用) ===');
for (const n of extra) {
  const srcs = methods.filter(x => x.name === n).map(x => x.file).join(',');
  console.log(' ', n, '<-', srcs);
}
