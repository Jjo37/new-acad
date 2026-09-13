import { describe, expect, it } from "vitest";
import {
  captureToolHandler,
  executeRegisteredTool,
} from "../src/tools/toolHandlerRegistry.js";
import { Civil3DRpcError } from "../src/utils/SocketClient.js";

describe("tool error contract", () => {
  it("preserves Civil 3D domain and numeric codes through the MCP envelope", async () => {
    const toolName = "test_p2_domain_error";
    captureToolHandler(toolName, async () => ({
      content: [{ type: "text", text: "Surface was not found" }],
      isError: true,
      errorCode: "CIVIL3D.OBJECT_NOT_FOUND",
      rpcCode: -32004,
    }));

    const error = await executeRegisteredTool(toolName, {})
      .then(() => undefined, (reason) => reason);

    expect(error).toBeInstanceOf(Civil3DRpcError);
    expect(error).toMatchObject({
      code: "CIVIL3D.OBJECT_NOT_FOUND",
      rpcCode: -32004,
      message: "Surface was not found",
    });
  });

  it("preserves untyped availability failures for HTTP classification", async () => {
    const toolName = "test_untyped_unavailable_error";
    captureToolHandler(toolName, async () => ({
      content: [{ type: "text", text: "Plugin not running" }],
      isError: true,
    }));

    const error = await executeRegisteredTool(toolName, {})
      .then(() => undefined, (reason) => reason);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(Civil3DRpcError);
    expect(error).toMatchObject({ message: "Plugin not running" });
  });
});
