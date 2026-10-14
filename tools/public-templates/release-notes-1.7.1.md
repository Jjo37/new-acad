## new-acad v1.7.1 — 支持范围更正：本插件不支持 Civil 3D 2024（2026-09-28）

### 先道歉

此前 README、使用手册和发布说明里都写着"支持 Civil 3D 2024 / 2025 / 2026"。
**这是错误的陈述 —— 本插件实际并不支持 Civil 3D 2024。** 对不起，是我们的疏忽，给装了 2024 的朋友添麻烦了。

### 实际情况

- 插件是 **.NET 8** 构建（`net8.0-windows`），而 **Civil 3D 2024 运行在 .NET Framework 4.8 上** —— 两者不兼容。在 2024 里插件**根本无法加载**（不是"不稳定"，是加载不了，面板不会出现）。
- **受支持的版本是 Civil 3D 2025 / 2026。** 插件包 `PackageContents.xml` 声明的适用系列为 `R25.0 ~ R25.1`。
- **作者本人一直用的是 Civil 3D 2025**：全部 417 个方法、土方与几何算法都是在 2025 上逐条实测出来的。2026 沿用同一条 .NET 8 + R25 API 技术线，理论兼容，但我们**尚未在 2026 上逐项实测**。

### 本次修复

- **文档全部更正**：README / README_EN / 使用手册 / 安装脚本提示，统一写明"支持 Civil 3D 2025 / 2026，2024 不兼容"。
- **插件包元数据版本号修复**：此前 bundle 里的 `PackageContents.xml` 版本号一直停留在 `1.6.2`（商店信息会显示错误版本）→ 现在由构建脚本按 `package.json` 自动注入。
- **安装向导多版本识别改进**：机器同时装了多个版本时，**优先识别 2025/2026**（旧逻辑按注册表键序遍历会误选 2024）；若只检测到 2024 及更早版本，会**明确提示"不受支持"**，不再让你装完发现用不了。

### 下载哪个

代码与 v1.7.0 **完全相同**，本次是文档与元数据更正：

- `new-acad-setup-v1.7.1.exe` — 一键安装器（推荐）
- `new-acad-v1.7.1.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

### 关于 2024 用户

如果你在用 Civil 3D 2024：目前确实用不了，再次抱歉。作者已把"适配 2024"列入后续计划（需要额外的 .NET Framework 构建 + API 兼容层，工作量不小），完成后会单独发版本说明。

---

## new-acad v1.7.1 — Supported versions corrected: Civil 3D 2024 is NOT supported (2026-09-28)

### Apology first

Our README, user guide and release notes previously said "supports Civil 3D 2024 / 2025 / 2026".
**That was wrong — this plugin does not support Civil 3D 2024.** Sorry for the mistake, and sorry to anyone who installed it on 2024.

### The facts

- The plugin is built for **.NET 8** (`net8.0-windows`), while **Civil 3D 2024 runs on .NET Framework 4.8** — incompatible. On 2024 the plugin **cannot load at all** (it is not "unstable"; the palette simply never appears).
- **Supported: Civil 3D 2025 (and 2026).** The bundle's `PackageContents.xml` declares the series `R25.0 ~ R25.1`.
- **The author has always used Civil 3D 2025** — all 417 methods and the earthwork/geometry algorithms were verified on 2025. 2026 shares the same .NET 8 + R25 API line and should work, but we have **not tested it item by item yet**.

### What this release fixes

- **Docs corrected** everywhere (README / README_EN / user guide / installer prompts): 2025 / 2026 supported, 2024 not.
- **Bundle metadata version fixed**: `PackageContents.xml` had been stuck at `1.6.2` (store listing would show a wrong version) → it is now injected from `package.json` at build time.
- **Installer multi-version detection improved**: with several versions installed it now **prefers 2025/2026** (the old registry-key order could pick 2024), and if only 2024 or older is found it **says so explicitly** instead of letting you install something that will not load.

### Downloads

Code is identical to v1.7.0 — this is a documentation/metadata correction:

- `new-acad-setup-v1.7.1.exe` — installer (recommended)
- `new-acad-v1.7.1.zip` — portable (installer included)
- `new-acad.bundle.zip` — Autodesk Autoloader bundle

### For 2024 users

If you are on Civil 3D 2024: it does not work today, sorry again. Adapting to 2024 is on the roadmap (it needs a separate .NET Framework build plus an API compatibility layer); we will publish a dedicated release note once it lands.
