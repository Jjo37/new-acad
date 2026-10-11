## new-acad v1.7.3 — 图片描摹大改：两种输出形态（描线 / 分层平涂），用户可选（2026-10-04）

### 本次重点：把"图片→CAD"做得更实用

**1）两种输出形态，你自己选**

- **描线（纯多段线）**：把图当线稿描，输出干净的多段线（`line` / `arc` / `outline` / `centerline`）。
- **分层平涂（色块/明暗分层）**：把画面按明暗**分层填充**，**每个色阶一个 CAD 图层**，浅→深叠加成整幅图；图层可**单独开关**。

在面板里说清楚要哪种即可（说"分层平涂/色块"走填充；说"描成线"走多段线）。不确定时它会问你。

**2）描摹质量改进**

- **`line` 档（线稿增强）**：中值降噪 + **Sauvola 局部自适应阈值**——脏扫描件、光照不均的图更稳。
- **`tone` 档（分层平涂）**：灰度分层 → **相邻区域共享边界（无缝、不重叠、不遮挡）**→ 每色一层填充。
- **`autotune`**：自动按"覆盖率/精度"挑最优管线与参数。

**3）修复（承 1.7.2）**

- 修复 **JPEG / GIF / TIFF 图片描摹失败**（此前会报"数据被截断"）。
- **新增 WEBP 格式描摹**。
- 去掉误导提示"JPEG 请先转 PNG"。

### 下载哪个

- `new-acad-setup-v1.7.3.exe` — 一键安装器（推荐）
- `new-acad-v1.7.3.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装旧版的用户：重跑安装器（或替换插件 DLL + server 目录）即可。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**（.NET Framework 4.8，插件无法加载）

---

## new-acad v1.7.3 — Image tracing overhaul: two output styles (linework / layered flat-fill), your choice (2026-10-04)

### Highlights: image -> CAD, made practical

**1) Two output styles, you pick**

- **Linework (pure polylines)**: trace the image as clean polylines (`line` / `arc` / `outline` / `centerline`).
- **Layered flat-fill**: split the image by tone into **filled regions**, **one CAD layer per grey level**, stacked light->dark to form the picture; each layer can be **toggled on/off**.

Just say which you want in the palette ("layered flat-fill / color blocks" for fill; "trace as lines" for polylines). If unsure, it will ask.

**2) Tracing quality**

- **`line` mode**: median denoise + **Sauvola local adaptive threshold** — robust on dirty scans / uneven lighting.
- **`tone` mode**: tonal layering -> **adjacent regions share edges (seamless, non-overlapping, no cover-up)** -> one filled layer per level.
- **`autotune`**: picks the best pipeline/params by coverage/precision automatically.

**3) Fixes (since 1.7.2)**

- Fixed **JPEG / GIF / TIFF tracing** (used to fail with a "data truncated" error).
- **Added WEBP tracing**.
- Removed the misleading "convert JPEG to PNG first" hint.

### Downloads

- `new-acad-setup-v1.7.3.exe` — installer (recommended)
- `new-acad-v1.7.3.zip` — portable (installer included)
- `new-acad.bundle.zip` — Autodesk Autoloader bundle

### Supported versions

- **Civil 3D 2025 / 2026** (.NET 8)
- **Civil 3D 2024 is NOT supported** (.NET Framework 4.8 — the plugin cannot load)
