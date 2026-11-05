import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["./build/index.js"],
  cwd: process.cwd(),
  env: { ...process.env, MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0" },
  stderr: "pipe",
});
const client = new Client({ name: "civil3d-live-smoke", version: "1.0.0" });

try {
  await client.connect(transport);
  const tools = await client.listTools();
  const healthTool = tools.tools.find((tool) => tool.name === "civil3d_health");
  if (!healthTool?.outputSchema || healthTool.annotations?.readOnlyHint !== true) {
    throw new Error("The production MCP client did not receive the typed health contract and annotations.");
  }

  const progress = [];
  const catalog = await client.callTool(
    { name: "civil3d_docs", arguments: { action: "list_tool_capabilities" } },
    undefined,
    { onprogress: (notification) => progress.push(notification), maxTotalTimeout: 30_000 },
  );
  if (catalog.isError || !catalog.structuredContent || progress.length < 2) {
    throw new Error(`Structured/progress MCP smoke failed: ${JSON.stringify({ catalog, progress })}`);
  }

  const reportLink = catalog.content.find((item) => item.type === "resource_link");
  if (!reportLink?.uri) throw new Error("Catalog result did not expose its report resource link.");
  const report = await client.readResource({ uri: reportLink.uri });
  if (!report.contents.some((content) => "text" in content && content.text.includes("civil3d_alignment"))) {
    throw new Error("The generated report resource could not be retrieved through MCP.");
  }

  const health = await client.callTool({ name: "civil3d_health", arguments: {} });
  if (health.isError || health.structuredContent?.action !== "health") {
    throw new Error(`Live health MCP call failed: ${JSON.stringify(health)}`);
  }
  const drawing = await client.callTool({ name: "civil3d_drawing", arguments: { action: "info" } });
  if (drawing.isError || drawing.structuredContent?.action !== "info") {
    throw new Error(`Live drawing MCP call failed: ${JSON.stringify(drawing)}`);
  }

  console.log(JSON.stringify({
    status: "passed",
    publicTools: tools.tools.length,
    progressNotifications: progress.length,
    reportResource: reportLink.uri,
    liveActions: ["health", "drawing.info"],
  }, null, 2));
} finally {
  await client.close().catch(() => undefined);
  await transport.close().catch(() => undefined);
}
