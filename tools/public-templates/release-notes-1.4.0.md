## new-acad v1.4.0

### 作者的话

我是中国开发者，写这个插件的时候**满脑子都是中文用户怎么用，压根没想起"语言"这回事**。直到有人在安装包里选了 English，装完发现界面还是全中文——我才意识到这是个问题。

这个版本把它补上了：**安装向导里选什么语言，装完就是什么语言**；装完之后，面板里也能随时切。

### 本版亮点：界面中英双语

- **面板可切换中/英文** — 配置区新增「**界面语言**」下拉（自动（跟随系统）/ 中文 / English），选中立即生效并记住，下次打开仍是该语言
- **安装向导语言直达插件** — 安装器里选 English，装出来就是英文界面；选中文同理
- 覆盖面板、端口诊断对话框与状态提示；`config.json` 的 `locale` 字段可手动改
- **目前界面语言只有「中文 / English」两套**（没有第三语言）
- 「自动（跟随系统）」的规则：中文系统 → 中文，**其它语言 → 英文**
- 修复：切换语言后，面板「选择模型」「项目名」两个下拉曾变为空白

### 安装包（推荐）
- `new-acad-setup-v1.4.0.exe` — 双击 → 向导（可选 中文 / English）→ 填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.4.0.zip` — 解压后双击 `install.bat`

**要求**：Windows + 正版 AutoCAD / Civil 3D 2024 / 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### A note from the author

I'm a Chinese developer. When I built this plugin my head was entirely in "how will Chinese users use it" — **it genuinely never crossed my mind that language was even a thing**. Then someone picked English in the installer, and got a fully Chinese UI. That's how I found out.

This release fixes it: **whatever language you choose in the setup wizard is the language you get** — and you can switch it any time from inside the panel.

### Highlights: bilingual UI (Chinese / English)

- **Switch the panel between Chinese and English** — new "**Language**" dropdown in the config area
  (Automatic (follow system) / 中文 / English). Takes effect immediately and is remembered.
- **The installer language carries through** — pick English in the setup wizard and the plugin ships in English (and vice versa).
- Covers the panel, the port-status dialog and status messages. The `locale` field in `config.json` can be set by hand.
- The UI currently ships in **Chinese and English only** (no third language).
- "Automatic" rule: Chinese system → Chinese; **any other language → English**.
- Fixed: after switching language, the "Provider" and "Project" dropdowns came up empty.

### Installer (recommended)
- `new-acad-setup-v1.4.0.exe` — run it, follow the wizard (Chinese or English), paste your own LLM API key

### Portable
- `new-acad-v1.4.0.zip` — unzip and run `install.bat`

**Requires**: Windows + licensed AutoCAD / Civil 3D 2024-2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
