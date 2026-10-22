## new-acad v1.10.0 — 看得见 AI 在做什么，也留得下做过什么（2026-10-21）

> 这一版两件事：**把"AI 在干什么"从日志搬进面板**，以及**把做成的事记下来、能安全地再跑一遍**。
> 以前你要么等、要么翻 `relay.log`；现在状态栏多一个「工具」按钮，点开就是实时调用流、方法目录，还有一份份可导出、可回放的工作流记录。

### 一、「工具」页签（新）

- **调用流**：时间 / 方法名 / 通道（TCP · MCP）/ 耗时 / 返回字节 / 状态。
  失败行标红并带错误码（例如 `CIVIL3D.INVALID_INPUT`），进行中显示 `…`。
- **方法目录**：**420 个 TCP 方法** + 中文描述 + 每个方法的**调用次数 / 最近耗时 / 失败次数**，按使用频率排序。
  数据来自插件实时 `listMethods`，点了才拉。
- 搜索框同时过滤方法名与描述；「只看失败」一键筛错；底部计数条。
- 页签住在消息区里：**看调用流时输入框和配置区仍在**，可以边看边继续问。

**隐私与体积**：事件**只带摘要**（方法/耗时/字节/错误码），**永不携带结果内容**——
一次导出几万实体的返回体不会进 UI，面板不会被拖死。

### 二、面板稳定性修复（一堆"看得见的怪现象"的根因）

| 你看到的现象 | 真正的原因 |
|---|---|
| 反复 `HANK_SHOW` 后状态栏多一行、「发送」按钮点了没反应 | 面板被关闭再打开时会把整套界面**又建一遍**叠在旧的上面；你点到的旧按钮，读的是新面板里那个空输入框 |
| 点「工具」只变色、界面不变 | 工具视图和对话区抢同一块空间，后加的被盖住 |
| 工具条第一行被状态栏压住 | **WinForms 停靠按 z-order 反序**：先加状态栏再加内容区，内容区吃掉整块、状态栏只能叠在它上面（历史「首句被遮挡」同源） |
| 按钮和表格挨在一起 /「方法目录」字被切 | 行高不够 + **高分屏（160% 缩放）下固定像素不跟随缩放** |

全部改为"结构上不可能重叠"的实现（先清后建、显式互斥、行高按内容首选尺寸计算），
并新增两处**布局探针**写进 `plugin.log`，以后这类问题可以直接读数：
`statusBarBottom=24 splitTop=24`、`barBottom=42 gridTop=42`、`modeW=89/need 89 ok`。

### 三、清空就清干净

「清理会话」和「更多 → 全部清理」现在会**同时清空工具调用记录**（relay 会广播 `tool_reset`，多个面板一起清）。
以前清完会话，工具页签还挂着上一轮的调用。实测：一轮对话 8 条事件 → 清空后 0 条。

### 四、工程侧（你不需要关心，但会影响后续质量）

- 新增门禁 **`check-running-stamp`**：确认"CAD 里跑的程序集"真的是刚构建的那份（假失败的一大来源）。
- **i18n 棘轮**修正：不再把代码注释里的中文当成未翻译文案（基线 76 → 24，剩下的都是真有意义的边界情况）。
- **`functional-smoke`** 判据补齐：参数校验类返回不再被误报成缺陷（曾经一次报出 12 个假问题）。
- 发布门禁 **36 步**；真机回归：通用 43 断言 · C3D 专业域 27 断言 · 数值不变量 31 项 · 面板 7 项 · 工具事件 8 项。


### 五、工作流捕获：把"做成的路径"记下来

每次任务结束后，系统会把这条**成功路径**清洗成一份记录（存 `exchange/projects/<项目>/workflows/`，全局任务存 `exchange/workflows/`）：

- 只记**方法 + 参数骨架 + 结果指纹**，**不记结果体**（一次 `getEntitiesByLayer` 可能 900KB，记了就把工具拖死）
- 失败步骤**保留并标记**，不静默丢弃（它是缺陷线索）
- job 轮询折叠回发起它的那一步，不然 60 步里有 8 步是无意义轮询
- 每条带**复用评分**和阻塞项（例如"无验证步骤""1 步依赖句柄"），评分写着"现在能不能放心复用"

**导出**：`JSON`（给机器）+ `Markdown`（给人读：目标 → 上下文 → 步骤表 → 参数骨架 → 评分），可以直接贴进方案文档。

### 六、dry-run 回放：只读步骤，先验证再谈自动

- 只重放**只读**步骤（`get/list/measure/calculate/analyze/assert`），写操作一律跳过并标注"需要 assisted 模式"
- 断言名单是**双向**的：允许名单之外，还有一份拒绝名单（`create/set/delete/purge/overkill/import/export/executeCommand/runLisp/...`）——名字看着只读也不会被执行
- 每步对比**结果指纹**：一致算 match，变了算 **mismatch 并让整次回放失败**（"跑通了但结果不一样"不算成功）
- assisted / auto 模式**尚未提供**，请求会被明确拒绝（501），不会假装支持

实测：13 步只读任务回放 **13/13 一致**；在图上加一条线后回放 → 2 步 mismatch、其余 11 步照旧一致。

### 七、安装包内容体检（发布流程）

这一版差点带着**开发者的长期记忆**发出去：`server/memory/agent-memory.bak` 被打进安装包，而它正是"回退记忆"的回滚目标——用户一回退就会拿到开发者的偏好。

已修：打包时 `server/memory` 只保留白名单文件；新增门禁 `check-package-contents`，发版前强制检查记忆白名单、`config.json` 无凭据、包内无图纸/备份/临时目录、DLL 版本与 `package.json` 一致。

### 已知问题（v1.10.0）

- 工具页签**只显示摘要**：想看参数与完整返回仍需查 `relay.log` / `readTrace`（这是刻意的设计，见 ADR 0005）。
- 面板拖得很窄时（< 320px）工具条会挤，按钮文字可能被压缩显示。
- 不支持 Civil 3D 2024（插件为 .NET 8，2024 是 .NET Framework 4.8）。
- 约 12 个方法底层仍走命令通道（FILlet/CHAMfer/TRim/EXTend/STRETCH/OVERKILL/角度标注/剖切面等），带 25 秒超时兜底。
- 描摹高色阶（≥16 档）块数会衰减，建议 2–12 档。
- 交互式命令卡住的自愈通道没有稳定复现手段，仍待真实报障样本。

### 下载哪个

| 文件 | 用途 |
|---|---|
| `new-acad-setup-v1.10.0.exe` | **推荐**。安装包，自动装 Autoloader bundle + 桌面图标 |
| `new-acad-v1.10.0.zip` | 绿色版，解压即用（需手动 NETLOAD 或放 ApplicationPlugins） |
| `new-acad.bundle.zip` | 只更新插件本体时用（放进 `%APPDATA%\Autodesk\ApplicationPlugins`） |

### 支持范围

AutoCAD / Civil 3D **2025 / 2026**（R25.0 / R25.1，.NET 8）· 界面语言：中文 / English（面板内切换，立即生效）

---

## new-acad v1.10.0 — See what the AI is doing, and keep what it got right (2026-10-21)

> One theme this release: **move "what is the AI doing" out of the log file and into the palette.**
> Previously you either waited or tailed `relay.log`. Now there is a **Tools** button in the status bar.

### 1. The Tools tab (new)

- **Call stream**: time / method / channel (TCP · MCP) / duration / result bytes / status.
  Failures are red and carry the error code; in-flight calls show `…`.
- **Method catalog**: all **420 TCP methods** with their Chinese description plus **call count / last duration / failure count**, sorted by usage. Loaded on demand from the live `listMethods`.
- Search filters both name and description; a "failed only" toggle; a counter line at the bottom.
- The tab lives inside the message pane, so **the input box and config area stay usable** while you watch.

**Privacy / size**: events carry **summaries only** (method, duration, bytes, error code) and **never the result payload** —
a 50k-entity export will not be pushed into the UI.

### 2. Palette fixes (the root cause of several visible oddities)

| What you saw | Why |
|---|---|
| Repeating `HANK_SHOW` added a second status bar and the Send button stopped working | Closing and reopening the palette rebuilt the whole UI **on top of** the old one; the visible button read the new, empty input box |
| The Tools button only changed colour | The tools view and the chat pane fought over the same space; the later control was hidden underneath |
| The toolbar row sat under the status bar | **WinForms docks in reverse z-order**: adding the status bar first made the content area claim everything, leaving the bar to overlay it (same root cause as the old "first message covered" report) |
| Buttons touched the grid / "方法目录" was clipped | Row too short, and **fixed pixels do not scale at 160% DPI** while fonts do |

All of it is now structurally non-overlapping, with two layout probes written to `plugin.log` so this class of bug is measurable instead of visual.

### 3. Clearing actually clears

"Clear chat" and "Clean everything" now also wipe the tool history (relay broadcasts `tool_reset` to every connected palette).
Verified: 8 events from one turn → 0 after clearing.

### 4. Engineering (invisible to users, matters for quality)

- New gate **`check-running-stamp`**: proves the assembly inside CAD is the one just built (a major source of false failures).
- **i18n ratchet** fixed: Chinese inside code comments is no longer counted as untranslated UI text (baseline 76 → 24).
- **`functional-smoke`** classifier fixed: parameter-validation responses are no longer reported as defects (12 false positives previously).
- Release gate now **36 steps**; live suites: 43 / 27 / 31 / 7 / 8 assertions.


### 5. Workflow capture — remembering the path that worked

When a task finishes, the successful path is cleaned up into a record (stored under `exchange/projects/<project>/workflows/`, or `exchange/workflows/` for project-less tasks):

- stores **method + parameter skeleton + result fingerprint** only — never the result body (one `getEntitiesByLayer` can be 900KB)
- failed steps are **kept and flagged**, never dropped silently (they are the defect trail)
- job polling is folded back into the step that started it, instead of padding the list with meaningless polls
- every record carries a **reusability score** and its blockers (e.g. "no verification step", "1 step depends on handles")

**Export**: `JSON` for machines and `Markdown` for humans (goal -> context -> step table -> parameter skeleton -> score), ready to paste into a report.

### 6. Dry-run replay — read-only steps only

- replays **read-only** steps (`get/list/measure/calculate/analyze/assert`); anything with side effects is skipped and labelled "needs assisted mode"
- the guard is two-sided: an allow-list **plus** a deny-list (`create/set/delete/purge/overkill/import/export/executeCommand/runLisp/...`) — a write method never runs just because its name looks harmless
- each step compares **result fingerprints**: equal means match, different means **mismatch and the whole run fails** ("it ran but the answer changed" is not a success)
- assisted / auto modes are **not available yet** and are refused explicitly (501) rather than pretended

Measured: a 13-step read-only task replayed **13/13 identical**; after drawing one extra line, 2 steps mismatched while the other 11 still matched.

### 7. Package content checks (release process)

This release almost shipped the **developer's long-term memory**: `server/memory/agent-memory.bak` was packed into the installer, and that file is exactly what the "revert memory" tool restores — so a user rolling back would inherit the developer's preferences.

Fixed: packaging now keeps only a whitelist inside `server/memory`; a new gate (`check-package-contents`) blocks a release when the memory whitelist is violated, when `config.json` carries credentials, when drawings/backups/scratch dirs are inside the package, or when the DLL version does not match `package.json`.

### Known issues (v1.10.0)

- The Tools tab shows **summaries only** — parameters and full results still live in `relay.log` / `readTrace` (deliberate, see ADR 0005).
- Below ~320px panel width the toolbar gets cramped and captions may be compressed.
- Civil 3D 2024 is not supported (.NET 8 vs .NET Framework 4.8).
- About 12 methods still use the command channel (FILLET/CHAMFER/TRIM/EXTEND/STRETCH/OVERKILL/angular dimension/section plane…), guarded by a 25s timeout.
- Tracing at high tone levels (≥16) loses block count; 2–12 is recommended.
- The interactive-command recovery path still has no stable reproduction, pending a real report.

### Which download

| File | Purpose |
|---|---|
| `new-acad-setup-v1.10.0.exe` | **Recommended** installer (Autoloader bundle + desktop icon) |
| `new-acad-v1.10.0.zip` | Portable, unzip and use |
| `new-acad.bundle.zip` | Plugin-only update (drop into `%APPDATA%\Autodesk\ApplicationPlugins`) |

### Support

AutoCAD / Civil 3D **2025 / 2026** (.NET 8) · UI language: Chinese / English (switch in the palette, applies immediately)
