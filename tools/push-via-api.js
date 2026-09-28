#!/usr/bin/env node
'use strict';
/**
 * push-via-api.js — 当 git push 走不通（github.com:443 被阻断/解析到不通 IP）时，
 * 用 GitHub Git Data API（api.github.com 通常仍可达）把本地快照树落到远端。
 *
 * 原理：只上传与远端树不同的 blob（内容取自 git 对象，规避 CRLF/LF 规范化差异）
 *      → 建树(base_tree 自动合并) → 建提交(parent=远端 HEAD) → 移动分支 ref → 建/移 tag → 校验。
 *
 * 用法：
 *   node tools/push-via-api.js --local D:\new-acad-public --repo Jjo37/new-acad \
 *        --branch main --tag v1.7.1-release --message "release: xxx" [--dry]
 *
 * 退出码：0 成功且校验一致；1 失败或校验不一致。
 */
const { execFileSync } = require('child_process');
const https = require('https');

function arg(name, def) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : def;
}
const LOCAL = arg('local');
const REPO = arg('repo');
const BRANCH = arg('branch', 'main');
const TAG = arg('tag', '');
const MESSAGE = arg('message', 'chore: snapshot via API');
const DRY = process.argv.includes('--dry');
if (!LOCAL || !REPO) { console.error('usage: node push-via-api.js --local <dir> --repo <owner/name> [--branch main] [--tag t] [--message m] [--dry]'); process.exit(2); }

function git(args, input, enc) {
  return execFileSync('git', ['-C', LOCAL, ...args], { input, encoding: enc || 'utf8', maxBuffer: 512 * 1024 * 1024 });
}
const TOK = (() => {
  const o = git(['credential', 'fill'], 'protocol=https\nhost=github.com\n\n');
  const m = o.match(/^password=(.+)$/m);
  if (!m) throw new Error('no stored GitHub credential (git credential)');
  return m[1].trim();
})();

function api(method, path, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : null;
    const req = https.request({
      host: 'api.github.com', path, method,
      headers: Object.assign({
        'User-Agent': 'new-acad-push-api', 'Accept': 'application/vnd.github+json', 'Authorization': 'Bearer ' + TOK,
      }, data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {}),
    }, res => {
      let raw = ''; res.on('data', c => raw += c);
      res.on('end', () => {
        let j = null; try { j = raw ? JSON.parse(raw) : null; } catch { }
        if (res.statusCode >= 200 && res.statusCode < 300) resolve(j);
        else reject(new Error(method + ' ' + path + ' -> ' + res.statusCode + ' ' + raw.slice(0, 250)));
      });
    });
    req.on('error', reject); if (data) req.write(data); req.end();
  });
}

(async () => {
  const ref = await api('GET', `/repos/${REPO}/git/ref/heads/${BRANCH}`);
  const remoteSha = ref.object.sha;
  const remoteCommit = await api('GET', `/repos/${REPO}/git/commits/${remoteSha}`);
  const remoteTree = await api('GET', `/repos/${REPO}/git/trees/${remoteCommit.tree.sha}?recursive=1`);
  if (remoteTree.truncated) throw new Error('remote tree truncated (repo too large for this tool)');
  const remoteMap = new Map();
  for (const e of remoteTree.tree) if (e.type === 'blob') remoteMap.set(e.path, e.sha);

  const localRaw = git(['-c', 'core.quotepath=false', 'ls-tree', '-r', 'HEAD']);
  const localMap = new Map();
  for (const line of localRaw.split('\n')) {
    const m = line.match(/^\d+\s+blob\s+([0-9a-f]+)\t(.+)$/);
    if (m) localMap.set(m[2], m[1]);
  }

  const changed = [], deleted = [];
  for (const [p, sha] of localMap) if (remoteMap.get(p) !== sha) changed.push({ path: p, sha });
  for (const p of remoteMap.keys()) if (!localMap.has(p)) deleted.push(p);

  console.log(`远端 ${REPO}@${BRANCH} = ${remoteSha.slice(0, 10)}（blobs ${remoteMap.size}）`);
  console.log(`本地 ${LOCAL} = ${localMap.size} 个文件 → 变更 ${changed.length} / 删除 ${deleted.length}`);
  changed.slice(0, 40).forEach(c => console.log('   M ' + c.path));
  if (changed.length > 40) console.log('   ... 其余 ' + (changed.length - 40));
  deleted.slice(0, 20).forEach(p => console.log('   D ' + p));
  if (DRY) { console.log('[dry] 未写入远端'); return; }
  if (!changed.length && !deleted.length) { console.log('远端已与本地一致，无需提交'); return; }

  const tree = [];
  let n = 0;
  for (const c of changed) {
    const buf = git(['cat-file', 'blob', c.sha], null, 'buffer');
    const blob = await api('POST', `/repos/${REPO}/git/blobs`, { content: buf.toString('base64'), encoding: 'base64' });
    if (blob.sha !== c.sha) console.log('   ⚠ blob sha 不一致（内容取自 git 对象却不同）: ' + c.path);
    tree.push({ path: c.path, mode: '100644', type: 'blob', sha: blob.sha });
    if (++n % 25 === 0) console.log('   blob ' + n + '/' + changed.length);
  }
  for (const p of deleted) tree.push({ path: p, mode: '100644', type: 'blob', sha: null });

  const newTree = await api('POST', `/repos/${REPO}/git/trees`, { base_tree: remoteCommit.tree.sha, tree });
  const newCommit = await api('POST', `/repos/${REPO}/git/commits`, { message: MESSAGE, tree: newTree.sha, parents: [remoteSha] });
  await api('PATCH', `/repos/${REPO}/git/refs/heads/${BRANCH}`, { sha: newCommit.sha, force: false });
  console.log(`提交 ${newCommit.sha.slice(0, 10)} → ${BRANCH}`);

  if (TAG) {
    try { await api('POST', `/repos/${REPO}/git/refs`, { ref: 'refs/tags/' + TAG, sha: newCommit.sha }); console.log('tag ' + TAG + ' 已建'); }
    catch (e) {
      if (/already exists|422/.test(e.message)) { await api('PATCH', `/repos/${REPO}/git/refs/tags/${TAG}`, { sha: newCommit.sha, force: true }); console.log('tag ' + TAG + ' 已存在 → 指向新提交'); }
      else throw e;
    }
  }

  const chk = await api('GET', `/repos/${REPO}/git/trees/${newCommit.sha}?recursive=1`);
  const nm = new Map(); for (const e of chk.tree) if (e.type === 'blob') nm.set(e.path, e.sha);
  let diff = 0;
  for (const [p, sha] of localMap) if (nm.get(p) !== sha) { diff++; if (diff <= 5) console.log('   差异: ' + p); }
  for (const p of nm.keys()) if (!localMap.has(p)) { diff++; if (diff <= 5) console.log('   多余: ' + p); }
  console.log('校验：本地 vs 远端新树差异 = ' + diff + (diff === 0 ? '  ✓ 完全一致' : '  ⚠'));
  process.exit(diff === 0 ? 0 : 1);
})().catch(e => { console.error('ERROR: ' + e.message); process.exit(1); });
