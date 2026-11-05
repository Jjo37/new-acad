#!/usr/bin/env node
/**
 * check-relay-endpoints.js -- relay endpoint inventory must match the router (2026-10-21)
 *
 * Companion to check-coverage-contract: that gate freezes the SET of endpoints, this one
 * forces every endpoint to be described (kind / auth exposure / side effects) so a new
 * route cannot slip in as an undocumented "just a small endpoint".
 *
 * Inventory: docs/audit/relay-endpoints.json
 *   { "endpoints": [ { "id": "POST /send", "kind": "write", "sideEffects": "spawns LLM turn", "note": "" } ] }
 *
 * Usage:
 *   node tools/check-relay-endpoints.js            # verify (both directions)
 *   node tools/check-relay-endpoints.js --update   # rewrite inventory from source (keeps existing notes)
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const SRC = path.join(ROOT, 'server', 'panel-relay.js');
const INV = path.join(ROOT, 'docs', 'audit', 'relay-endpoints.json');
const UPDATE = process.argv.includes('--update');

function derive() {
  const src = fs.readFileSync(SRC, 'utf8');
  const set = new Set();
  const all = [];   // raw, duplicates included -- dedupe is fine, silent holes are not
  // three shapes seen in panel-relay: exact, ?-stripped, prefix
  const take = (id) => { all.push(id); set.add(id); };
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url === '([^']+)'/g)) take(m[1] + ' ' + m[2]);
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url\.split\('\?'\)\[0\] === '([^']+)'/g)) take(m[1] + ' ' + m[2]);
  for (const m of src.matchAll(/req\.method === '(\w+)' && req\.url\.startsWith\('([^']+)'\)/g)) take(m[1] + ' ' + m[2] + '*');
  // anti-silent-hole: every `req.method ===` check must map to a parsed route. Two routes may
  // share one derived shape (/workflows/<id>/export and /replay both -> POST /workflows/*), so
  // the count is compared before dedupe.
  const checks = (src.match(/req\.method === '/g) || []).length;
  if (checks !== all.length) {
    throw new Error('route derivation gap: ' + checks + ' method checks vs ' + all.length + ' parsed routes -- extend the regexes instead of shipping a blind spot');
  }
  if (all.length !== set.size) console.log('  note: ' + (all.length - set.size) + ' route(s) share a derived shape (expected)');
  return [...set].sort();
}

const KINDS = { GET: 'read', POST: 'write', PUT: 'write', DELETE: 'write' };
let derived;
try { derived = derive(); } catch (e) { console.log('  CANNOT DERIVE ROUTES: ' + e.message); process.exit(2); }
const prev = fs.existsSync(INV) ? JSON.parse(fs.readFileSync(INV, 'utf8')) : { endpoints: [] };
const prevById = new Map((prev.endpoints || []).map((e) => [e.id, e]));

if (UPDATE) {
  const endpoints = derived.map((id) => {
    const old = prevById.get(id);
    const [method] = id.split(' ');
    return old || { id, kind: KINDS[method] || 'write', sideEffects: '', note: '' };
  });
  fs.mkdirSync(path.dirname(INV), { recursive: true });
  fs.writeFileSync(INV, JSON.stringify({ updated: new Date().toISOString().slice(0, 10), endpoints }, null, 2) + '\n');
  console.log('  inventory updated: ' + endpoints.length + ' endpoints -> ' + path.relative(ROOT, INV));
  process.exit(0);
}

const problems = [];
const cur = new Set(derived);
const inv = new Set((prev.endpoints || []).map((e) => e.id));

for (const id of derived) if (!inv.has(id)) problems.push('undocumented endpoint in code: ' + id);
for (const id of inv) if (!cur.has(id)) problems.push('documented endpoint gone from code: ' + id);
for (const e of prev.endpoints || []) {
  if (!e.id || !e.kind) problems.push('inventory entry missing kind: ' + JSON.stringify(e).slice(0, 60));
  if (e.kind === 'write' && !String(e.sideEffects || '').trim()) problems.push('write endpoint without sideEffects note: ' + e.id);
}

if (problems.length) {
  console.log('  endpoint inventory out of sync (' + problems.length + '):');
  for (const p of problems.slice(0, 20)) console.log('    x ' + p);
  console.log('  fix: describe the route in docs/audit/relay-endpoints.json (kind + sideEffects),');
  console.log('       or run --update to import new routes, then fill in the notes.');
  process.exit(1);
}
console.log('  ' + derived.length + ' endpoints documented and in sync');
process.exit(0);
