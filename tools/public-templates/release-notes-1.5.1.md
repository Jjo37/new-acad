## new-acad v1.5.1（2026-09-21）

> 本版含 v1.5.0（含 2026-09-15 重打包修复版）的全部内容，外加下面的新修复。直接覆盖安装即可，无需改配置，API Key 与设置保留。

### 本版修复

- **修复：给廊道加曲面会把 AutoCAD 卡住（算量刚需功能）** —— 此前该操作会让 CAD 无响应 3~6 分钟，而且约一半概率回报"失败"（其实曲面已经生成好了，AI 会被误导去重试）。根因是这类"内部重建"操作在 AutoCAD 的命令上下文里会被严重节流。改为在空闲时机执行后，提交耗时从 **215 秒降到 0.06 秒**。现在默认会等曲面真正生成完成再返回（实测约 90 秒），返回即可直接算量。
- **修复：外部脚本的语言现在跟随面板** —— 端口检测、端口配置、清理脚本会读取界面语言设置；面板切成英文后，这些脚本的输出也是英文。
- **修复：端口检测结论误判** —— 检测脚本的结论判断有一处类型转换问题，可能一直误报"C3D 未开"。已修，现在能正确报"全部正常"。

### 安装包（推荐）
- `new-acad-setup-v1.5.1.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.5.1.zip` —— 解压后双击 install.bat

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 new-acad.bundle，复制到 %APPDATA%\Autodesk\ApplicationPlugins\ 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### new-acad v1.5.1 (2026-09-21)

> Includes everything from v1.5.0 (and its 2026-09-15 repackaged fix build), plus the fixes below. Install over the old version - no configuration change needed, your API key and settings are kept.

#### Fixes in this release

- **Fixed: adding a corridor surface could freeze AutoCAD (needed for quantity take-off)** - the operation left CAD unresponsive for 3-6 minutes, and about half the time reported "failed" even though the surface HAD been created (which sent the AI chasing a phantom error). Root cause: this kind of internal regeneration is heavily throttled while running inside AutoCAD's command context. Running it at idle time brought the commit down from **215 seconds to 0.06 seconds**. It now waits until the surface is genuinely finished (about 90 s in our tests) and returns only when it is ready to use.
- **Fixed: external scripts now follow the palette language** - the port check, port config and cleanup scripts read the UI language setting; switch the palette to English and their output is English too.
- **Fixed: port check verdict was misreported** - a type-conversion issue in the verdict logic could always report "C3D is not running". Fixed - it now correctly reports "all good".

**Assets**: `new-acad-setup-v1.5.1.exe` (installer) | `new-acad-v1.5.1.zip` (portable) | `new-acad.bundle.zip` (Autoloader/App Store package)

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
