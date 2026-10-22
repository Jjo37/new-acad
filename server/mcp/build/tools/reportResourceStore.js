import { randomUUID } from "node:crypto";
const MAX_REPORT_RESOURCES = 64;
const MAX_REPORT_RESOURCE_BYTES = 8 * 1024 * 1024;
const MAX_TOTAL_REPORT_BYTES = 64 * 1024 * 1024;
const REPORT_TTL_MS = 60 * 60 * 1000;
const LARGE_RESULT_BYTES = 32 * 1024;
const REPORT_ACTION = /(?:^|_)(?:report|export|summary|capabilities)(?:_|$)/i;
const reports = new Map();
function removeExpired(now = Date.now()) {
    for (const [id, report] of reports) {
        if (report.expiresAt <= now) {
            reports.delete(id);
        }
    }
}
function retainedBytes() {
    let total = 0;
    for (const report of reports.values())
        total += report.size;
    return total;
}
function enforceCapacity(incomingBytes) {
    while (reports.size >= MAX_REPORT_RESOURCES ||
        retainedBytes() + incomingBytes > MAX_TOTAL_REPORT_BYTES) {
        const oldestId = reports.keys().next().value;
        if (!oldestId)
            return;
        reports.delete(oldestId);
    }
}
export function maybeStoreReportResource(action, serializedResult) {
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
    const report = {
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
export function getReportResource(id) {
    removeExpired();
    return reports.get(id);
}
export function listReportResources() {
    removeExpired();
    return [...reports.values()];
}
export function clearReportResourcesForTesting() {
    reports.clear();
}
