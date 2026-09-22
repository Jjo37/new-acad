## new-acad v1.5.0

### 本版亮点：官方 Autoloader 打包 + 自装自卸

- **改为 Autodesk 官方 Autoloader 包（.bundle）** —— 把 new-acad.bundle 放进 %APPDATA%\Autodesk\ApplicationPlugins\ 即完成安装，AutoCAD / Civil 3D 启动时自动加载并弹出面板
- **全套依赖都在包里** —— 包内自带 Node 运行时、中继服务与 MCP 服务器，插件启动时自己拉起并守护；**不再使用计划任务，也不再改写 CAD 的支持路径**
- **卸载干净** —— 关掉 CAD、删掉那个文件夹即彻底移除（设置、日志、对话记录、AI 记忆都在包内）；实测无残留进程、无占用端口
- **修复：不同安装布局下的路径解析** —— 统一为探测式根目录（包内布局与旧版解压布局都可用），解决"包内启动时找不到服务脚本"的问题
- **工具清单与描述** —— 274 个 MCP 工具描述全部英文化并修正 5 条写错的说明（管网选型 / 管网纵断面自动化 / 管网设计向导 / 曲面比较向导 / 曲面排水向导）

### 安装包（推荐）
- `new-acad-setup-v1.5.0.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.5.0.zip` —— 解压后双击 install.bat

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 new-acad.bundle，复制到 %APPDATA%\Autodesk\ApplicationPlugins\ 即可（详见包内 README-FIRST.txt）

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### Highlights: official Autoloader packaging, self-installing

- **Shipped as an Autodesk Autoloader bundle (.bundle)** - copy new-acad.bundle into %APPDATA%\Autodesk\ApplicationPlugins\ and AutoCAD / Civil 3D loads it automatically and opens the palette
- **Fully self-contained** - the bundle carries its own Node runtime, the relay service and the MCP server, and the plugin starts and supervises them itself. No scheduled task, and no changes to your CAD support paths
- **Clean uninstall** - close CAD and delete that one folder; settings, logs, chat history and AI memory all live inside it. Verified: no leftover processes, no occupied ports
- **Fixed: path resolution across install layouts** - root detection is now probe-based, so both the bundle layout and the older extracted layout work (fixes "service script not found" inside the bundle)
- **Tool manifest** - all 274 MCP tool descriptions documented in English, with five previously wrong descriptions corrected

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
