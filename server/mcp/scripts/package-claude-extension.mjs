import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  access,
  copyFile,
  cp,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const packageJsonPath = path.join(repositoryRoot, "package.json");
const packageLockPath = path.join(repositoryRoot, "package-lock.json");
const manifestSourcePath = path.join(
  repositoryRoot,
  "packaging",
  "claude-desktop",
  "manifest.json",
);
const extensionReadmePath = path.join(
  repositoryRoot,
  "packaging",
  "claude-desktop",
  "README.md",
);
const outputDirectory = path.join(repositoryRoot, "dist", "claude-desktop");
const stagingDirectory = path.join(outputDirectory, "civil3d-mcp");
const npmCliPath = process.env.npm_execpath;

if (!npmCliPath) {
  throw new Error("Run this script through an npm command so npm_execpath is available.");
}

function runNpm(args, cwd = repositoryRoot) {
  run(process.execPath, [npmCliPath, ...args], cwd);
}

function run(command, args, cwd = repositoryRoot) {
  const result = spawnSync(command, args, {
    cwd,
    env: process.env,
    stdio: "inherit",
  });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} exited with code ${result.status}.`);
  }
}

async function sha256(filePath) {
  const contents = await readFile(filePath);
  return createHash("sha256").update(contents).digest("hex");
}

async function requireFile(filePath, message) {
  try {
    await access(filePath);
  } catch {
    throw new Error(`${message}: ${filePath}`);
  }
}

const packageJson = JSON.parse(await readFile(packageJsonPath, "utf8"));
const artifactBaseName = `${packageJson.name}-${packageJson.version}`;
const mcpbPath = path.join(outputDirectory, `${artifactBaseName}.mcpb`);
const dxtPath = path.join(outputDirectory, `${artifactBaseName}.dxt`);
const checksumPath = path.join(outputDirectory, "SHA256SUMS.txt");
const validateOnly = process.argv.includes("--validate-only");

if (validateOnly) {
  await requireFile(mcpbPath, "MCPB artifact not found; run npm run package:claude first");
  await requireFile(dxtPath, "Legacy DXT artifact not found; run npm run package:claude first");

  runNpm(["exec", "--", "mcpb", "info", mcpbPath]);

  const mcpbHash = await sha256(mcpbPath);
  const dxtHash = await sha256(dxtPath);
  if (mcpbHash !== dxtHash) {
    throw new Error("The legacy .dxt copy does not match the validated .mcpb artifact.");
  }

  console.log(`Validated ${path.relative(repositoryRoot, mcpbPath)}`);
  console.log(`Legacy copy matches (${mcpbHash}).`);
  process.exit(0);
}

await requireFile(
  path.join(repositoryRoot, "build", "index.js"),
  "Compiled server entry point not found; run npm run build first",
);

const manifest = JSON.parse(await readFile(manifestSourcePath, "utf8"));
manifest.version = packageJson.version;

await rm(stagingDirectory, { recursive: true, force: true });
await mkdir(path.join(stagingDirectory, "server"), { recursive: true });
await cp(path.join(repositoryRoot, "build"), path.join(stagingDirectory, "server"), {
  recursive: true,
});
await copyFile(path.join(repositoryRoot, "LICENSE"), path.join(stagingDirectory, "LICENSE"));
await copyFile(extensionReadmePath, path.join(stagingDirectory, "README.md"));
await copyFile(packageJsonPath, path.join(stagingDirectory, "package.json"));
await copyFile(packageLockPath, path.join(stagingDirectory, "package-lock.json"));
await writeFile(
  path.join(stagingDirectory, "manifest.json"),
  `${JSON.stringify(manifest, null, 2)}\n`,
  "utf8",
);

runNpm(
  ["ci", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund"],
  stagingDirectory,
);

const runtimePackageJson = {
  name: packageJson.name,
  version: packageJson.version,
  description: packageJson.description,
  type: packageJson.type,
  private: true,
  engines: {
    node: ">=18.17.0",
  },
  dependencies: packageJson.dependencies,
};
await writeFile(
  path.join(stagingDirectory, "package.json"),
  `${JSON.stringify(runtimePackageJson, null, 2)}\n`,
  "utf8",
);
await rm(path.join(stagingDirectory, "package-lock.json"), { force: true });

runNpm(["exec", "--", "mcpb", "validate", path.join(stagingDirectory, "manifest.json")]);
await rm(mcpbPath, { force: true });
await rm(dxtPath, { force: true });
runNpm(["exec", "--", "mcpb", "pack", stagingDirectory, mcpbPath]);
runNpm(["exec", "--", "mcpb", "info", mcpbPath]);

await copyFile(mcpbPath, dxtPath);
const mcpbHash = await sha256(mcpbPath);
const dxtHash = await sha256(dxtPath);
if (mcpbHash !== dxtHash) {
  throw new Error("Failed to create an exact legacy .dxt compatibility copy.");
}

const mcpbSize = (await stat(mcpbPath)).size;
await writeFile(
  checksumPath,
  `${mcpbHash}  ${path.basename(mcpbPath)}\n${dxtHash}  ${path.basename(dxtPath)}\n`,
  "utf8",
);
await rm(stagingDirectory, { recursive: true, force: true });

console.log("Claude Desktop extension package created successfully:");
console.log(`  MCPB: ${mcpbPath}`);
console.log(`  DXT:  ${dxtPath}`);
console.log(`  Size: ${mcpbSize.toLocaleString()} bytes`);
console.log(`  SHA-256: ${mcpbHash}`);
