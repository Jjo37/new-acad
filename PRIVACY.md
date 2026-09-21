# Privacy Policy — new-acad

**Version:** v1.0 (2026-09-14) · **Applies to:** **new-acad** (an add-in for Autodesk AutoCAD / Civil 3D, including its palette and local MCP server)

**new-acad** is a local desktop add-in. This policy explains what data the software processes, where it goes, and how you can delete it.

---

## English

### 1. What we collect, how, and how it is used

**We (the publisher) do not collect any data about you.** The software contains **no telemetry, no analytics and no crash reporting**, and it requires **no account**. Nothing is sent to us or to any server we operate.

Everything below stays **on your own computer**, unless you explicitly use a feature that sends it to the AI model provider *you* configured:

| Data | Where it is stored | Purpose |
|---|---|---|
| LLM API key, provider, model, base URL | local `config.json` (next to the installed files) | to call the model provider you chose |
| Port numbers, project name, UI language | local `config.json` | application settings |
| Plugin / relay logs (may include drawing names, file paths, method names, timings) | `%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log`, `<install dir>\server\*.log` | local troubleshooting |
| Chat history, queued messages, selection cache | `<install dir>\exchange\` | so the palette can resume and stay consistent |
| AI long-term memory (rules and preferences you asked it to remember) | `<install dir>\server\memory\agent-memory.md` | to personalise behaviour |

### 2. Data sent to third parties (the important part)

To do its work, the assistant must send context to **an AI model provider that you choose and configure** (for example DeepSeek, OpenAI, Alibaba Qwen, Moonshot Kimi, Zhipu, MiniMax, OpenRouter, xAI, or a custom OpenAI-compatible endpoint). This may include:

- the instructions you type;
- information about the entities you selected and the resulting values;
- the contents of local files you ask the software to read;
- results of drawing operations, which the model sees in order to continue the task.

This data is transmitted **directly from your machine to that provider**, authenticated with your own API key. It is **not routed through any server of ours**. The provider's own privacy policy governs how they handle it — please review theirs and only use providers you are comfortable with. Providers you connect to must be used under their published data-protection terms, which have to be at least as protective as described here.

The MCP server component listens only on **localhost** (default port 3000); the software does not expose it to the internet.

### 3. Retention and deletion

- We retain **no data** about you, because we receive none.
- Local data (config, logs, chat history, memory, caches) stays on your machine until **you** delete it. You can delete individual items from the palette ("More" → cleanup), or remove everything by running `uninstall.ps1` / deleting the install folder. Your API key is removed together with `config.json`.

### 4. Revoking consent and requesting deletion

- **Stop data sharing at any time:** clear or remove the API key in the palette config area (or delete `config.json`), close the palette, or uninstall the software.
- **Delete local data:** use the cleanup menu, `uninstall.ps1`, or delete the folders listed above.
- **Requests and questions:** since no personal data is held by us, there is nothing for us to delete on your behalf — but if you have a concern or a question about this policy, please open an issue at <https://github.com/Jjo37/new-acad/issues>.

### 5. Your responsibility

The assistant can read files and drawing data you point it to, and sends that content to your chosen model provider. **Please do not ask it to process confidential or regulated information unless you accept sending it to that provider.**

---

## 中文（要点）

**new-acad** 是本地桌面插件（AutoCAD / Civil 3D），本政策说明它处理哪些数据、数据去哪里、你怎么删。

1. **本软件不向我们收集任何数据**：无遥测、无统计、无崩溃上报、无需注册账号；不会向任何我方服务器发送内容。API Key / 服务商 / 模型等设置存在本机 `config.json`；日志在 `plugin.log` 与 `server\*.log`；对话与缓存在 `exchange\`；长期记忆在 `server\memory\agent-memory.md`。
2. **会发给第三方的情形**：AI 要工作，必须把你**自己配置的模型服务商**所需的上下文发出去 —— 包括你的指令、选中图元信息、你让它读取的本地文件内容、操作结果。**数据从你本机直连该服务商，不经过我方任何服务器**，处理方式受该服务商隐私政策约束；请只使用你信任的服务商。
3. **保留与删除**：我方不持有任何数据；本机数据由你自行删除（面板「更多」→ 清理 / `uninstall.ps1` / 直接删除文件，API Key 随 `config.json` 一起删除）。
4. **撤回同意**：清空或删除 API Key、关闭面板，或卸载软件，即可停止数据外发。
5. **你的责任**：不要让助手处理你不愿发送给该服务商的机密或受监管数据。

MCP 服务器只监听**本机 localhost**（默认 3000），软件不对外网暴露。任何疑问请到 <https://github.com/Jjo37/new-acad/issues> 提 issue。

---

*This policy may be updated; the version and date above always apply to the current release.*
