export class IdempotencyConflictError extends Error {
    code = "CIVIL3D.CONFLICT";
    rpcCode = -32009;
    constructor(scope, key) {
        super(`Idempotency key '${key}' was already used with different parameters for '${scope}'.`);
        this.name = "IdempotencyConflictError";
    }
}
export class IdempotencyCapacityError extends Error {
    code = "CIVIL3D.HOST_BUSY";
    rpcCode = -32029;
    constructor(capacity) {
        super(`Idempotency store has ${capacity} in-flight executions; retry after work completes.`);
        this.name = "IdempotencyCapacityError";
    }
}
export class IdempotencyStore {
    maxEntries;
    ttlMs;
    now;
    entries = new Map();
    constructor(maxEntries = 512, ttlMs = 2 * 60 * 1000, now = Date.now) {
        this.maxEntries = maxEntries;
        this.ttlMs = ttlMs;
        this.now = now;
    }
    async execute(scope, key, parameters, factory) {
        this.purgeExpired();
        const cacheKey = `${scope}\u0000${key}`;
        const signature = stableSerialize(parameters);
        const existing = this.entries.get(cacheKey);
        if (existing) {
            if (existing.signature !== signature) {
                throw new IdempotencyConflictError(scope, key);
            }
            return existing.promise;
        }
        this.evictForCapacity();
        const promise = Promise.resolve().then(factory);
        const entry = {
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
        }
        catch (error) {
            if (this.entries.get(cacheKey)?.promise === promise) {
                this.entries.delete(cacheKey);
            }
            throw error;
        }
    }
    get size() {
        this.purgeExpired();
        return this.entries.size;
    }
    purgeExpired() {
        const now = this.now();
        for (const [key, entry] of this.entries) {
            if (entry.settled && entry.expiresAt <= now)
                this.entries.delete(key);
        }
    }
    evictForCapacity() {
        while (this.entries.size >= this.maxEntries) {
            const oldestSettled = [...this.entries].find(([, entry]) => entry.settled)?.[0];
            if (oldestSettled === undefined)
                throw new IdempotencyCapacityError(this.maxEntries);
            this.entries.delete(oldestSettled);
        }
    }
}
function stableSerialize(value) {
    if (value === null || typeof value !== "object")
        return JSON.stringify(value) ?? "undefined";
    if (Array.isArray(value))
        return `[${value.map(stableSerialize).join(",")}]`;
    const object = value;
    return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableSerialize(object[key])}`).join(",")}}`;
}
export const idempotencyStore = new IdempotencyStore();
