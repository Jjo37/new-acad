import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

function readVersion(path: string): string {
  const value = JSON.parse(readFileSync(path, "utf8")) as { version?: string };
  return value.version ?? "unknown";
}

const require = createRequire(import.meta.url);
const packageJsonPath = new URL("../package.json", import.meta.url);
const sdkEntry = require.resolve("@modelcontextprotocol/sdk/server/mcp.js");

export const APP_VERSION = readVersion(fileURLToPath(packageJsonPath));
export const MCP_SDK_VERSION = readVersion(resolve(dirname(sdkEntry), "../../../package.json"));

export function dependencyVersions() {
  return {
    application: APP_VERSION,
    mcpSdk: MCP_SDK_VERSION,
    node: process.version,
  };
}
