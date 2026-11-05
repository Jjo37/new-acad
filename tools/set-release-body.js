#!/usr/bin/env node
// set-release-body.js — rewrite a GitHub release body from a UTF-8 notes file, correctly.
//
// Why this exists (2026-10-27 incident): after publishing v1.11.0-fix3 the online release body was
// entirely "?" — every Chinese character replaced. Cause: Windows PowerShell 5.1
// `Invoke-RestMethod -Body <string>` encodes the string as ASCII before sending, so any non-ASCII
// char becomes '?'. (publish-release.ps1 itself is fine: it uses StringContent(..., UTF8, ...).)
// Node encodes JSON as UTF-8 by default, so this tool is the safe path for body edits.
//
// Usage:
//   node tools/set-release-body.js --repo Jjo37/new-acad --tag v1.11.0-fix3 \
//        --notes tools/public-templates/release-notes-1.11.0-fix.md [--append extra.md] [--check]
//
// Token: $env:GH_TOKEN, else read from `git credential fill` in the current repo.

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const arg = (name, def = '') => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : def;
};
const has = (name) => process.argv.includes('--' + name);

const repo = arg('repo');
const tag = arg('tag');
const notesFile = arg('notes');
const appendFile = arg('append');
if (!repo || !tag || !notesFile) {
  console.error('usage: node tools/set-release-body.js --repo <owner/name> --tag <tag> --notes <file.md> [--append <file.md>] [--check]');
  process.exit(2);
}

function tokenFromGitCredential() {
  try {
    const out = execFileSync('git', ['credential', 'fill'], {
      input: 'protocol=https\nhost=github.com\n\n',
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'ignore']
    });
    const m = out.match(/^password=(.+)$/m);
    return m ? m[1].trim() : '';
  } catch {
    return '';
  }
}

const token = process.env.GH_TOKEN || process.env.GITHUB_TOKEN || tokenFromGitCredential();
if (!token) { console.error('no GitHub token (env GH_TOKEN or git credential)'); process.exit(2); }

const headers = {
  'User-Agent': 'new-acad-set-release-body',
  Accept: 'application/vnd.github+json',
  'X-GitHub-Api-Version': '2022-11-28',
  Authorization: 'Bearer ' + token,
  'Content-Type': 'application/json; charset=utf-8'
};

const cjk = (s) => (s.match(/[\u4e00-\u9fff]/g) || []).length;
const q = (s) => (s.match(/\?/g) || []).length;

let body = fs.readFileSync(notesFile, 'utf8').replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');
if (appendFile) body = body.replace(/\s*$/, '\n') + '\n' + fs.readFileSync(appendFile, 'utf8').replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');

(async () => {
  const relRes = await fetch(`https://api.github.com/repos/${repo}/releases/tags/${tag}`, { headers });
  const rel = await relRes.json();
  if (!rel.id) { console.error('release not found: ' + JSON.stringify(rel).slice(0, 200)); process.exitCode = 1; return; }

  const before = { len: (rel.body || '').length, cjk: cjk(rel.body || ''), q: q(rel.body || '') };
  console.log(`[before] len=${before.len} cjk=${before.cjk} ?=${before.q}`);

  if (has('check')) {
    // Compare against the local notes file so English-only notes do not false-fail.
    const expected = cjk(body);
    const tooFewCjk = expected >= 50 && before.cjk < expected * 0.5;
    const questionAllowed = Math.max(20, Math.round(before.len * 0.02));
    const tooManyQuestions = before.q > questionAllowed;
    const bad = tooFewCjk || tooManyQuestions;
    if (bad) console.log('[FAIL] body looks garbled (local cjk=' + expected + ', remote cjk=' + before.cjk + ', questions=' + before.q + ')');
    else console.log('[OK] body looks healthy (local cjk=' + expected + ', remote cjk=' + before.cjk + ')');
    process.exitCode = bad ? 1 : 0;
    return;
  }

  const res = await fetch(`https://api.github.com/repos/${repo}/releases/${rel.id}`, {
    method: 'PATCH',
    headers,
    body: JSON.stringify({ body })
  });
  const out = await res.json();
  const after = { len: (out.body || '').length, cjk: cjk(out.body || ''), q: q(out.body || '') };
  console.log(`[after ] status=${res.status} len=${after.len} cjk=${after.cjk} ?=${after.q}`);
  if (res.status !== 200 || (cjk(body) > 0 && after.cjk === 0)) {
    console.error('[FAIL] body did not round-trip as UTF-8');
    process.exitCode = 1;
    return;
  }
  console.log('[OK] release body updated (UTF-8 verified by counting CJK characters)');
})();
