import { describe, expect, it } from "vitest";
import {
  createRpcRequestId,
  currentAbortSignal,
  currentRequestId,
  runWithRequestId,
} from "../src/utils/requestContext.js";

describe("requestContext", () => {
  it("propagates request identity and caller cancellation through nested work", async () => {
    const controller = new AbortController();
    await runWithRequestId("trace-123", async () => {
      expect(currentRequestId()).toBe("trace-123");
      expect(currentAbortSignal()).toBe(controller.signal);
      expect(createRpcRequestId()).toMatch(/^trace-123\./);

      await runWithRequestId("trace-123", async () => {
        expect(currentAbortSignal()).toBe(controller.signal);
      });
    }, controller.signal);
  });
});
