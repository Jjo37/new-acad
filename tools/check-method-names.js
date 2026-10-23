// check-method-names.js — listMethods 清单 vs CommandDispatcher 注册 一致性检查
//
// 目标：保证「广告面 == 可调用面」。
//   - 广告面 = listMethods 输出（插件反射 static 类里 public static Task<object?> XxxAsync
//              + RegisteredNameOverrides 改名 + AdditionalRegisteredNames 补发）
//   - 可调用面 = CommandDispatcher.DispatchInnerAsync 的 switch 注册名
// 静态分析，无需 C3D。退出码 0=一致，1=不一致。
//
// 检查项：
//   A. ghost     —— 广告了但没注册（AI 调即 METHOD_NOT_FOUND；2026-10-15 的 5 个 *Geo 属此类）
//   B. invisible —— 注册了但没广告（AI 永远发现不了）
//   C. target    —— 注册臂指向的方法/类在源码里不存在（断链 / 拼写错）
//   D. nodesc    —— 广告了但没有描述（listMethods 无描述 → AI 不知道参数形状）
// 用法：node tools/check-method-names.js            仅静态（无需 C3D）
//       node tools/check-method-names.js --live     + 真机 listMethods 交叉校验（需插件在线）
const fs = require('fs');
const path = require('path');
const net = require('net');

const ARGV = process.argv.slice(2);
const LIVE = ARGV.includes('--live');
const PORT = (() => { try { return JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'config.json'), 'utf8').replace(/^\uFEFF/, '')).bridgePort || 8080; } catch { return 8080; } })();

const SRC = path.join(__dirname, '..', 'plugin', 'AcBridge-v24', 'src');
const files = fs.readdirSync(SRC).filter((f) => f.endsWith('.cs'));
const read = (f) => fs.readFileSync(path.join(SRC, f), 'utf8');

// ---- 逐文件扫描：类声明 + 方法声明（关联到「最近的先前类」；命令类基本一类一文件）----
const staticClasses = new Set();          // 任意 partial 声明为 static 的类
const methodsByClass = new Map();          // class -> [{name, pub, taskObj}]
// 注意：用「非标识符边界」而非「行首」——源码里存在 `}  public static ... (` 同行写法。
const reDecl = /(?:^|[^A-Za-z0-9_])(public|internal|private|protected)?\s*static\s+(?:partial\s+)?(?:class\s+(\w+)|(?:async\s+)?([A-Za-z_][\w<>?,\.\[\]]*)\s+(\w+)\s*\()/g;
for (const f of files) {
  const s = read(f);
  let m, cur = null;
  while ((m = reDecl.exec(s))) {
    if (m[2]) { cur = m[2]; staticClasses.add(cur); if (!methodsByClass.has(cur)) methodsByClass.set(cur, []); continue; }
    if (!m[4]) continue;
    if (!cur) continue;
    methodsByClass.get(cur).push({ name: m[4], pub: m[1] === 'public', taskObj: m[3].trim() === 'Task<object?>' });
  }
}

// ---- 反射广告名（严格镜像 ListMethodsAsync 的反射条件：public static Task<object?> *Async）----
const reflected = new Set();
for (const meths of methodsByClass.values()) {
  for (const md of meths) {
    if (!md.pub || !md.taskObj || !md.name.endsWith('Async')) continue;
    const base = md.name.slice(0, -5);
    if (!base) continue;
    reflected.add(base[0].toLowerCase() + base.slice(1));
  }
}
reflected.delete('dispatch'); // CommandDispatcher.DispatchAsync 是内部入口，listMethods 里也删掉

// ---- 解析 RegisteredNameOverrides / AdditionalRegisteredNames（从源码，勿硬编码）----
const dispSrc = read('CommandDispatcher.cs');
const overrides = {};
{
  const b = dispSrc.match(/RegisteredNameOverrides\s*=\s*new\(\)\s*\{([\s\S]*?)\n\s*\};/);
  if (b) for (const m of b[1].matchAll(/\["(\w+)"\]\s*=\s*new\[\]\s*\{([^}]*)\}/g)) {
    overrides[m[1]] = [...m[2].matchAll(/"(\w+)"/g)].map((x) => x[1]);
  }
}
const additional = new Set();
{
  const b = dispSrc.match(/AdditionalRegisteredNames\s*=\s*\n?\s*\{([\s\S]*?)\n\s*\};/);
  if (b) for (const m of b[1].matchAll(/"(\w+)"/g)) additional.add(m[1]);
}

// ---- 广告集合 = 反射名(改名后) ∪ additional ----
const advertised = new Set();
for (const n of reflected) for (const x of overrides[n] || [n]) advertised.add(x);
for (const x of additional) advertised.add(x);
advertised.delete('dispatch');

// ---- 注册集合（switch 臂： "name" => 目标 ）----
const arms = new Map();
{
  const b = dispSrc.match(/return method switch\s*\{([\s\S]*?)\n\s*\};/);
  if (!b) { console.error('FATAL: 找不到 dispatcher switch 块'); process.exit(1); }
  for (const m of b[1].matchAll(/"(\w+)"\s*=>\s*([^\n]+)/g)) arms.set(m[1], m[2].trim().replace(/,\s*$/, ''));
}
const registered = new Set(arms.keys());

// ---- 差异 ----
const ghost = [...advertised].filter((n) => !registered.has(n));
const invisible = [...registered].filter((n) => !advertised.has(n));

// 断链：注册臂目标 Class.Method（无 Class. 前缀视为同类 CommandDispatcher）
const targetBroken = [];
for (const [name, target] of arms) {
  const m = target.match(/(?:^|[(<])\s*([A-Za-z_]\w*)\.([A-Za-z_]\w*)\s*\(/) || target.match(/^([A-Za-z_]\w*)\s*\(\s*\)/);
  let cls, meth;
  if (m && m[2]) { cls = m[1]; meth = m[2]; }
  else if (m && m[1]) { cls = 'CommandDispatcher'; meth = m[1]; }
  else { targetBroken.push(name + ' -> ' + target.slice(0, 60)); continue; }
  if (cls !== 'CommandDispatcher' && !staticClasses.has(cls)) { targetBroken.push(name + ' -> ' + cls + ' (类不存在/非静态)'); continue; }
  const pool = (methodsByClass.get(cls) || []).map((x) => x.name);
  if (!pool.includes(meth)) targetBroken.push(name + ' -> ' + cls + '.' + meth + ' (方法不存在)');
}

// ---- 描述覆盖（zh 内联表 + en 表）----
const descKeys = new Set([...dispSrc.matchAll(/\["(\w+)"\]\s*=\s*"/g)].map((m) => m[1]));
let descKeysEn = new Set();
try { descKeysEn = new Set([...read('CommandDescsEn.cs').matchAll(/\["(\w+)"\]\s*=\s*"/g)].map((m) => m[1])); } catch {}
const noDesc = [...advertised].filter((n) => !descKeys.has(n) && !descKeysEn.has(n));

// ---- 真机交叉校验（--live）：拿插件真实 listMethods 与静态推演比对 ----
function tcp(method, params, timeout) {
  return new Promise((resolve) => {
    const c = new net.Socket(); let done = false;
    const fin = (v) => { if (!done) { done = true; c.destroy(); resolve(v); } };
    c.connect(PORT, '127.0.0.1', () => c.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) + '\n'));
    let b = ''; c.on('data', (d) => (b += d.toString()));
    c.on('close', () => { try { const r = JSON.parse(b); fin(r.error ? { ok: false, error: r.error.message || String(r.error) } : { ok: true, result: r.result }); } catch { fin({ ok: false, error: 'BAD_JSON: ' + String(b).slice(0, 100) }); } });
    c.on('error', (e) => fin({ ok: false, error: e.message }));
    setTimeout(() => fin({ ok: false, error: 'TIMEOUT' }), timeout || 20000);
  });
}

async function liveCheck() {
  const health = await tcp('getCivil3DHealth', {}, 8000);
  if (!health.ok || (health.result && health.result.connected === false)) return { skip: '插件不在线 (:' + PORT + ')' };
  const r = await tcp('listMethods', {}, 20000);
  if (!r.ok) return { skip: 'listMethods 失败: ' + r.error };
  const { methods, descriptions } = r.result;
  const live = new Set(methods);
  return {
    live,
    liveExtra: [...live].filter((n) => !advertised.has(n)),   // 真机广告了但静态没推演出来（反射规则漂移）
    staticExtra: [...advertised].filter((n) => !live.has(n)), // 静态推演有但真机没给
    liveNoDesc: methods.filter((m) => !(descriptions && descriptions[m])),
  };
}

// ---- 报告 ----
if (process.argv.includes('--json')) {
  console.log(JSON.stringify({ advertised: advertised.size, registered: registered.size, ghost: ghost.length, invisible: invisible.length, targetBroken: targetBroken.length, noDesc: noDesc.length, names: [...advertised].sort(), registeredNames: [...registered].sort() }));
  process.exit(0);
}


(async () => {
console.log('== 清单 vs dispatcher 一致性 ==');
console.log('  静态类:' + staticClasses.size + '  反射广告名:' + reflected.size + '  overrides:' + Object.keys(overrides).length + '  additional:' + additional.size);
console.log('  广告名(listMethods):' + advertised.size + '  注册名(switch):' + registered.size);
console.log('');
let problem = ghost.length + invisible.length + targetBroken.length + noDesc.length;
if (ghost.length) { console.log('A. ghost 广告未注册 (' + ghost.length + ') [FAIL]:'); ghost.forEach((n) => console.log('   ' + n)); }
if (invisible.length) { console.log('B. invisible 注册未广告 (' + invisible.length + ') [FAIL]:'); invisible.forEach((n) => console.log('   ' + n)); }
if (targetBroken.length) { console.log('C. target 断链 (' + targetBroken.length + ') [FAIL]:'); targetBroken.forEach((n) => console.log('   ' + n)); }
if (noDesc.length) { console.log('D. nodesc 无描述 (' + noDesc.length + ') [FAIL]:'); noDesc.forEach((n) => console.log('   ' + n)); }

if (LIVE) {
  const L = await liveCheck();
  if (L.skip) { console.log('E. live 交叉校验: SKIP — ' + L.skip); }
  else {
    const lp = L.liveExtra.length + L.staticExtra.length + L.liveNoDesc.length;
    problem += lp;
    console.log('E. live 交叉校验 [真机 ' + L.live.size + ' vs 静态 ' + advertised.size + ']: ' + (lp ? 'FAIL' : 'PASS'));
    if (L.liveExtra.length) { console.log('   真机多出(静态没推演出) (' + L.liveExtra.length + '):'); L.liveExtra.forEach((n) => console.log('     ' + n)); }
    if (L.staticExtra.length) { console.log('   静态多出(真机没给) (' + L.staticExtra.length + '):'); L.staticExtra.forEach((n) => console.log('     ' + n)); }
    if (L.liveNoDesc.length) { console.log('   真机无描述 (' + L.liveNoDesc.length + '):'); L.liveNoDesc.slice(0, 30).forEach((n) => console.log('     ' + n)); }
    if (!lp) console.log('   真机清单 == 静态推演，且真机全有描述。');
  }
} else {
  console.log('(未加 --live，跳过真机交叉校验)');
}

if (!problem) console.log('结果: PASS — 广告面 == 可调用面' + (LIVE ? '，静态与真机一致' : '') + '，且全有描述。');
console.log('');
console.log(problem ? ('一致性: FAIL (' + problem + ' 处)') : '一致性: PASS');
process.exit(problem ? 1 : 0);
})();

