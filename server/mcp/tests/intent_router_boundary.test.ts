import { describe, expect, it } from "vitest";
import { routeIntent } from "../src/orchestration/IntentRouter.js";

// 2026-10-15: 标签抽取曾无词界 → "stop 5" 误配 topElevation、"filename: x" 误配 name。
describe("IntentRouter label word boundaries", () => {
  it("does not extract when the label is inside a larger word", () => {
    expect(routeIntent("stop 5").extractedParams.topElevation).toBeUndefined();
    expect(routeIntent("open the filename: report").extractedParams.name).toBeUndefined();
  });

  it("still extracts on genuine labels", () => {
    expect(routeIntent("bottom elevation 2.5").extractedParams.bottomElevation).toBe(2.5);
    expect(routeIntent("create a surface named Ground").extractedParams.surfaceName).toBe("Ground");
  });
});
