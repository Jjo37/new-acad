#!/usr/bin/env node
// test-check-error-codes.js -- 证明 check-error-codes.js 真的会红 (2026-10-29)
//
// 只跑通过不算数：判据可能根本没命中任何东西（今天就是这么漏的 —— 映射表改了，调用点没改）。
// 这里把调用点改回硬编码 -32603，要求检查器 FAIL；恢复后必须绿。
//
// Usage: node tools/test-check-error-codes.js
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const ROOT = path.join(__dirname, "..");
const RUNTIME = path.join(ROOT, "plugin", "AcBridge-v24", "src", "PluginRuntime.cs");
const CHECK = path.join(ROOT, "tools", "check-error-codes.js");

function run() {
  const r = spawnSync(process.execPath, [CHECK, "--json"], { encoding: "utf8", cwd: ROOT });
  let j = null;
  try { j = JSON.parse(r.stdout); } catch { /* keep null */ }
  return { code: r.status, out: (r.stdout || "") + (r.stderr || ""), json: j };
}

const results = [];
const check = (name, ok, detail) => { results.push({ name, ok }); console.log((ok ? "PASS  " : "FAIL  ") + name + (detail ? "  (" + detail + ")" : "")); };

const baseline = run();
check("baseline is green", baseline.code === 0, baseline.json ? baseline.json.notes.join(" / ") : baseline.out.slice(0, 120));

const original = fs.readFileSync(RUNTIME, "utf8");
const patched = original.replace(
  'JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode("CIVIL3D.CIVIL_NOT_INSTALLED"), "CIVIL3D.CIVIL_NOT_INSTALLED"',
  'JsonRpcProtocol.SerializeError(id, -32603, "CIVIL3D.CIVIL_NOT_INSTALLED"'
);
try {
  if (patched === original) throw new Error("mutation anchor not found (call site changed?)");
  fs.writeFileSync(RUNTIME, patched, "utf8");
  const mutant = run();
  check("hardcoded -32603 is caught", mutant.code !== 0 && /CIVIL_NOT_INSTALLED/.test(mutant.out), "exit=" + mutant.code);
} catch (e) {
  check("hardcoded -32603 is caught", false, e.message);
} finally {
  fs.writeFileSync(RUNTIME, original, "utf8");
}

const restored = run();
check("restored is green again", restored.code === 0, "exit=" + restored.code);

const bad = results.filter((r) => !r.ok);
console.log(bad.length === 0 ? "ALL PASS (" + results.length + ")" : "FAILED: " + bad.length + "/" + results.length);
process.exit(bad.length === 0 ? 0 : 1);
