import { describe, expect, it, vi } from "vitest";
import {
  IdempotencyCapacityError,
  IdempotencyConflictError,
  IdempotencyStore,
} from "../src/tools/idempotencyStore.js";

describe("IdempotencyStore", () => {
  it("deduplicates concurrent and completed calls with the same signature", async () => {
    const store = new IdempotencyStore();
    const factory = vi.fn(async () => ({ ok: true }));

    const [first, second] = await Promise.all([
      store.execute("surface:list", "retry-1", { action: "list", nested: { b: 2, a: 1 } }, factory),
      store.execute("surface:list", "retry-1", { nested: { a: 1, b: 2 }, action: "list" }, factory),
    ]);
    const third = await store.execute("surface:list", "retry-1", { action: "list", nested: { a: 1, b: 2 } }, factory);

    expect(first).toEqual({ ok: true });
    expect(second).toBe(first);
    expect(third).toBe(first);
    expect(factory).toHaveBeenCalledTimes(1);
  });

  it("rejects reuse of a key with changed parameters", async () => {
    const store = new IdempotencyStore();
    await store.execute("surface:list", "retry-2", { name: "A" }, async () => "A");

    await expect(store.execute("surface:list", "retry-2", { name: "B" }, async () => "B"))
      .rejects.toBeInstanceOf(IdempotencyConflictError);
  });

  it("does not cache failures and expires successful entries", async () => {
    let now = 100;
    const store = new IdempotencyStore(4, 50, () => now);
    const failing = vi.fn(async () => { throw new Error("transient"); });
    await expect(store.execute("health:get", "retry-3", {}, failing)).rejects.toThrow("transient");

    const successful = vi.fn(async () => "ok");
    await expect(store.execute("health:get", "retry-3", {}, successful)).resolves.toBe("ok");
    now = 151;
    await expect(store.execute("health:get", "retry-3", {}, successful)).resolves.toBe("ok");
    expect(successful).toHaveBeenCalledTimes(2);
  });

  it("never evicts an in-flight execution under capacity pressure", async () => {
    const store = new IdempotencyStore(1);
    let finish!: (value: string) => void;
    const first = store.execute("drawing:info", "active", {}, async () =>
      await new Promise<string>((resolve) => { finish = resolve; }));
    await Promise.resolve();

    await expect(store.execute("surface:list", "second", {}, async () => "duplicate"))
      .rejects.toBeInstanceOf(IdempotencyCapacityError);
    finish("done");
    await expect(first).resolves.toBe("done");
    await expect(store.execute("surface:list", "second", {}, async () => "safe"))
      .resolves.toBe("safe");
  });
});
