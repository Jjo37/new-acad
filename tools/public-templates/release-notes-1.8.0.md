## new-acad v1.8.0 — 大图纸上下文保护 + 面板 AI 记忆/知识库 + 英文开箱（2026-10-14）

> 本版是**功能版**：三块新能力，外加一批在真机面板里抓出来的修复。
> 每条都对应已提交的改动与可复现的实测；仍未解决的列在「已知问题」里，不含绝对化承诺。

### 一、大图纸 / 大文件不再「爆上下文」

以前的样子：读大文件被**静默截断到 4000 字**（而且不告诉你截了）；工具没有分页，AI 嘴上说"可分段读取"其实是空头支票；上下文一到上限就**直接停**。

- `readFile` **分页**：新增 `offset / limit / maxChars / outline`，返回 `totalLines / from / to / hasMore / nextOffset`；`outline` 只回结构预览。
- 新增 **`queryFile`**：**本地聚合，原文不进上下文** —— `head / tail / sample / wc / grep / groupby / stats / json`（CSV 分组求和、日志 grep、JSON 取值）。
- **工具结果外溢**：任一工具结果 > 8KB 时，完整写入 `exchange\agent-work\_spill\<tool>-<ts>.txt`，上下文里只留「路径 + 字符数 + 前 1200 字预览」。**数据零丢失**，AI 可回读 / 分页 / 聚合（替换掉旧的 4000 字静默截断）。
- **上下文触顶先抢救再停**：先外溢最大的工具结果 → 压缩历史 → 仍超才停（并说明已尝试的措施）。
- `writeFile` 支持 **`append`**（大输出分段写）。
- **图片超限交用户决定**（不硬拒、不自动压缩）：返回选项 —— 压缩看 `resize:{maxSide}` / 局部高清 `crop:{x,y,w,h}` / 用户自处理。新增插件方法 **`exportImageView`**（GDI+ 解码任意格式 → 裁剪/等比缩放）。**临摹通道不受影响**（`traceImage` 独立读原图）。
- **限额可配**：`config.json` 的 `limits.*` 覆盖内置默认（读 / 图 / 写 / 分页 / 外溢 / 上下文）。

实测：`server/test-large-file.js` **18/18 PASS**、`test-image-ctx-guard.js` **8/8 PASS**；真机验「大图 → A/B/C 选项 → 选 A → 图正确进视觉通道并读出内容」。

### 二、面板 AI 的记忆与知识库（M1–M6）

以前：记忆注入被砍，但提示词还写着"已自动注入"（自相矛盾），记忆形同虚设；只增不减、无法修正回退；短期记忆没有"任务"边界；用户知识库从未实现。

- **长期记忆**：改为**纯索引注入**（只注节标题）；写前自动 `.bak` 备份 + **`revertMemory`** 回退；写入遵循「当前状态快照」语义（不留"此前是…"痕迹）。
- **短期记忆**：历史窗口可配（`limits.historyKeep`）；新任务自动清掉旧任务上下文；任务计划**按项目隔离并持久化**。
- **用户知识库**：`ingestKnowledge` 摄取建索引（md/txt/csv/json/docx/doc/xlsx/xls/pptx/**pdf 文本层**）+ `searchKnowledge` 跨文件定位条款；索引自动注入。
- **PDF 文本层**：插件新增 `readPdf`（PdfPig），抽取 PDF 文本；**扫描件不做 OCR**。

实测：M1–M5 自测通过；M6 真机 E2E（抽 PDF 6 页 / 4174 字 → 建索引 → 命中「编制依据」条款原文）。

### 三、英文用户开箱即英文（i18n A）

以前：SYSTEM_PROMPT 全中文、没有任何语言规则 → 英文用户照样收到中文回复。

- 回复**按用户语言自适应**；`locale`（zh-CN / en-US）决定默认语言注入。
- 铁律：内部中文（工具返回 / 错误 / 方法描述 / 知识库）**向用户转述时必须翻成用户语言**。
- **知识层英文版**：`knowledge.en.md` / `image-to-cad.en.md` / `sac-xaml-guide.en.md`，按 locale 选用；打包同步带上英文版。
- 真机验：英文问 → 英文答（无中文泄漏）；locale=zh → 中文知识，en → 英文知识。

### 四、修复（含发布后用户反馈）

- **安装期 AI 配置不生效**：`install.ps1` 先起 relay、后写 `config.json`，relay 启动时只读一次配置 → 内存里是空的 → 面板显示「未配置」，用户得再配一次。已修：写完配置即通知 relay 热加载。
- **webp 图片解码**：`exportImageView`（看图/压缩/裁剪走它）以前只走 GDI+，对 webp 必挂，且 webp 还依赖系统 WIC 编解码器。已修：解码链改为 GDI+ → WIC → **内置 ImageSharp**（随包分发）→ **webp 在任何机器都能解**。
- 读图 / 读文件成功时也计为「有进展」，不再被「连续 N 轮无成功进展」误杀中止。
- `exportImageView` 返回的大 base64 不再被插件侧 `ResultSummarizer`（64KB）截断致图坏。
- **打包修复**：M6 引入的 PdfPig 依赖 DLL 以前**没随包**（`build-dist` 只拷主 DLL、同步清单也没有）→ 发布版 `readPdf` 会 `FileNotFound`。本版已把插件目录内全部托管依赖纳入打包与同步。

### 已知问题（v1.8.0 仍未解决）

1. **英文界面未全覆盖**：421 条插件方法描述 + SYSTEM_PROMPT 仍是中文（i18n B 待办）。
2. 高色阶描摹不稳（≥24 档块数锐减；SOP 仍建议 2–12 档）。
3. **扫描件 PDF 不做 OCR**（只处理文本层）。
4. 模态对话框下超时看门狗**不能自动解锁**（ESC 对模态无效，需手动关掉对话框 / 重启 CAD）。
5. `render` 仍是命令投递（25.0.58 无渲染 API）。
6. `trim / extend / overkill` 与同步 `*Geo` 版功能重叠，是否下线旧方法**待定**。
7. **没有自动化测试网 / CI**：目前仍是 QC 脚本 + 发布门禁。
8. **不支持 Civil 3D 2024**（.NET Framework 4.8，插件无法加载）。

### 下载哪个

- `new-acad-setup-v1.8.0.exe` — 一键安装器（推荐）
- `new-acad-v1.8.0.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装旧版：重跑安装器即可（或替换插件 DLL + server 目录）。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.8.0 — big-drawing context protection + memory/knowledge + English out-of-the-box (2026-10-14)

> A **feature release**: three new capabilities plus a batch of fixes found on a real palette session.
> Every claim maps to a committed change and a reproducible measurement; open items stay under "Known issues".

### 1. Big drawings / large files no longer blow up the context

- `readFile` **pagination**: `offset / limit / maxChars / outline`, returns `totalLines / from / to / hasMore / nextOffset`.
- New **`queryFile`**: aggregates locally, **the raw text never enters the context** — `head / tail / sample / wc / grep / groupby / stats / json`.
- **Tool-result spill**: any tool result > 8KB is written whole to `exchange\agent-work\_spill\`; only a handle + preview stays in context. **No data loss** (replaces the old silent 4000-char truncation).
- **Context overflow: rescue first, stop last** — spill the largest tool result → compact history → stop only if still over.
- `writeFile` gained **`append`**.
- **Oversized images are the user's call** (no hard reject, no auto-compress): options are `resize:{maxSide}` / `crop:{x,y,w,h}` / handle-it-yourself; new plugin method **`exportImageView`**. **Tracing is unaffected** (`traceImage` reads the original independently).
- **Configurable limits**: `config.json` `limits.*` overrides the built-in defaults.

Measured: `server/test-large-file.js` **18/18 PASS**, `test-image-ctx-guard.js` **8/8 PASS**; verified on a real palette session.

### 2. Palette AI gains memory + a user knowledge base (M1–M6)

- **Long-term memory**: index-only injection; auto `.bak` before writes + **`revertMemory`**; "current-state snapshot" semantics.
- **Short-term memory**: configurable history window; new-task boundary; per-project plan isolation + persistence.
- **User knowledge base**: `ingestKnowledge` (md/txt/csv/json/docx/doc/xlsx/xls/pptx/pdf text layer) + `searchKnowledge`; index injected automatically.
- **PDF text layer**: plugin `readPdf` (PdfPig). **Scanned PDFs are not OCR'd.**

### 3. English users get English out of the box (i18n A)

- Replies adapt to the user's language; `locale` (zh-CN / en-US) sets the default language injection.
- Hard rule: internal Chinese (tool output / errors / method descriptions / knowledge base) **must be translated into the user's language** when relayed.
- **English knowledge layer**: `knowledge.en.md` / `image-to-cad.en.md` / `sac-xaml-guide.en.md`, chosen by locale.

### 4. Fixes (incl. post-release user reports)

- **Install-time AI config had no effect**: `install.ps1` starts the relay first and writes `config.json` later; the relay reads config once at start, so its in-memory config was empty and the palette showed "not configured". Fixed: notify the relay to hot-reload right after the config is written.
- **webp decoding**: `exportImageView` (used by image view / compress / crop) used GDI+ only, so it always failed on webp (and webp depended on the system WIC codec). Fixed: the decode chain is now GDI+ -> WIC -> bundled ImageSharp, so **webp works on any machine**.
- Read-image / read-file success now counts as "progress" (no more false "no progress" aborts).
- `exportImageView`'s large base64 is no longer truncated by the plugin-side result summarizer (64KB).
- **Packaging fix**: the PdfPig dependency DLLs were not shipped (`build-dist` copied only the main DLL). Fixed: all managed dependencies in the plugin folder are now packaged and synced.

### Known issues (still open in v1.8.0)

1. English UI is not complete: 421 plugin method descriptions + SYSTEM_PROMPT are still Chinese (i18n B pending).
2. High tone levels (>= 24) are unstable (SOP still recommends 2-12).
3. Scanned PDFs are not OCR'd.
4. The watchdog cannot unlock a modal dialog (close it by hand / restart CAD).
5. `render` is still a posted command (no render API in 25.0.58).
6. `trim / extend / overkill` duplicate their `*Geo` counterparts — removal is undecided.
7. No automated test suite / CI yet (QC scripts + release gate only).
8. Civil 3D 2024 is not supported.

### Downloads

`new-acad-setup-v1.8.0.exe` / `new-acad-v1.8.0.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). **Civil 3D 2024 is NOT supported.**
