#!/usr/bin/env node
// check-host-evidence.js — 纯 AutoCAD 是"别的电脑"，所以把它的验证变成可携带的证据（2026-10-27）
//
// 问题：纯 AutoCAD 只有另一台机器有，发布流水线跑不到那台机上，于是"纯 CAD 验过了"只能靠人嘴说。
// 做法：在那台机器上跑 tools/smoke-plain-acad.js --evidence <file>，产出一个 JSON，里面带
//       **当前插件 DLL 的 sha256**、宿主类型、逐项结果。把 JSON 拷回本仓库
//       docs/audit/host-evidence/ 之后，本机发布链就能校验"这个构建真的在纯 CAD 上跑过"。
//
// 用法：
//   node tools/check-host-evidence.js                 # 只报告（warn）
//   node tools/check-host-evidence.js --require       # 没有合格证据 -> 退出码 1（发布链用这个）
//   node tools/check-host-evidence.js --dll <path>    # 指定要比对的 DLL（默认仓库插件目录）
//
// 合格判据（全部满足）：
//   1. schema = new-acad.host-evidence.v1
//   2. pluginDllSha256 == 当前 DLL 的 sha256（证据必须对得上这个构建，旧证据无效）
//   3. passed === true 且 skipped === false 且 civilAvailable === false
//   4. generatedAt 在 --max-age-days（默认 60）天以内

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = path.join(__dirname, '..');
const DIR = (() => {
  const i = process.argv.indexOf('--dir');
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : path.join(ROOT, 'docs', 'audit', 'host-evidence');
})();

const arg = (name, def = '') => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : def;
};
const REQUIRE = process.argv.includes('--require');
const MAX_AGE_DAYS = Number(arg('max-age-days', '60')) || 60;
const DLL = arg('dll', path.join(ROOT, 'plugin', 'AcBridge-v24', 'Civil3DMcpPlugin.dll'));

function sha256(file) {
  try { return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'); } catch { return ''; }
}

// Evidence binds to a BUILD, and a build is not byte-reproducible (buildStamp embeds commit +
// timestamp). So the question is "is this the same plugin code?", not "same bytes?":
// the evidence passes if either the DLL is identical, or the plugin source tree at the stamped
// commit is identical to the plugin source tree at HEAD. Without the second rule, one docs-only
// commit after the build invalidates perfectly good cross-machine evidence and people start
// re-running it "because the gate says so" (2026-10-29: plain-AutoCAD evidence from 6a07b40 was
// rejected at HEAD 4616982 although plugin/ had not changed since f1ce116).
function pluginSourceSameAs(commit) {
  if (!commit) return { ok: false, why: 'no stamped commit' };
  const git = (args) => require('child_process').execFileSync('git', ['-C', ROOT].concat(args), { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  try {
    git(['rev-parse', '--verify', commit + '^{commit}']);
  } catch {
    return { ok: false, why: 'stamped commit ' + commit + ' is not in this repo' };
  }
  try {
    const r = require('child_process').spawnSync('git', ['-C', ROOT, 'diff', '--quiet', commit, 'HEAD', '--', 'plugin/'], { encoding: 'utf8' });
    if (r.status === 0) return { ok: true, why: 'plugin/ identical between ' + commit + ' and HEAD' };
    return { ok: false, why: 'plugin/ changed between ' + commit + ' and HEAD' };
  } catch (e) {
    return { ok: false, why: 'git diff failed: ' + e.message };
  }
}

const current = sha256(DLL);
let currentCommit = '';
// Version comes from package.json (single source of truth). Adds the .0 AssemblyFileVersion
// suffix only if the evidence carries a 4-part version.
let pkgVersion = '';
try {
  pkgVersion = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8').replace(/^\uFEFF/, '')).version || '';
} catch { pkgVersion = ''; }
try {
  currentCommit = require('child_process').execFileSync('git', ['-C', ROOT, 'rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim();
} catch { currentCommit = ''; }
if (!current) {
  console.log('  [FAIL] cannot read plugin DLL: ' + DLL);
  process.exit(REQUIRE ? 1 : 0);
}
console.log('  plugin DLL sha256 = ' + current.slice(0, 16) + '…  (' + path.basename(DLL) + ')');

if (!fs.existsSync(DIR)) {
  console.log('  no evidence directory yet: docs/audit/host-evidence/');
  if (REQUIRE) {
    console.log('  FAIL: plain-AutoCAD evidence is required for this build.');
    process.exit(1);
  }
  process.exit(0);
}

const files = fs.readdirSync(DIR).filter((f) => f.endsWith('.json')).sort();
let best = null;
const rejected = [];

for (const f of files) {
  const abs = path.join(DIR, f);
  let j;
  try { j = JSON.parse(fs.readFileSync(abs, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { rejected.push(f + ': unreadable JSON'); continue; }

  const problems = [];
  if (j.schema !== 'new-acad.host-evidence.v1') problems.push('schema mismatch');
  // Acceptance rule: the evidence must describe THIS code. The plugin build stamp embeds the
  // commit sha and a timestamp, and builds are not byte-reproducible, so the commit is the
  // contract; an exact DLL hash match is accepted too (it implies the same build).
  const stampCommit = String(j.buildStamp || '').split('-')[0];
  const shaOk = j.pluginDllSha256 === current;
  let srcOk = { ok: false, why: 'not checked' };
  if (!shaOk && stampCommit) srcOk = pluginSourceSameAs(stampCommit);
  const commitOk = !!stampCommit && (stampCommit === currentCommit || srcOk.ok);
  if (!shaOk && !commitOk) {
    problems.push('dll sha mismatch (' + String(j.pluginDllSha256 || '').slice(0, 12) + '…) and buildStamp commit ' + (stampCommit || '?') + ' is not the same plugin source as HEAD ' + (currentCommit || '?') + ' (' + srcOk.why + ')');
  }
  if (pkgVersion && j.pluginVersion) {
    const v = String(j.pluginVersion);
    const ok = v === pkgVersion || v.startsWith(pkgVersion + '.');
    if (!ok) problems.push('pluginVersion ' + v + ' != package.json ' + pkgVersion);
  }
  if (j.passed !== true) problems.push('passed=' + j.passed);
  if (j.skipped !== false) problems.push('skipped=' + j.skipped);
  if (j.civilAvailable !== false) problems.push('civilAvailable=' + j.civilAvailable);
  const age = j.generatedAt ? (Date.now() - Date.parse(j.generatedAt)) / 86400000 : Infinity;
  if (!isFinite(age) || age > MAX_AGE_DAYS) problems.push('stale (' + (isFinite(age) ? age.toFixed(1) + 'd' : 'no date') + ')');

  if (problems.length) { rejected.push(f + ': ' + problems.join(', ')); continue; }
  if (!best || Date.parse(j.generatedAt) > Date.parse(best.j.generatedAt)) best = { f, j, age, matchedBy: shaOk ? 'dll sha256' : 'plugin source == ' + stampCommit };
}

for (const r of rejected) console.log('  ~ rejected ' + r);

if (best) {
  console.log('  [OK] evidence: ' + best.f +
    '  (host=' + (best.j.hostProduct || '?') + ', dllSha=' + String(best.j.pluginDllSha256).slice(0, 16) + '…, ' +
    'age=' + best.age.toFixed(1) + 'd, checks=' + (Array.isArray(best.j.checks) ? best.j.checks.length : '?') + ')');
  process.exit(0);
}

if (REQUIRE) {
  console.log('  FAIL: no valid plain-AutoCAD evidence for this build (' + current.slice(0, 16) + '…).');
  console.log('        在一台纯 AutoCAD 2025/2026 机器上（装好本构建后）运行：');
  console.log('          "<安装目录>\\node.exe" tools\\smoke-plain-acad.js --evidence plain-acad.json');
  console.log('        然后把 plain-acad.json 拷到 docs/audit/host-evidence/ 再发版。');
  process.exit(1);
}

console.log('  warn: no valid plain-AutoCAD evidence for this build (release will refuse; dev is fine)');
process.exit(0);
