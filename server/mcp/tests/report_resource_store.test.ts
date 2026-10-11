import { beforeEach, describe, expect, it } from "vitest";
import {
  clearReportResourcesForTesting,
  listReportResources,
  maybeStoreReportResource,
} from "../src/tools/reportResourceStore.js";

describe("report resource store", () => {
  beforeEach(() => clearReportResourcesForTesting());

  it("does not retain an oversized report", () => {
    const oversized = "x".repeat(8 * 1024 * 1024 + 1);
    expect(maybeStoreReportResource("export", oversized)).toBeUndefined();
    expect(listReportResources()).toHaveLength(0);
  });

  it("evicts old reports to keep retained bytes bounded", () => {
    const chunk = "x".repeat(2 * 1024 * 1024);
    for (let index = 0; index < 40; index += 1) {
      expect(maybeStoreReportResource(`report_${index}`, chunk)).toBeDefined();
    }
    const retained = listReportResources();
    expect(retained.length).toBeLessThan(40);
    expect(retained.reduce((total, report) => total + report.size, 0))
      .toBeLessThanOrEqual(64 * 1024 * 1024);
  });
});
