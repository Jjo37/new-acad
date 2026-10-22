import { AsyncLocalStorage } from "node:async_hooks";
import { randomUUID } from "node:crypto";
const requestContext = new AsyncLocalStorage();
export function currentRequestId() {
    return requestContext.getStore()?.requestId;
}
export function currentAbortSignal() {
    return requestContext.getStore()?.signal;
}
export function createRequestId() {
    return currentRequestId() ?? randomUUID();
}
export function createRpcRequestId() {
    const traceId = currentRequestId();
    return traceId ? `${traceId}.${randomUUID()}` : randomUUID();
}
export function runWithRequestId(requestId, action, signal = currentAbortSignal()) {
    return requestContext.run({ requestId, signal }, action);
}
