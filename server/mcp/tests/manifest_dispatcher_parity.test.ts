import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { GENERATED_TOOL_CATALOG_ENTRIES } from "../src/tools/toolManifest.js";

describe("manifest/dispatcher parity", () => {
  it("maps every manifest plugin method in the native dispatcher", () => {
    const dispatcher = readFileSync(new URL("../../../plugin/AcBridge-v24/src/CommandDispatcher.cs", import.meta.url), "utf8");
    const dispatched = new Set([...dispatcher.matchAll(/^\s*"([^"]+)"\s*=>/gm)].map((match) => match[1]));
    const declared = new Set(GENERATED_TOOL_CATALOG_ENTRIES.flatMap((entry) => entry.pluginMethods ?? []));
    const missing = [...declared].filter((method) => !dispatched.has(method)).sort();

    expect(dispatched.size).toBeGreaterThan(200);
    expect(missing).toEqual([]);
  });
});
