const confirmation = "DISPOSABLE_DRAWING";
if (process.env.CIVIL3D_LIVE_SMOKE_CONFIRM !== confirmation) {
  throw new Error(`Refusing to mutate Civil 3D. Set CIVIL3D_LIVE_SMOKE_CONFIRM=${confirmation} and use a disposable session.`);
}

const baseUrl = process.env.CIVIL3D_MCP_HTTP_URL ?? "http://127.0.0.1:3000";
const headers = {
  "Content-Type": "application/json",
  ...(process.env.MCP_HTTP_TOKEN
    ? { Authorization: `Bearer ${process.env.MCP_HTTP_TOKEN}` }
    : {}),
};

async function request(path, init = {}) {
  const response = await fetch(`${baseUrl}${path}`, { ...init, headers: { ...headers, ...init.headers } });
  const body = await response.json();
  if (!response.ok) throw new Error(`${path} returned ${response.status}: ${JSON.stringify(body)}`);
  return body;
}

async function execute(tool, parameters) {
  return request("/execute", { method: "POST", body: JSON.stringify({ tool, parameters }) });
}

async function approved(toolName, action, parameters) {
  const receipt = await execute("civil3d_request_approval", { toolName, action, parameters });
  return execute(toolName, { ...parameters, approvalToken: receipt.approvalToken });
}

const ready = await request("/health/ready");
if (!ready.ready) throw new Error(`Civil 3D host is not ready: ${JSON.stringify(ready)}`);

await execute("civil3d_drawing", { action: "info" });
const concurrentQueries = await Promise.all(Array.from({ length: 4 }, () =>
  execute("civil3d_drawing", { action: "info" })));
if (concurrentQueries.length !== 4) throw new Error("Concurrent host-query smoke did not complete.");
await approved("civil3d_drawing", "new", { action: "new" });
await execute("civil3d_drawing", { action: "info" });

const surfaceStamp = Date.now();
const baseSurface = `MCP_LIVE_BASE_${surfaceStamp}`;
const comparisonSurface = `MCP_LIVE_COMPARISON_${surfaceStamp}`;
let volumeRollbackVerified = false;
try {
  await approved("civil3d_surface", "create", { action: "create", name: baseSurface });
  await approved("civil3d_surface", "create", { action: "create", name: comparisonSurface });
  await approved("civil3d_surface", "add_points", {
    action: "add_points",
    name: baseSurface,
    points: [
      { x: 0, y: 0, z: 100 },
      { x: 100, y: 0, z: 100 },
      { x: 0, y: 100, z: 100 },
      { x: 100, y: 100, z: 100 },
    ],
  });
  await approved("civil3d_surface", "add_points", {
    action: "add_points",
    name: comparisonSurface,
    points: [
      { x: 0, y: 0, z: 101 },
      { x: 100, y: 0, z: 102 },
      { x: 0, y: 100, z: 103 },
      { x: 100, y: 100, z: 104 },
    ],
  });

  const surfacesBeforeVolume = await execute("civil3d_surface", { action: "list" });
  const volume = await execute("civil3d_surface", {
    action: "compute_volume",
    baseSurface,
    comparisonSurface,
  });
  if (![volume.cutVolume, volume.fillVolume, volume.netVolume].every(Number.isFinite)) {
    throw new Error(`Surface-volume smoke returned invalid values: ${JSON.stringify(volume)}`);
  }

  const surfacesAfterVolume = await execute("civil3d_surface", { action: "list" });
  const beforeNames = surfacesBeforeVolume.surfaces.map((surface) => surface.name).sort();
  const afterNames = surfacesAfterVolume.surfaces.map((surface) => surface.name).sort();
  if (JSON.stringify(afterNames) !== JSON.stringify(beforeNames)) {
    throw new Error(`Temporary volume surface escaped its read transaction: ${JSON.stringify({ beforeNames, afterNames })}`);
  }
  volumeRollbackVerified = true;
} finally {
  const existingSurfaces = await execute("civil3d_surface", { action: "list" });
  for (const name of [comparisonSurface, baseSurface]) {
    if (existingSurfaces.surfaces.some((surface) => surface.name === name)) {
      await approved("civil3d_surface", "delete", { action: "delete", name });
    }
  }
}
if (!volumeRollbackVerified) throw new Error("Temporary volume-surface rollback was not verified.");

await approved("civil3d_geometry", "create_line_segment", {
  action: "create_line_segment", startX: 10, startY: 10, endX: 110, endY: 10,
});
await approved("civil3d_drawing", "undo", { action: "undo", steps: 1 });

const pointNumber = 900000 + (Date.now() % 90000);
await approved("civil3d_point", "create", {
  action: "create", startNumber: pointNumber,
  points: [{ x: 50, y: 50, z: 100, description: "MCP LIVE SMOKE" }],
});
await approved("civil3d_point", "delete", { action: "delete", pointNumbers: [pointNumber] });

const outputPath = process.env.CIVIL3D_LIVE_SMOKE_QC_PATH;
if (!outputPath?.toLowerCase().endsWith(".txt")) {
  throw new Error("Set CIVIL3D_LIVE_SMOKE_QC_PATH to an allowed disposable .txt report path.");
}
const uniqueOutputPath = outputPath.replace(/\.txt$/i, `-${Date.now()}.txt`);
const job = await approved("civil3d_job", "start", {
  action: "start", operation: "bulk_qc_report", parameters: { outputPath: uniqueOutputPath },
});
let cancelled = await execute("civil3d_job", { action: "cancel", jobId: job.jobId });
if (!cancelled.cancellationRequested) {
  throw new Error(`Job cancellation was not recorded: ${JSON.stringify(cancelled)}`);
}
for (let attempt = 0; attempt < 100 && cancelled.state === "running"; attempt += 1) {
  await new Promise((resolve) => setTimeout(resolve, 100));
  cancelled = await execute("civil3d_job", { action: "status", jobId: job.jobId });
}
if (!new Set(["cancelled", "completed"]).has(cancelled.state)) {
  throw new Error(`Job did not reach a terminal state after cancellation: ${JSON.stringify(cancelled)}`);
}

const completedOutputPath = uniqueOutputPath.replace(/\.txt$/i, "-completed.txt");
let completedJob = await approved("civil3d_job", "start", {
  action: "start", operation: "bulk_qc_report", parameters: { outputPath: completedOutputPath },
});
for (let attempt = 0; attempt < 300 && completedJob.state === "running"; attempt += 1) {
  await new Promise((resolve) => setTimeout(resolve, 100));
  completedJob = await execute("civil3d_job", { action: "status", jobId: completedJob.jobId });
}
if (completedJob.state !== "completed") {
  throw new Error(`Bulk QC completion smoke failed: ${JSON.stringify(completedJob)}`);
}
if (completedJob.durationMs == null || !completedJob.requestId || !completedJob.drawingIdentity || !completedJob.registry?.capacity) {
  throw new Error(`Completed job is missing production telemetry: ${JSON.stringify(completedJob)}`);
}
const responseLimit = Number.parseInt(process.env.CIVIL3D_MAX_RESPONSE_BYTES ?? "8388608", 10);
const completedResponseBytes = Buffer.byteLength(JSON.stringify(completedJob), "utf8");
if (completedResponseBytes >= responseLimit) {
  throw new Error(`Legitimate QC job response (${completedResponseBytes} bytes) exceeds configured limit ${responseLimit}.`);
}

console.log(JSON.stringify({
  status: "passed",
  coverage: ["query", "concurrency", "document-switch", "create", "volume-surface-rollback", "delete", "cancellation", "job-completion", "response-limit"],
  cancellationState: cancelled.state,
  completedJobDurationMs: completedJob.durationMs,
  completedResponseBytes,
}, null, 2));
