#!/usr/bin/env node
/**
 * check-coverage-contract.js -- "new capability must bring verification assets" (2026-10-21)
 *
 * The audit and the gates so far only inspect assets that already exist. Nothing
 * stopped a NEW method / MCP tool / relay endpoint from landing with zero tests:
 * the gates would stay green because they never knew the item appeared.
 *
 * This gate turns capability growth into an explicit, reviewable event:
 *   surfaces are derived from code (never hand-listed)
 *   each surface's item set is frozen in docs/audit/contract.json
 *   any ADDED item => FAIL, until someone updates the contract AND names the asset
 *   any REMOVED item => FAIL too (ratchet: coverage may not silently shrink)
 *
 * Usage:
 *   node tools/check-coverage-contract.js                  # verify
 *   node tools/check-coverage-contract.js --skip-mcp       # when server/mcp build is absent
 *   node tools/check-coverage-contract.js --update --asset tools/functional-smoke.js --reason "..."
 */
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const CONTRACT = path.join(ROOT, 'docs', 'audit', 'contract.json');
const SKIP_MCP = process.argv.includes('--skip-mcp');
const UPDATE = process.argv.includes('--update');
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : d; };

// ---------- surface derivation ----------
function derivePluginMethods() {
  const r = spawnSync(process.execPath, [path.join(ROOT, 'tools', 'check-method-names.js'), '--json'], { encoding: 'utf8' });
  const out = (r.stdout || '').trim();
  if (!out) throw new Error('check-method-names --json produced no output');
  const j = JSON.parse(out.split(/\r?\n/).pop());
  if (!Array.isArray(j.names)) throw new Error('no names[] in --json output');
  return j.names.slice().sort();
}

function deriveMcpTools() {
  const built = path.join(ROOT, 'server', 'mcp', 'build', 'tools', 'toolManifest.js');
  // 2026-10-21: the packaged/public line ships build/ but NOT node_modules (the catalog needs zod).
  // Requiring it there throws ENOENT('zod') and took down the whole gate -> treat as "cannot derive".
  const deps = path.join(ROOT, 'server', 'mcp', 'node_modules');
  if (!fs.existsSync(built) || !fs.existsSync(deps)) {
    if (SKIP_MCP) return null;
    throw new Error('server/mcp build or node_modules missing (npm ci && npm run build, or pass --skip-mcp)');
  }
  const m = require(built);
  const cat = m.GENERATED_TOOL_CATALOG_ENTRIES || [];
  return [...new Set(cat.map((c) => c.toolName))].sort();
}

function deriveRelayEndpoints() {
  const src = fs.readFileSync(path.join(ROOT, 'server', 'panel-relay.js'), 'utf8');
  const set = new Set();
  const all = [];   // raw, duplicates included -- two routes can share one derived shape
  const take = (id) => { all.push(id); set.add(id); };
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url === '([^']+)'/g)) take(m[1] + ' ' + m[2]);
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url\.split\('\?'\)\[0\] === '([^']+)'/g)) take(m[1] + ' ' + m[2]);
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url\.startsWith\('([^']+)'\)/g)) take(m[1] + ' ' + m[2] + '*');
  const checks = (src.match(/req\.method === '/g) || []).length;
  if (checks !== all.length) throw new Error('relay route derivation gap: ' + checks + ' checks vs ' + all.length + ' parsed routes');
  return [...set].sort();
}

const SURFACES = [
  { id: 'plugin.methods', derive: derivePluginMethods, what: 'plugin TCP methods (listMethods)' },
  { id: 'mcp.tools', derive: deriveMcpTools, what: 'MCP tools (domain catalog)' },
  { id: 'relay.endpoints', derive: deriveRelayEndpoints, what: 'relay HTTP/SSE endpoints' },
];

// ---------- helpers ----------
const hashOf = (items) => crypto.createHash('sha256').update(items.join('\n')).digest('hex').slice(0, 16);
function loadContract() {
  if (!fs.existsSync(CONTRACT)) return { version: 1, updated: null, surfaces: {} };
  return JSON.parse(fs.readFileSync(CONTRACT, 'utf8'));
}

// ---------- main ----------
let failed = 0;
const derived = {};
try {
  for (const s of SURFACES) derived[s.id] = s.derive();
} catch (e) {
  console.log('  CANNOT DERIVE SURFACE: ' + e.message);
  process.exit(2);
}

const contract = loadContract();
const next = { version: 1, updated: contract.updated, surfaces: {} };
const report = [];

for (const s of SURFACES) {
  const items = derived[s.id];
  const prev = contract.surfaces[s.id] || null;
  const entry = {
    what: s.what,
    count: items ? items.length : 'skipped',
    hash: items ? hashOf(items) : 'skipped',
    assets: (prev && prev.assets) || [],
    exempt: (prev && prev.exempt) || [],
    items: items || (prev ? prev.items : []),
  };

  if (!items) { report.push({ id: s.id, state: 'skipped', msg: 'build missing, surface not checked' }); next.surfaces[s.id] = entry; continue; }

  if (UPDATE) {
    const asset = arg('asset', '');
    const only = arg('surface', '');          // --surface limits the asset to one capability surface
    if (asset && (!only || only === s.id) && !entry.assets.includes(asset)) entry.assets.push(asset);
    report.push({ id: s.id, state: 'updated', msg: entry.count + ' items, ' + entry.assets.length + ' assets' });
    next.surfaces[s.id] = entry;
    continue;
  }

  if (!prev) {
    report.push({ id: s.id, state: 'new-surface', msg: 'not in contract yet (' + items.length + ' items)' });
    failed++;
    next.surfaces[s.id] = entry;
    continue;
  }

  // asset files must exist
  const missingAssets = entry.assets.filter((a) => !fs.existsSync(path.join(ROOT, a)));
  // 2026-10-21: the published snapshot ships the gates but not every dev-only test asset
  // (live-c3d-regression, panel-selftest...). Losing SOME assets is a warning; losing ALL of
  // them means the surface has no verification at all -> that still fails.
  if (missingAssets.length) {
    if (missingAssets.length === entry.assets.length) {
      report.push({ id: s.id, state: 'no-asset-present', msg: 'none of the declared assets exist: ' + missingAssets.join(', ') });
      failed++;
    } else {
      report.push({ id: s.id, state: 'warn-asset', msg: 'not in this line: ' + missingAssets.join(', ') });
    }
  }
  if (!entry.assets.length) {
    report.push({ id: s.id, state: 'no-asset', msg: 'surface declares zero verification assets' });
    failed++;
  }

  const prevItems = prev.items || [];
  const prevSet = new Set(prevItems);
  const curSet = new Set(items);
  const exempt = new Set(entry.exempt || []);
  const added = items.filter((n) => !prevSet.has(n) && !exempt.has(n));
  const removed = prevItems.filter((n) => !curSet.has(n));

  if (added.length) {
    report.push({ id: s.id, state: 'added', msg: added.length + ' new: ' + added.slice(0, 8).join(', ') + (added.length > 8 ? ' …' : '') });
    failed++;
  }
  if (removed.length) {
    report.push({ id: s.id, state: 'removed', msg: removed.length + ' gone: ' + removed.slice(0, 8).join(', ') + (removed.length > 8 ? ' …' : '') });
    failed++;
  }
  if (!added.length && !removed.length && entry.assets.length && missingAssets.length < entry.assets.length) {
    report.push({ id: s.id, state: 'ok', msg: item2(entry.count, entry.assets.length) });
  }
  next.surfaces[s.id] = entry;
}
function item2(a, b) { return a + ' items / ' + b + ' assets'; }

if (UPDATE) {
  next.updated = new Date().toISOString().slice(0, 10);
  fs.mkdirSync(path.dirname(CONTRACT), { recursive: true });
  fs.writeFileSync(CONTRACT, JSON.stringify(next, null, 2) + '\n');
  for (const r of report) console.log('  [' + r.id + '] ' + r.state + ' -- ' + r.msg);
  console.log('  contract updated: ' + path.relative(ROOT, CONTRACT));
  process.exit(0);
}

for (const r of report) {
  const mark = r.state === 'ok' ? '  ' : r.state === 'skipped' ? '  ~' : '  x';
  console.log(mark + ' [' + r.id + '] ' + r.msg);
}
if (failed) {
  console.log('');
  console.log('  FAIL: capability surface changed without a contract update.');
  console.log('  Every new method / MCP tool / endpoint must come with a verification asset:');
  console.log('    node tools/check-coverage-contract.js --update --asset tools/<your-test>.js --reason "why"');
  console.log('  Removals are also rejected: shrinking coverage needs an explicit decision.');
  process.exit(1);
}
console.log('  contract OK (' + SURFACES.length + ' surfaces)');
process.exit(0);
