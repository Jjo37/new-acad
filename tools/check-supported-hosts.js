#!/usr/bin/env node
// check-supported-hosts.js — "声称支持的宿主，必须是真的验过的宿主" (2026-10-27)
//
// Why: v1.11.0 claimed "AutoCAD / Civil 3D 2025/2026" while the plugin did not work on plain
// AutoCAD at all. Documentation was never tied to evidence.
//
// Design: no duplicated claim strings. The gate reads the README(s) listed in
// docs/audit/supported-hosts.json, extracts every "product + year" mention that is NOT negated
// ("2024 and earlier are not supported" is ignored), and requires a status=verified host entry
// covering each of those years with non-empty evidence.
//
// Usage: node tools/check-supported-hosts.js [--json]

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const CFG = path.join(ROOT, 'docs', 'audit', 'supported-hosts.json');
const AS_JSON = process.argv.includes('--json');

function finish(errors, notes) {
  if (AS_JSON) {
    console.log(JSON.stringify({ ok: errors.length === 0, errors, notes }, null, 2));
  } else {
    for (const n of notes) console.log('  ok: ' + n);
    for (const e of errors) console.log('  FAIL: ' + e);
    console.log(errors.length === 0
      ? `  supported hosts contract OK (${notes.length} claim(s) covered)`
      : `  supported hosts contract FAILED (${errors.length} problem(s))`);
  }
  process.exit(errors.length === 0 ? 0 : 1);
}

let cfg;
try {
  cfg = JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^\uFEFF/, ''));
} catch (e) {
  finish(['cannot read ' + path.relative(ROOT, CFG) + ': ' + e.message], []);
}

const hosts = Array.isArray(cfg.hosts) ? cfg.hosts : [];
const sources = Array.isArray(cfg.sources) ? cfg.sources : [];
const negations = Array.isArray(cfg.negations) ? cfg.negations : [];

const errors = [];
const notes = [];

if (!hosts.length) errors.push('supported-hosts.json has no hosts[]');
if (!sources.length) errors.push('supported-hosts.json has no sources[] (nothing is being checked)');

for (const h of hosts) {
  if (!h.product || !Array.isArray(h.years) || !h.years.length) errors.push(`host ${h.id || '?'}: product/years missing`);
  if (h.status === 'verified' && (!h.evidence || String(h.evidence).trim().length < 20)) {
    errors.push(`host ${h.id}: status=verified but evidence is empty/too short`);
  }
}

const isNegated = (line) => negations.some((n) => line.toLowerCase().includes(String(n).toLowerCase()));

// A line can mix a claim and a negation ("Civil 3D 2025/2026 — standalone AutoCAD is not
// supported"). Judging the whole line let exactly that line through, which is how a stale
// "standalone AutoCAD is not supported" survived in 使用手册.html while the README was fixed.
// So negations are evaluated per clause.
const CLAUSE_SPLIT = /[。；;]|\.(?:\s|$)/;

for (const rel of sources) {
  const abs = path.join(ROOT, rel);
  if (!fs.existsSync(abs)) { errors.push('source file missing: ' + rel); continue; }
  const lines = fs.readFileSync(abs, 'utf8').split(/\r?\n/);

  lines.forEach((line, i) => {
    if (!/20\d{2}/.test(line)) return;

    for (const clause of line.split(CLAUSE_SPLIT)) {
      if (!/20\d{2}/.test(clause)) continue;
      if (isNegated(clause)) continue;

      const years = [...new Set((clause.match(/20\d{2}/g) || []).map(Number))];
      const products = new Set();
      // A clause may claim both products (the v1.11.0 wording did exactly that) - record both.
      if (/Civil 3D/i.test(clause)) products.add('Civil 3D');
      if (/AutoCAD/i.test(clause)) products.add('AutoCAD');
      if (!products.size) continue;

      for (const product of products) {
        const covering = hosts.filter((h) => h.product === product && h.status === 'verified' && years.every((y) => h.years.includes(y)));
        const where = rel + ':' + (i + 1);
        if (!covering.length) {
          errors.push(`unsupported claim at ${where}: ${product} ${years.join('/')} has no verified host entry`);
        } else {
          notes.push(`${where}: ${product} ${years.join('/')} -> ${covering.map((h) => h.id).join(',')}`);
        }
      }
    }

    // Same clause, other direction: denying a host that IS verified contradicts the evidence.
    // ("claims  subset verified" cannot see this, because a denial is not a claim.)
    for (const clause of line.split(CLAUSE_SPLIT)) {
      if (!isNegated(clause)) continue;
      const years = [...new Set((clause.match(/20\d{2}/g) || []).map(Number))];
      if (!years.length) continue;
      const products = new Set();
      if (/Civil 3D/i.test(clause)) products.add('Civil 3D');
      if (/AutoCAD/i.test(clause)) products.add('AutoCAD');
      for (const product of products) {
        const verified = hosts.filter((h) => h.product === product && h.status === 'verified' && years.every((y) => h.years.includes(y)));
        if (verified.length) {
          errors.push(`contradiction at ${rel}:${i + 1}: the text denies ${product} ${years.join('/')} but ${verified.map((h) => h.id).join(',')} is verified`);
        }
      }
    }

    // A denial of "plain AutoCAD" with no year is the same contradiction in disguise
    // (this is the exact sentence that stayed in the manual after plain AutoCAD shipped).
    // Judged per clause: a line can mention "纯 AutoCAD 能用" and "2024 及更早不能用" at once.
    for (const clause of line.split(CLAUSE_SPLIT)) {
      if (!/(单独|纯)\s*AutoCAD|standalone\s*AutoCAD|AutoCAD\s*单独/i.test(clause)) continue;
      if (!isNegated(clause)) continue;
      const verifiedPlain = hosts.filter((h) => h.product === 'AutoCAD' && h.status === 'verified');
      if (verifiedPlain.length) {
        errors.push(`contradiction at ${rel}:${i + 1}: the text denies plain AutoCAD support but ${verifiedPlain.map((h) => h.id).join(',')} is verified`);
      }
    }
  });
}

finish(errors, notes);
