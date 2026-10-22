## new-acad v1.7.4 — 描摹修复 + 可靠性改进（2026-10-11）

> 说明：本次是**修复与加固**为主，功能上没有大改。下面每条都对应本次已提交的改动与实测数据；
> 本次**没有**解决的问题一律列在"已知问题"里。

### 一、图片 → CAD 描摹

- **色阶上限 8 → 32**：以前请求 10 档会被**悄悄砍成 8 档**且没有提示；现在按请求执行，超出上限或结果异常会给出**明确警告**（stats 里回传 `requestedLevels/effectiveLevels/blocks/warnings`）。
  ⚠️ **但高色阶目前不稳**——合成图实测（1400×1000）：8 档 97 块 / 12 档 136 块 / 16 档 128 块 / **24 档 47 块 / 32 档 2 块**。SOP 仍建议 **2–12 档**。
- **"画了多少块"报数口径修正**：以前报的是图层/填充对象数；现在明确区分 **轮廓块数（loops）/ 路径数 / 图层数**。
  真机（C3D 2025 空白图，请求 8 档）：**8 个填充图层 / 81 个轮廓环 / 0 失败**。
- **小连通域吸收**：碎环并入相邻区域，同档数下空洞明显减少（L8 覆盖率 **91.6% → 97.5%**，spike 实测）；残余仍由 `toneMinArea` 控制，**不是零空洞**。
- **格式统一**：JPG/PNG 不再走两条管线——插件侧统一回**彩色**；**隔行 PNG** 自动兜底；`.tif/.tiff` 可直接交给 AI 看图；面板文件过滤器新增 **webp**。
- 新增 **明暗对比 / 灰度旋钮**（`toneContrast` / `toneGamma`）。

### 二、可靠性

- **静默异常治理（本次补了 30 处）**：插件 **16 处** + relay **14 处**"吞掉异常"的 catch 加上日志。
  以前出问题只表现为"没反应"，现在日志里能看到**卡在哪一步、为什么**。
  ℹ️ **仓库里仍有 144 处空 catch 待逐一复核**（本次实测；其中一部分是合理的 best-effort）。
- **命令上下文超时看门狗**：CAD 被命令占用时周期探测，**真空闲即解锁**；同时保证不会永久卡在 `busy`（诊断信息里能看到卡住的命令名与耗时）。
  ⚠️ **限制（真机实测）**：遇到**模态对话框**（如 NETLOAD 的信任弹窗）时投递 ESC 无效——必须手动关掉对话框/重启 CAD；该次实测恢复耗时 **60010ms**（撞满 60s 上限）。看门狗的价值是"不永久卡死 + 诊断可见"，**不是"自动脱困"**。
- **命令投递契约诚实化**：源码里 **14 处真实命令投递**逐一分类（8 处属设计性投递、6 类需回读），注册表里标注"入队异步执行、无同步结果"，并指向回读通道（`*Geo` / `getSelectedCivilObjectsInfo` / `listDataShortcuts`）。新增 `getVisualStyle`：实测 `VSCURRENT` 在 25.0.58 读不到 → **如实回退**并说明。

### 三、命令方法 API 化

- 本次新增 **7 个**（真机验证通过）：`convToSurface` / `convToSolid` / `sectionPlane` / `fillet` / `chamfer` / `stretch` / `dimAngular`。
- 另有 `trim` / `extend` / `overkill` 早先已 API 化。
- 差别：以前这类操作用"投递命令"实现（拿不到同步结果），现在**同步返回结果**。

### 四、工程（不影响日常使用，但让发布更可靠）

- **版本号唯一来源**（`tools/sync-version.js --check`）+ **发布门禁** `tools/release-gate.js`（语法 / 版本一致 / 描摹链路自测 / 缺陷清单生成 / 插件编译；`--with-cad` 再跑真机 smoke）。
- 修复打包脚本在中文 Windows 下把版本号读成 `0.0.0`（包名会变成 `v0.0.0`）。
- `HANK_SHOW` 入口补日志 —— "面板不弹"以前无法诊断，现在能查。

### 已知问题（v1.7.4 仍未解决）

1. **144 处空 catch** 仍待逐一复核（本次只处理了其中 30 处确认有问题的）。
2. **高色阶描摹不稳**（≥24 档块数锐减，见上）。
3. **模态对话框下看门狗不能自动解锁**（ESC 对模态窗口无效）。
4. `trim/extend/overkill` 的"投递版"与"同步 Geo 版"**功能重叠**，是否把旧方法重定向/下线**待定**。
5. `render` 仍是命令投递（25.0.58 无渲染 API）。
6. **没有自动化测试网**：目前是 QC 脚本 + 发布门禁，不是 CI 回归。
7. **不支持 Civil 3D 2024**（.NET Framework 4.8，插件无法加载）。

### 下载哪个

- `new-acad-setup-v1.7.4.exe` — 一键安装器（推荐）
- `new-acad-v1.7.4.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装旧版：重跑安装器即可（或替换插件 DLL + server 目录）。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.7.4 — Tracing fixes + reliability work (2026-10-11)

> This release is mostly **fixes and hardening**. Every claim below maps to a committed change
> and measured data. Anything we did NOT fix is listed under "Known issues".

### 1. Image -> CAD tracing

- **Tone level cap 8 -> 32** (requests above 8 used to be silently clamped, with no warning). Out-of-range or anomalous results now raise **explicit warnings** (`requestedLevels/effectiveLevels/blocks/warnings` in stats).
  ⚠️ **High levels are still unstable** — synthetic 1400x1000 test: 8 levels 97 blocks / 12 levels 136 / 16 levels 128 / **24 levels 47 / 32 levels 2**. The SOP still recommends **2–12 levels**.
- **Block-count reporting fixed**: used to report layer/hatch object count; now separates **outline loops / paths / layers**. Real machine (C3D 2025, 8 levels requested): **8 fill layers / 81 loops / 0 failures**.
- **Small-region absorption**: specks merge into neighbours — holes drop noticeably (L8 coverage **91.6% -> 97.5%**, spike measurement). The remainder is still controlled by `toneMinArea`; it is **not zero-hole**.
- **One pipeline**: color is returned for JPEG as well; **interlaced PNG** falls back gracefully; `.tif/.tiff` can be read; **webp** added to the file filter.
- New **contrast / gamma** knobs (`toneContrast` / `toneGamma`).

### 2. Reliability

- **Silent exceptions (30 fixed this round)**: 16 in the plugin + 14 in the relay now log. Failures used to look like "nothing happened".
  ℹ️ **144 empty catches remain** repo-wide (measured today; some are legitimate best-effort).
- **Command-context watchdog**: probes while CAD is busy and unlocks as soon as the host is really idle; guarantees you never get stuck in `busy` forever (diagnostics show the blocking command and elapsed time).
  ⚠️ **Limitation (measured)**: with a **modal dialog** up (e.g. the NETLOAD trust prompt) posted ESC has no effect — the dialog must be closed by hand. In that test recovery took **60010 ms** (hit the 60 s ceiling). The watchdog buys "no permanent stall + visibility", **not auto-recovery**.
- **Honest command-delivery contracts**: all **14 real command deliveries** classified (8 by design, 6 need readback); the registry says "queued, no synchronous result" and points at a readback channel. New `getVisualStyle` honestly falls back (`VSCURRENT` is unreadable on 25.0.58).

### 3. Commands promoted to real APIs

New this round (**7**, verified on a real machine): `convToSurface` / `convToSolid` / `sectionPlane` / `fillet` / `chamfer` / `stretch` / `dimAngular` (+ `trim` / `extend` / `overkill` earlier). They return results synchronously instead of only posting a command.

### 4. Engineering (no day-to-day impact)

Single source of truth for the version + `tools/release-gate.js`; fixed the packaging script reading the version as `0.0.0` on Chinese Windows; `HANK_SHOW` now logs.

### Known issues (still open in v1.7.4)

1. 144 empty catches await review (only the 30 confirmed problems were handled).
2. High tone levels (>= 24) are unstable.
3. The watchdog cannot unlock a modal dialog (ESC is ignored there).
4. `trim/extend/overkill` duplicate their synchronous `*Geo` counterparts — redirect/remove is undecided.
5. `render` is still a posted command (no render API in 25.0.58).
6. No automated test suite yet (QC scripts + release gate only).
7. Civil 3D 2024 is not supported.

### Downloads / Supported versions

`new-acad-setup-v1.7.4.exe` / `new-acad-v1.7.4.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). **Civil 3D 2024 is NOT supported.**
