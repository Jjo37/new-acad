## new-acad v1.9.0 — 中英双语界面 + 多源只读知识库 + C3D 专业域真机回归（2026-10-19）

> 本版是「可用性 + 可信度」两件事：界面终于能跟着配置出中文或英文（切了立刻生效，不用重启），
> 知识库从"必须把文件复制进来"改成"直接引用你已有的目录（只读）"，
> 并且把 Civil 3D 的专业对象（曲面/路线/管网/廊道）拉进真机回归，堵掉一个"空管网炸掉整个列表"的真 bug。

### 一、双语界面（i18n）

- **界面跟配置**：`config.json → locale` 支持 `auto | zh | en`；面板、菜单、对话框、错误消息、提示全部跟随。
  **切换后立即生效，不用重启 C3D**（真机验收：英文下错误文案全英文，切回中文同一调用立即变中文）。
- **对话跟用户**：AI 回复自然镜像用户刚说的话（中文问中文答、英文问英文答）；
  没有用户消息时（问候 / 后台完成 / 超时通知）用界面语言。**没有写任何"请用某种语言回答"的硬指令**。
- **术语与质量**：新增 `i18n/glossary.md`（中文 → Autodesk 官方英文），文案表 `i18n/messages.json` 中英均为人工撰写，不做机翻。
- **日志统一英文**（排障材料）；代码注释保持中文；图纸内文字与标注不跟语言走。
- **防回退**：新增 i18n 审计与**棘轮门禁**（release-gate 第 10 项）——新增未本地化的用户可见中文会直接让检查失败。
  本轮把插件内未本地化的用户可见中文从 **216 条压到 76 条**（余下多为视觉样式输入别名与已成对的双语片段）。

### 二、多源只读知识库

- `config.json → knowledgeSources` 可列多个目录（字符串或 `{path,label,enabled}`），**源目录只读**：
  不复制、不改名、不写入；派生缓存与索引全部落在 `user-knowledge/.cache/`。
- 面板「更多 → **知识源设置…（只读引用）**」：添加文件夹 / 移除 / 保存并使用；保存前自动备份 `config.json`，并做原子写入。
- **摄取报告如实化**：每个源分别报「入库 / 跳过（原因）/ 无文本层」；低于 20 字符的抽取结果不入库（扫描件不再冒充"已入库"）。

### 三、C3D 专业域真机回归（新工具）

`tools/live-c3d-regression.js`：**自建对象 → 读数校验 → 全量清理**，不碰用户图纸。

覆盖曲面（建 / 加点 / 统计 / 体积对照）、路线（建 / 详情 / 量长）、管网（部件表 / 建网 / 详情）、
廊道链路（对齐 → 纵断面 → 装配 BasicLane → 廊道）。真机结果 **27 passed / 0 failed，零残留**。

**顺带修掉的真 bug**：**空管网**（无管、无结构）会让 `listPipeNetworks` / `getPipeNetwork` 整体报 `INTERNAL_ERROR`
（某个 `ReferenceSurfaceId` 读取即抛，一个属性炸掉整个方法）。现改为逐字段 best-effort，失败字段单独回报 `<字段>Error`。

### 四、超时与卡死自愈

- **模态对话框自动解锁**：超时看门狗向本进程的模态弹窗直投 `ESC`，无效 20s 后升级 `WM_CLOSE`；
  识别改用 `EnumWindows` 枚举本进程窗口（原来只看前台窗口，切到别的程序就找不到），并有双保险确保**绝不误关 C3D 主窗口**。
  真机（`_OPTIONS`）：25.1s 超时 → 对话框在同一秒消失 → **3s 内恢复**，用户全程未操作，C3D 存活。
- **无图纸 fail-fast**：没有活动图纸时，命令上下文类调用立即返回 `CIVIL3D.NO_DRAWING`（1–5ms），
  不再硬卡 25s 引发 `HOST_BUSY` 级联；`openDrawing` / `newDrawing` 保持可用（破局通道）。
- `App.Idle` 通道的 20s 额度与 job 的 300s 额度分离，超时**不污染** `HostBusy`、不产生级联（真机 20042ms 精确返回）。

### 五、路径与打包

- **权威根目录统一**：`CIVIL3D_ROOT` 环境变量 → relay `/health` 的 `root` → 程序集探测。
  修掉"bundle 内那份 `config.json` 被当成主安装目录"导致面板把配置写错位置、用户操作静默失效的问题。
- **打包洁净检查**：安装包内不得含个人配置 / 用户数据 / 开发文档；两张白名单（同步与打包）现在由门禁互相校验。
- 门禁从 7 项扩到 **10 项**（新增路径解析、同步白名单覆盖、i18n 棘轮）。

### 六、其它修复

- `getEntityInfo`：非法 handle 不再返回 `INTERNAL_ERROR`（伪装成内部故障），改为 `INVALID_INPUT` + 明确提示；不存在的 handle 返回 `not found`。
- `getEntityInfo` 新增稳定字段 `colorIndex`（ACI 索引）与 `colorMethod`；`color` 明确标注为**本地化色名**，不可用于逻辑判断。
- 长期记忆上限改为 **8000 字符硬上限**：超限**拒写并给出压缩指引**（原来静默截断，会悄悄丢内容）。
- 方法清单与文档对齐到 **420 个方法**；新增文档审计工具，三线文档 HIGH/MED 问题为 0。

### 七、发布前增补（2026-10-20，审计发现）

本轮用一套可执行的审计（标准 + 门禁 + 误报排查）扫了全项目，修掉几个**用户能直接感受到**的问题：

- **AI 不会再悄悄覆盖你的文件**：系统提示词早就写着「writeTextFile 只创建、绝不覆盖」，但实现从未执行。
  现在 AI 传 `overwrite: true` 会被直接拒绝（`OVERWRITE_NOT_ALLOWED`），同名文件报 `CONFLICT` 让你换名；
  内部快照是唯一的可覆盖入口，且被静态门禁盯着（新代码想绕过会让构建失败）。
- **取消不再是「AI 处理出错」**：以前点取消会看到红色报错。现在识别为正常取消，安静收尾。
- **选中的实体不再「跨图串味」**：插件写选择集快照时带了 BOM，relay 一直解析失败、只能吃旧缓存；
  外加图纸重名（都叫 Drawing1.dwg）会读到上一张图的 handle。两处都已修，切换图纸后选择集以当前图纸为准。
- **长时间使用后面板整个变哑**（内部计数器泄漏）：每次超时后的「宿主忙」拒绝都会让排队计数 +1 且永不归还，
  累积到上限后所有调用一律返回「队列已满」，只能重启 C3D。已修为两笔账分开记，实测跑完 420 个方法计数仍为 0。
- **`overkillEntities` 会把 C3D 卡在命令提示符**：2025 版 OVERKILL 的提示序列变了，调用后命令不结束，
  后续所有操作都要等到超时。该方法早已标记弃用，现改为直接返回提示并指向自研的 `overkillGeo`，不再投递任何命令。
- **文件读写边界改为分级**：**导出全盘放行**（成果想存桌面、D 盘、NAS 都行）；
  **导入限定目录**（用户目录 / 我的文档 / 程序目录 / `exchange\import`，NAS 等用 `CIVIL3D_IMPORT_ROOTS` 加）。
  读外部文件是攻击面，写成果不是——这个区别以前没分开。越界时会告诉你可用目录，不只是一个错误码。

工程侧（对外无感知，但让上面这些能持续成立）：门禁从 22 项扩到 **29 项**，把 37 个 TypeScript 测试套件（441 用例）、
C# 文件边界套件、图片上下文护栏、描述参数回归全部接进发布门禁；新增数值准确性自检 **31 项不变量**（与 Wolfram 独立核算对照）。
### 已知问题（v1.9.0）

1. **交互式命令停住时的自愈**：能稳定复现的只有"模态对话框"场景；在命令行提示处停住时插件调用反而会把提示清掉（观测者效应），**仍缺稳定复现手段**，该项继续挂在待办。
2. **描摹高色阶**：属管线固有取舍（档数越高每档区域越小，越易被小连通域过滤吞掉），**定案不再投入**，结果中如实回报 `requestedLevels` / `effectiveLevels` / `warnings`。
3. 少数英文文案是按中文语境人工转写的，若发现不地道欢迎反馈（术语表会持续补）。

> 设计边界（**非缺陷**）：不支持 Civil 3D 2024；`render` 等约 12 个方法因无公开 API 不可用；扫描件 OCR 依赖系统语言包。完整台账见 `KNOWN-ISSUES.md`。

### 下载哪个

- `new-acad-setup-v1.9.0.exe` — 一键安装器（推荐）
- `new-acad-v1.9.0.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.9.0 — bilingual UI, multi-source read-only knowledge, and a real Civil 3D regression suite (2026-10-19)

> Two themes this release: **usability** and **trustworthiness**. The UI now follows your configured language
> (Chinese or English, effective immediately, no restart); the knowledge base references your existing folders
> read-only instead of demanding copies; and Civil 3D objects (surfaces, alignments, pipe networks, corridors)
> are now covered by a live regression suite — which caught a real bug where one empty pipe network
> broke the entire pipe-network listing.

### 1. Bilingual UI (i18n)

- **UI follows configuration**: `config.json → locale` accepts `auto | zh | en`; panels, menus, dialogs, error messages and hints all follow it. **Switching takes effect immediately — no CAD restart** (verified on a real machine: errors were fully English, then immediately Chinese after switching back).
- **Conversation follows the user**: replies mirror the language of your last message (ask in Chinese → Chinese answer; ask in English → English answer). With no user message (greeting, background completion, timeout notice) the UI language is used. **No hard-coded "answer in language X" instructions anywhere.**
- **Terminology**: new `i18n/glossary.md` (Chinese → official Autodesk English). The copy catalog `i18n/messages.json` is written by hand in both languages — no machine translation.
- **Logs are English-only** (troubleshooting material); code comments stay Chinese; text and labels inside drawings are never translated.
- **No regressions allowed**: new i18n audit plus a **ratchet gate** (release-gate item 10) — introducing a new un-localized user-visible Chinese string fails the check. Un-localized user-visible Chinese inside the plugin went from **216 to 76** (the remainder is mostly visual-style input aliases and already-bilingual fragments).

### 2. Multi-source read-only knowledge base

- `config.json → knowledgeSources` takes a list of directories (plain strings or `{path,label,enabled}`). **Source folders are read-only**: nothing is copied, renamed or written; derived caches and indexes live under `user-knowledge/.cache/`.
- Panel: **More → Knowledge sources… (read-only references)** — add folder / remove / save and apply; `config.json` is backed up before writing and written atomically.
- **Honest ingestion report**: per-source counts of ingested / skipped (with reasons) / no text layer. Extractions shorter than 20 characters are not ingested, so scanned PDFs no longer masquerade as "ingested".

### 3. Live Civil 3D regression suite (new tool)

`tools/live-c3d-regression.js` — **create objects → verify readings → clean up everything**, never touching user drawings.

Covers surfaces (create / add points / statistics / volume cross-check), alignments (create / details / length),
pipe networks (parts list / create / details) and the corridor chain (alignment → profile → BasicLane subassembly → corridor).
Result on a real machine: **27 passed / 0 failed, zero leftovers**.

**Real bug found and fixed**: an **empty pipe network** made `listPipeNetworks` / `getPipeNetwork` fail with `INTERNAL_ERROR` — reading one `ReferenceSurfaceId` threw and took the whole method down. Properties are now read per-field, best-effort, and failing fields report `<field>Error` individually.

### 4. Timeout and hang recovery

- **Modal dialog auto-unlock**: the watchdog posts `ESC` directly to this process's modal dialogs and escalates to `WM_CLOSE` after 20 s of no effect. Detection now enumerates this process's windows via `EnumWindows` (previously only the foreground window, so switching apps hid the dialog), with double safeguards so CAD's main window is never closed by mistake. Real machine (`_OPTIONS`): 25.1 s timeout → dialog gone within the same second → **recovered within 3 s**, without user interaction, CAD alive.
- **Fail fast without a drawing**: command-context calls return `CIVIL3D.NO_DRAWING` in 1–5 ms instead of stalling 25 s and cascading into `HOST_BUSY`; `openDrawing` / `newDrawing` remain available as the way out.
- The `App.Idle` channel has its own 20 s budget, separate from the 300 s job budget; timeouts **do not pollute** `HostBusy` and cause no cascade (measured 20042 ms, exact).

### 5. Paths and packaging

- **One authoritative root**: `CIVIL3D_ROOT` env var → relay `/health` `root` → assembly probe. This fixes the bug where the `config.json` inside the Autoloader bundle was mistaken for the main install, so the panel silently wrote settings to the wrong place.
- **Package cleanliness check**: installers must not contain personal config, user data or development docs; the two whitelists (sync and packaging) are now cross-checked by the gate.
- The gate grew from 7 to **10 items** (path resolution, sync-whitelist coverage, i18n ratchet).

### 6. Other fixes

- `getEntityInfo`: an invalid handle no longer surfaces as `INTERNAL_ERROR` (which disguised an input error as an internal failure) — now `INVALID_INPUT` with a clear hint; a valid-but-missing handle returns `not found`.
- `getEntityInfo` exposes stable `colorIndex` (ACI) and `colorMethod`; `color` is documented as a **localized color name** — do not use it for logic.
- Long-term memory now has a hard **8000-character cap**: exceeding it **refuses the write and asks for compaction** instead of silently truncating.
- Method inventory and docs reconciled at **420 methods**; new documentation audit tool reports zero HIGH/MED issues across all three repositories.

### 7. Pre-release additions (2026-10-20, from an internal audit)

A scriptable audit (standard + gates + false-positive checks) surfaced several issues you can actually feel:

- **The AI can no longer silently overwrite your files.** The system prompt had promised "create-only, never overwrite"
  since the beginning; the implementation never enforced it. Now `overwrite: true` from the AI is rejected
  (`OVERWRITE_NOT_ALLOWED`), an existing name returns `CONFLICT` so the AI picks another one, and the single internal
  snapshot entry point is watched by a static gate that fails the build if new code tries to bypass it.
- **Cancel no longer shows up as "AI error".** It used to surface a red failure; now it ends quietly as a cancellation.
- **No more selection leaking across drawings.** The plugin wrote its selection snapshot with a UTF-8 BOM, so the relay
  had never been able to parse it and kept serving a stale cache; on top of that, drawings sharing the default name
  (`Drawing1.dwg`) could return handles from the previous drawing. Both fixed.
- **The panel no longer goes silent after long use** (internal counter leak): every "host busy" rejection after a timeout
  incremented a queue counter that was never returned; once it hit the limit, every call answered "queue is full" until
  Civil 3D was restarted. Fixed — after running all 420 methods the counter is still 0.
- **`overkillEntities` could wedge Civil 3D at a command prompt.** Civil 3D 2025 changed the OVERKILL prompt sequence,
  so the dispatched command never ended and every later call waited for a timeout. The method was already deprecated;
  it now returns a notice pointing at the native `overkillGeo` and dispatches nothing.
- **File boundaries are now tiered**: **exports are unrestricted** (save results to Desktop, D:, a NAS — anywhere),
  while **imports are limited** to your profile, Documents, the program folder and `exchange\import`
  (add more via `CIVIL3D_IMPORT_ROOTS`). Reading external files is an attack surface; writing results is not.
  Rejections list the allowed folders instead of just returning an error code.

Engineering (invisible to users, but it keeps the above true): the release gate grew from 22 to **29 checks**, now including
37 TypeScript suites (441 tests), the C# file-boundary suite, the image/context guard and the description regression;
a new accuracy self-test covers **31 invariants** cross-checked against an independent Wolfram computation.
### Known issues (v1.9.0)

1. **Self-healing from stalled interactive commands**: only the modal-dialog case can be reproduced reliably; when paused at a command-line prompt, a plugin call actually clears the prompt (observer effect), so **we still lack a stable reproduction**. Tracked as an open item.
2. **High tone levels in tracing**: an inherent trade-off of the pipeline (more levels → smaller regions → more absorbed by the small-component filter). **Decision: no further investment.** Results report `requestedLevels` / `effectiveLevels` / `warnings` honestly.
3. A few English strings were written by hand from Chinese context; if anything reads awkwardly, please report it — the glossary keeps growing.

> Design boundaries (**not defects**): Civil 3D 2024 is unsupported; ~12 methods (e.g. `render`) have no public API; scanned-PDF OCR depends on system language packs. Full ledger in `KNOWN-ISSUES.md`.

### Which download

- `new-acad-setup-v1.9.0.exe` — one-click installer (recommended)
- `new-acad-v1.9.0.zip` — portable zip (includes the installer)
- `new-acad.bundle.zip` — Autodesk Autoloader bundle

### Support

- **Civil 3D 2025 / 2026** (.NET 8)
- **Civil 3D 2024 is not supported**
