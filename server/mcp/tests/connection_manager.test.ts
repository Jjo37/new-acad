import { afterEach, describe, expect, it, vi } from "vitest";
import * as net from "node:net";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("withApplicationConnection", () => {
  it("uses a fresh TCP connection for every command in a composite action", async () => {
    let connectionCount = 0;
    const server = net.createServer((socket) => {
      connectionCount += 1;
      socket.once("data", (data) => {
        const request = JSON.parse(data.toString()) as { id: string; method: string };
        socket.end(JSON.stringify({
          jsonrpc: "2.0",
          id: request.id,
          result: { method: request.method },
        }));
      });
    });

    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", resolve);
    });

    try {
      const address = server.address() as net.AddressInfo;
      vi.stubEnv("CIVIL3D_HOST", "127.0.0.1");
      vi.stubEnv("CIVIL3D_PORT", String(address.port));
      vi.resetModules();

      const { withApplicationConnection } = await import("../src/utils/ConnectionManager.js");
      const results = await withApplicationConnection(async (client) => [
        await client.sendCommand("first", {}),
        await client.sendCommand("second", {}),
      ]);

      expect(results).toEqual([{ method: "first" }, { method: "second" }]);
      expect(connectionCount).toBe(2);
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => error ? reject(error) : resolve());
      });
    }
  });
});
