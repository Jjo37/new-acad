# KNOWN-ISSUES — 已知问题台账（唯一来源）

> 发版说明的「已知问题」**从本表派生**。**四段口径**：
> - **A 设计边界** = 非缺陷/固有权衡（不再逐版重复）
> - **B 待办** = 可闭、带目标版本/状态
> - **C 已修复** = **真实改动**（必须能对应到提交/测试）
> - **D 已定案** = 做了**决策**（非代码修复，如"保留并弃用"）
>
> **反"为了解而解"**：口径变化（"这其实是边界"）归 A/D，**不许**塞进 C 虚增战果；
> 只改了个阈值/加了个标签、没动行为 —— 不算 C。
> 维护纪律见 `BOOTSTRAP.md` / `DASHBOARD.md`。上次更新：2026-10-21

---

## A. 设计边界（非缺陷 —— 不再逐版重复列入发版说明）

| 项 | 说明 | 处理 |
|----|------|------|
| 不支持 Civil 3D 2024 | 插件为 .NET 8；2024 是 .NET Framework 4.8 | 产品边界 |
| `render` 无渲染 API | 25.0.58 无托管渲染接口 | 命令投递 + 超时兜底 |
| 约 12 个 command 方法无公开托管 API | fillet/chamfer/trim/extend/stretch/overkill/dimAngular/sectionPlane/convertTo*/render 等 | 保留 command + 超时兜底（部分已几何自研） |
| 扫描件 OCR 依赖系统 OCR 语言包 | Windows OCR 引擎按语言包工作 | 产品边界；v1.8.4 起 install 预检 + 缺包明确报错 |
| 超时/busy 期间的宿主诊断走**免门**出口 `getCivil3DHealth` | 该方法不经 `HostExecutionGate`，直接返回 `hostExecution`（busy 状态 + `lastTimeout` 快照），**busy 时仍可达**；`getDrawingInfo` 走文档上下文（需命令上下文），busy 时不可达属正常 | 产品边界（诊断入口 = `getCivil3DHealth`）；`check-host-guards` 有断言防回归 |
| **高色阶描摹块数下降（≥~16 档）** | **根因**：`toneChains` 的小连通域过滤是**绝对阈值**（按图像百分比），档数越高每档区域越小、越易被吸收/丢弃 → 块数下降。**非 bug，是管线权衡**（代码 2026-10-10 已注明"超 12 档管线自身衰减"）。 | 结果已**如实回报** `requestedLevels`/`effectiveLevels.`/`warnings`；建议 2–12 档。真修见 B 表（自适应阈值，需真机视觉验证） |

## B. 待办（可闭 —— 带目标版本/状态）

| 项 | 目标 | 状态 | 说明 |
|----|------|:--:|------|
| ~~`FileBoundaryHarness` 断言过时~~ **已关闭（2026-10-20）** | **已修** | 已定案 | Bro 拍板 **C 方案（读写分级）**：**导出全盘放行**（用户要把成果存到桌面/D盘/NAS）、**导入限定 roots**（读外部文件是攻击面；默认 = 用户目录 + 程序目录 + `exchange\import`，NAS 等用 `CIVIL3D_IMPORT_ROOTS` 显式加）。断言同步改为"导出放行、导入拒绝"，**门禁已从 WARN 升为阻断**。变异测试确认：把 `enforceRoots` 关掉套件立刻红 → 断言是活的。 |
| 交互式命令卡住时的自愈（ESC ESC 通道） | 待真机 | **代码已改；旧复现路径不成立** | 旧台账写「CAD 敲 `LINE` 停住 → 触发超时」，**2026-10-19 真机实测不成立**：手动停在 `LINE` 提示时 `getDrawingInfo` 349ms 正常返回、`CMDACTIVE=0`、`hostBusy=false`，根本进不了超时分支（且插件调用本身会把该提示清掉，属观测者效应）。**能稳定触发的是模态对话框**（见 C 表）。ESC ESC 通道当前只在模态场景被间接验证（对话框 1s 内被关、3s 内 `recovered=true`）；纯交互命令场景仍无稳定复现手段 |

## C. 已修复（真实改动 —— 必须能对应到提交/测试）

| 版本 | 项 | 证据 |
|------|----|------|
| 1.10.0 发布前修复 | **安装包把开发者的长期记忆一起发出去** —— `dist/server/memory/` 整目录 `Copy-Item -Recurse` 后再把 `agent-memory.md` 重置为中性骨架，但 **`agent-memory.bak` 没重置**（840B）。`.bak` 正是 `revertMemory` 的回滚目标 → 新用户一旦回退记忆，会拿到开发者的偏好（"对话回复用英文"等）。1.9.0 同样带，1.8.4 没有（`.bak` 是 10-13 加的 M2 功能） | `tools/check-package-contents.js`（新门禁：`server/memory` 白名单 + `.md` 必须中性 + `config.json` 无凭据 + DLL 版本对齐）+ `build-dist.ps1` 重置骨架后按白名单清理；本地 dist 与已装目录的 `.bak` 已删 |
| 已发 1.10.0 | **重复 HANK_SHOW 会叠出第二套界面 →「发送」按钮失灵、状态栏多一行** —— `BuildUI` 无条件调 `BuildContent`，而后者不幂等（每次都 new 一套 statusBar/split/toolsView）。面板被 × 关掉后再 HANK_SHOW，旧控件留在下层、新控件叠在上面；按钮是静态方法，读的是新面板里那个**空输入框** → 点了没反应 | 统一入口 `RebuildContentPreservingChat()`：先 Dispose 现有子控件再建（`BuildUI` 与 `RebuildUi` 共用）；`BuildToolsView` 先摘旧视图；日志增 `content build start/done (children=N)` 便于量测。提交 `29c2c8d` |
| 已发 1.10.0 | **「工具」页签点了只变色、界面不变** —— 工具视图与对话区同属一个容器且都是 `Dock=Fill`；后加入的在下层被前者完全覆盖，切 `Visible` 看不出变化 | 工具视图移入消息区容器（`split.Panel1`），与消息框**显式互斥**；UIA 点击验证生效（日志 `tool catalog loaded: 420 methods`）。提交 `6a62b36` |
| 已发 1.10.0 | **面板内容顶部 24px 被状态栏压住（工具条首行不可见）** —— WinForms 停靠按 z-order **反序**：后加入的先停靠。原顺序「先 statusBar 再 split」→ split(Fill) 先吃掉整块客户区，statusBar 只能叠在它上面；对话区同样受影响（即历史「首句被遮挡」） | 改「先 split 后 statusBar」；一次性布局探针写入 `plugin.log`，实测 `statusBarBottom=24 splitTop=24`（零重叠）。提交 `6a62b36` |
| 已发 1.10.0 | **工具条按钮与表格碰撞 /「方法目录」字被切** —— ① 行高 34px 容纳不下 AutoSize 按钮；② 屏幕 160% DPI 下**固定像素不缩放、字体缩放**，固定 88px 列宽切字 | 三行 `TableLayoutPanel`；行高按控件 `PreferredSize` 计算（实测 `barNeed=37 → 51px`）；按钮列 AutoSize。探针验证 `modeW=89/need 89 ok`、`barBottom=42 gridTop=42`。提交 `41e1d66`/`28da6de`/`de78c62` |
| 已发 1.10.0 | **清空会话/全部清理后，「工具」页签仍显示上一轮调用记录** —— 两层缓存：relay 的 200 条环形缓冲（每次重连回放）+ 面板静态 `toolRows` 与方法计数 | relay `/clear`（`scope=chat\|all`）清空 `toolEvents` 并广播 `tool_reset`；面板据此清行/清计数/清排队；工具页签「清空」按钮同源。实测 `replay 0 → 8（一轮对话）→ 0（清空后）`。提交 `de65572` |
| 已发 1.10.0 | **i18n 棘轮把 C# 注释里的中文当成未包装文案（假红）** —— 新增一行带引号的中文注释就会让门禁变红，逼着人不敢写注释 | 判据跳过注释行（与 node 分支一致）；重写基线 **76 → 24**（那 52 条本来就是注释噪声）。提交 `29c2c8d` |
| 已发 1.10.0 | **`functional-smoke` 把参数校验文案当成「真问题」（12 条假红）** —— 判据正则含 `non-empty` 但没收 `handles must not be empty`，于是空句柄类方法被误判为缺陷 | 补词表（`must not be empty`/`must be provided`/`cannot be empty`/`is empty`）→ 真问题回到 0。提交 `d3c47d3` |
| 已发 1.10.0 | **新门禁 `check-running-stamp` 自身两次假红** —— ① 用 git 提交号判断（构建后再提交就报「陈旧」，而代码没变）；② 扫描包含 `src/obj/**/AssemblyInfo.cs`（构建生成，必然比进程新） | 改时间线判据（插件源码 mtime vs CAD 进程启动时间）+ 跳过 `obj/bin`。提交 `2c9b77e`/`af35f39`/`d3c47d3` |
| 已发 1.9.0 | **用户点「取消」被报成「AI 处理出错」** —— `/cancel` 只把 `pending`/`claimed` 标 cancelled，而正在跑的那条是 `processing` → 名单为空、状态没改；catch 靠 `entry.status` 判断用户取消不成立 → 落到通用错误分支；而 abort 抛的文案正是 `任务已取消`，于是主动取消显示成系统故障 | `/cancel` 标记加入 `processing`；catch 增加 `wasCancelled` 三重判定（status / 文案 / `signal.aborted`），命中回 `⏹ 已取消（用户中止）`。真机实测：`cancelled: f` → `Cancelled by user: f` → 回复已取消。提交 `ebfc229`；`panel-selftest` 第 3 项固化该回归 |
| 已发 1.9.0 | **`_sel.json` 带 BOM 导致 relay 从未读到 + 跨图残留** —— ① 插件用 `System.Text.UTF8Encoding.UTF8` 写文件＝**带 BOM**，relay `JSON.parse` 直接抛 `Unexpected token`，被 `catch (_)` 吞掉只留「读取失败」→ 一直用内存旧缓存；② 图纸名会重名（AutoCAD 默认 `Drawing1.dwg`），单靠 `docName` 判断不出跨图 → 脏 handle 被 `getSelection` 返回，插件逐个 `eUnknownHandle` 跳过 | ① 读取剥 BOM ② **以插件为准**：插件说当前选中 0 而文件仍有 handle → 就地重置为空 ③ `docName` 不一致同样重置 ④ `catch (_)` → `catch (e)` 并打印原因。实测 `[SEL] Snapshot contradicts plugin (file 2 vs plugin 0) -> reset`，`_sel.json` 变 `items:[]`，AI 正确回答 0 个。提交 `1c864a3` |
| 已发 1.9.0 | **AI 可自行覆盖用户文件** —— 系统提示词早已写明 writeTextFile「create-only, never overwrite」，但实现没执行：13 处命令直读 `overwrite` 参数、`ExportSelectionToFileAsync` 硬编码 `overwrite: true` | ① 新增 `PluginRuntime.GetAiOverwrite()`：AI 传 true 直接抛 `CIVIL3D.OVERWRITE_NOT_ALLOWED`；13 处调用点全改走它 ② AI 导出改 `overwrite:false`（冲突即报 CIVIL3D.CONFLICT，让 AI 换名） ③ 内部快照（选择集 _sel.json）改走 `FileBoundary.WriteAllTextAtomicInternal()`，硬编码 true 只允许出现在 FileBoundary.cs ④ 新增门禁 `tools/check-overwrite-guard.js`（3 条断言：护栏存在/无直读绕过/硬编码仅限内部），已挂 release-gate 第 2k 步 ⑤ 描述表去掉 `overwrite?`（EN+ZH 各 10 处），不再宣传已禁止的参数。门禁 20/20 通过 ⑥ **真机已验证**（2026-10-20 13:47，C3D 25.0s / 插件 1.9.0.0，DLL sha256 `37284aaf…` 与 bundle 一致）：`overwrite:true` → `CIVIL3D.OVERWRITE_NOT_ALLOWED`；新文件导出成功；同名重导 → `CIVIL3D.CONFLICT`；换名成功 ⑦ CONFLICT 文案改掉"Set overwrite=true"（原提示教 AI 做已被拒绝的操作） |
| 已发 1.9.0 | **空管网导致 `listPipeNetworks` / `getPipeNetwork` 整体 `INTERNAL_ERROR`** —— 根因：空管网（无管无结构）的 `ReferenceSurfaceId` 读取抛 `CivilException: Retrieve attribute failed.`，原来一个属性炸掉整个方法 | 改为逐字段 best-effort + `<字段>Error` 回报。真机（2026-10-19）：建 `Sanitary Sewer` 空网 → `listPipeNetworks` 92ms 正常且含新网、`getPipeNetwork` 86ms 正常；`surfaceError` 精确指向 `ReferenceSurfaceId`。专业域回归 **17 passed / 0 failed**（`tools/live-c3d-regression.js`） |
| 已发 1.9.0 | **模态对话框下超时看门狗不能自动解锁** —— 进程内 ESC + 升级关闭；识别改为 `EnumWindows` 枚举本进程窗口（原先只看 `GetForegroundWindow()`，切到别的程序就找不到）；恢复判据改为「`CMDACTIVE=0` 且无阻塞对话框」（不再要求无未结束的放弃任务） | 真机（2026-10-19，`_OPTIONS`）：25.1s 超时 → 对话框在 **同一秒** 消失（外部枚举 `#32770` 10:17:33 在 / 10:17:34 无，用户全程未操作）→ `recovered=true after=2999ms`、`busy=false`、随后 `getDrawingInfo` **121ms**、**C3D 存活**。提交 `e352cc1` + 后续（`check-host-guards` 第 10 项、负向测试通过） |
| 已发 1.9.0 | **安装包漏打包运行时模块 → 用户端 relay 启动即 `MODULE_NOT_FOUND`** —— 根因：`build-dist.ps1` 是手写文件清单，新增的 `server/knowledge-sources.js`/`server/i18n.js`/`i18n/messages.json` 都没登记；且 dev 与 release 各有一份 `build-dist.ps1`，sync 会用 dev 的旧版覆盖 release 的修补 | ① 清单补 3 项 ② `build-dist.ps1` 末尾加 **dist 模块闭环自检** ③ `check-package-clean.js` 断言必需 server 模块 + i18n key 完整 ④ 新增 `tools/check-package-runtime.js`（包内语法/require 闭环/文案表/DLL 非空壳）与 `tools/smoke-packaged-relay.js`（用**包里的 node.exe** 起 relay 问 `/health`），两者接入发版第 6 步。实测 1.9.0：dist 与 bundle 两处自检通过、冒烟 `/health` 200 |
| 已发 1.9.0 | **`.ps1` 中文注释把代码整行吞掉（灵异级）** —— `release-all.ps1` 头部写着 ASCII-only，加了一行中文注释（UTF-8 无 BOM）→ 中文 Windows 的 PowerShell 5.1 按 GBK 读 → 乱码拼出伪 `<# ... #>` 块注释 → 紧随其后的 `$bundleContents = ...` 被吞 → 变量恒 null → `Test-Path ""` 报"Path 不能为空"。症状极具误导性：相邻 `$setup`/`$zip` 正常，同名赋值失败换名就成功，`Get-Variable` 说变量不存在 | ① 该注释改英文（纯 ASCII 校验 0 非 ASCII）② 新增 `tools/check-ps-ascii.js` 编码守卫并接入门禁（第 11 项）：纯 ASCII / UTF-8+BOM / GBK 放行，**UTF-8 无 BOM 含非 ASCII 一律 FAIL** ③ 给已有 5 个同类隐患脚本补 BOM（build-bundle / publish-public / install-hooks × dev+release） |
| 已发 1.9.0 | `getEntityInfo` 补**稳定字段** `colorIndex`(ACI)/`colorMethod`；`color` 标注为本地化名勿用于判断 | 真机回归断言 `colorIndex=1` 通过（23/0/7） |
| 已发 1.9.0 | **零文档 fail-fast 守卫**：无活动图纸时命令上下文操作立即返回 `CIVIL3D.NO_DRAWING`（不再硬卡 25s → HOST_BUSY 级联）；文档级 `openDrawing`/`newDrawing` 走 `ExecuteDocumentOpAsync` 不受限，保留破局通道 | 真机（2026-10-16，C3D 停开始页）：`getDrawingInfo`/`listCorridors`/`listSurfaces`/`overkillEntities`/`renderScene` 全部 **1–5 ms** 返回 NO_DRAWING，复测**无 HOST_BUSY 级联**；`newDrawing` 3.5 s 成功、随后 `listCorridors` 20 ms 正常 |
| 已发 1.9.0 | **界面语言 `auto` 未真正解析**（`llm-agent` 直接用 config 原始值判断，`auto` 既非 en 也非 zh）→ 语言注记为空、系统提示却走中文 → 面板回复语言随缘（Bro 报「英文打招呼后蹦一句中文」） | 新增 `server/i18n-en.js#resolveLocale()`（`en*`/`zh*` 直判，其余按系统 ICU locale 解析＝跟随系统）；`llm-agent` 的 `getUiLocale()` 与内联 `uiLocale` 共用之。本机（locale=auto）实测 `resolveLocale()==='zh'` |
| v1.8.4 | 扫描件 OCR 缺语言包**静默降级** → 缺包明确报错 + 指引；新增 `tools/ocr-check.ps1`；`install.ps1` 自动预检 | `server/ocr.js` 已有错误分流 + 新增预检脚本；install 调用 |
| v1.8.1 | 英文界面未全覆盖（421 条描述 + SYSTEM_PROMPT）→ i18n B 完成 | i18n 全量 |
| v1.8.1 | 扫描件 PDF 不做 OCR → WinRT OCR | `server/ocr.js` |
| v1.8.0 | 大图纸读全量卡顿 → 预算化画像 + 结果裁剪 | summarizeDrawing |

## D. 已定案（决策，非代码修复）

| 版本 | 项 | 决策 |
|------|----|------|
| v1.9（待定） | 描摹高色阶（≥16 档）碎块/块数衰减 | **不再投入**（Bro 决定，2026-10-19）：开发期已得到阶段性最优解，继续调自适应阈值收益低。结果继续**如实回报** `requestedLevels` / `effectiveLevels` / `warnings`，建议 2–12 档（已在 A 表作为设计边界登记） |
| v1.8.4 | `trim/extend/overkill` 与 `*Geo` 版重叠、去留待定 | **保留命令版并标记【已弃用】(deprecated)**，新用法一律走 `*Geo`；下个大版本评估移除。（**决策**，未改行为） |

---

## 发版流程要求

1. 发版前：本版 C 表只放**真实改动**；决策放 D；"其实不是 bug"的重新归 A。
2. 发版说明的「已知问题」= **B 表快照**；另列「本版变更」（C/D 各一行）。
3. B 变短**不是目标**——诚实归类才是。`tools/check-known-issues.js` 只强制"最近两版清单不逐字相同"，
   防止清单冻结；它**不**奖励"凑数划掉"。
