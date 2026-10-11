## new-acad v1.4.2

### 本版亮点：可靠性 + 工具描述修正

- **中继自愈，不再依赖系统计划任务** —— 插件启动时自己拉起并守护中继服务（端口探测 + 进程锁双重防重复启动）；中继意外退出后由守护器 3 秒自动重启，连续崩溃 5 次进入 60 秒冷却，避免打爆 CPU
- **修正 5 条写错的工具描述** —— 「管网水力选型」「管网纵断面图自动化」「管网设计向导」「曲面比较向导」「曲面排水向导」原先都挂着别的工具的说明文字，会让 AI 选错工具；现已按各自真实能力重写
- **工具清单全面对齐** —— 274 个 MCP 工具描述补齐并全部英文化（中/英双语手册同步）；同时修掉一处隐患：源码与出货产物不一致，重新构建会少掉 4 个工具（含"创建廊道"）
- **面板中英切换 + 状态/进度实时推送** 保持不变

### 安装包（推荐）
- `new-acad-setup-v1.4.2.exe` — 双击 → 向导（可选 中文 / English）→ 填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.4.2.zip` — 解压后双击 `install.bat`

**要求**：Windows + 正版 AutoCAD / Civil 3D 2024 / 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### Highlights: reliability & tool-description fixes

- **Self-healing relay — no scheduled task required** — the plugin starts and supervises the relay service by itself (port probe plus a process lock to prevent double starts). If the relay exits, a guardian restarts it after 3 s; five consecutive crashes trigger a 60 s cooldown so it can never spin the CPU
- **Five tool descriptions were wrong** — "pipe network sizing", "pipe profile view automation", "pipe network design workflow", "surface comparison workflow" and "surface drainage workflow" were all carrying another tool's text, which could make an AI pick the wrong tool. Rewritten from their real capabilities
- **Tool manifest fully aligned and in English** — all 274 MCP tool descriptions are now documented in English (the bilingual manual follows); a latent inconsistency was also fixed where rebuilding from source would silently drop 4 tools (including "create corridor")
- **Bilingual palette and real-time status/progress push** are unchanged

### Installer (recommended)
- `new-acad-setup-v1.4.2.exe` — double-click, follow the wizard (Chinese / English), then paste your own LLM API key

### Portable
- `new-acad-v1.4.2.zip` — unzip and run `install.bat`

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2024 / 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
