import { randomUUID } from "node:crypto";

const MAX_REPORT_RESOURCES = 64;
const MAX_REPORT_RESOURCE_BYTES = 8 * 1024 * 1024;
const MAX_TOTAL_REPORT_BYTES = 64 * 1024 * 1024;
const REPORT_TTL_MS = 60 * 60 * 1000;
const LARGE_RESULT_BYTES = 32 * 1024;
const REPORT_ACTION = /(?:^|_)(?:report|export|summary|capabilities)(?:_|$)/i;

export interface StoredReportResource {
  id: string;
  uri: string;
  name: string;
  text: string;
  size: number;
  createdAt: number;
  expiresAt: number;
}

const reports = new Map<string, StoredReportResource>();

function removeExpired(now = Date.now()): void {
  for (const [id, report] of reports) {
    if (report.expiresAt <= now) {
      reports.delete(id);
    }
  }
}

function retainedBytes(): number {
  let total = 0;
  for (const report of reports.values()) total += report.size;
  return total;
}

function enforceCapacity(incomingBytes: number): void {
  while (
    reports.size >= MAX_REPORT_RESOURCES ||
    retainedBytes() + incomingBytes > MAX_TOTAL_REPORT_BYTES
  ) {
    const oldestId = reports.keys().next().value as string | undefined;
    if (!oldestId) return;
    reports.delete(oldestId);
  }
}

export function maybeStoreReportResource(
  action: string,
  serializedResult: string,
): StoredReportResource | undefined {
  const size = Buffer.byteLength(serializedResult, "utf8");
  if (size < LARGE_RESULT_BYTES && !REPORT_ACTION.test(action)) {
    return undefined;
  }
  if (size > MAX_REPORT_RESOURCE_BYTES || size > MAX_TOTAL_REPORT_BYTES) {
    return undefined;
  }

  removeExpired();
  enforceCapacity(size);
  const id = randomUUID();
  const now = Date.now();
  const report: StoredReportResource = {
    id,
    uri: `civil3d://reports/${id}`,
    name: `Civil 3D ${action} result`,
    text: serializedResult,
    size,
    createdAt: now,
    expiresAt: now + REPORT_TTL_MS,
  };
  reports.set(id, report);
  return report;
}

export function getReportResource(id: string): StoredReportResource | undefined {
  removeExpired();
  return reports.get(id);
}

export function listReportResources(): StoredReportResource[] {
  removeExpired();
  return [...reports.values()];
}

export function clearReportResourcesForTesting(): void {
  reports.clear();
}
