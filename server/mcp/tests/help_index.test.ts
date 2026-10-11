import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { describe, expect, it } from "vitest";
import { discoverOfflineHelpInstallations } from "../src/help/helpDiscovery.js";
import { Civil3DHelpManager } from "../src/help/helpIndex.js";
import { extractWrappedTopicHtml, parseHelpTopic } from "../src/help/helpParser.js";
import { AutodeskVideoCatalog } from "../src/help/videoCatalog.js";
import { registerHelpResources, registerHelpTool } from "../src/tools/helpTool.js";

const TOPIC_ID = "GUID-11111111-2222-3333-4444-555555555555";

describe("Civil 3D offline help", () => {
  it("discovers Autodesk Offline Help installations and prefers the requested version", () => {
    const fixture = createHelpFixture();
    try {
      const installations = discoverOfflineHelpInstallations({
        programFilesRoot: fixture.programFilesRoot,
        configuredVersion: "2026",
      });
      expect(installations).toEqual([
        expect.objectContaining({ version: "2026", language: "English", root: fixture.helpRoot }),
      ]);
    } finally {
      fs.rmSync(fixture.tempRoot, { recursive: true, force: true });
    }
  });

  it("decodes wrapped topics without executing JavaScript and filters decorative images", () => {
    const fixture = createHelpFixture();
    try {
      const wrapped = fs.readFileSync(fixture.topicPath, "utf8");
      const html = extractWrappedTopicHtml(wrapped);
      expect(html).toContain("Grading Optimization Workflow");
      expect(html).not.toContain("globalThis");

      const topic = parseHelpTopic(fixture.helpRoot, fixture.topicPath, "2026");
      expect(topic).toMatchObject({
        topicId: TOPIC_ID,
        title: "Grading Optimization Workflow",
        version: "2026",
        featureArea: "Grading",
        topicType: "Procedure",
        canonicalUrl: `https://help.autodesk.com/view/CIV3D/2026/ENG/?guid=${TOPIC_ID}`,
      });
      expect(topic?.markdown).toContain("civil3d://help/images/2026/");
      expect(topic?.images).toHaveLength(1);
      expect(topic?.images[0]).toMatchObject({ mimeType: "image/png", width: 640, height: 360 });
    } finally {
      fs.rmSync(fixture.tempRoot, { recursive: true, force: true });
    }
  });

  it("searches topics and serves topic and image resources through MCP", async () => {
    const fixture = createHelpFixture();
    const manager = new Civil3DHelpManager({
      installations: [{
        root: fixture.helpRoot,
        version: "2026",
        language: "English",
        displayName: "Fixture help",
        configured: true,
      }],
      cacheRoot: path.join(fixture.tempRoot, "cache"),
    });
    const server = new McpServer({ name: "help-fixture", version: "test" });
    const client = new Client({ name: "help-client", version: "test" });
    const emptyVideoCatalog = new AutodeskVideoCatalog([]);
    registerHelpTool(server, manager, emptyVideoCatalog);
    registerHelpResources(server, manager, emptyVideoCatalog);
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();

    try {
      await server.connect(serverTransport);
      await client.connect(clientTransport);
      const search = await client.callTool({
        name: "civil3d_help",
        arguments: { action: "search", query: "how do I optimize a grading surface?", version: "2026" },
      });
      const searchPayload = JSON.parse((search.content[0] as { text: string }).text);
      expect(searchPayload.results[0]).toMatchObject({
        title: "Grading Optimization Workflow",
        version: "2026",
        imageCount: 1,
      });

      const topicId = searchPayload.results[0].id as string;
      const topic = await client.callTool({
        name: "civil3d_help",
        arguments: { action: "get_topic", id: topicId, version: "2026", includeImages: true, maxImages: 1 },
      });
      expect(topic.content.map((item) => item.type)).toEqual(["text", "resource_link", "image"]);
      const imageUri = (topic.content[1] as { uri: string }).uri;
      const image = await client.readResource({ uri: imageUri });
      expect(image.contents[0]).toMatchObject({ uri: imageUri, mimeType: "image/png" });
      expect((image.contents[0] as { blob?: string }).blob).toBeTruthy();

      const topicUri = `civil3d://help/topics/2026/${topicId}`;
      const topicResource = await client.readResource({ uri: topicUri });
      expect((topicResource.contents[0] as { text?: string }).text).toContain("## Recommended Steps");
    } finally {
      await client.close();
      await server.close();
      fs.rmSync(fixture.tempRoot, { recursive: true, force: true });
    }
  });

  it("returns a playable video resource and direct MP4 fallback in chat content", async () => {
    const fixture = createHelpFixture();
    const manager = new Civil3DHelpManager({
      installations: [{
        root: fixture.helpRoot,
        version: "2026",
        language: "English",
        displayName: "Fixture help",
        configured: true,
      }],
      cacheRoot: path.join(fixture.tempRoot, "cache"),
    });
    const mp4Url = "https://help.autodesk.com/videos/test-grading/video.mp4";
    const videoCatalog = new AutodeskVideoCatalog([{
      id: "grading-video",
      uri: "civil3d://help/videos/2026/grading-video",
      title: "Create a Grading Criteria Set and Criteria",
      sourceVersion: "2026",
      pageUrl: "https://help.autodesk.com/cloudhelp/2026/ENG/Civil3D-UserGuide/files/GUID-VIDEO.htm",
      mp4Url,
      webmUrl: "https://help.autodesk.com/videos/test-grading/video.webm",
    }]);
    const server = new McpServer({ name: "help-video-fixture", version: "test" });
    const client = new Client({ name: "help-video-client", version: "test" });
    registerHelpTool(server, manager, videoCatalog);
    registerHelpResources(server, manager, videoCatalog);
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();

    try {
      await server.connect(serverTransport);
      await client.connect(clientTransport);
      const result = await client.callTool({
        name: "civil3d_help",
        arguments: { action: "search_videos", query: "create grading criteria" },
      });
      expect(result.content.map((item) => item.type)).toEqual(["text", "resource", "resource_link"]);
      expect((result.content[2] as { uri?: string; mimeType?: string })).toMatchObject({
        uri: mp4Url,
        mimeType: "video/mp4",
      });

      const resource = await client.readResource({
        uri: "civil3d://help/videos/2026/grading-video",
      });
      const html = (resource.contents[0] as { text?: string }).text ?? "";
      expect(html).toContain("<video controls");
      expect(html).toContain(mp4Url);
      expect(html).toContain("Content-Security-Policy");
    } finally {
      await client.close();
      await server.close();
      fs.rmSync(fixture.tempRoot, { recursive: true, force: true });
    }
  });
});

function createHelpFixture(): {
  tempRoot: string;
  programFilesRoot: string;
  helpRoot: string;
  topicPath: string;
} {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "civil3d-help-test-"));
  const programFilesRoot = path.join(tempRoot, "Program Files");
  const helpRoot = path.join(
    programFilesRoot,
    "Autodesk",
    "Offline Help for Civil 3D 2026 - English",
    "Help",
  );
  const topicsRoot = path.join(helpRoot, "wrapped-filesCUG");
  const imagesRoot = path.join(helpRoot, "images");
  fs.mkdirSync(topicsRoot, { recursive: true });
  fs.mkdirSync(imagesRoot, { recursive: true });
  fs.writeFileSync(path.join(helpRoot, "index.html"), "<!doctype html><title>Civil 3D Help</title>");

  const meaningfulImage = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
    "base64",
  );
  meaningfulImage.writeUInt32BE(640, 16);
  meaningfulImage.writeUInt32BE(360, 20);
  fs.writeFileSync(path.join(imagesRoot, "grading-workflow.png"), meaningfulImage);
  fs.writeFileSync(path.join(imagesRoot, "ccLink.png"), meaningfulImage);

  const html = `<!doctype html>
    <html><head>
      <meta name="topicid" content="${TOPIC_ID}">
      <meta name="release" content="2026">
      <meta name="product" content="CIV3D">
      <meta name="topic-type" content="task">
      <meta name="description" content="Optimize grading objects and balance cut and fill volumes.">
    </head><body><div class="body_content">
      <h1>Grading Optimization Workflow</h1>
      <p>Use Grading Optimization to edit feature lines, grading criteria, surface constraints, and earthwork targets for a proposed site.</p>
      <h2>Recommended Steps</h2>
      <ol><li>Create grading objects and define their constraints.</li><li>Run optimization and review cut and fill quantities.</li></ol>
      <p><img src="../images/grading-workflow.png" alt="Grading optimization constraints and surface preview"></p>
      <p><img src="../images/ccLink.png" alt="decorative link icon"></p>
      <div class="uifinderbtn"><img src="../images/grading-workflow.png" alt="UI finder"></div>
    </div></body></html>`;
  const topicPath = path.join(topicsRoot, `${TOPIC_ID}.htm.js`);
  fs.writeFileSync(topicPath, `var topic = ${JSON.stringify(html)};\nglobalThis.shouldNotRun = true;`);
  return { tempRoot, programFilesRoot, helpRoot, topicPath };
}
