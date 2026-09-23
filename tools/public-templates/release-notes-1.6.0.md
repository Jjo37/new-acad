## new-acad v1.6.0（2026-09-22）

> 本版含 v1.5.1 的全部内容，另加下面的新能力。直接覆盖安装即可，无需改配置，API Key 与设置保留。

### 新增能力

- **位图 → CAD 矢量线条（描摹）**：面板「上传文件」选一张图，说"画进 CAD"即可。四种模式——**色块区域**（扁平/纯色/卡通/logo/矢量壁纸首选，线最少最好看）、**圆弧拟合**（线稿/手绘/照片）、**骨架细线**（便于继续编辑）、**描边**（保笔画粗细）。**不指定模式时自动按图片类型判定**。实测：1920×1080 卡通壁纸 6 条轮廓 / 全流程 0.5 秒（旧管线同图 83 条碎线）
- **面板「上传文件」按钮**：不做粘贴图片——直接选本地文件（可多选），路径自动填进输入框，补充说明后发送；AI 自己读取（图片可描摹、文档（Word/Excel/PPT/文本）可直接解析）
- **聊天记录落盘**：面板对话按天保存（全局 `exchange\panel-chat-日期.jsonl`；项目模式存到项目目录内），**重启不丢**；「清理会话」真删当前作用域记录（带二次确认），「清理项目」连带该项目记录一起删
- **AI 会话按作用域隔离**：面板对话记忆按「全局 / 各项目」分开保存，选中项目后不会串上下文，重启可续
- **图片 → CAD 分诊流程**：图上**有尺寸标注**时，AI 不再照像素描，而是先列尺寸表 → 尺寸链自检（分段和=总长）→ 拿不准的数字问你 → 按真实尺寸画 → 回读对账；**完全没有尺寸**时只问你一次"有没有哪一段长度你知道"，绝不硬猜
- **批量能力提速**：`importVectorPaths` 一次事务画完几百条多段线（524 条 0.16 秒，比逐条快 57 倍）；`exportImageGray` 让描摹支持 JPEG / GIF / TIFF（此前仅 PNG / BMP）

### 安装包（推荐）
- `new-acad-setup-v1.6.0.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.6.0.zip` —— 解压后双击 `install.bat`

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 `new-acad.bundle`，复制到 `%APPDATA%\Autodesk\ApplicationPlugins\` 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp


---

### New in this release

- **Bitmap -> CAD vector lines (tracing)**: pick an image with the panel's "Upload file" button and say "draw it into CAD". Four modes — **color regions** (best for flat / solid-color art, cartoons, logos, vector wallpapers: fewest lines, best look), **arc fitting** (line art / hand-drawn / photos), **skeleton centerlines** (easy to keep editing), **outline** (preserves stroke width). **Leave the mode empty and it auto-detects the image type.** Measured: a 1920x1080 cartoon wallpaper comes out as 6 contours, 0.5 s end-to-end (the old pipeline produced 83 fragmented lines for the same image).
- **"Upload file" button in the palette**: no clipboard-image support by design — pick local file(s), the path is dropped into the input box, add a note and send. The AI reads them itself (images can be traced; documents — Word / Excel / PowerPoint / text — are parsed directly).
- **Chat history persisted (per scope)**: panel conversations are saved per day (global `exchange\panel-chat-<date>.jsonl`; project mode stores them inside the project folder) and **survive restarts**. "Clear chat" really deletes the current scope (with a confirmation); "Clear project" also removes that project's records.
- **AI sessions isolated by scope**: panel memory is kept separately for "global" and each project — selecting a project no longer mixes contexts, and it resumes after a restart.
- **Image -> CAD triage flow**: when the image **has dimensions**, the AI no longer traces pixels — it first lists a dimension table, self-checks the dimension chain (segments sum to the total), asks about anything uncertain, draws to real size, then reads it back to verify. When there are **no dimensions at all** it asks exactly once: "is there any length you know?" — it never guesses.
- **Batch performance**: `importVectorPaths` draws hundreds of polylines in a single transaction (524 in 0.16 s — 57x faster than one-by-one); `exportImageGray` extends tracing to JPEG / GIF / TIFF (previously PNG / BMP only).

### Installer (recommended)
- `new-acad-setup-v1.6.0.exe` — run it, follow the wizard (Chinese or English), paste your own LLM API key

### Portable
- `new-acad-v1.6.0.zip` — unzip and run `install.bat`

### Autoloader (store) package
- `new-acad.bundle.zip` — unzip to get `new-acad.bundle`, copy it into `%APPDATA%\Autodesk\ApplicationPlugins\`

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
