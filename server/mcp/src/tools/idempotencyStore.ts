type StoredExecution<T> = {
  signature: string;
  promise: Promise<T>;
  expiresAt: number;
  settled: boolean;
};

export class IdempotencyConflictError extends Error {
  readonly code = "CIVIL3D.CONFLICT";
  readonly rpcCode = -32009;

  constructor(scope: string, key: string) {
    super(`Idempotency key '${key}' was already used with different parameters for '${scope}'.`);
    this.name = "IdempotencyConflictError";
  }
}

export class IdempotencyCapacityError extends Error {
  readonly code = "CIVIL3D.HOST_BUSY";
  readonly rpcCode = -32029;

  constructor(capacity: number) {
    super(`Idempotency store has ${capacity} in-flight executions; retry after work completes.`);
    this.name = "IdempotencyCapacityError";
  }
}

export class IdempotencyStore {
  private readonly entries = new Map<string, StoredExecution<unknown>>();

  constructor(
    private readonly maxEntries = 512,
    private readonly ttlMs = 2 * 60 * 1000,
    private readonly now: () => number = Date.now,
  ) {}

  async execute<T>(scope: string, key: string, parameters: unknown, factory: () => Promise<T>): Promise<T> {
    this.purgeExpired();
    const cacheKey = `${scope}\u0000${key}`;
    const signature = stableSerialize(parameters);
    const existing = this.entries.get(cacheKey) as StoredExecution<T> | undefined;

    if (existing) {
      if (existing.signature !== signature) {
        throw new IdempotencyConflictError(scope, key);
      }
      return existing.promise;
    }

    this.evictForCapacity();
    const promise = Promise.resolve().then(factory);
    const entry: StoredExecution<T> = {
      signature,
      promise,
      expiresAt: this.now() + this.ttlMs,
      settled: false,
    };
    this.entries.set(cacheKey, entry);

    try {
      const result = await promise;
      if (this.entries.get(cacheKey) === entry) {
        entry.settled = true;
        entry.expiresAt = this.now() + this.ttlMs;
      }
      return result;
    } catch (error) {
      if (this.entries.get(cacheKey)?.promise === promise) {
        this.entries.delete(cacheKey);
      }
      throw error;
    }
  }

  get size(): number {
    this.purgeExpired();
    return this.entries.size;
  }

  private purgeExpired(): void {
    const now = this.now();
    for (const [key, entry] of this.entries) {
      if (entry.settled && entry.expiresAt <= now) this.entries.delete(key);
    }
  }

  private evictForCapacity(): void {
    while (this.entries.size >= this.maxEntries) {
      const oldestSettled = [...this.entries].find(([, entry]) => entry.settled)?.[0];
      if (oldestSettled === undefined) throw new IdempotencyCapacityError(this.maxEntries);
      this.entries.delete(oldestSettled);
    }
  }
}

function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value) ?? "undefined";
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(",")}]`;
  const object = value as Record<string, unknown>;
  return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableSerialize(object[key])}`).join(",")}}`;
}

export const idempotencyStore = new IdempotencyStore();
