import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const pluginDirectory = fileURLToPath(new URL("../../../plugin/AcBridge-v24/src/", import.meta.url));

describe("P2 security boundaries", () => {
  it("keeps generated artifact writes behind FileBoundary", () => {
    // 2026-10-15: 只看“真写入”。原来的正则把只读 FileStream(FileAccess.Read) 也算违规（3 处误报）。
    const directWritePattern = /File\.WriteAllText|File\.WriteAllBytes|File\.AppendAllText|new\s+(?:System\.IO\.)?StreamWriter|new\s+(?:System\.IO\.)?FileStream\([^)]*FileAccess\.Write/;
    // 经核实的例外（有意直接写，均非“绕过边界的用户产物”）——注意正则收得很紧，
    // 同文件里出现其它形式的直接写仍会被报：
    //  - AssemblyCreationCommands：写 %TEMP%\hank_sac\<guid>\ 下的 xaml/cfg/atc/emd/pvd（SAC 打包中间文件）
    //  - AcadCommands：writeFile 工具，用户显式指定的绝对路径（已过 AssertReadablePath + 扩展名黑名单 + 2MB 上限 + 不覆盖）
    //  - OperationTrace：操作轨迹日志（<root>\exchange\trace，best-effort，失败不影响主流程）
    const allowedDirectWrites: Record<string, RegExp> = {
      "AssemblyCreationCommands.cs": /File\.WriteAllText\((?:xamlFile|cfgFile|atcFile|emdFile|pvdFile)/,
      "AcadCommands.cs": /File\.WriteAllBytes\(full, bytes\)/,
      "OperationTrace.cs": /File\.AppendAllText\(CurrentTraceFile\(\)/,
    };
    const violations: string[] = [];

    for (const fileName of readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"))) {
      if (fileName === "FileBoundary.cs" || fileName === "PluginLog.cs") continue;
      const allow = allowedDirectWrites[fileName];
      const source = readFileSync(`${pluginDirectory}/${fileName}`, "utf8");
      source.split(/\r?\n/).forEach((line, index) => {
        if (!directWritePattern.test(line)) return;
        if (allow && allow.test(line)) return;
        violations.push(`${fileName}:${index + 1}`);
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
