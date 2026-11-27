'use strict';
// host-matrix.js — 宿主矩阵证据的单一实现（2026-11-23 P35）
//
// 背景（为什么不沿用 v1 的"一张纯 AutoCAD 照片"）：
//   安装器现在会自动探测所有宿主、让用户勾选装到哪几个 → 落地形态是矩阵：
//     {产品 AutoCAD / Civil 3D} × {年份 2025/2026/2027} × {运行时 net8/net10}
//   拿"某一台机器的某一次运行"当全产品的证据，既不充分（覆盖不到别的格子），
//   又太脆（绑整包 DLL sha → 改一行文档就得重跑整条真机链）→ 结果必然是发布前 -SkipHostEvidence。
//
// 本模块的口径：
//   · 证据单元 = cell，一个 cell 证明一个 (产品, 年份, 运行时) 组合在某个插件提交上跑通过
//   · 绑定 = **插件源码提交**（plugin/AcBridge-v24/src 的最后一次提交），不是整包 DLL sha：
//     只改文档/模板的重建不会让证据作废，改了插件才作废 —— 这正是证据该管的范围
//   · 契约 = docs/audit/supported-hosts.json（README 里能怎么声明由 check-supported-hosts 管）
//   · 一格只证明它自己那一年：cell.years 必须包含 host 声明的年份（2027 的 cell 不能顶 2025）
//   · status=waived = 明确不验（必须写 waivedOn + note 理由）：矩阵里照样列出来，不算静默降级
//     每个 status=verified 的 host 必须有本期 cell；没有就必须是 unverified + 写明原因
//   · 倒退保护：历史上跑通过的宿主，本版降级成 unverified 却不说一句 → 直接 FAIL
//
// 判据可证伪：删掉一个 cell / 改一条 note，judge() 的 errors 必须跟着变。

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const SCHEMA_CELL = 'new-acad.host-evidence.v2';

function rootOf(dir) { return path.resolve(dir || path.join(__dirname, '..')); }

function git(dir, args) {
  try { return execFileSync('git', ['-C', dir, ...args], { encoding: 'utf8' }).trim(); }
  catch { return ''; }
}

/** 插件源码的当前提交（证据的绑定对象）。 */
function pluginCommit(root) {
  const sha = git(root, ['log', '-1', '--format=%H', '--', 'plugin/AcBridge-v24/src']);
  return sha || git(root, ['rev-parse', 'HEAD']);
}

/**
 * 从 buildStamp 里取完整提交号。buildStamp 形如 `<短sha>-20261123-181745+<完整sha>`。
 * 为什么需要：证据脚本在**装好的包**里运行时没有 .git，git 取不到提交号；
 * 只靠 git 会让异机产出的 cell 永远 commit='' → 门禁永远不认（真机踩点）。
 */
function commitFromBuildStamp(buildStamp) {
  const m = String(buildStamp || '').match(/\+([0-9a-f]{7,40})/i);
  if (m) return m[1];
  const m2 = String(buildStamp || '').match(/^([0-9a-f]{7,40})/i);
  return m2 ? m2[1] : '';
}
function loadContract(root) {
  // 2026-11-24: 安装包里没有 docs/ 树（也不该有），契约随包放在 tools/supported-hosts.json。
  // 三条路径依次找，装好的包和 dev 仓都能用 —— 否则异机产的 cell 永远拿不到 id。
  const tries = [
    path.join(root, 'docs', 'audit', 'supported-hosts.json'),
    path.join(root, 'tools', 'supported-hosts.json'),
  ];
  let p = '';
  for (const t of tries) { if (fs.existsSync(t)) { p = t; break; } }
  if (!p) throw new Error('supported-hosts.json not found (tried: ' + tries.join(' | ') + ')');
  const raw = JSON.parse(fs.readFileSync(p, 'utf8'));
  return { path: p, raw, hosts: Array.isArray(raw.hosts) ? raw.hosts : [] };
}

/** 从宿主产品串里取年份：'AutoCAD 2025' → [2025]。取不到就空数组（单元格视为"年份未知"）。 */
function yearsFromProduct(productRaw) {
  const out = [];
  for (const m of String(productRaw || '').matchAll(/\b(20\d{2})\b/g)) {
    const y = Number(m[1]);
    if (!out.includes(y)) out.push(y);
  }
  return out;
}

/**
 * 产品判定：先看 civilAvailable，再看产品名。
 * 2026-11-23 真机踩到：Civil 3D 宿主里 (getvar "PRODUCT") 也只返回 "AutoCAD"（不带年份），
 * 只按字符串判断会把 C3D 运行记成纯 AutoCAD —— 证据会记到错的宿主名下。
 */
function productFromHealth(health) {
  const h = health || {};
  if (h.civilAvailable === true) return 'Civil 3D';
  const byName = productFromProductRaw(h.hostProduct || '');
  if (byName) return byName;
  return h.civilAvailable === false ? 'AutoCAD' : '';
}

/**
 * ACADVER → 年份（仅映射确定过的值，其余返回 []，宁缺毋滥）：
 *   AutoCAD 2025 = R25.0；2027 = R26.0（Bro 2026-11-09/10 实测 2027 走 net10 段）。
 * 年份只是展示用的标签，门禁按 host.id 匹配；hostVersion 原样记在 cell 里。
 */
function yearsFromAcadver(acadver) {
  const m = String(acadver || '').match(/^(\d+)\.(\d+)/);
  if (!m) return [];
  // 2026-11-24: 以前只按 major 映射（25->2025、26->2027），把 R25.1 的 AutoCAD 2026 错记成 2025。
  // AutoCAD 的年份与主版本号不是简单的 +2000：R25.0=2025、R25.1=2026、R26.0=2027。按 major.minor 精确映射。
  const map = { '25.0': 2025, '25.1': 2026, '26.0': 2027, '26.1': 2028 };
  const y = map[m[1] + '.' + m[2]];
  return y ? [y] : [];
}
function productFromProductRaw(productRaw) {
  const s = String(productRaw || '').toLowerCase();
  if (s.includes('civil')) return 'Civil 3D';
  if (s.includes('autocad')) return 'AutoCAD';
  return String(productRaw || '').trim();
}

/** v1（单次运行的扁平证据）→ 统一 cell。历史文件只用来做"曾经跑通过"的判断。 */
function normalize(raw, fileName) {
  if (!raw || typeof raw !== 'object') return null;
  if (raw.schema === SCHEMA_CELL) {
    return {
      schema: SCHEMA_CELL,
      id: raw.id || '',
      product: raw.product || '',
      productRaw: raw.productRaw || '',
      years: Array.isArray(raw.years) ? raw.years : [],
      runtime: raw.runtime || '',
      commit: raw.commit || '',
      pluginDllSha256: raw.pluginDllSha256 || '',
      pluginVersion: raw.pluginVersion || '',
      generatedAt: raw.generatedAt || '',
      machine: raw.machine || '',
      source: raw.source || '',
      result: raw.result === 'pass' ? 'pass' : (raw.result || 'unknown'),
      checks: Array.isArray(raw.checks) ? raw.checks : [],
      file: fileName || '',
      legacy: false,
    };
  }
  if (raw.schema === 'new-acad.host-evidence.v1') {
    const checks = Array.isArray(raw.checks) ? raw.checks : [];
    const failed = checks.filter((c) => c && c.ok === false).length;
    // v1 里没有宿主产品串，但文件名是 plain-acad-<version>.json；跑这脚本的机器就是纯 AutoCAD
    const product = /plain-acad/i.test(fileName || '') ? 'AutoCAD' : '';
    return {
      schema: SCHEMA_CELL,
      id: '',
      product,
      productRaw: '',
      years: [],
      runtime: '',
      commit: '',                       // v1 没有插件提交，只有 buildStamp 里的短 sha
      pluginDllSha256: raw.pluginDllSha256 || '',
      pluginVersion: raw.pluginVersion || '',
      generatedAt: raw.generatedAt || '',
      machine: raw.machine || '',
      source: 'legacy v1',
      result: failed === 0 && checks.length > 0 ? 'pass' : 'fail',
      checks,
      file: fileName || '',
      legacy: true,
    };
  }
  return null;
}

function loadCells(root) {
  const dir = path.join(root, 'docs', 'audit', 'host-evidence');
  if (!fs.existsSync(dir)) return [];
  const cells = [];
  for (const name of fs.readdirSync(dir).sort()) {
    if (!name.toLowerCase().endsWith('.json')) continue;
    try {
      const raw = JSON.parse(fs.readFileSync(path.join(dir, name), 'utf8'));
      const c = normalize(raw, name);
      if (c) cells.push(c);
    } catch { /* 坏文件由 judge 之外的检查负责，这里跳过 */ }
  }
  return cells;
}

/** 一个 cell 是否覆盖某一年：id 对得上，且 cell.years 含该年（cell 没写年份视为"年份未知"，不算覆盖）。 */
function cellCovers(cell, host, year) {
  if (!cell || cell.result !== 'pass') return false;
  if (cell.id && host.id && cell.id !== host.id) return false;
  if (cell.product && host.product && cell.product !== host.product) return false;
  if (!year) return true;
  return Array.isArray(cell.years) && cell.years.includes(year);
}
const DECLINE_WORDS = /(未验证|待验证|没有验证|not verified|unverified|declined|暂停验证)/i;

/**
 * 判据：
 *   E1 verified 宿主必须有本期 cell（commit == 插件提交 && result=pass）
 *   E2 unverified 宿主必须写明原因（note）
 *   E3 历史上跑通过的宿主，本版降级成 unverified 且没写原因 → 静默降级，FAIL
 *   E4 契约里出现未知 status → FAIL
 */
/** 提交号比较：允许一方是另一方的短 sha（打包/手抄都可能只剩 7 位）。 */
function sameCommit(a, b) {
  const x = String(a || '').toLowerCase();
  const y = String(b || '').toLowerCase();
  if (!x || !y) return false;
  return x === y || x.startsWith(y) || y.startsWith(x);
}
function judge({ contract, cells, pluginCommitSha, headSha }) {
  const errors = [];
  const warnings = [];
  const rows = [];

  const isCurrent = (c) => c.result === 'pass' && sameCommit(c.commit, pluginCommitSha);
  const passedEver = (product) => cells.some((c) => c.result === 'pass' && c.product === product);

  for (const h of contract.hosts) {
    const id = h.id || '?';
    const product = h.product || '';
    const mine = cells.filter((c) => (c.id && c.id === id) || (!c.id && c.product === product));
    const years = Array.isArray(h.years) ? h.years : [];
    // 一年一格：host 声明的每一年都要有本期、且那一年对得上的 cell
    const freshPerYear = years.map((y) => mine.find((c) => isCurrent(c) && cellCovers(c, h, y)) || null);
    const missingYears = years.filter((y, i) => !freshPerYear[i]);
    const freshCell = freshPerYear.find(Boolean) || null;
    const stalePass = mine.filter((c) => c.result === 'pass' && !sameCommit(c.commit, pluginCommitSha));
    const status = h.status || '';

    if (status !== 'verified' && status !== 'unverified' && status !== 'waived') {
      errors.push(`${id}: status 必须是 verified / unverified / waived（现在是 ${JSON.stringify(status)}）`);
      rows.push({ id, product, years: h.years || [], status: status || '?', cell: '?', detail: 'status 不合法' });
      continue;
    }

    if (status === 'verified') {
      if (missingYears.length) {
        const how = product.includes('Civil')
          ? 'node tools/release-gate.js --with-cad'
          : 'node tools/smoke-plain-acad.js --evidence docs/audit/host-evidence/<文件名>.json';
        errors.push(
          `${id}（${product}）标了 verified，但这些年份没有本期证据：${missingYears.join('/')}（插件提交 ${String(pluginCommitSha).slice(0, 8)}）。` +
          (stalePass.length
            ? ` 旧证据：${stalePass.map((c) => (c.generatedAt || c.file).slice(0, 10) + (c.years && c.years.length ? '' : '(年份未知)')).join(', ')}。`
            : ' 从来没有跑通过。') +
          ` 补齐：${how}`
        );
      }
      rows.push({
        id, product, years: h.years || [], status,
        cell: missingYears.length === 0 ? 'fresh' : (freshCell ? 'partial' : (stalePass.length ? 'stale' : 'none')),
        detail: missingYears.length === 0 ? (freshCell && freshCell.machine ? freshCell.machine : '')
          : '缺 ' + missingYears.join('/') + (stalePass.length ? '（有旧构建通过）' : ''),
      });
      continue;
    }

    // status === 'waived'：明确不验，但必须留日期 + 理由（矩阵里照样列，不算静默）
    if (status === 'waived') {
      const note = String(h.note || '').trim();
      if (!note) errors.push(`${id}: status=waived 必须写 note（为什么这次不验）`);
      if (!String(h.waivedOn || '').trim()) errors.push(`${id}: status=waived 必须写 waivedOn（哪天拍的这个板）`);
      warnings.push(`${id}（${product} ${years.join('/')}）本次按 waived 放行 —— 未验证，发布说明里必须如实写`);
      rows.push({ id, product, years, status, cell: 'waived', detail: note.slice(0, 60) });
      continue;
    }

    if (status !== 'unverified') {
      errors.push(`${id}: 未知 status ${JSON.stringify(status)}（只允许 verified / unverified / waived）`);
      rows.push({ id, product, years, status, cell: '?', detail: 'status 不合法' });
      continue;
    }

    // status === 'unverified'
    const note = String(h.note || h.evidence || '').trim();
    if (!note) errors.push(`${id}: status=unverified 必须写 note 说明原因（否则就是悄悄降级）`);
    if (passedEver(product) && !DECLINE_WORDS.test(note)) {
      errors.push(
        `${id}: 历史上 ${product} 跑通过，本版却降级成 unverified 且没有写明（note 需含「未验证/待验证/not verified」）—— 静默降级。`
      );
    }
    rows.push({ id, product, years: h.years || [], status, cell: 'n/a', detail: note.slice(0, 60) });
  }

  // 契约里没登记、但库里躺着的 cell：只提示，不算错（历史文件会很多）
  const known = new Set(contract.hosts.map((h) => h.product));
  for (const c of cells) {
    if (!c.product || known.has(c.product)) continue;
    warnings.push(`证据文件 ${c.file} 的宿主产品「${c.product}」没在 supported-hosts.json 里登记`);
  }
  return { rows, errors, warnings, pluginCommit: pluginCommitSha, head: headSha, cells };
}

function renderMarkdown(j) {
  const lines = [];
  lines.push('# 宿主验证矩阵（自动生成，勿手改 —— `node tools/check-host-evidence.js --write-matrix`）');
  lines.push('');
  lines.push('- 生成时间：' + new Date().toISOString());
  lines.push('- 插件提交（证据绑定对象）：`' + String(j.pluginCommit || '').slice(0, 12) + '`');
  lines.push('- 结论：' + (j.errors.length ? `**不通过**（${j.errors.length} 个问题）` : '通过'));
  lines.push('');
  lines.push('| 宿主 | 产品 | 年份 | 状态 | 本期证据 | 说明 |');
  lines.push('|---|---|---|---|---|---|');
  for (const r of j.rows) {
    lines.push(`| ${r.id} | ${r.product} | ${(r.years || []).join('/') || '—'} | ${r.status} | ${r.cell} | ${String(r.detail || '').replace(/\|/g, '/')} |`);
  }
  lines.push('');
  lines.push('cell 口径：`fresh` = 本插件提交上、且覆盖该年份；`partial` = 部分年份缺；`stale` = 更早构建通过、本版未复跑；`none` = 无证据；`waived` = 明确不验（带日期+理由）；`n/a` = 声明为未验证。');
  return lines.join('\n') + '\n';
}

module.exports = {
  SCHEMA_CELL,
  rootOf,
  pluginCommit,
  commitFromBuildStamp,
  loadContract,
  loadCells,
  normalize,
  yearsFromProduct,
  productFromProductRaw,
  productFromHealth,
  yearsFromAcadver,
  judge,
  renderMarkdown,
};
