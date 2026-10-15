## new-acad v1.8.4 — 口径修正版：1 项真修 + 1 项定案 + 1 项诚实降级（2026-10-15）

> 本版顺手纠正了一处「**为了划掉而划掉**」的假修复（见第三节）——把"其实不是 bug"的东西诚实地归位，
> 而不是凑一条"已解决"来让清单好看。台账口径也已改成 A 边界 / B 待办 / C 真修 / D 定案。

### 一、真修：扫描件 OCR 不再静默降级

扫描件 PDF 的 OCR 依赖系统 OCR 语言包；以前缺包时可能"看起来跑了、其实没字"。本版：

- 缺语言包时**明确报错 + 指引**（设置 → 时间和语言 → 语言 → 语言选项 → 添加中文「光学字符识别」组件）。
- 新增 `tools/ocr-check.ps1` 排查工具；`install.ps1` 安装时**自动预检**并提示（有语言包则通过）。

### 二、定案（决策，非修复）：trim / extend / overkill 命令版弃用

这三对方法与自研的 `trimEntityGeo` / `extendEntityGeo` / `overkillGeo` **功能重叠**，此前"去留待定"。本版**定案**：

- 命令版**保留可用，但标记【已弃用】(deprecated)**；**新用法一律用 `*Geo` 版**。下个大版本评估移除。

> 这是**决策**，没有改行为——所以记在台账 **D 已定案**，不算"修复"。

### 三、诚实降级：高色阶描摹 —— 撤销一次"假修复"

**背景**：曾把档数上限从 32 调到 16，声称"修了 ≥24 档不稳"。**这是错的**：

- **根因**：`toneChains` 的小连通域过滤是**绝对阈值**（按图像百分比）。档数越高，每档区域越小，越容易被 min-area 吸收/丢弃 → **块数下降**。这是**管线的固有权衡**，与"上限设多少"无关。
- **所以改上限什么都没修**。本版**撤销**该改动，恢复 32（原意：仅为挡 AI 乱传），并把该项**从"待办/已解决"归为 A 设计边界**。
- 结果里 `requestedLevels` / `effectiveLevels` / `warnings` 会**如实回报**（这部分 2026-10-10 已有）。
- **真修**是让 min-area 随档数自适应（高色阶才真正可用），但**有质量风险**（可能引入碎块），必须**真机对照图验证** → 列入台账 **B 待办**。

### 已知问题（v1.8.4）

> 本版待办共 **3** 项：

1. 描摹高色阶**真修**（小区域阈值随档数自适应）—— 待真机视觉验证。
2. 模态对话框下超时看门狗**不能自动解锁**（需手动关闭 / 重启 CAD）。
3. 重操作（CAD 长时间不空闲）超时的极端情形**待真机验证**。

> 设计边界（**非缺陷**）：不支持 Civil 3D 2024；`render`/约 12 个方法无公开 API；扫描件 OCR 依赖系统语言包；**高色阶块数下降属管线权衡**。完整台账见 `KNOWN-ISSUES.md`。

### 本版变更

- **C 真修**：扫描件 OCR 缺语言包静默降级 → 明确报错 + 预检工具
- **D 定案**：`trim/extend/overkill` 命令版弃用（决策）
- **A 归位**：高色阶描摹 → 设计边界（撤销假修复）

### 下载哪个

- `new-acad-setup-v1.8.4.exe` — 一键安装器（推荐）
- `new-acad-v1.8.4.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.8.4 — honesty pass: 1 real fix + 1 decision + 1 honest re-classification (2026-10-15)

> This release also corrects a **fake fix** (section 3) — a "resolved" that changed nothing, added just to shrink a list.

### 1. Real fix: scanned-PDF OCR no longer silently downgrades

Missing OCR language pack now yields a **clear error + guidance**; added `tools/ocr-check.ps1` and an automatic precheck in `install.ps1`.

### 2. Decision (not a fix): trim / extend / overkill command versions deprecated

They duplicate the custom `*Geo` versions. Decision: keep usable but mark **[DEPRECATED]**; new usage should use `*Geo`. Recorded under **D (decisions)**, not as a fix.

### 3. Honest re-classification: high tone levels — a fake fix reverted

We had raised/lowered the level cap (32->16) claiming it "fixed >=24 instability". **It did not.** Root cause: the small-region filter uses an **absolute threshold** (image percentage); more levels -> smaller regions -> more get absorbed/dropped -> fewer blocks. That's an inherent **pipeline trade-off**, unrelated to the cap. This release **reverts** the change and moves the item to **A (design limits)**. A real fix (adaptive min-area) is in the backlog (**B**) and needs real-machine visual verification.

### Known issues (v1.8.4)

1. Real fix for high tone levels (adaptive small-region threshold) — needs visual verification.
2. The timeout watchdog cannot auto-unlock a modal dialog.
3. Timeout behaviour of heavy operations (CAD not idle) needs real-machine verification.

### Changes

- **C real fix**: OCR silent downgrade -> explicit error + precheck tool
- **D decision**: trim/extend/overkill command versions deprecated
- **A re-classified**: high tone levels -> design limit (fake fix reverted)

### Downloads

`new-acad-setup-v1.8.4.exe` / `new-acad-v1.8.4.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). Civil 3D 2024 not supported.
