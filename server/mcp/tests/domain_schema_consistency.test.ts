import { describe, expect, it } from "vitest";
import { MIGRATED_DOMAIN_DEFINITIONS } from "../src/tools/toolManifest.js";
import type { DomainToolDefinition, DomainToolExposure } from "../src/tools/domainRuntime.js";

// 回归防护：civil3d_surface 的规范输入里 method 曾只允许体积法枚举，
// 而 sample_elevations 需要 grid/points/transect → 经 MCP 永远参数校验失败。
function surfaceExposure(name: string): DomainToolExposure {
  const surface = MIGRATED_DOMAIN_DEFINITIONS.find(
    (d) => (d as DomainToolDefinition).domain === "surface",
  ) as DomainToolDefinition;
  const exposure = surface.exposures.find((e) => e.toolName === name);
  expect(exposure, `${name} exposure exists`).toBeDefined();
  return exposure as DomainToolExposure;
}

describe("civil3d_surface method enum", () => {
  it("canonical exposure accepts both volume and sampling method values", () => {
    const shape = surfaceExposure("civil3d_surface").inputShape as Record<string, { safeParse: (v: unknown) => { success: boolean } }>;
    for (const v of ["tin_volume", "average_end_area", "prismoidal", "grid", "points", "transect"]) {
      expect(shape.method.safeParse(v).success, `method '${v}' should be accepted`).toBe(true);
    }
    expect(shape.method.safeParse("bogus").success).toBe(false);
  });

  it("dedicated sample_elevations alias restricts to sampling methods", () => {
    const shape = surfaceExposure("civil3d_surface_sample_elevations").inputShape as Record<string, { safeParse: (v: unknown) => { success: boolean } }>;
    expect(shape.method.safeParse("grid").success).toBe(true);
    expect(shape.method.safeParse("tin_volume").success).toBe(false);
  });
});
