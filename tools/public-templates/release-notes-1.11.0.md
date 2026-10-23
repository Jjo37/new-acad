## new-acad v1.11.0 — 能改参数重放，也能撤回一条（2026-10-23）

这一版的主题是**「干过的活能复用，做错的一步能退」**。三个新能力都是从"AI 干完就没了"这个痛点上长出来的。

### 一、回放：一步一步确认，不给你惊喜（P2.3）

一条干成功的任务现在能存成**工作流记录**，下次重跑。

- 每个步骤按**可逆性**分档：A 只读（自动跑）· B 可逆写（要你确认）· **C 破坏性（删/清/脚本：永远逐步确认，没有批量放行）**
- 计划视图里能看到每一步的方法、参数、档位；C 档标红，下一行标「待确认」
- 失败立即停，不会"错了还继续往下跑"
- 换图纸时，引用旧句柄的步骤会被拦下（句柄只属于录制那张图）

### 二、改参数重放：绑定表（P2.4）

记录里的数值/名称会抽成**槽位**，改完再跑：

- **同值同键自动合并**：`distance = 50` 出现在六步里 → 改一次，六步全变
- **空着不填就阻断**，不会偷偷用旧值
- **句柄永远不是槽位**（那是对象绑定，不是参数）
- 跑完给**验收清单**：新增/修改/删除各几个 + 实际用了哪些参数

### 三、撤回一条动作（P2.5，这一版重做）

"撤回上次"以前是栈语义（退到第 5 步，6~N 一起废）。现在是**每个动作独立可撤**：

- 建的东西 → 撤掉；删/改的东西 → 用调用前的快照恢复
- **撤一条，旁边的动作完全不动**
- 快照存放在独立的内存库（ADR 0008），**你的图纸里不会多出隐藏图层或幽灵实体**
- 撤不回的会**明说原因**：`无逆操作`（purge/脚本/写文件）/ `C3D 对象不支持` / `已超出窗口` / `句柄已失效`——不再假装成功
- 只对**普通 AutoCAD 图元**承诺（线/多段线/圆/弧/文字/点/块参照）；路线/曲面/廊道/管网如实拒绝

### 四、面板稳定性（实测点出来的六个问题）

| 现象 | 根因 |
|---|---|
| 首点「调用流」会闪一下工作流 | 先画再切模式，还多发一次异步请求 |
| 从日志视图回来还显示日志列 | 视图模式标记没清 |
| 点「清空」后重连，旧调用又回来 | 只清了面板，服务端事件缓冲没清 |
| 日志视图的「清空」清错了对象 | 清的是调用流计数，与回退清单无关 |
| 「清空回退清单」一键到底太危险 | 改为**两击确认**（8 秒窗口，零模态框） |
| 偏移实际成功，记录却显示失败 | 判定改成只看动作类失败（探针失败后重试成功不算） |

### 五、提示全部双语，跟面板语言走

以前服务端直接吐英文字符串到面板里。现在**服务端只给原因代码，文案归界面**：撤回的 4 类原因、回放的 4 类拦截，全部中英各一份，切语言立即生效。

### 六、给非中文用户

- **坐标文件两种小数点都认**（`1234.56` 与 `1234,56`），读不出来明确报错并指出行号 —— 修掉一个会**静默放大 100 倍**的区域设置陷阱
- 工作流记录按 **UTC 时间**排序（不再按本地时间文件名），跨时区不会把新记录当旧的删掉

### 七、使用手册重写 + 面板直达

- 手册从"45KB 长页、中英各半、无目录"改成**单文件双语 + 两级可折叠目录**，8 章 50 小节
- 新增第 4 章「面板功能逐项」：每个按钮都写了**作用 / 会发生什么 / 别这么用**
- 面板「更多 → **使用手册**」直接打开安装目录里那一份（离线、与版本匹配）

### 已知问题（v1.11.0）

- **Civil 3D 2024 及更早不支持**（.NET 8 vs .NET Framework 4.8）
- 工作流**不能跨图纸回放**（句柄语义决定，不是缺陷）
- 撤销只覆盖普通 AutoCAD 图元；Civil 3D 对象会明确标"不可撤"
- 约 12 个方法仍走命令通道（FILLET / CHAMFER / TRIM / EXTEND / STRETCH / OVERKILL / 角度标注 / 剖切面等），带 25s 超时兜底
- 描摹高色阶（≥16 档）块数会衰减，建议 2–12 档
- 信任模型（模块识别 + trust 计分）**不在本版**，下一版再做
- 交互式命令卡住的自愈仍无稳定复现路径

### 下载哪个

| 文件 | 用途 |
|---|---|
| `new-acad-setup-v1.11.0.exe` | **推荐**：安装包（注册插件自启 + 桌面图标） |
| `new-acad-v1.11.0.zip` | 绿色版，解压即用 |
| `new-acad.bundle.zip` | 只更新插件（丢进 `%APPDATA%\Autodesk\ApplicationPlugins`） |

### 支持范围

AutoCAD / Civil 3D **2025 / 2026**（.NET 8）· 界面语言：中文 / English（面板内切换，立即生效）· 内置方法 **423** 个

---

## new-acad v1.11.0 — Replay with new numbers, undo one step (2026-10-23)

This release is about **keeping the work that went right and taking back the one step that went wrong**.

### 1. Assisted replay — one confirmation at a time (P2.3)

A task that worked becomes a **workflow record** you can run again.

- Each step is classified by reversibility: A read-only (runs itself) · B reversible write (needs you) · **C destructive (delete / purge / script: always step by step, no batch approval)**
- The plan view shows method, parameters and tier; tier C is red and the next row is marked "needs confirmation"
- A failure stops the run immediately — it never carries on after an error
- In another drawing, steps that reference old handles are blocked (handles belong to the file they were recorded in)

### 2. Replay with new parameters — the binding table (P2.4)

Numbers and names in the record become **slots** you edit before re-running:

- **Identical values collapse into one slot**: `distance = 50` used by six steps changes once, all six follow
- **An empty slot blocks the run** — it never silently reuses the old value
- **A handle is never a slot** (that is an object binding, not a parameter)
- You get an **acceptance summary**: how many created / modified / deleted, and which values were actually used

### 3. Undo one action (P2.5, reworked)

"Undo last" used to be stack semantics (roll back to step 5 and 6..N go with it). Now **each action is independently revertible**:

- What was created is erased; what was deleted or modified is restored from a snapshot taken before the call
- **Reverting one step leaves its neighbours completely untouched**
- Snapshots live in a separate in-memory database (ADR 0008) — **no hidden layer, no orphan entities in your drawing**
- What cannot be reverted says why: `no inverse` (purge / script / file write) / `Civil 3D object not supported` / `outside the undo window` / `handles stale` — never a fake success
- The promise covers **plain AutoCAD entities** (line / polyline / circle / arc / text / point / block reference); alignments, surfaces, corridors and pipe networks are refused honestly

### 4. Palette stability (six issues found on the real machine)

| Symptom | Root cause |
|---|---|
| First click on "Call stream" flashed the workflow view | it painted first and switched mode afterwards |
| The journal's columns stayed after leaving the journal | the view flag was never cleared |
| "Clear" then reconnect brought the old calls back | only the palette was cleared, not the service-side buffer |
| The journal's "Clear" cleared the wrong thing | it reset call counters, unrelated to the journal |
| "Clear the whole journal" was one click from disaster | now **two clicks within 8 seconds**, still modal-free |
| The offset task worked but the record said failed | only action failures count now (a retried probe does not) |

### 5. Every hint bilingual, following the panel language

The service used to push English sentences straight into the palette. Now **the service sends a reason code and the UI owns the wording**: four undo reasons and four replay blockers, each in Chinese and English, switching with the UI language.

### 6. For users outside Chinese locales

- **Coordinate files accept both decimal separators** (`1234.56` and `1234,56`), and an unreadable value names the line — this fixes a locale trap that **silently multiplied coordinates by 100**
- Workflow records sort by **UTC createdAt** instead of the local-time file name, so changing timezone no longer makes a new record look old (and get pruned)

### 7. User guide rewritten, and reachable from the palette

- From "a 45 KB page with a Chinese half and an English half and no index" to **one bilingual page with a two-level collapsible index**: 8 chapters, 50 sections
- Chapter 4 documents every control as **what it does / what happens / when not to use it**
- "More → **User Guide**" opens the copy installed alongside the program (offline, matches the version)

### Known issues (v1.11.0)

- **Civil 3D 2024 and earlier are not supported** (.NET 8 vs .NET Framework 4.8)
- A workflow **cannot be replayed in another drawing** (handle semantics, not a defect)
- Undo covers plain AutoCAD entities only; Civil 3D objects are marked "cannot be reverted"
- About 12 methods still use the command channel (FILLET / CHAMFER / TRIM / EXTEND / STRETCH / OVERKILL / angular dimension / section plane …), guarded by a 25 s timeout
- Tracing at high tone levels (≥16) loses block count; 2–12 is recommended
- The trust model (module identification + trust scoring) is **not** in this release; next one
- The interactive-command recovery path still has no stable reproduction

### Which download

| File | Purpose |
|---|---|
| `new-acad-setup-v1.11.0.exe` | **Recommended** installer (autoloader registration + desktop icon) |
| `new-acad-v1.11.0.zip` | Portable, unzip and use |
| `new-acad.bundle.zip` | Plugin-only update (drop into `%APPDATA%\Autodesk\ApplicationPlugins`) |

### Support

AutoCAD / Civil 3D **2025 / 2026** (.NET 8) · UI language: Chinese / English (switch in the palette, applies immediately) · **423** built-in methods
