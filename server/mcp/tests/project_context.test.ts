import { afterEach, describe, expect, it, vi } from "vitest";
import * as net from "node:net";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("getProjectContext", () => {
  it("loads drawing, object, and selection context in one native command", async () => {
    let connectionCount = 0;
    const server = net.createServer((socket) => {
      connectionCount += 1;
      socket.once("data", (data) => {
        const request = JSON.parse(data.toString()) as {
          id: string;
          method: string;
        };

        socket.end(JSON.stringify({
          jsonrpc: "2.0",
          id: request.id,
          result: request.method === "getProjectContext"
            ? {
              drawingInfo: { name: "Roadway.dwg" },
              objectTypes: ["Alignment", "TinSurface"],
              selectedObjects: [{ type: "Alignment", name: "Mainline" }],
            }
            : null,
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

      const { getProjectContext } = await import("../src/orchestration/ProjectContextService.js");
      const context = await getProjectContext(10);

      expect(context).toEqual({
        drawingInfo: { name: "Roadway.dwg" },
        objectTypes: ["Alignment", "TinSurface"],
        selectedObjects: [{ type: "Alignment", name: "Mainline" }],
      });
      expect(connectionCount).toBe(1);
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => error ? reject(error) : resolve());
      });
    }
  });
});
