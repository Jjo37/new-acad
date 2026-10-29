#!/usr/bin/env node
// test-provider-neutrality.js -- 证明"中性"的每条规则真的会红（2026-10-29）
//
// 只跑 check-provider-neutrality.js 通过是不够的：规则可能根本没命中任何东西。
// 这里对每条规则做一个变异（把旧写法放回去 / 打乱顺序 / 删掉声明），要求它 FAIL；
// 再验一次运行期行为：没配置 baseUrl 时 callLLM 必须明确报错，而不是偷偷去调某一家。
//
// Usage: node tools/test-provider-neutrality.js
// Exit: 0 all good, 1 any assertion failed

const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const NODE = process.execPath;
const CHECK = path.join(ROOT, 'tools', 'check-provider-neutrality.js');

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log((ok ? 'PASS  ' : 'FAIL  ') + name + (detail ? '  (' + detail + ')' : ''));
}

function runCheck() {
  try {
    const out = execFileSync(NODE, [CHECK], { encoding: 'utf8', cwd: ROOT });
    return { code: 0, out };
  } catch (e) {
    return { code: e.status || 1, out: (e.stdout || '') + (e.stderr || '') };
  }
}

function withMutant(rel, mutate, name, expectFailOn) {
  const abs = path.join(ROOT, rel);
  const bak = abs + ".neutrality-bak";
  fs.copyFileSync(abs, bak);
  try {
    const before = fs.readFileSync(abs, "utf8");
    const after = mutate(before);
    if (after === before) { check(name + " (mutation applied)", false, "mutate() changed nothing"); return; }
    fs.writeFileSync(abs, after, "utf8");
    const r = runCheck();
    const hit = r.code !== 0 && (!expectFailOn || r.out.includes(expectFailOn));
    check(name, hit, hit ? "gate went red" : "gate stayed green (dead rule)");
  } finally {
    fs.copyFileSync(bak, abs);
    fs.unlinkSync(bak);
  }
}

// baseline must be green
{
  const r = runCheck();
  check("baseline gate", r.code === 0, r.code === 0 ? "green" : r.out.trim().slice(0, 200));
}

// N1: put the old praise back into the manual provider table
withMutant("使用手册.html", (s) => {
  const anchor = "<tr><td>已经有某家的账号</td>";
  const i = s.indexOf(anchor);
  if (i < 0) return s;
  return s.slice(0, i) + '<tr><td>DeepSeek</td><td><code>https://api.deepseek.com/v1</code></td><td>便宜、够用，中文好</td></tr>\n      ' + s.slice(i);
}, "N1 vendor praise is caught", "N1");

// N2: reorder presets so a vendor jumps to the front
withMutant("server/panel-relay.js", (s) => {
  const m = s.match(/const LLM_PRESETS = \[([\s\S]*?)\n\];/);
  if (!m) return s;
  const rows = m[1].split(/\r?\n/).filter((l) => l.trim().startsWith("{"));
  const reordered = [rows[rows.length - 2], ...rows.filter((_, i) => i !== rows.length - 2)];
  const body = "\n" + reordered.join("\n") + "\n";
  return s.replace(m[0], "const LLM_PRESETS = [" + body + "];");
}, "N2 preset order is enforced", "N2");

// N3: bring the silent vendor fallback back
withMutant("server/compact.js", (s) => {
  const from = "    const baseUrl = String(cfg.baseUrl).replace(/\\/+$/, '');";
  if (!s.includes(from)) return s;
  return s.replace(from, "    const baseUrl = (cfg.baseUrl || 'https://api.deepseek.com/v1').replace(/\\/+$/, '');");
}, "N3 vendor fallback is caught", "N3");

// N4: drop the disclaimer from the installer menu
withMutant("install.ps1", (s) => s.replace("（按名称字母排序，顺序不代表推荐）", ""), "N4 installer disclaimer is required", "N4");

// runtime: unconfigured must fail loudly, not call a default vendor
(async () => {
  const agent = require(path.join(ROOT, "server", "llm-agent.js"));
  for (const [name, cfg, want] of [
    ["no baseUrl", { model: "x" }, "未配置模型服务商"],
    ["no model", { baseUrl: "https://example.invalid/v1" }, "未配置模型名"],
  ]) {
    try {
      await agent.callLLM(cfg, [{ role: "user", content: "hi" }], 3000, null, null);
      check("runtime: " + name + " rejects", false, "callLLM resolved instead of rejecting");
    } catch (e) {
      check("runtime: " + name + " rejects", String(e.message).includes(want), e.message.slice(0, 60));
    }
  }

  const bad = results.filter((r) => !r.ok);
  console.log(bad.length === 0 ? "ALL PASS (" + results.length + ")" : "FAILED: " + bad.length + "/" + results.length);
  process.exit(bad.length === 0 ? 0 : 1);
})();
