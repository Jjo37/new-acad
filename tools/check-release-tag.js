#!/usr/bin/env node
/**
 * check-release-tag.js —— 发版前防呆（2026-10-15）
 *
 * 背景：GitHub 的 immutable release 一旦发布，其 tag 名就被**永久占用**——
 * 删掉 release 也救不回（PATCH 发布会报 422 tag_name was used by an immutable release）。
 * 2026-10-15 我们正是踩了这个坑（删 v1.8.2 重发失败，被迫发 v1.8.3）。
 *
 * 作用：发布前查目标 tag：若已存在 release（或 tag 本身已存在）→ 拒绝，提示 +1 版本。
 *
 * 用法：
 *   node tools/check-release-tag.js --repo Jjo37/new-acad --tag v1.9.0-release
 * 退出码：
 *   0 = 安全（可发）
 *   3 = 拒绝（该 tag 已被 release 占用 / tag 已存在）→ 应换新版本号
 *   4 = 无法校验（网络/凭据问题）→ 调用方自行决定（默认放行并告警）
 */
const { execFileSync } = require('child_process');
const https = require('https');

function arg(name, def) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : def;
}
const REPO = arg('repo');
const TAG = arg('tag');
if (!REPO || !TAG) { console.error('usage: node check-release-tag.js --repo <owner/name> --tag <tag>'); process.exit(2); }

const TOK = (() => {
  try {
    const o = execFileSync('git', ['credential', 'fill'], { input: 'protocol=https\nhost=github.com\n\n', encoding: 'utf8' });
    const m = o.match(/^password=(.+)$/m);
    return m ? m[1].trim() : '';
  } catch { return ''; }
})();

function req(method, path) {
  return new Promise((resolve, reject) => {
    const r = https.request({
      host: 'api.github.com', path: path + (path.includes('?') ? '&' : '?') + 'cb=' + Math.random().toString(36).slice(2), method,
      headers: { 'User-Agent': 'new-acad-guard', 'Accept': 'application/vnd.github+json', ...(TOK ? { 'Authorization': 'Bearer ' + TOK } : {}) },
    }, (res) => { let raw = ''; res.on('data', c => raw += c); res.on('end', () => resolve({ status: res.statusCode, raw })); });
    r.on('error', reject); r.end();
  });
}

(async () => {
  try {
    const rel = await req('GET', `/repos/${REPO}/releases/tags/${TAG}`);
    if (rel.status === 200) {
      console.error(`[guard] ✗ tag "${TAG}" 已存在 release —— immutable release 的 tag 名不可复用，发布会失败。`);
      console.error(`[guard]   请改用新版本号（例：v1.9.0-release）。绝不删除已发布的 release。`);
      process.exit(3);
    }
    if (rel.status === 404) {
      const ref = await req('GET', `/repos/${REPO}/git/ref/tags/${TAG}`);
      if (ref.status === 200) {
        console.error(`[guard] ✗ tag "${TAG}" 已存在（虽无 release）—— 名可能已被占用，继续发布有风险。`);
        console.error(`[guard]   请改用新版本号。`);
        process.exit(3);
      }
      console.log(`[guard] ✓ tag "${TAG}" 未被占用，可发布。`);
      process.exit(0);
    }
    console.error(`[guard] ? 校验返回 ${rel.status}（无法确认），放行但请注意。`);
    process.exit(4);
  } catch (e) {
    console.error('[guard] ? 无法访问 api.github.com（' + e.message + '），放行但请注意。');
    process.exit(4);
  }
})();
