import { describe, it, expect } from "vitest";
import {
  computeFullFlowCapacity,
  computeVelocity,
  solveRequiredDiameter,
  unitIsMetric,
  pipeUnitProfile,
} from "../src/tools/domains/pipeDomain.js";

const areaOf = (d: number) => (Math.PI * d * d) / 4;

// v1.8.2「单位自适应」回归：Manning 单位常数 + 目标流速默认值 + 单位解析
describe("pipe units — Manning 满管流量 (k = 1.0 SI / 1.486 英尺)", () => {
  it("SI (k=1.0)：与手算 Manning 公式一致 + 量级对账", () => {
    const d = 0.3, slopePct = 1, n = 0.013;
    const got = computeFullFlowCapacity(d, slopePct, n, 1.0);
    const A = areaOf(d), R = d / 4, S = slopePct / 100;
    const want = (1 / n) * A * Math.pow(R, 2 / 3) * Math.sqrt(S);
    expect(got).toBeCloseTo(want, 12);
    expect(got).toBeCloseTo(0.0967, 3); // ≈0.097 m³/s
  });

  it("英制 (k=1.486)：同数值输入下吞吐 = SI × 1.486", () => {
    const si = computeFullFlowCapacity(0.3, 1, 0.013, 1.0);
    const imp = computeFullFlowCapacity(0.3, 1, 0.013, 1.486);
    expect(imp / si).toBeCloseTo(1.486, 12);
  });

  it("单调性：直径↑ / 坡度↑ → 吞吐↑", () => {
    expect(computeFullFlowCapacity(0.4, 1, 0.013, 1)).toBeGreaterThan(computeFullFlowCapacity(0.3, 1, 0.013, 1));
    expect(computeFullFlowCapacity(0.3, 2, 0.013, 1)).toBeGreaterThan(computeFullFlowCapacity(0.3, 1, 0.013, 1));
  });

  it("坡度 0 / 负值 → 有下限保护（不 NaN、不 0 除）", () => {
    const q0 = computeFullFlowCapacity(0.3, 0, 0.013, 1);
    expect(Number.isFinite(q0)).toBe(true);
    expect(q0).toBeGreaterThan(0);
    expect(Number.isFinite(computeFullFlowCapacity(0.3, -1, 0.013, 1))).toBe(true);
  });
});

describe("pipe units — solveRequiredDiameter / computeVelocity", () => {
  it("往返：解出的管径其满管吞吐 ≈ 目标流量", () => {
    const target = 0.05;
    const d = solveRequiredDiameter(target, 1, 0.013, 1.0);
    expect(computeFullFlowCapacity(d, 1, 0.013, 1.0)).toBeCloseTo(target, 6);
  });

  it("英制解出的管径，其英制吞吐同样 ≈ 目标（k 传导正确）", () => {
    const target = 0.05;
    const d = solveRequiredDiameter(target, 1, 0.013, 1.486);
    expect(computeFullFlowCapacity(d, 1, 0.013, 1.486)).toBeCloseTo(target, 6);
  });

  it("computeVelocity = Q / A", () => {
    const d = 0.3, q = 0.05;
    expect(computeVelocity(q, d)).toBeCloseTo(q / areaOf(d), 12);
  });
});

describe("pipe units — 单位画像（纯函数）", () => {
  it("unitIsMetric：args.units 覆盖 > 图纸单位（契约：图纸单位只可能 meters / feet）", () => {
    expect(unitIsMetric("metric", "feet")).toBe(true); // 显式覆盖优先
    expect(unitIsMetric("imperial", "meters")).toBe(false);
    expect(unitIsMetric("auto", "meters")).toBe(true);
    expect(unitIsMetric("auto", "feet")).toBe(false);
    expect(unitIsMetric("auto", "")).toBe(true); // 拿不到 → 默认米制
    expect(unitIsMetric("AUTO", "meters")).toBe(true);
    expect(unitIsMetric("", "FEET")).toBe(false); // 大小写不敏感
  });

  it("pipeUnitProfile：SI vs 英制 k / 目标流速默认值", () => {
    expect(pipeUnitProfile(true)).toEqual({ k: 1.0, vMin: 0.6, vMax: 3.0 });
    expect(pipeUnitProfile(false)).toEqual({ k: 1.486, vMin: 2.0, vMax: 10.0 });
  });
});
