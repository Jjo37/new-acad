import { AsyncLocalStorage } from "node:async_hooks";
import { randomUUID } from "node:crypto";

interface RequestExecutionContext {
  requestId: string;
  signal?: AbortSignal;
}

const requestContext = new AsyncLocalStorage<RequestExecutionContext>();

export function currentRequestId(): string | undefined {
  return requestContext.getStore()?.requestId;
}

export function currentAbortSignal(): AbortSignal | undefined {
  return requestContext.getStore()?.signal;
}

export function createRequestId(): string {
  return currentRequestId() ?? randomUUID();
}

export function createRpcRequestId(): string {
  const traceId = currentRequestId();
  return traceId ? `${traceId}.${randomUUID()}` : randomUUID();
}

export function runWithRequestId<T>(
  requestId: string,
  action: () => T,
  signal: AbortSignal | undefined = currentAbortSignal(),
): T {
  return requestContext.run({ requestId, signal }, action);
}
