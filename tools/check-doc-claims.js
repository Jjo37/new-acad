#!/usr/bin/env node
/**
 * check-doc-claims.js -- doc numeric claims must match the live inventory (2026-10-21)
 *
 * Why: check-doc-coverage only proves "keyword present"; check-method-names only
 * compares code against code. Nothing ever compared NUMBERS written in docs with
 * reality, so public README drifted to "398 CAD methods" (real 420) and
 * "270 MCP tools" (real 274) for several releases without a single gate firing.
 *
 * Metrics (computed, never hardcoded):
 *   pluginMethods  -- tools/check-method-names.js --json (static listMethods surface)
 *   mcpToolsAlias  -- MCP tools/list with CIVIL3D_ENABLE_TOOL_ALIASES=true
 *   mcpToolsDefault-- MCP tools/list with default env
 *
 * Docs scanned: user-facing files only (public README templates, POSITIONING,
 * BOOTSTRAP, the shipped manual). Archive / DASHBOARD history is excluded on
 * purpose: those legitimately quote past numbers.
 *
 * Exit: 0 all claims match, 1 mismatch, 2 cannot compute metrics.
 */
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const NODE = process.execPath;
const MCP_DIR = path.join(ROOT, 'server', 'mcp');

// required: must exist everywhere (dev + public snapshot).
// optional: internal docs the public snapshot intentionally drops.
const DOCS = [
  // The public snapshot ships the rendered READMEs at the repo root and drops the template dir
  // (developer docs stay local since 2026-10-24), so both locations are optional-on-presence.
  { path: 'README.md', required: false },
  { path: 'README_EN.md', required: false },
  { path: 'tools/public-templates/README.md', required: false },
  { path: 'tools/public-templates/README_EN.md', required: false },
  { path: 'POSITIONING.md', required: false },
  { path: 'BOOTSTRAP.md', required: false },
  { path: '\u4f7f\u7528\u624b\u518c.html', required: true },
];

// ---------- metric 1: plugin methods (static) ----------
function pluginMethodCount() {
  const r = spawnSync(NODE, [path.join(ROOT, 'tools', 'check-method-names.js'), '--json'], { encoding: 'utf8' });
  const out = (r.stdout || '').trim();
  if (!out) throw new Error('check-method-names.js --json produced no output: ' + (r.stderr || '').slice(0, 200));
  const last = out.split(/\r?\n/).pop();
  const j = JSON.parse(last);
  if (typeof j.advertised !== 'number') throw new Error('no advertised count in --json output');
  return j.advertised;
}

// ---------- metric 2/3: MCP tools/list ----------
function mcpCounts() {
  const sdkClient = path.join(MCP_DIR, 'node_modules', '@modelcontextprotocol', 'sdk', 'dist', 'cjs', 'client', 'index.js');
  const sdkStdio = path.join(MCP_DIR, 'node_modules', '@modelcontextprotocol', 'sdk', 'dist', 'cjs', 'client', 'stdio.js');
  const built = path.join(MCP_DIR, 'build', 'index.js');
  if (!fs.existsSync(sdkClient) || !fs.existsSync(sdkStdio)) {
    throw new Error('MCP SDK missing -> run: cd server/mcp && npm ci');
  }
  if (!fs.existsSync(built)) {
    throw new Error('MCP build missing -> run: cd server/mcp && npm run build');
  }
  const { Client } = require(sdkClient);
  const { StdioClientTransport } = require(sdkStdio);

  async function list(aliases) {
    const env = { ...process.env };
    if (aliases) env.CIVIL3D_ENABLE_TOOL_ALIASES = 'true';
    else delete env.CIVIL3D_ENABLE_TOOL_ALIASES;
    const transport = new StdioClientTransport({ command: NODE, args: [built], cwd: MCP_DIR, env, stderr: 'ignore' });
    const client = new Client({ name: 'doc-claims', version: '1.0.0' }, { capabilities: {} });
    await client.connect(transport);
    try {
      const res = await client.listTools();
      return (res.tools || []).length;
    } finally {
      await client.close().catch(() => {});
    }
  }
  return (async () => ({ alias: await list(true), def: await list(false) }))();
}

// ---------- claim scanning ----------
const RULES = [
  { metric: 'pluginMethods', re: /(\d{3,4})\s*(?:\u4e2a)?\s*CAD\s*\u65b9\u6cd5|(\d{3,4})\s+CAD\s+methods/gi },
  // bare "423 \u65b9\u6cd5" / "423 methods" -- no "CAD" in front. Restricted to 3-4 digits on purpose:
  // the manual legitimately says "12 \u4e2a\u65b9\u6cd5\u6ca1\u6709\u516c\u5f00 API", and that is not the method surface.
  // 2026-10-24: the public README said "420 \u65b9\u6cd5\u76ee\u5f55" for a release and no gate fired.
  { metric: 'pluginMethods', re: /(\d{3,4})\s*(?:\u4e2a)?\s*\u65b9\u6cd5|(\d{3,4})\s+methods/gi },
  { metric: 'mcpToolsAlias', re: /(\d{3,4})\s*(?:\u4e2a)?\s*MCP\s*(?:\u5de5\u5177|tools)/gi },
  // diagram / shorthand forms: "（server/mcp，270 工具）" / "(server/mcp, 270 tools)" -- only on lines that mention MCP
  { metric: 'mcpToolsAlias', re: /(\d{3,4})\s*(?:\u4e2a)?\s*(?:\u5de5\u5177|tools)/gi, needLine: /mcp/i },
];

function scanDoc(file, expected) {
  const abs = path.join(ROOT, file);
  if (!fs.existsSync(abs)) return { missing: true, hits: [] };
  const lines = fs.readFileSync(abs, 'utf8').split(/\r?\n/);
  const hits = [];
  lines.forEach((raw, i) => {
    const line = raw.replace(/<[^>]+>/g, ' ');
    for (const rule of RULES) {
      if (!(rule.metric in expected)) continue; // --methods-only mode: only check computed metrics
      if (rule.needLine && !rule.needLine.test(line)) continue;
      rule.re.lastIndex = 0;
      let m;
      while ((m = rule.re.exec(line)) !== null) {
        const num = Number(m[1] || m[2]);
        if (!Number.isFinite(num)) continue;
        hits.push({ line: i + 1, metric: rule.metric, got: num, want: expected[rule.metric], text: line.trim().slice(0, 110) });
      }
    }
  });
  return { missing: false, hits };
}

const METHODS_ONLY = process.argv.includes('--methods-only');

// ---------- version claims ----------
// Docs that tell a user "download this file / this is the version you are reading" must name the
// version we actually ship. The manual said v1.10.0 in its English footer and in four install
// commands for a whole release, and nothing checked it (2026-10-24).
const VERSION_DOCS = ['README.md', 'README_EN.md', 'tools/public-templates/README.md', 'tools/public-templates/README_EN.md', '\u4f7f\u7528\u624b\u518c.html'];
const VERSION_RES = [
  /new-acad-setup-v(\d+\.\d+\.\d+)\.exe/g,
  /new-acad-v(\d+\.\d+\.\d+)\.zip/g,
  /(?:\u5bf9\u5e94\u7248\u672c|for version)\s*v?(\d+\.\d+\.\d+)/gi,
];

function versionClaims(wantVersion) {
  const bad = [];
  let total = 0;
  for (const file of VERSION_DOCS) {
    const abs = path.join(ROOT, file);
    if (!fs.existsSync(abs)) continue;
    const lines = fs.readFileSync(abs, 'utf8').split(/\r?\n/);
    lines.forEach((raw, i) => {
      for (const re of VERSION_RES) {
        re.lastIndex = 0;
        let m;
        while ((m = re.exec(raw)) !== null) {
          total++;
          if (m[1] !== wantVersion) {
            bad.push({ file, line: i + 1, got: m[1], want: wantVersion, text: raw.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 110) });
          }
        }
      }
    });
  }
  return { total, bad };
}

const PKG_VERSION = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8')).version;

(async () => {
  let pluginMethods, mcp;
  try {
    pluginMethods = pluginMethodCount();
    mcp = METHODS_ONLY ? { alias: 0, def: 0 } : await mcpCounts();
  } catch (e) {
    console.log('  CANNOT COMPUTE METRICS: ' + e.message);
    process.exit(2);
  }
  const expected = METHODS_ONLY ? { pluginMethods } : { pluginMethods, mcpToolsAlias: mcp.alias };

  const bad = [];
  let total = 0;
  for (const doc of DOCS) {
    const { missing, hits } = scanDoc(doc.path, expected);
    if (missing) {
      if (doc.required) { console.log('  MISSING DOC: ' + doc.path); bad.push({ file: doc.path, line: 0, reason: 'required file not found' }); }
      else { console.log('  ~ skip (not in this line): ' + doc.path); }
      continue;
    }
    total += hits.length;
    for (const h of hits) if (h.got !== h.want) bad.push({ file: doc.path, ...h });
  }

  const ver = METHODS_ONLY ? { total: 0, bad: [] } : versionClaims(PKG_VERSION);
  total += ver.total;
  for (const b of ver.bad) bad.push({ ...b, metric: 'version' });

  if (bad.length) {
    console.log('  doc numbers do not match inventory (' + bad.length + ' of ' + total + ' claims):');
    for (const b of bad) {
      if (b.reason) { console.log('    x ' + b.file + ' -- ' + b.reason); continue; }
      if (b.metric === 'version') {
        console.log('    x ' + b.file + ':' + b.line + '  version = ' + b.got + '  (shipping ' + b.want + ')');
        console.log('      | ' + b.text);
        continue;
      }
      console.log('    x ' + b.file + ':' + b.line + '  ' + b.metric + ' = ' + b.got + '  (actual ' + b.want + ')');
      console.log('      | ' + b.text);
    }
    console.log('  actual: pluginMethods=' + pluginMethods + '  mcpToolsAlias=' + mcp.alias + '  mcpToolsDefault=' + mcp.def);
    process.exit(1);
  }
  console.log('  ' + total + ' numeric claims in ' + DOCS.length + ' docs match: pluginMethods=' + pluginMethods + (METHODS_ONLY ? ' (methods-only mode)' : '  mcpTools=' + mcp.alias + ' (default ' + mcp.def + ')  version=' + PKG_VERSION));
  process.exit(0);
})().catch((e) => { console.log('  FAILED: ' + e.message); process.exit(2); });
