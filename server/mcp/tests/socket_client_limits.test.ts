import { afterEach, describe, expect, it, vi } from "vitest";
import * as net from "node:net";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("ApplicationClientConnection response limits", () => {
  it("rejects an oversized plugin response before buffering it indefinitely", async () => {
    const server = net.createServer((socket) => {
      socket.once("data", () => {
        socket.write(JSON.stringify({
          jsonrpc: "2.0",
          id: "oversized-response",
          result: { report: "x".repeat(512) },
        }));
      });
    });

    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", resolve);
    });

    try {
      const address = server.address() as net.AddressInfo;
      vi.stubEnv("CIVIL3D_MAX_RESPONSE_BYTES", "64");
      vi.resetModules();

      const { ApplicationClientConnection } = await import("../src/utils/SocketClient.js");
      const client = new ApplicationClientConnection("127.0.0.1", address.port);

      await new Promise<void>((resolve, reject) => {
        client.socket.once("connect", resolve);
        client.socket.once("error", reject);
        client.connect();
      });

      await expect(client.sendCommand("getLargeReport", {})).rejects.toThrow(
        "response exceeds 64 bytes",
      );
      expect(client.responseCallbacks.size).toBe(0);
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => error ? reject(error) : resolve());
      });
    }
  });
});
