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
  { path: 'tools/public-templates/README.md', required: true },
  { path: 'tools/public-templates/README_EN.md', required: true },
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

  if (bad.length) {
    console.log('  doc numbers do not match inventory (' + bad.length + ' of ' + total + ' claims):');
    for (const b of bad) {
      if (b.reason) { console.log('    x ' + b.file + ' -- ' + b.reason); continue; }
      console.log('    x ' + b.file + ':' + b.line + '  ' + b.metric + ' = ' + b.got + '  (actual ' + b.want + ')');
      console.log('      | ' + b.text);
    }
    console.log('  actual: pluginMethods=' + pluginMethods + '  mcpToolsAlias=' + mcp.alias + '  mcpToolsDefault=' + mcp.def);
    process.exit(1);
  }
  console.log('  ' + total + ' numeric claims in ' + DOCS.length + ' docs match: pluginMethods=' + pluginMethods + (METHODS_ONLY ? ' (methods-only mode)' : '  mcpTools=' + mcp.alias + ' (default ' + mcp.def + ')'));
  process.exit(0);
})().catch((e) => { console.log('  FAILED: ' + e.message); process.exit(2); });
