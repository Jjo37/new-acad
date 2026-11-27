import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// 2026-10-15: 锁定 C# 方法描述表平价 —— 中文表(CommandDispatcher.ListMethodsAsync 内联)
// 与英文表(CommandDescsEn.cs) 的 key 必须一致，且每个 dispatch 出去的方法都有中文描述。
// 防止「改一处漏一处」的描述漂移。
function keysOf(cs: string): Set<string> {
  return new Set([...cs.matchAll(/\["([^"]+)"\]\s*=/g)].map((m) => m[1]));
}
const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), "utf8");

const dispatcher = read("../../../plugin/AcBridge-v24/src/CommandDispatcher.cs");
const englishTable = read("../../../plugin/AcBridge-v24/src/CommandDescsEn.cs");

function chineseDescriptionKeys(): Set<string> {
  const lines = dispatcher.split(/\r?\n/);
  const startLine = lines.findIndex((l) => l.includes("CommandDescsEn.Build()"));
  const dictStart = lines.findIndex((l, i) => i >= startLine && l.includes("new Dictionary<string, string>"));
  let dictEnd = -1;
  for (let i = dictStart + 1; i < lines.length; i++) {
    if (lines[i].trim() === "};") { dictEnd = i; break; }
  }
  return keysOf(lines.slice(dictStart, dictEnd + 1).join("\n"));
}

describe("i18n method-description parity (C#)", () => {
  it("Chinese and English description tables have identical keys", () => {
    const zh = chineseDescriptionKeys();
    const en = keysOf(englishTable);
    expect([...zh].filter((k) => !en.has(k)).sort()).toEqual([]);
    expect([...en].filter((k) => !zh.has(k)).sort()).toEqual([]);
  });

  it("every dispatched method has a Chinese description", () => {
    const dispatched = new Set([...dispatcher.matchAll(/^\s*"([^"]+)"\s*=>/gm)].map((m) => m[1]));
    const zh = chineseDescriptionKeys();
    expect([...dispatched].filter((m) => !zh.has(m)).sort()).toEqual([]);
    expect(dispatched.size).toBeGreaterThan(200);
  });
});
