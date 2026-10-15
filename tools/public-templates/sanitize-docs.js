// sanitize-docs.js <outDir> <tplDir>
// Public-repo doc pass:
//  1. install the public README.md / README_EN.md from templates
//  2. drop tree lines that reference internal-only docs (BOOTSTRAP / DASHBOARD / ROADMAP)
//  3. de-personalize ONBOARDING.md (absolute dev paths -> <安装目录>, refresh architecture line)
const fs = require('fs');
const path = require('path');

const out = process.argv[2];
const tpl = process.argv[3];
if (!out || !tpl) {
  console.error('usage: node sanitize-docs.js <outDir> <tplDir>');
  process.exit(2);
}

const INTERNAL = /BOOTSTRAP\.md|DASHBOARD\.md|ROADMAP\.md/;
const log = [];

// 1. public READMEs
for (const name of ['README.md', 'README_EN.md']) {
  const src = path.join(tpl, name);
  if (!fs.existsSync(src)) { console.error('missing template: ' + src); process.exit(1); }
  fs.copyFileSync(src, path.join(out, name));
  log.push('installed ' + name);
}

// 2 + 3. ONBOARDING scrub
const ob = path.join(out, 'ONBOARDING.md');
if (fs.existsSync(ob)) {
  let t = fs.readFileSync(ob, 'utf8').replace(/\r\n/g, '\n');
  const before = t;

  // drop internal-doc tree lines
  t = t.split('\n').filter((l) => !INTERNAL.test(l)).join('\n');

  // de-personalize absolute dev paths
  t = t.split('D:\\new-acad').join('<安装目录>');

  // refresh stale architecture line + version header
  t = t.replace(
    '面板 → relay :19876 → 网关 → AI → /tcp 桥 → C3D 插件 :8080',
    '面板 → relay :19876 → AI（内置 LLM 代理，或自建 OpenClaw 网关）→ /tcp 桥 → C3D 插件 :8080'
  );
  // 2026-09-14: ONBOARDING 模板已更新到 v1.4.1 架构行；这里保留一次幂等归一（旧快照/旧分支仍能修好）
  t = t.replace('更新: 2026-08-04（同步 v1.2.2 架构', '更新: 2026-09-14（同步 v1.4.1 架构');
  t = t.replace('更新: 2026-09-13（同步 v1.3.3 架构', '更新: 2026-09-14（同步 v1.4.1 架构');

  t = t.replace(/\s*$/, '\n');
  if (t !== before) {
    fs.writeFileSync(ob, t, 'utf8');
    log.push('scrubbed ONBOARDING.md');
  }
}

console.log('sanitized: ' + log.join(', '));
