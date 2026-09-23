## new-acad v1.6.1（2026-09-23）

> 本版是 v1.6.0 的**描摹质量修复**。直接覆盖安装即可，无需改配置，API Key 与设置保留。

### 修复 / 改进

- **描摹质量大幅提升（约 3~4 倍）**：修复「把图片描进 CAD 出来一堆碎块」的问题。根因是抓线用的边缘检测对**带网点的扫描件/插画**会产生大量碎片；现在**白底线稿/扫描件会自动改用墨迹阈值抓线**。实测（794×1280 网点漫画）：墨迹覆盖 **17.6% → 52.9%**、线条命中率 **18.0% → 78.3%**
- **自动选型更准**：不指定模式时 —— 扁平/纯色插画 → 色块区域；**白底线稿/扫描件（含实心黑块）→ 外轮廓**（实测比中心线更像原画）；其余 → 圆弧拟合
- **长度过滤自适应**：按图宽的 5% 自动定阈值（12~80px），大图不误砍、小图不糊成一团
- 新增可选清理参数 `open`（去毛刺）/ `fill`（填孔），默认关闭（开启会损失细线/笔画内部细节）
- **面板助手行为加固**：① 禁止凭空声称"插件不支持某功能"（必须先查实时方法清单求证）；② 重描或换参数前**自己清掉上一版图层**，不再让你手动删
- 修复 Autoloader 包清单里的版本号陈旧（PackageContents.xml 1.5.1 → 1.6.1）

### 安装包（推荐）
- `new-acad-setup-v1.6.1.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.6.1.zip` —— 解压后双击 `install.bat`

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 `new-acad.bundle`，复制到 `%APPDATA%\Autodesk\ApplicationPlugins\` 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### Fixes / improvements

- **Tracing quality roughly 3–4x better**: fixes "the image traces into CAD as a heap of fragments". The root cause was the edge-detection mask — on **halftone scans / illustrations** it produced thousands of fragments. White-background line art and scans now switch to an **ink threshold** automatically. Measured (794x1280 halftone manga): ink coverage **17.6% → 52.9%**, line-on-ink hit rate **18.0% → 78.3%**.
- **Smarter auto mode**: with no mode given — flat / solid-color art → color regions; **white-background line art / scans (with solid blacks) → outline** (measurably closer to the original than centerlines); everything else → arc fitting.
- **Adaptive length filter**: the threshold is set at 5% of the image width (12–80 px), so large images don't lose real content and small ones don't turn to mush.
- New optional cleanup parameters `open` (despeckle) and `fill` (hole filling), off by default — they can drop thin lines and interior detail.
- **Panel assistant hardened**: (1) it may no longer claim "the plugin doesn't support X" without checking the live method list first; (2) before re-tracing it **cleans up its own previous layer** instead of asking you to delete it by hand.
- Fixed the stale version in the Autoloader manifest (`PackageContents.xml` 1.5.1 → 1.6.1).

### Installer (recommended)
- `new-acad-setup-v1.6.1.exe` — run it, follow the wizard (Chinese or English), paste your own LLM API key

### Portable
- `new-acad-v1.6.1.zip` — unzip and run `install.bat`

### Autoloader (store) package
- `new-acad.bundle.zip` — unzip to get `new-acad.bundle`, copy it into `%APPDATA%\Autodesk\ApplicationPlugins\`

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
