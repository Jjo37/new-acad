import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const pluginDirectory = fileURLToPath(new URL("../../../plugin/AcBridge-v24/src/", import.meta.url));

describe("P2 security boundaries", () => {
  it("keeps generated artifact writes behind FileBoundary", () => {
    const directWritePattern = /File\.WriteAllText|new (?:System\.IO\.)?StreamWriter|new (?:System\.IO\.)?FileStream/;
    const violations: string[] = [];

    for (const fileName of readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"))) {
      if (fileName === "FileBoundary.cs" || fileName === "PluginLog.cs") continue;
      const source = readFileSync(`${pluginDirectory}/${fileName}`, "utf8");
      source.split(/\r?\n/).forEach((line, index) => {
        if (directWritePattern.test(line)) violations.push(`${fileName}:${index + 1}`);
      });
    }

    expect(violations).toEqual([]);
  });

  it("canonicalizes paths, enforces roots and extensions, and writes by atomic move", () => {
    const source = readFileSync(`${pluginDirectory}/FileBoundary.cs`, "utf8");
    expect(source).toContain("Path.GetFullPath");
    expect(source).toContain("Path.GetRelativePath");
    expect(source).toContain("CIVIL3D_IMPORT_ROOTS");
    expect(source).toContain("CIVIL3D_EXPORT_ROOTS");
    expect(source).toContain("CIVIL3D.PATH_NOT_ALLOWED");
    expect(source).toContain("CIVIL3D.FILE_TYPE_NOT_ALLOWED");
    expect(source).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain("File.Move(tempPath, path, overwrite)");
    expect(source).toContain("Set overwrite=true");
  });

  it("uses JSON-RPC numeric errors and retains domain identifiers in error.data.code", () => {
    const source = readFileSync(`${pluginDirectory}/JsonRpcProtocol.cs`, "utf8");
    expect(source).toContain('["code"] = numericCode');
    expect(source).toContain('["data"] = new JsonObject');
    expect(source).toContain('["code"] = domainCode');
    expect(source).toContain('"CIVIL3D.METHOD_NOT_FOUND" => -32601');
    expect(source).toContain('"CIVIL3D.INVALID_INPUT" => -32602');
  });
});
