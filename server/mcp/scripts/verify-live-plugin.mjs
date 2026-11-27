import { withApplicationConnection } from "../build/utils/ConnectionManager.js";
import { getProjectContext } from "../build/orchestration/ProjectContextService.js";

async function main() {
  const health = await withApplicationConnection(async (client) =>
    client.sendCommand("getCivil3DHealth", {}),
  );
  const context = await getProjectContext();

  process.stdout.write(`${JSON.stringify({ health, context }, null, 2)}\n`);
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error);
  process.stderr.write(`Live Civil 3D plugin validation failed: ${message}\n`);
  process.exitCode = 1;
});
