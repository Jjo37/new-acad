## new-acad v1.5.0（2026-09-15 重新打包 / repackaged）

> 版本号不变，**仅重打包修复版**。如果你在 2026-09-15 之前装过 v1.5.0，请重新下载覆盖安装；无需改配置，API Key 与设置保留。

### 本版修复（相对 2026-09-14 的 v1.5.0）

- **修复：Autoloader 包内的路径解析差一级** —— 插件端四处根目录解析统一为探测式。此前在 .bundle 安装下，面板「端口 → 打开配置向导」点了没反应，界面语言/端口配置读到默认值，选择集快照文件也写到包内前一级目录。现在包内布局与解压布局都正确。
- **修复：读图会把上下文算爆** —— 读取图片时 base64 体积被当成文本计入上下文上限，导致图片任务刚起步就中断（且图片实际未送达模型）。现在图片按单独限额约束、只发送一次，视觉通道真正可用。
- **改进：错误不再静默** —— 找不到配置文件时给出明确提示（并写明查找过的目录）；端口诊断异常写入日志。

### 安装包（推荐）
- `new-acad-setup-v1.5.0.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.5.0.zip` —— 解压后双击 install.bat

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 new-acad.bundle，复制到 %APPDATA%\Autodesk\ApplicationPlugins\ 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### Repackaged v1.5.0 (2026-09-15)

> Same version number, **rebuilt with fixes only**. If you installed v1.5.0 before 2026-09-15, please download again and reinstall - no configuration change needed.

#### Fixes since the 2026-09-14 build

- **Fixed: path resolution inside the Autoloader bundle** - all four plugin-side root lookups now use probe-based detection. In the .bundle install the palette's "Ports -> Open Setup Wizard" did nothing, locale/port settings fell back to defaults, and the selection snapshot was written one level above the bundle. Both bundle and extracted layouts now resolve correctly.
- **Fixed: reading an image blew up the context budget** - image base64 was counted as text against the context limit, so image tasks aborted immediately (and the image never actually reached the model). Images are now bounded separately and sent once, so the vision channel really works.
- **Improved: no more silent failures** - missing config now shows a clear message including the folders searched; port-diagnostics errors are logged.

**Assets**: `new-acad-setup-v1.5.0.exe` (installer) | `new-acad-v1.5.0.zip` (portable) | `new-acad.bundle.zip` (Autoloader/App Store package)

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
