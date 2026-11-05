import { cp, copyFile, mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = path.join(repositoryRoot, "src", "standards", "data");
const outputDirectory = path.join(repositoryRoot, "build", "standards", "data");

await mkdir(outputDirectory, { recursive: true });
await cp(sourceDirectory, outputDirectory, { recursive: true, force: true });

const videoCatalogSource = path.join(repositoryRoot, "autodesk_videos.json");
const videoCatalogOutput = path.join(repositoryRoot, "build", "help", "data", "autodesk_videos.json");
await mkdir(path.dirname(videoCatalogOutput), { recursive: true });
await copyFile(videoCatalogSource, videoCatalogOutput);
