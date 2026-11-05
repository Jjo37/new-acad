#!/usr/bin/env node
/**
 * release-gate.js —— 发布门禁（2026-10-10，D-054/058）
 * 把散落的自检串成一条命令；任一步失败 → 退出码 1。
 *
 * 用法:
 *   node tools/release-gate.js              # 无 CAD 门禁（语法 / 版本一致 / 描摹落地链路）
 *   node tools/release-gate.js --with-cad   # 额外跑插件的 smoke-test（需 C3D + 插件在线）
 *
 * 设计：不依赖 CAD 的都能跑；CAD 相关只在 --with-cad（或探测到 :8080 在线）时执行。
 */
const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const net = require('net');

const ROOT = path.join(__dirname, '..');
const NODE = process.execPath;
const WITH_CAD = process.argv.includes('--with-cad');
const PRECOMMIT = process.argv.includes('--precommit');   // 2026-10-15: 提交前钩子用（只跑只读检查，不写文件、不编译）
// 2026-10-15: 端口从 config.json 读（与插件/relay 一致），原来写死 8080 → 改 bridgePort 后门禁永远 SKIP
const BRIDGE_PORT = (() => { try { return JSON.parse(fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8').replace(/^\uFEFF/, '')).bridgePort || 8080; } catch { return 8080; } })();
const results = [];
// 同步端口探测（step 回调里是同步执行，不能用 await 版 portOpen）
function portOpenSync(port) {
  const code = "const n=require('net');const s=n.connect(" + port + ",'127.0.0.1');" +
    "s.on('connect',()=>{s.destroy();process.exit(0)});s.on('error',()=>process.exit(1));" +
    'setTimeout(()=>process.exit(1),1500)';
  const r = spawnSync(NODE, ['-e', code], { encoding: 'utf8', timeout: 6000 });
  return r.status === 0;
}

function step(name, fn) {
  process.stdout.write('▶ ' + name + ' ... ');
  try { const detail = fn(); console.log('PASS' + (detail ? '  (' + detail + ')' : '')); results.push([name, true, String(detail || '')]); }
  catch (e) { console.log('FAIL: ' + String(e.message || e).slice(0, 160)); results.push([name, false, String(e.message || e).slice(0, 300)]); }
}
// 2026-10-21: 落一份机器可读报告 —— 否则"跑过全绿"只是我的一句话，别人无法复核。
function writeGateReport() {
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
    let head = '';
    try { head = String(require('child_process').execFileSync('git', ['-C', ROOT, 'rev-parse', '--short', 'HEAD'])).trim(); } catch (e) { /* not a git checkout */ }
    const fail = results.filter(([, ok]) => !ok).length;
    const report = {
      at: new Date().toISOString(),
      version: pkg.version,
      mode: WITH_CAD ? 'with-cad' : (PRECOMMIT ? 'precommit' : 'local'),
      head,
      total: results.length,
      passed: results.length - fail,
      failed: fail,
      steps: results.map(([name, ok, detail]) => ({ name, ok, detail: detail || '' })),
    };
    fs.mkdirSync(path.join(ROOT, 'docs', 'audit'), { recursive: true });
    fs.writeFileSync(path.join(ROOT, 'docs', 'audit', 'gate-report.json'), JSON.stringify(report, null, 2) + String.fromCharCode(10));
  } catch (e) { console.log('[WARN] gate report not written: ' + e.message); }
}

function walk(dir, pred, acc = []) {
  let es; try { es = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return acc; }
  for (const e of es) { const p = path.join(dir, e.name); if (/node_modules|\bdist\b|dist-bundle|\.git|exchange|\bbin\b|\bbuild\b/.test(p)) continue; if (e.isDirectory()) walk(p, pred, acc); else if (pred(e.name)) acc.push(p); }
  return acc;
}
function portOpen(port) {
  return new Promise((res) => { const s = net.connect(port, '127.0.0.1'); s.setTimeout(800); s.on('connect', () => { s.destroy(); res(true); }); s.on('error', () => res(false)); s.on('timeout', () => { s.destroy(); res(false); }); });
}

(async () => {
  // 1. 语法
  step('语法检查 (server + tools 的 .js)', () => {
    const files = [...walk(path.join(ROOT, 'server'), (n) => n.endsWith('.js')), ...walk(path.join(ROOT, 'tools'), (n) => n.endsWith('.js'))];
    let bad = [];
    for (const f of files) { const r = spawnSync(NODE, ['--check', f], { encoding: 'utf8' }); if (r.status !== 0) bad.push(path.relative(ROOT, f) + ': ' + (r.stderr || '').split('\n')[0]); }
    if (bad.length) throw new Error('\n    ' + bad.slice(0, 5).join('\n    '));
    return files.length + ' 文件';
  });

  // 2. 版本一致性
  step('版本一致 (sync-version --check)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'sync-version.js'), '--check'], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error((r.stdout || '') + (r.stderr || ''));
    return 'canonical 一致';
  });

  // 2b. 打包清单互校（2026-10-15）
  step('打包清单互校 (check-manifests)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-manifests.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).slice(0, 200));
    return '清单条目都在';
  });

  // 2c. 文档覆盖（2026-10-15）——防“功能有、文档没有”
  step('文档覆盖检查 (check-doc-coverage)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-doc-coverage.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return '用户可见功能都有文档';
  });

  // 2c6. decision records (2026-10-21): ADRs must exist, be evidenced and indexed
  step('decision records (check-adr)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-adr.js')], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return (r.stdout || '').trim();
  });

  // 2c7. regression matrix must be regenerated, not hand-edited (2026-10-21)
  step('regression matrix (gen --check)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'gen-regression-matrix.js'), '--check'], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return (r.stdout || '').trim();
  });

  // 2c3. capability coverage contract (2026-10-21): new method/tool/endpoint must bring assets
  step('coverage contract (check-coverage-contract)', () => {
    const args = [path.join(ROOT, 'tools', 'check-coverage-contract.js')];
    if (!fs.existsSync(path.join(ROOT, 'server', 'mcp', 'build', 'tools', 'toolManifest.js'))) args.push('--skip-mcp');
    const r = spawnSync(NODE, args, { encoding: 'utf8', timeout: 120000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 600));
    return 'capability surface matches contract';
  });

  // 2c4. relay endpoint inventory (2026-10-21)
  step('relay endpoints (check-relay-endpoints)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-relay-endpoints.js')], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return 'endpoints documented and in sync';
  });

  // 2c5. red-line tier mapping (2026-10-21): touching a red-line file requires its verification tier
  step('redline tiers (check-redlines)', () => {
    const tiers = WITH_CAD ? 'local,cad' : 'local';
    const mode = PRECOMMIT ? 'warn' : 'release';
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-redlines.js'), '--tiers', tiers, '--mode', mode], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 500));
    return 'tiers: ' + tiers;
  });

  // 2c2. doc numeric claims (2026-10-21): numbers written in docs == live inventory
  step('doc numeric claims (check-doc-claims)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-doc-claims.js')], { encoding: 'utf8', timeout: 180000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 500));
    return 'docs numbers == inventory';
  });

  // 2c2. 支持宿主契约（2026-10-27）——声称支持 ≠ 真的验过（v1.11.0 就栽在这）
  step('支持宿主契约 (check-supported-hosts)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-supported-hosts.js')], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return 'claims ⊆ verified hosts';
  });

  // 2c3. 服务商中性（2026-10-29）——文档不夸厂商、预设不排优先级、代码不静默兜底
  step('服务商中性 (check-provider-neutrality)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-provider-neutrality.js')], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return '文档/预设/代码 neutral（变异测试 tools/test-provider-neutrality.js）';
  });

  // 2c4. 错误码一致性（2026-10-29）—— 域码必须走同一张表；映射表改完忘了改调用点就是今天的事故
  step('错误码一致性 (check-error-codes)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-error-codes.js')], { encoding: 'utf8', timeout: 60000 });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return '域码/数字码一致（变异测试 tools/test-check-error-codes.js）';
  });

  // 2d. 已知问题台账（2026-10-15）——防“清单四个版本没动”
  step('已知问题台账 (check-known-issues)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-known-issues.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return '台账结构 + 每版 diff';
  });

  // 2e. 工具面一致性（2026-10-16）——防“广告了却调不到”的幽灵方法 / 注册未广告
  step('工具面一致性 (check-method-names)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-method-names.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    const m = (r.stdout || '').match(/广告名\(listMethods\):(\d+)\s+注册名\(switch\):(\d+)/);
    return m ? ('广告==注册 ' + m[1] + ' 个') : '广告面==可调用面';
  });

  // 2h. 命令通道清单（2026-10-24）——文档写“12 个方法走命令通道”写了几个版本，实际成员早已换掉
  step('命令通道清单 (check-command-channel)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-command-channel.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 500));
    return (r.stdout || '').trim().replace(/^\s+/, '');
  });

  // 2f. 宿主状态守卫链路（2026-10-16）——防“打断投递退化成无效 / 看门狗空闲即解锁分支被删”
  step('宿主守卫链路 (check-host-guards)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-host-guards.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return (r.stdout || '').trim().replace(/^宿主守卫链路 OK —— /, '');
  });
  // 2g. 路径解析（2026-10-19）——防"装到别的目录/盘就失效"（bundle 副本被误认成根目录）
  step('路径解析 (check-paths)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-paths.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return (r.stdout || '').trim().replace(/^路径解析门禁 OK —— /, '');
  });
  // 2h. 发版同步白名单覆盖（2026-10-19）——防"新增 server 模块忘了加白名单 → 用户端 MODULE_NOT_FOUND"
  step('同步白名单覆盖 (check-sync-coverage)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-sync-coverage.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return (r.stdout || '').trim().replace(/^同步白名单覆盖 OK —— /, '');
  });
  // 2i. i18n 棘轮（2026-10-19）——防"新增了未本地化的用户可见中文"
  step('i18n 棘轮 (i18n-audit --ratchet)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'i18n-audit.js'), '--ratchet'], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return (r.stdout || '').trim().replace(/^i18n 棘轮 OK —— /, '');
  });
  // 2j. .ps1 编码守卫（2026-10-19：UTF-8 无 BOM 的中文注释会被 PS 5.1 按 GBK 读成伪 <# #>，吞掉代码）
  step('.ps1 编码守卫 (check-ps-ascii)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-ps-ascii.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 400));
    return (r.stdout || '').trim().replace(/^\.ps1 编码守卫 OK —— /, '');
  });
  // pre-commit 模式：到此为止（只读检查跑完）——提交前钩子用，不动文件
  if (PRECOMMIT) {
    writeGateReport();
    const f = results.filter(([, ok]) => !ok).length;
    console.log('\n' + (f ? '✗ 提交前检查未通过：' + f + '/' + results.length : '✓ 提交前检查通过：' + results.length + '/' + results.length));
    process.exit(f ? 1 : 0);
  }

  // 2k. 包内容体检（2026-10-21）——防"发出去的包里带着我们自己的数据/记忆"
  step('包内容体检 (check-package-contents)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-package-contents.js')], { encoding: 'utf8' });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status === 2) return 'SKIP（本机无 dist/，打包前才会存在）';
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || out.slice(0, 300));
    return (out.split(/\r?\n/).filter((l) => /package contents OK/.test(l)).pop() || 'ok').replace(/^\s*/, '');
  });


  // 3. 描摹落地链路（桩）
  step('描摹落地链路自测 (trace-draw-selftest)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'trace-draw-selftest.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error((r.stdout || '') + (r.stderr || ''));
    const m = (r.stdout || '').match(/blocks=(\d+) req\/eff=(\d+)\/(\d+)/);
    return m ? ('blocks=' + m[1] + ' req/eff=' + m[2] + '/' + m[3]) : 'ok';
  });

  // 4. 缺陷清单可生成
  step('缺陷清单生成 (gen-deficiencies)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'gen-deficiencies.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error((r.stderr || '').slice(0, 120));
    return (r.stdout || '').trim().split('\n').pop();
  });

  // 5. 插件编译（默认跑；--fast 跳过）
  // 2026-10-20 审计：CAD 开着时 bundle 的 DLL 被锁，build 无法更新它。
  // 默认严格（build 会 FAIL），只有明确接受"跑旧插件"时才放行。
  const cadUpFront = await portOpen(BRIDGE_PORT);
  const allowLocked = WITH_CAD || cadUpFront;
  if (!process.argv.includes('--fast')) {
    // 2026-10-27 (P2.6 S2): clear-scope layering contract, exercised in a sandbox relay.
  step('状态不变量 S2（清空分层契约）', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-state-invariants.js')], { encoding: 'utf8', timeout: 300000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /FAIL/.test(l)).join(' | ').slice(0, 400) || out.slice(-300));
    const sum = (out.match(/合计:.*/) || [''])[0];
    return sum || 'sandbox contract ok';
  });
  // 2026-10-28 (P2.6 S3): 判定只有一个入口。行为侧测规则（探针失败不拖垮整单 / 末步失败 / 取消 / local / blocked），
  // 结构侧测「没有第二份判定」（内联表达式、插件自重算都会 FAIL）。
  step('判定单入口·行为 (test-judgement)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-judgement.js')], { encoding: 'utf8', timeout: 180000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^FAIL/.test(l.trim())).join(' | ').slice(0, 300) || '非 0 退出');
    const m = out.match(/合计: (\d+) passed/);
    return m ? (m[1] + ' passed') : 'ok';
  });
  step('判定单入口·结构 (check-judgement-single-entry)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-judgement-single-entry.js')], { encoding: 'utf8', timeout: 120000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
    return (out.split(/\r?\n/).pop() || 'ok').replace(/^判定单入口 OK —— /, '');
  });
  // 2026-10-28 (P2.6 S4): 破坏性动作必须过同一个确认守卫。清单 docs/audit/destructive-actions.json 是唯一真相；
  // 未登记的破坏性入口、自建确认状态、多个守卫实例都会 FAIL。
  step('破坏性动作守卫 (check-destructive-guard)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-destructive-guard.js')], { encoding: 'utf8', timeout: 120000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
    return (out.split(/\r?\n/).pop() || 'ok').replace(/^破坏性动作守卫 OK —— /, '');
  });
  // 2026-10-28 (P2.6 S5): 槽位抽取是白名单制。行为侧测三条硬保证（句柄/几何大数组/内部字段），
  // 结构侧测「判据只有一份」——抽取器自带第二套黑名单、或别处再定义 extractSlots 都会 FAIL。
  step('槽位白名单·行为 (test-slot-policy)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-slot-policy.js')], { encoding: 'utf8', timeout: 180000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^FAIL/.test(l.trim())).join(' | ').slice(0, 300) || '非 0 退出');
    const m = out.match(/合计: (\d+) passed/);
    return m ? (m[1] + ' passed') : 'ok';
  });
  step('槽位白名单·结构 (check-slot-policy)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-slot-policy.js')], { encoding: 'utf8', timeout: 120000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
    return (out.split(/\r?\n/).pop() || 'ok').replace(/^槽位判据单来源 OK —— /, '');
  });
  // 2026-10-28 (P2.6 S6): 换图/关图只有一个处置点。结构侧测『谁在清日志/谁在订阅文档事件』——
  // 任何人绕开 DocumentLifecycle 自己清、或另开一个事件订阅，都会 FAIL。
  step('文档生命周期·结构 (check-doc-lifecycle)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-doc-lifecycle.js')], { encoding: 'utf8', timeout: 120000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
    return (out.split(/\r?\n/).pop() || 'ok').replace(/^文档生命周期单入口 OK —— /, '');
  });
  // 2026-10-28 (P2.6 S7): 时间只有一种存法。行为侧跨时区验证（同一时刻不同 TZ 落盘必须一致），
  // 结构侧禁掉「自己拼本地时间」—— 本地取值器只允许出现在 time-policy.js / PluginTime.cs。
  step('时间口径·行为 (test-time-policy)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-time-policy.js')], { encoding: 'utf8', timeout: 180000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^FAIL/.test(l.trim())).join(' | ').slice(0, 300) || '非 0 退出');
    const m = out.match(/合计: (\d+) passed/);
    return m ? (m[1] + ' passed') : 'ok';
  });
  step('时间口径·结构 (check-time-policy)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-time-policy.js')], { encoding: 'utf8', timeout: 120000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /^\s+x /.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
    return (out.split(/\r?\n/).pop() || 'ok').replace(/^时间口径单来源 OK —— /, '');
  });
  step('插件编译 (build-plugin.ps1)', () => {
      const buildArgs = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(ROOT, 'build-plugin.ps1')];
      if (allowLocked) buildArgs.push('-AllowBundleLocked');
      const r = spawnSync('powershell', buildArgs, { encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      const errs = out.split(/\r?\n/).filter((l) => /error CS/.test(l));
      if (errs.length) throw new Error(errs.slice(0, 3).join(' | '));
      const dll = path.join(ROOT, 'plugin', 'AcBridge-v24', 'Civil3DMcpPlugin.dll');
      const sz = fs.existsSync(dll) ? fs.statSync(dll).size : 0;
      if (sz < 200000) throw new Error('DLL 体积异常 ' + sz + 'B（可能是 4KB 空壳）');
      return '0 error, DLL ' + sz + 'B';
    });
  } else {
    console.log('▶ 插件编译 ... SKIP（--fast）');
  }

  // 5a2. 纯 AutoCAD 宿主探针（2026-10-27）：无 Aecc 进程内强制 JIT + listMethods 断言
  if (!process.argv.includes('--fast')) {
    step('纯 AutoCAD 宿主探针 (HostModeProbe)', () => {
      const r = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(ROOT, 'tools', 'run-host-probe.ps1'), '-Dev', ROOT], { encoding: 'utf8', timeout: 300000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status !== 0) throw new Error(out.split(/\r?\n/).slice(-4).join(' | ').slice(0, 400));
      const jit = (out.match(/\[jit\][^\n]*/) || [''])[0];
      const list = (out.match(/\[list\][^\n]*/) || [''])[0];
      return (jit + ' ' + list).trim() || 'probe passed';
    });
  }

  // 5b. running build stamp (2026-10-21): syncing the bundle is not enough, the live host must run it
  if (!process.argv.includes('--fast')) {
    step('running plugin stamp (check-running-stamp)', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-running-stamp.js')], { encoding: 'utf8', timeout: 60000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status === 2) return 'SKIP: host offline';
      if (r.status !== 0) throw new Error(out.slice(0, 220));
      return out.replace(/^\s*/, '');
    });
  }
  // 5c. C# 纯函数单测（单位系统）—— 不需 CAD（与插件共用 UnitSystem.cs）
  step('C# 单位单测 (UnitSystemTests)', () => {
    const r = spawnSync('dotnet', ['run', '--project', path.join(ROOT, 'plugin', 'AcBridge-v24', 'tests', 'UnitSystemTests')], { encoding: 'utf8', timeout: 300000 });
    const out = (r.stdout || '') + (r.stderr || '');
    if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).slice(-3).join(' | ').slice(0, 200));
    const m = out.match(/UnitSystemTests: (\d+) passed, (\d+) failed/);
    return m ? (m[1] + ' passed') : 'ok';
  });

  // 2k. 覆盖保护（2026-10-20 审计 D08：AI 不得自行 overwrite，Bro 拍板安全第一）
  step('覆盖保护 (check-overwrite-guard)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-overwrite-guard.js')], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300));
    return (r.stdout || '').trim().replace(/^覆盖保护 OK —— /, '');
  });

  // 2l. C# 文件边界套件（2026-10-20 审计 F7 -> 现已阻断）
  // 2026-10-20 Bro 拍板 C 方案（读写分级）后，断言与实现一致，升级为阻断：
  //   导出全盘放行 / 导入限定 roots（默认 用户目录 + 程序目录 + exchange\import）。
  // 之前是 WARN 且套件在第一个断言就中断 —— 等于那 20 个检查点一次都没跑过。
  const fbhProj = path.join(ROOT, 'server', 'mcp', 'tests', 'FileBoundaryHarness', 'FileBoundaryHarness.csproj');
  if (fs.existsSync(fbhProj)) {
    step('C# 文件边界套件 (FileBoundaryHarness)', () => {
      // 2026-10-20：dotnet run 会复用陈旧程序集（实测改完源码仍跑旧逻辑，结果假通过/假失败）。
      // 先强制 Rebuild，再 --no-build 跑，保证测的是当前源码。
      const build = spawnSync('dotnet', ['build', fbhProj, '-t:Rebuild', '-v:q', '--nologo'], { encoding: 'utf8', timeout: 600000 });
      if (build.status !== 0) {
        const msg = ((build.stdout || '') + (build.stderr || '')).trim().split(/\r?\n/).filter((l) => /error/.test(l)).slice(0, 2).join(' | ');
        throw new Error('重编译失败: ' + (msg || 'dotnet build 非 0').slice(0, 200));
      }
      const r = spawnSync('dotnet', ['run', '--no-build', '--project', fbhProj], { encoding: 'utf8', timeout: 600000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status !== 0) {
        const msg = out.split(/\r?\n/).filter((l) => l.trim()).slice(-2).join(' | ').slice(0, 200);
        throw new Error(msg || 'dotnet run 非 0 退出');
      }
      return out.split(/\r?\n/).filter((l) => l.trim()).pop().slice(0, 110);
    });
  } else {
    console.log('▶ C# 文件边界套件 ... SKIP（无 FileBoundaryHarness.csproj）');
  }

  // 5b. enc 授权契约自检（仅在授权发布环境：私钥 + 自检工具都在 —— dev 上自动跳过）
  const licTool = path.join(ROOT, 'tools', 'license-contract-test.js');
  const licKey = path.join(ROOT, 'server', 'license-private-key.pem');
  if (fs.existsSync(licTool) && fs.existsSync(licKey)) {
    step('enc 授权契约自检 (license-contract-test)', () => {
      const r = spawnSync(NODE, [licTool], { encoding: 'utf8', timeout: 300000 });
      if (r.status !== 0) {
        const errs = ((r.stdout || '') + (r.stderr || '')).split(/\r?\n/).filter((l) => l.indexOf('✗') >= 0);
        throw new Error(errs.slice(0, 3).join(' | ') || '非 0 退出');
      }
      return '服务端格式 + 插件验签(含混淆产物) OK';
    });
  } else {
    console.log('▶ enc 授权契约自检 ... SKIP（非授权发布环境）');
  }

  // 2026-10-20 审计 D08：把"没接线"的测试接上（这两步不需要 CAD）
  {
    const vitestBin = path.join(ROOT, 'server', 'mcp', 'node_modules', 'vitest', 'vitest.mjs');
    if (fs.existsSync(vitestBin)) {
      step('server 单元测试 (vitest)', () => {
        const r = spawnSync(NODE, [vitestBin, 'run'], { cwd: path.join(ROOT, 'server', 'mcp'), encoding: 'utf8', timeout: 900000 });
        const out = (r.stdout || '') + (r.stderr || '');
        if (r.status !== 0) {
          const bad = out.split(/\r?\n/).filter((l) => /FAIL |AssertionError/.test(l)).slice(0, 3).join(' | ');
          throw new Error((bad || 'vitest 非 0 退出').replace(/\x1b\[[0-9;]*m/g, '').slice(0, 260));
        }
        const m = out.match(/Tests\s+[0-9]+ passed/);
        return m ? m[0].replace(/\s+/g, ' ') : 'ok';
      });
    } else {
      console.log('▶ server 单元测试 (vitest) ... SKIP（未安装 node_modules）');
    }
    // 2026-10-28: 依赖面门禁 —— 42 步里原先没人审依赖，而 server/mcp 的 node_modules 随包发布。
    // 判据在 tools/dependency-audit-policy.json，豁免在 tools/dependency-audit-allowlist.json（必须带到期日）。
    step('依赖漏洞 (check-dependency-audit)', () => {
      const tool = path.join(ROOT, 'tools', 'check-dependency-audit.js');
      if (!fs.existsSync(tool)) throw new Error('缺 tools/check-dependency-audit.js');
      const r = spawnSync(NODE, [tool], { cwd: ROOT, encoding: 'utf8', timeout: 300000 });
      const out = ((r.stdout || '') + (r.stderr || '')).replace(/\x1b\[[0-9;]*m/g, '');
      if (r.status !== 0) {
        const fails = out.split(/\r?\n/).filter((l) => /FAIL/.test(l)).slice(0, 3).join(' | ');
        throw new Error(fails.slice(0, 260) || ('非 0 退出:' + String(r.status)));
      }
      const line = out.split(/\r?\n/).find((l) => l.trim().startsWith('- '));
      return line ? line.trim() : 'ok';
    });

    step('图片/上下文护栏回归 (test-image-ctx-guard)', () => {
      const script = path.join(ROOT, 'server', 'test-image-ctx-guard.js');
      if (!fs.existsSync(script)) return 'SKIP（无脚本）';
      const r = spawnSync(NODE, [script], { encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /FAIL/.test(l)).slice(0, 2).join(' | ').slice(0, 200) || '非 0 退出');
      const m = out.match(/ALL PASS \((\d+)\)/);
      return m ? ('ALL PASS ' + m[1]) : 'ok';
    });
  }
  // 2026-10-20 审计 D14：dist/ 若存在就验一遍"干净 + 能跑"。
  // 背景：仓库里那份 dist 曾是过期产物（缺 i18n/messages.json 和 2 个 server 模块），
  // 谁把它当成品发出去，用户端 relay 会直接 MODULE_NOT_FOUND。dist 不入库，
  // 所以这里失败只警告不阻断（dev 仓里 dist 过期是常态），但必须让人看见。
  {
    const distDir = path.join(ROOT, 'dist');
    if (!fs.existsSync(distDir)) {
      console.log('▶ 包体可用性 (check-package) ... SKIP（无 dist/）');
    } else {
      const clean = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-package-clean.js'), distDir], { encoding: 'utf8', timeout: 300000 });
      const runtime = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-package-runtime.js'), distDir], { encoding: 'utf8', timeout: 300000 });
      if (clean.status === 0 && runtime.status === 0) {
        console.log('▶ 包体可用性 (check-package) ... PASS  (干净 + 可运行)');
      } else {
        console.log('⚠ 包体可用性 (check-package) ... WARN（dist/ 存在但不可用，别直接发这个目录）');
        const why = ((clean.stdout || '') + (clean.stderr || '') + (runtime.stdout || '') + (runtime.stderr || '')).split(/\r?\n/).filter((l) => /缺|missing|缺失|✗/.test(l)).slice(0, 3);
        for (const w of why) console.log('    - ' + w.trim().slice(0, 140));
      }
    }
  }
  // 6. CAD 相关（可选）
  // 审计状态（AUDIT-STANDARD v1.0 §10）—— 警告级，永不阻断门禁
  {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-audit-status.js'), '--json'], { encoding: 'utf8', timeout: 60000 });
    let v = null;
    try { v = JSON.parse(r.stdout || "{}"); } catch { }
    if (v && v.verdict === "OK") {
      console.log('▶ 审计状态 (check-audit-status) ... PASS  (' + v.reds.length + ' 个红区)');
    } else {
      console.log('⚠ 审计状态 (check-audit-status) ... ' + ((v && v.verdict) || 'UNKNOWN') + '（警告，不阻断）');
      if (v) {
        console.log('    上次审计: ' + (v.lastAudit || '无') + (v.daysSince !== null ? '  距今 ' + v.daysSince + ' 天' : ''));
        for (const rs of (v.reasons || []).slice(0, 4)) console.log('    - ' + rs);
        for (const nd of (v.needRetest || [])) console.log('    待复测: ' + nd.id + ' ' + nd.name + '（' + nd.line + '）');
      }
    }
  }

  const cadUp = cadUpFront !== undefined ? cadUpFront : await portOpen(BRIDGE_PORT);
  if (WITH_CAD || cadUp) {
    // 2026-10-21: the heavy suites used to run back-to-back and cascade into timeouts once
    // the host command context was still occupied. Wait for idle before each of them.
    const awaitIdle = () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'wait-host-idle.js'), '--timeout', '120'], { encoding: 'utf8', timeout: 160000 });
      return r.status === 0;
    };
    // 2026-10-28: 真机档的前置 —— 之前「C3D 开着但停在开始页」会让整批真机回归报 NO_DRAWING（v1.11.0 发布时踩过），
    // release-all 里已有这一步，但直接跑 release-gate --with-cad 没有，等于每次还得人先开图。
    step('真机前置·保证有图纸 (ensure-drawing)', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'ensure-drawing.js')], { encoding: 'utf8', timeout: 120000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status !== 0) throw new Error(out.split(/\r?\n/).pop().slice(0, 200) || 'ensure-drawing 非 0 退出');
      return (out.split(/\r?\n/).pop() || 'ok').replace(/^\[ensure-drawing\]\s*/, '');
    });
    step('C3D 插件 smoke-test', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'smoke-test.js')], { encoding: 'utf8', timeout: 120000 });
      if (r.status !== 0) throw new Error('smoke-test 非 0 退出');
      return 'ok';
    });
    // 2026-10-28: 纯 AutoCAD 宿主冒烟（7 步，见 tools/smoke-plain-acad.js）。C3D 上它会自己 SKIP ——
    // 保留这一步是为了留痕：真机档里能看到「这一版没在纯 CAD 上跑过」，而不是靠人记得。
    step('纯 AutoCAD 宿主冒烟 (smoke-plain-acad)', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'smoke-plain-acad.js')], { encoding: 'utf8', timeout: 300000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /FAIL/.test(l)).join(' | ').slice(0, 240) || 'smoke-plain-acad 非 0 退出');
      const m = out.match(/合计: (\d+) passed/);
      const skip = out.split(/\r?\n/).find((l) => /SKIP/.test(l));
      return skip ? skip.trim().slice(0, 120) : (m ? m[1] + ' passed' : 'ok');
    });
    // 2026-10-20 审计 D08：历史 bug 回归（需 relay :19876 + C3D）
    step('描述参数回归 (test-desc-regression)', () => {
      awaitIdle();
      const script = path.join(ROOT, 'server', 'test-desc-regression.js');
      if (!portOpenSync(19876)) return 'SKIP（relay :19876 未在线）';
      if (!fs.existsSync(script)) return 'SKIP（无脚本）';
      const r = spawnSync(NODE, [script], { cwd: path.join(ROOT, 'server'), encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /FAIL/.test(l)).slice(0, 2).join(' | ').slice(0, 200) || '非 0 退出');
      const m = out.match(/(\d+)\s*passed/);
      return m ? (m[1] + ' passed') : 'ok';
    });
    step('C3D 真机回归 (live-regression)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'live-regression.js')], { encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).slice(-3).join(' | ').slice(0, 200));
      const m = out.match(/结果：(\d+) passed, (\d+) failed/);
      return m ? (m[1] + ' passed') : 'ok';
    });
    // 2026-10-20 审计 F2：此套件早已存在但一直没挂门禁（只能手工跑）→ 接进来
    step('C3D 专业域回归 (live-c3d-regression)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'live-c3d-regression.js')], { encoding: 'utf8', timeout: 900000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).slice(-3).join(' | ').slice(0, 200));
      const m = out.match(/(\d+) passed, (\d+) failed/);
      return m ? (m[1] + ' passed / ' + m[2] + ' failed') : 'ok';
    });
    // 2026-10-20 审计 F3：全量方法可达性 + 参数契约（420 方法，约 2 分钟）
    // 2026-10-20 审计：先等宿主空闲再跑整批方法。原因见 tools/wait-host-idle.js 头注释——
    // 命令上下文未释放时读类方法会阻塞（实测 qtyAlignmentLengths 8s 超时，单独调用仅 35ms）。
    step('等待宿主空闲 (wait-host-idle)', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'wait-host-idle.js'), '--timeout', '150'], { encoding: 'utf8', timeout: 200000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim().split(/\r?\n/).pop() || '';
      if (r.status !== 0) throw new Error(out.slice(0, 160) || '宿主未回到空闲');
      return out.replace(/^宿主空闲\s*/, '').slice(0, 100);
    });
    step('工具面全量可达性 (functional-smoke)', () => {
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'functional-smoke.js')], { encoding: 'utf8', timeout: 900000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).slice(-3).join(' | ').slice(0, 200));
      const m = out.match(/只读真跑 OK\s*:\s*(\d+)/);
      return m ? ('只读 ' + m[1] + ' 真跑 OK') : 'ok';
    });
    // 2026-10-20 审计 D15：面板 UI 六项自检（把人工 checklist 自动化，V 5->2）
    step('面板 UI 自检 (panel-selftest)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'panel-selftest.js')], { encoding: 'utf8', timeout: 900000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /^(FAIL|合计)/.test(l.trim())).join(' | ').slice(0, 240) || 'panel-selftest 非 0 退出');
      const m = out.match(/合计: (\d+) passed/);
      return m ? (m[1] + ' passed') : 'ok';
    });
    // 2026-10-28 (P2.6 S1 接线): 面板视图身份必须可断言 —— 10-23 那两条 bug 只能靠人眼看，所以漏了。
    // 判据：视图切换后 view/columns 必须自洽；从日志返回调用流不许残留日志列。
    step('面板视图状态 (palette-view-selftest)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'palette-view-selftest.js')], { encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /FAIL|palette-view-selftest:/.test(l)).join(' | ').slice(0, 300) || 'palette-view-selftest 非 0 退出');
      const m = out.match(/palette-view-selftest: (\d+) passed/);
      return m ? (m[1] + ' passed') : 'ok';
    });
    // 2026-10-28 (P2.6 S6): 换图真事件 —— 走 DocumentActivated，断言日志真被清、跨图快照真被拒。
    step('文档生命周期·真机 (test-doc-lifecycle)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-doc-lifecycle.js')], { encoding: 'utf8', timeout: 300000 });
      const out = ((r.stdout || '') + (r.stderr || '')).trim();
      if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /FAIL|test-doc-lifecycle:/.test(l)).join(' | ').slice(0, 300) || '非 0 退出');
      const m = out.match(/test-doc-lifecycle: (\d+) passed/);
      return m ? (m[1] + ' passed') : 'ok';
    });
    // 2026-10-20 审计 D02：数值准确性（不变量 + WL 金标准）—— 回答"算得对不对"
    // 2026-10-27: the missing tier -- a real user's path (dirty state), not an API path.
  step('用户路径冒烟 (user-path-smoke)', () => {
    const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'user-path-smoke.js')], { encoding: 'utf8', timeout: 300000 });
    const out = ((r.stdout || '') + (r.stderr || '')).trim();
    if (r.status !== 0) throw new Error(out.split(/\r?\n/).filter((l) => /FAIL/.test(l)).join(' | ').slice(0, 400) || out.slice(-300));
    const sum = (out.match(/合计:.*/) || [''])[0];
    const skip = (out.match(/SKIP \| [^\n]*/) || [''])[0];
    return (sum + ' ' + skip).trim();
  });
  step('数值准确性 (accuracy-selftest)', () => {
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'accuracy-selftest.js')], { encoding: 'utf8', timeout: 600000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /^(FAIL|合计)/.test(l.trim())).join(' | ').slice(0, 240) || 'accuracy-selftest 非 0 退出');
      const m = out.match(/合计: (\d+) passed/);
      return m ? (m[1] + ' 项不变量一致') : 'ok';

    });
    // 2026-10-21 P1: tool-call visualisation feed (palette tab data source). Needs relay + model.
    step('tool events (test-tool-events)', () => {
      if (!portOpenSync(19876)) return 'SKIP: relay :19876 offline';
      awaitIdle();
      const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'test-tool-events.js')], { encoding: 'utf8', timeout: 300000 });
      const out = (r.stdout || '') + (r.stderr || '');
      if (r.status !== 0) throw new Error(out.trim().split(/\r?\n/).filter((l) => /FAIL/.test(l)).slice(0, 2).join(' | ').slice(0, 200) || 'non-zero exit');
      const m2 = out.match(/(\d+) passed/);
      return m2 ? (m2[1] + ' passed') : 'ok';
    });
  } else {
    console.log('▶ C3D 插件 smoke-test ... SKIP（:' + BRIDGE_PORT + ' 未在线，加 --with-cad 且开 C3D 再跑）');
  }

  const fail = results.filter(([, ok]) => !ok).length;
  writeGateReport();
  console.log('\n' + (fail ? '✗ 门禁未通过：' + fail + '/' + results.length + ' 失败' : '✓ 门禁通过：' + results.length + '/' + results.length));
  process.exit(fail ? 1 : 0);
})();
