import fs from "node:fs";
import path from "node:path";
import type { HelpInstallation } from "./types.js";

const INSTALL_NAME = /^Offline Help for Civil 3D (\d{4}) - (.+)$/i;

export interface HelpDiscoveryOptions {
  configuredRoot?: string;
  configuredVersion?: string;
  programFilesRoot?: string;
}

export function discoverOfflineHelpInstallations(
  options: HelpDiscoveryOptions = {},
): HelpInstallation[] {
  const configuredRoot = options.configuredRoot ?? process.env.CIVIL3D_HELP_ROOT;
  const configuredVersion = options.configuredVersion ?? process.env.CIVIL3D_HELP_VERSION;
  const installations: HelpInstallation[] = [];

  if (configuredRoot) {
    const root = path.resolve(configuredRoot);
    if (isHelpRoot(root)) {
      installations.push({
        root,
        version: configuredVersion ?? detectVersion(root) ?? "unknown",
        language: detectLanguage(root) ?? "unknown",
        displayName: "Configured Civil 3D offline help",
        configured: true,
      });
    }
  }

  const programFilesRoot = options.programFilesRoot
    ?? process.env.ProgramFiles
    ?? "C:\\Program Files";
  const autodeskRoot = path.join(programFilesRoot, "Autodesk");
  if (fs.existsSync(autodeskRoot)) {
    for (const entry of fs.readdirSync(autodeskRoot, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const match = entry.name.match(INSTALL_NAME);
      if (!match) continue;
      const root = path.join(autodeskRoot, entry.name, "Help");
      if (!isHelpRoot(root)) continue;
      installations.push({
        root: path.resolve(root),
        version: match[1],
        language: match[2],
        displayName: entry.name,
        configured: false,
      });
    }
  }

  const unique = new Map<string, HelpInstallation>();
  for (const installation of installations) {
    const key = installation.root.toLowerCase();
    if (!unique.has(key) || installation.configured) unique.set(key, installation);
  }

  return [...unique.values()].sort((left, right) => {
    if (left.configured !== right.configured) return left.configured ? -1 : 1;
    if (configuredVersion) {
      if (left.version === configuredVersion && right.version !== configuredVersion) return -1;
      if (right.version === configuredVersion && left.version !== configuredVersion) return 1;
    }
    return Number(right.version) - Number(left.version) || left.language.localeCompare(right.language);
  });
}

export function selectHelpInstallation(
  installations: HelpInstallation[],
  requestedVersion?: string,
): HelpInstallation | undefined {
  const preferredVersion = requestedVersion
    ?? process.env.CIVIL3D_HELP_VERSION
    ?? "2026";
  return installations.find((installation) => installation.version === preferredVersion)
    ?? installations[0];
}

export function assertPathInsideHelpRoot(root: string, candidate: string): string {
  const resolvedRoot = fs.realpathSync.native(path.resolve(root));
  const resolvedCandidate = fs.realpathSync.native(path.resolve(candidate));
  const relative = path.relative(resolvedRoot, resolvedCandidate);
  if (!relative || (!relative.startsWith("..") && !path.isAbsolute(relative))) {
    return resolvedCandidate;
  }
  throw new Error("Requested Civil 3D help asset is outside the configured help root.");
}

function isHelpRoot(root: string): boolean {
  if (!fs.existsSync(root) || !fs.statSync(root).isDirectory()) return false;
  if (!fs.existsSync(path.join(root, "index.html"))) return false;
  return fs.readdirSync(root, { withFileTypes: true }).some((entry) =>
    entry.isDirectory() && (/^wrapped-files/i.test(entry.name) || /^files/i.test(entry.name))
  );
}

function detectVersion(root: string): string | undefined {
  return root.match(/Civil 3D (\d{4})/i)?.[1];
}

function detectLanguage(root: string): string | undefined {
  return root.match(/Offline Help for Civil 3D \d{4} - ([^\\/]+)/i)?.[1];
}
