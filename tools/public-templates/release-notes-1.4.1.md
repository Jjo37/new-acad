## new-acad v1.4.1

### 本版亮点：状态与进度改成「实时推送」

- **进度实时化** —— 状态栏显示 AI 当前在做什么，改为**服务端实时推送**（不再等定时刷新）；任务结束自动回落
- **重连即同步** —— 面板重开或断线重连后，立刻拿到当前进度，不用等下一拍
- **文档纠错** —— 旧文档写"面板用 2 秒轮询接收回复（SSE 未切换）"：实际 **2026-08-12 起就是 SSE 推送**。本次把文档改成事实
- **新增 `POSITIONING.md`** —— 讲清这个产品**是什么、不是什么**：内置 AI 大脑 / TCP 常驻通道 / 决策链；MCP 只是对外兼容出口
- **使用手册中英双语** —— `使用手册.html` 右上角一键切换 中文 / English（非中文系统首次打开默认英文）

### 安装包（推荐）
- `new-acad-setup-v1.4.1.exe` — 双击 → 向导（可选 中文 / English）→ 填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.4.1.zip` — 解压后双击 `install.bat`

**要求**：Windows + 正版 AutoCAD / Civil 3D 2024 / 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### Highlights: real-time status & progress

- **Progress is pushed, not polled** — the status bar shows what the AI is doing in real time, and falls back automatically when the task finishes
- **Instant sync on reconnect** — reopen the palette (or recover from a dropped connection) and you immediately get the current progress
- **Doc correction** — older docs claimed "the palette polls for replies every 2 s (SSE not wired up)"; it has been SSE since 2026-08-12. The docs now match reality
- **New `POSITIONING.md`** — states plainly what this product **is and is not**: a built-in AI brain / a persistent TCP channel / a decision chain; MCP is only a compatibility exit
- **Bilingual user guide** — `使用手册.html` toggles between Chinese and English from the top-right corner (non-Chinese systems open in English by default)

### Installer (recommended)
- `new-acad-setup-v1.4.1.exe` — run it, follow the wizard (Chinese or English), paste your own LLM API key

### Portable
- `new-acad-v1.4.1.zip` — unzip and run `install.bat`

**Requires**: Windows + licensed AutoCAD / Civil 3D 2024-2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
