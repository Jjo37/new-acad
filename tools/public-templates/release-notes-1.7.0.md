## new-acad v1.7.0（2026-09-28）

> 本版三块新能力：**决策链可验证**、**大图纸上下文**、**土方专项**。另有批次 0 的一批修复与补漏。
> 覆盖安装即可，无需改配置，API Key 与设置保留。

### 新增能力

#### 1. 决策链可验证（做了什么都可查、可回放、可断言）
- **结构化操作日志**：每个请求一行 JSONL（方法 / 结果 / 耗时 / 图纸 / 结果指纹 / 参数），`readTrace`、`traceStats` 查询；**排障直接给真实异常**（此前对外只有一句 "unexpected error"）
- **回放器** `tools/replay.js`：逐条重放并对比 —— 只读方法结果漂移 = 失败；变更类方法 = 正常差异；错误条目比错误码
- **断言** `assertChecks`：图元存在 / 计数（支持 eq·ge·le·gt·lt）/ 图层存在 / 字段值（长度·面积·半径·顶点数，带容差）/ 曲面已构建；批量落地自动附"每层数量 ≥ 本次创建数"自检
#### 2. 大图纸上下文（不把上下文塞爆）
- `summarizeDrawing`：图层维度 + 空间网格 + 全局计数的**预算化画像**，输出字节数被硬约束（实测 1.5 万实体摘要约 3 KB）
- 大结果**自动降级**：超过阈值改成"保持原结构的裁剪"（数组截断成前缀 + `_summarized` 说明），需要全量时加 `fullResult:true`
#### 3. 土方专项（Civil 3D）
- **廊道土方** `computeCorridorEarthwork`：网格 + 桩号归属，逐桩号给出挖填方；实测与 C3D 曲面体积法偏差 **0.14%**（填 0.08%）
- **放坡优化** `optimizeGrading`：给坡率上限、平台标高、挡墙/台阶（`walls`）与可选土方平衡，自动生成满足约束的设计面并落成 TIN 曲面；实测与 C3D 体积法偏差 **0.9%**；约束不可满足时会**如实报错并给出建议**
- **场地调配** `computeSiteMassHaul`：两张曲面 → 逐桩挖填 → 调配方案（平衡区间、就近配对、免费/超运距、弃方/借方）

### 修复与补漏（批次 0）
- 新增 `createEllipse`（走真 API）
- **廊道再生护栏**：廊道写操作等待内部再生完成，不再"假失败"（实测：探测间隔 5 秒 → 再生被饿死、300 秒未完；30 秒 → 约 60 秒建好）
- **曲面算量健壮化**：支持网格法兜底；发现并采用 C3D 内置"廊道曲面烘焙"，算量链路自动烘焙后走精确体积曲面；`netVolume` 口径统一为 `填−挖`
- 几何级编辑：`trimEntityGeo`（真交点打断）/ `extendEntityGeo`（真延伸）/ `overkillGeo`（几何指纹去重）

### 已知限制（诚实版）
- 放坡优化定位为**初稿设计面生成器**：排水最小坡度用平台预倾斜表达，挡墙/台阶用 `walls` 声明；多级挡墙、管线避让等复杂约束仍需人工复核
- 廊道再生类操作本身较慢（30 秒 ~ 10 分钟），期间其它请求短暂排队（会自动恢复）

### 安装包（推荐）
- `new-acad-setup-v1.7.0.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.7.0.zip` —— 解压后双击 `install.bat`

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 `new-acad.bundle`，复制到 `%APPDATA%\Autodesk\ApplicationPlugins\` 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp

---

### New capabilities

#### 1. Verifiable action log (what the AI did, replayable and assertable)
- **Structured operation log**: one JSONL line per request (method / result / duration / drawing / result digest / parameters), queried via `readTrace` and `traceStats`. Troubleshooting now yields the **real exception** instead of a bare "unexpected error"
- **Replayer** `tools/replay.js`: replays each call and diffs it — read-only drift = failure, mutating calls = expected difference, errors compared by error code
- **Assertions** `assertChecks`: entity exists / entity count (eq, ge, le, gt, lt) / layer exists / field values (length, area, radius, vertex count, with tolerance) / surface built. Batch drawing operations now carry a built-in "entities on layer ≥ created" self-check
#### 2. Large-drawing context (without blowing up the context window)
- `summarizeDrawing`: a **budget-capped** summary across layers, a spatial grid and global counts — output size is a hard constraint (a 15k-entity drawing summarises to ~3 KB)
- **Automatic downgrade of oversized results**: instead of truncating, the payload keeps its shape (arrays trimmed to a prefix plus a `_summarized` note); use `fullResult:true` for everything
#### 3. Earthwork toolkit (Civil 3D)
- **Corridor earthwork** `computeCorridorEarthwork`: grid + station banding with per-station cut/fill; measured **0.14%** off Civil 3D's surface-volume method (0.08% on fill)
- **Grading optimisation** `optimizeGrading`: supply a max slope, platform levels, retaining walls/steps (`walls`) and optional cut/fill balance; it generates a design surface that satisfies the constraints and builds it as a TIN. Measured **0.9%** off Civil 3D's volume method. When constraints are unsatisfiable it says so **honestly, with a suggestion**
- **Site mass haul** `computeSiteMassHaul`: two surfaces → per-station cut/fill → a haul plan (balanced sections, nearest pairing, free vs overhaul haul, waste/borrow)

### Fixes and gap-filling (batch 0)
- Added `createEllipse` (real API path)
- **Corridor regeneration guard**: corridor write operations now wait for the internal regeneration instead of "failing" spuriously (measured: probing every 5 s starves regeneration, still unfinished after 300 s; 30 s probing finishes in ~60 s)
- **Robust surface volumes**: grid-method fallback; discovered and adopted Civil 3D's built-in "bake corridor surface", so the volume path bakes automatically and uses exact volume surfaces; `netVolume` convention unified as `fill − cut`
- Geometry-level editing: `trimEntityGeo` (true intersection split), `extendEntityGeo` (true extension), `overkillGeo` (geometry-fingerprint de-duplication)

### Known limitations (honest version)
- Grading optimisation is a **preliminary design-surface generator**: minimum drainage slope is expressed as a platform tilt, retaining walls/steps via `walls`; multi-tier walls and utility conflicts still need human review
- Corridor regeneration is inherently slow (30 s – 10 min) and briefly queues other requests (recovers automatically)

### Installer (recommended)
- `new-acad-setup-v1.7.0.exe` — run it and follow the wizard (Chinese or English); paste your own LLM API key

### Portable
- `new-acad-v1.7.0.zip` — unzip and run `install.bat`

### Autoloader (store) package
- `new-acad.bundle.zip` — unzip to get `new-acad.bundle`, copy it into `%APPDATA%\Autodesk\ApplicationPlugins\`

**Requirements**: Windows + genuine AutoCAD / Civil 3D 2025 / 2026 + an LLM API key
**License**: MIT | Upstream credit: Civil3D-mcp
