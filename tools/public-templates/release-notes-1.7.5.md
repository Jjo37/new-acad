## new-acad v1.7.5 — 静默失败治理收口（2026-10-12）

> 本版是**诊断性补丁**：**没有新功能、没有可见的行为变更**，改动集中在"出问题时能不能查到原因"。
> 每条都对应已提交的改动与可复现的实测；未解决的仍然列在"已知问题"里。

### 一、静默异常治理：全仓空 catch 复核清零

- **空 catch 复核数 144 → 0**（`node tools/scan-empty-catch.js` 实测）。
  分批处理：**server 42 处**（relay / llm-agent / file-tools / cad-tools / launcher）+ **插件 82 处**（面板、命令、执行器等）。
  以前失败只表现为"没反应"，现在日志里能看到**卡在哪一步、为什么**。
- 处置口径（按 `knowledge/catch-audit-2026-10-10.md` 的规则）：
  - **该报错的路径**（写盘 / 事务 / 配置读取 / 参数赋值）→ 补**日志**；
  - **确实可忽略的**（探测、容错解析、清理回退）→ 补**理由注释**，明确"为什么可以吞"。
- 另有 **20 处"日志包裹型"**（单行 `try { <日志调用> } catch {}`）经复核确认为无害（catch 只保护日志调用本身），扫描器已分类、不计入复核数。
- 扫描器规则同时对齐审计文档：`catch { /* 原因 */ }` 视为**合规**，只有**无注释**的 `catch { }` 才计入待复核。

### 二、工程（不影响日常使用）

- 两个扫描器（`scan-empty-catch.js` / `gen-deficiencies.js`）不再把**注释 / XML 文档**里出现的 `catch {}` 示例当成真实代码（长期假阳性）；`gen-deficiencies.js` 同步识别"日志包裹型"。

### 已知问题（v1.7.5 仍未解决）

1. **高色阶描摹不稳**（≥24 档块数锐减；SOP 仍建议 2–12 档）。
2. **模态对话框下超时看门狗不能自动解锁**（ESC 对模态窗口无效，需手动关掉对话框/重启 CAD）。
3. `trim/extend/overkill` 的"投递版"与"同步 Geo 版"**功能重叠**，是否下线旧方法**待定**。
4. `render` 仍是命令投递（25.0.58 无渲染 API）。
5. **没有自动化测试网 / CI**：目前仍是 QC 脚本 + 发布门禁。
6. **不支持 Civil 3D 2024**（.NET Framework 4.8，插件无法加载）。
7. 本版**不含功能改动**；如你在等功能层面的修复，本版可跳过。

### 下载哪个

- `new-acad-setup-v1.7.5.exe` — 一键安装器（推荐）
- `new-acad-v1.7.5.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装旧版：重跑安装器即可（或替换插件 DLL + server 目录）。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**

---

## new-acad v1.7.5 — silent-failure cleanup completed (2026-10-12)

> This is a **diagnostics-only patch**: **no new features, no visible behavior change**. It is about
> being able to tell *why* something failed. Every claim maps to a committed change and a
> reproducible measurement; anything not fixed is listed under "Known issues".

### 1. Empty-catch review closed repo-wide

- **Empty-catch review count 144 -> 0** (measured with `node tools/scan-empty-catch.js`).
  Done in batches: **42 on the server side** (relay / llm-agent / file-tools / cad-tools / launcher)
  + **82 in the plugin** (palette, commands, executors). Failures used to look like "nothing happened".
- Policy (`knowledge/catch-audit-2026-10-10.md`):
  - **Must-report paths** (writes / transactions / config reads / parameter assignment) now **log**;
  - **Genuinely ignorable ones** (probes, tolerant parsing, cleanup fallback) got a **reason comment**.
- **20 "log-wrapper" sites** (a one-line `try { <log call> } catch {}`) were verified harmless
  (the catch only guards the log call) and are classified, not counted.
- The scanner now matches the audit rule: `catch { /* reason */ }` is **compliant**; only a
  bare, comment-less `catch { }` is flagged.

### 2. Engineering (no day-to-day impact)

- Both scanners no longer treat `catch {}` examples inside **comments / XML docs** as real code
  (a long-standing false positive); `gen-deficiencies.js` also recognises log-wrappers.

### Known issues (still open in v1.7.5)

1. High tone levels (>= 24) are unstable (the SOP still recommends 2–12).
2. The watchdog cannot unlock a modal dialog (ESC is ignored there; close it by hand / restart CAD).
3. `trim/extend/overkill` duplicate their synchronous `*Geo` counterparts — removal is undecided.
4. `render` is still a posted command (no render API in 25.0.58).
5. No automated test suite / CI yet (QC scripts + release gate only).
6. Civil 3D 2024 is not supported.
7. This release has **no feature changes** — safe to skip if you are waiting for functional fixes.

### Downloads / Supported versions

`new-acad-setup-v1.7.5.exe` / `new-acad-v1.7.5.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). **Civil 3D 2024 is NOT supported.**
