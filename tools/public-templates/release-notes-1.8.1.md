## new-acad v1.8.1 — 扫描件 OCR + 全英文界面 + 图片解码修复（2026-10-14）

> 本版新增两项能力（扫描件文字识别、完整英文界面），并合入 v1.8.0 线上一轮修复。
> 每条对应已提交的改动与可复现的实测；未解决的列在「已知问题」，不含绝对化承诺。

### 一、新增：扫描件 PDF 自动 OCR

以前扫描件（无文本层）进知识库是**死路**，只能手打。现在自动识别：

- 整理知识库时，**文本层过稀的 PDF 自动走 OCR**（无需用户操作）；索引中该文件标注 `[OCR]`，可被检索。
- 引擎用 **Windows 自带的 WinRT**（`Windows.Data.Pdf` 渲染 + `Windows.Media.Ocr` 识别），**离线、随系统、零额外打包**（不引 Tesseract / pdfium 等）。
- 实测：一本 **72 页全扫描件（文本层 0 字）** 自动 OCR 出可读文本并入知识库、可检索。

> 说明：识别质量取决于系统 OCR 语言包；中文 Windows 自带简体中文识别，缺失时工具会提示去「设置 → 语言」添加。单次识别有页数上限（默认 60 页）。

### 二、新增：完整英文界面（i18n-B）

以前英文用户虽然**对话回复**已是英文，但内部方法描述、工具说明仍是中文。本版补齐：

- **421 条 C3D 插件方法描述**中英双版，按界面语言自动切换（`listMethods` 返回对应语言）。
- **工具调用说明（工具 schema）与系统提示词**同样提供英文版，界面语言为英文时全部输出英文。
- 界面语言跟随面板右上角切换（自动 / 中文 / English），无需重启。

### 三、修复（含 v1.8.0 线上反馈）

- **webp 图片解码**：看图（`exportImageView`）与描摹（`exportImageGray`）**两条解码路**以前都只靠系统 GDI+/WIC，webp 在部分机器上会失败。现解码链统一为 GDI+ → WIC → **内置 ImageSharp**（随包分发）→ **webp 在任何机器都能看、能描摹**。
- **安装期 AI 配置不生效**：安装向导填的模型/Key 以前要进面板再配一次才生效（relay 启动时只读一次配置）。现装完即用。
- **打包**：随插件分发的托管依赖（PdfPig、ImageSharp）已完整打进发布包与同步清单。

### 已知问题（v1.8.1 仍未解决）

1. 高色阶描摹不稳（≥24 档块数锐减；SOP 仍建议 2–12 档）。
2. **扫描件 OCR 依赖系统 OCR 语言包**（中文 Windows 自带；纯英文系统需另装中文语言组件才能识别中文扫描件）；单次上限 60 页。
3. 模态对话框下超时看门狗**不能自动解锁**（ESC 对模态无效，需手动关掉对话框 / 重启 CAD）。
4. `render` 仍是命令投递（25.0.58 无渲染 API）。
5. `trim / extend / overkill` 与同步 `*Geo` 版功能重叠，是否下线旧方法**待定**。
6. **没有自动化测试网 / CI**：目前仍是 QC 脚本 + 发布门禁。
7. **不支持 Civil 3D 2024**（.NET Framework 4.8）。

### 下载哪个

- `new-acad-setup-v1.8.1.exe` — 一键安装器（推荐）
- `new-acad-v1.8.1.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装旧版：重跑安装器即可。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.8.1 — scanned-PDF OCR + full English UI + image-decode fixes (2026-10-14)

> Two new capabilities (scanned-document OCR, a complete English UI) plus the v1.8.0-line fixes.

### 1. New: automatic OCR for scanned PDFs

- When building the user knowledge base, a PDF with a sparse/empty text layer **automatically falls back to OCR**; the entry is marked `[OCR]` in the index and is searchable.
- Engine: **built-in Windows WinRT** (`Windows.Data.Pdf` render + `Windows.Media.Ocr`) — offline, no extra bundling.
- Verified: a **72-page fully scanned PDF (0 text layer)** was OCR'd into readable, searchable knowledge-base text.

> Quality depends on the system OCR language pack (Chinese Windows ships Simplified Chinese; the tool tells you where to add it otherwise). A page cap (default 60) applies per run.

### 2. New: complete English UI (i18n-B)

- **421 C3D plugin method descriptions** now have English versions, switched by UI language.
- **Tool-call schemas and the system prompt** also have English versions; with the UI set to English everything is English.
- UI language follows the palette toggle (auto / Chinese / English) with no restart.

### 3. Fixes (incl. v1.8.0 user feedback)

- **webp decoding**: both the image-view path (`exportImageView`) and the trace path (`exportImageGray`) now use GDI+ -> WIC -> **bundled ImageSharp**, so webp can be viewed and traced on any machine.
- **Install-time AI config**: the provider/key entered in the setup wizard now takes effect immediately (no need to re-enter in the palette).
- **Packaging**: managed dependencies (PdfPig, ImageSharp) are fully shipped in the release package.

### Known issues (still open in v1.8.1)

1. High tone levels (>= 24) are unstable (SOP still recommends 2-12).
2. Scanned OCR depends on the system OCR language pack; page cap 60 per run.
3. The watchdog cannot unlock a modal dialog (close it by hand / restart CAD).
4. `render` is still a posted command (no render API in 25.0.58).
5. `trim / extend / overkill` duplicate their `*Geo` counterparts — removal is undecided.
6. No automated test suite / CI yet.
7. Civil 3D 2024 is not supported.

### Downloads

`new-acad-setup-v1.8.1.exe` / `new-acad-v1.8.1.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). **Civil 3D 2024 is NOT supported.**
