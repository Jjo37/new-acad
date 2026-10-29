#!/usr/bin/env node
// test-host-evidence.js -- 证明"跨机证据"的接受判据既不过严也不过松 (2026-10-29)
//
// 过严的代价：构建之后多一个 docs-only 提交，好证据就被判无效，人会去重跑一遍"因为门禁说的"。
// 过松的代价：改了插件代码还拿旧证据发版。所以两条都要测：
//   1 sha256 相同                          -> 收
//   2 stamp 提交与 HEAD 的 plugin/ 完全相同  -> 收（本次新增的等价判据）
//   3 stamp 提交早于最后一次插件改动        -> 拒（代码变了，证据作废）
//   4 passed=false / civilAvailable=true   -> 拒
//
// Usage: node tools/test-host-evidence.js

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync, execFileSync } = require("child_process");

const ROOT = path.join(__dirname, "..");
const NODE = process.execPath;
const CHECK = path.join(ROOT, "tools", "check-host-evidence.js");

// a commit from before the last plugin/ change (f1ce116, 2026-10-28 17:21)
const BEFORE_PLUGIN_CHANGE = "32f193d";
// a commit after it, with identical plugin/ source at HEAD
const AFTER_PLUGIN_CHANGE = "6a07b40";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "hostev-"));
const results = [];

function currentDllSha() {
  const crypto = require("crypto");
  return crypto.createHash("sha256").update(fs.readFileSync(path.join(ROOT, "plugin", "AcBridge-v24", "Civil3DMcpPlugin.dll"))).digest("hex");
}
const PKG = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8").replace(/^\uFEFF/, ""));

function writeEvidence(name, over) {
  const base = {
    schema: "new-acad.host-evidence.v1",
    generatedAt: new Date().toISOString(),
    machine: "TESTBOX",
    os: "win32 10.0.0",
    pluginDll: "C:\\test\\Civil3DMcpPlugin.dll",
    pluginDllSha256: "678476b4e086bd1554199c02b6a717481041561f13ed6c895304771c00cc156b",
    checks: [{ name: "x", ok: true, detail: "" }],
    passed: true,
    skipped: false,
    civilAvailable: false,
    buildStamp: AFTER_PLUGIN_CHANGE + "-20261028-181458",
    pluginVersion: PKG.version + ".0",
    hostProduct: "AutoCAD",
  };
  const obj = Object.assign({}, base, over || {});
  fs.writeFileSync(path.join(tmp, name), JSON.stringify(obj, null, 2), "utf8");
}

function run(extra) {
  const r = spawnSync(NODE, [CHECK, "--require", "--dir", tmp].concat(extra || []), { encoding: "utf8" });
  return { code: r.status, out: (r.stdout || "") + (r.stderr || "") };
}

function caseRun(name, expectOk, over) {
  for (const f of fs.readdirSync(tmp)) fs.unlinkSync(path.join(tmp, f));
  writeEvidence("evidence.json", over);
  const r = run();
  const ok = (r.code === 0) === expectOk;
  results.push({ name, ok, code: r.code, out: r.out });
  console.log((ok ? "PASS  " : "FAIL  ") + name + "  (exit=" + r.code + ")");
}

// sanity: the two commits exist and really differ in plugin/ as assumed
{
  const r = spawnSync("git", ["-C", ROOT, "diff", "--quiet", BEFORE_PLUGIN_CHANGE, "HEAD", "--", "plugin/"]);
  const changed = r.status !== 0;
  const r2 = spawnSync("git", ["-C", ROOT, "diff", "--quiet", AFTER_PLUGIN_CHANGE, "HEAD", "--", "plugin/"]);
  const same = r2.status === 0;
  console.log((changed && same ? "PASS  " : "FAIL  ") + "fixture assumption: " + BEFORE_PLUGIN_CHANGE + " changed plugin/, " + AFTER_PLUGIN_CHANGE + " did not");
  results.push({ name: "fixture assumption", ok: changed && same });
}

caseRun("same DLL sha -> accepted", true, { pluginDllSha256: currentDllSha() });
caseRun("older stamp, plugin/ identical -> accepted", true, {});
caseRun("older stamp, plugin/ changed -> rejected", false, { buildStamp: BEFORE_PLUGIN_CHANGE + "-20261028-160000" });
caseRun("stamp commit not in repo -> rejected", false, { buildStamp: "deadbee-20261028-000000" });
caseRun("passed=false -> rejected", false, { passed: false });
caseRun("civilAvailable=true -> rejected", false, { civilAvailable: true });

fs.rmSync(tmp, { recursive: true, force: true });
const bad = results.filter((r) => !r.ok);
console.log(bad.length === 0 ? "ALL PASS (" + results.length + ")" : "FAILED: " + bad.length + "/" + results.length);
if (bad.length) for (const b of bad) console.log("--- " + b.name + " ---\n" + (b.out || "").slice(0, 600));
process.exit(bad.length === 0 ? 0 : 1);
