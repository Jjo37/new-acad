## new-acad v1.8.3 - 安全修复 + 全单位支持 + 一致性/去重(2026-10-15)

> **注**：本版为 v1.8.2 的**重发版** —— v1.8.2 的 release 对象被误删、其 tag 名已被 GitHub 标记为**不可复用**，无法恢复；内容与 v1.8.2 一致，并补齐使用手册「清除配置」一节。

> 本版是一次“体检 + 治理”版本：修掉一个审批门禁绕过漏洞，让所有计算按图纸单位自适应（公制/英制都能用），
> 并清理了一批重复/漂移/死代码。每条对应已提交改动与可复现实测；未解决的列在「已知问题」。
>
> **另：本版把两项核心能力（AI 记忆、知识库）写清楚**——见下「重点」。

### ★ 重点：两项核心能力（AI 记忆 & 知识库）

> 这两项在 v1.8.0 随线，但之前的说明一带而过。它们才是 new-acad 与「给 AI 套一层 CAD 工具」的分水岭——让 AI **越用越懂你、越用越懂你的项目规范**。

**AI 记忆（长期记忆）** —— 记住你的习惯，不用每次重复交代

- 说一句「记住：我习惯用米制」「以后出图默认 1:500」，AI 就记进 `server\memory\agent-memory.md`，**重启不丢**。
- 只记**规则 / 偏好 / 方法**，不写具体任务与图纸数据（防污染）；「清理会话」只清当前对话，**长期记忆不动**。
- 面板「**更多 → 查看 AI 记忆**」可直接打开查看/编辑。

**知识库（把你的规范 / 资料喂给 AI）** —— 让 AI 懂你的项目规矩

- 把**设计规范、图纸说明、计算规则、项目做法**放进 `user-knowledge\`（面板「**更多 → 打开知识库文件夹**」直达）。
- 回面板说一句 **「整理知识库」**：AI 抽取文本、建索引 `_index.md`，之后干活时**按需引用**——不用每次把规则重贴一遍。
- 支持 md / txt / csv / json / log / Word / Excel / PPT / PDF；**扫描版 PDF 自动走 Windows OCR**。
- 全局生效（不分项目），与 CAD 图纸数据分离。

---

### ★ 模型服务商列表升级

- 面板「服务商 / 模型」下拉**全面刷新**：内置 **9 家**服务商——**DeepSeek / OpenAI / 通义千问 / Kimi / 智谱 GLM / MiniMax / OpenRouter / xAI / 自定义**。
- 模型列表更新到各家最新主力（如 DeepSeek `deepseek-flash` / `deepseek-v4-pro`、OpenAI `gpt-6.1-sol`、通义 `qwen3.8-max`、Kimi `kimi-k3`、智谱 `glm-5.3`、MiniMax `M3`、xAI `grok-4.7`…）。
- **模型下拉动态拉取**：从服务商 `/models` 实时获取可用模型，以面板实际显示为准（过期型号不再硬编）。
- 换服务商后重选模型即可；Key 只存本机 `config.json`，面板/使用手册已同步更新。
- 配置区新增「**清除配置**」按钮：一键清除已保存的服务商 / 模型 / Key（换服务商或换 Key 时用，带确认）。

---

### 一、安全：修复审批门禁绕过

- 面板 AI 修改图纸的工具(移动/旋转/缩放/复制/布尔运算...)**此前可绕过审批**直接执行(能力枚举写成了非法值,被类型开关掩盖)。现已修复:这批工具**恢复为需要审批令牌**,只读工具不受影响。

### 二、计算:全单位支持(任意单位都能用)

以前水力/调蓄/边坡/视距等工程计算在**公式常数与默认值上假定英制**,公制图纸会**静默给出错误结果**。本版:

- **公式常数按单位取**:Manning 满管流量(SI 1.0 / 英尺 1.486,原固定 1.0 → 英制偏小 1.486×)、Froude/孔口出流重力(9.81 / 32.2)。
- **默认值按图纸单位换算**(minCoverDepth、minVelocity、maxVelocity、底宽、超高、采样/站间距...):英制结果 ≈ 原值,公制得正确值。
- **输出单位随图纸标注**:体积(m3/ft3/acre-ft)、孔口直径(mm/in)、流速/流量标签。
- **默认自动探测图纸单位**(`Insunits`),并可**显式覆盖**:调用时传 `units = auto|metric|imperial`。
- 比例/时间类(坡比、历时、反应时间)本就无量纲,未改。

### 三、修复(一致性 / 可靠性)

- **看英文**:英文界面下写文件的目标路径曾显示为开发机绝对路径 → 现按真实工作区显示。
- **读图**:图片读取在特定参数下会抛错 → 已修。
- **消息挂起**:面板某回执接口带回复时会**永久挂起** → 已修。
- **选中集**:`listSelectedTexts` 恒返回 0(快照格式变更未同步)→ 已修。
- **选择集快照路径 / 面板 relay 地址 / MCP 端口**:改为按配置解析(改端口不再失联/错位)。
- **意图解析**:修正标签匹配无词界导致的误配(如 "stop 5" 被误当 topElevation)。
- **必填字段**改为从工具 schema 派生(修掉一处漂移)。

### 四、内部:去重 / 清理

- 点坐标模式、路由参数映射、必填字段表**收敛到单一来源**;删除一批死代码与 10 个一次性脚本;修正若干注释乱码。
- 新增回归测试:审批覆盖、单位、意图词界、i18n 描述平价、打包清单互校。

### 已知问题(v1.8.3 仍未解决)

> 本版为 v1.8.2 **重发版**，已知问题**未变动**（原因：仅修正手册 + 重发）。完整台账（设计边界 / 待办 / 已解决）见 `KNOWN-ISSUES.md`。

1. 高色阶描摹不稳(≥24 档;建议 2-12 档)。
2. 扫描件 OCR 依赖系统 OCR 语言包(中文 Windows 自带);单次上限 60 页。
3. 模态对话框下超时看门狗**不能自动解锁**(需手动关闭 / 重启 CAD)。
4. `render` 仍是命令投递(25.0.58 无渲染 API)。
5. `trim / extend / overkill` 与 `*Geo` 版重叠,去留待定。
6. 约 12 个 command 方法无公开 API(无可替代)。
7. **没有自动化测试网 / CI**(目前是 QC 脚本 + 发布门禁)。
8. **不支持 Civil 3D 2024**(.NET Framework 4.8)。
9. 部分工程计算的重操作在极端情形(CAD 长时间不空闲)下的超时行为**待真机进一步验证**。

### 下载哪个

- `new-acad-setup-v1.8.3.exe` - 一键安装器(推荐)
- `new-acad-v1.8.3.zip` - 绿色版(内含安装器)
- `new-acad.bundle.zip` - Autodesk Autoloader 包

> 已装旧版:重跑安装器即可。

### 支持范围

- 支持 **Civil 3D 2025 / 2026**(.NET 8)
- **不支持 Civil 3D 2024**

---

## new-acad v1.8.3 - security fix + full unit support + consistency/de-dup (2026-10-15)

> **Note:** this is the **re-issue of v1.8.2** (its release object was deleted and its tag name is now flagged non-reusable by GitHub). Content matches v1.8.2, plus the user guide now documents the "Clear" config button.

> A "health check + governance" release: fixes an approval-gate bypass, makes all calculations unit-aware
> (metric and imperial both work), and cleans up duplication/drift/dead code.
>
> **Also: this release spells out two core capabilities (AI memory, knowledge base)** — see Highlights below.

### ★ Highlights: two core capabilities (AI memory & knowledge base)

> Both shipped with v1.8.0 but were only mentioned in passing. They are what sets new-acad apart from "an AI wrapped around some CAD tools" — the AI **gets to know you and your project conventions the more you use it**.

**AI memory (long-term)** — remembers your habits so you don't have to repeat yourself

- Say "remember: I work in metric" or "default plot scale 1:500" and the AI stores it in `server\memory\agent-memory.md` — **survives restarts**.
- Stores **rules / preferences / methods only**, never task or drawing data (anti-pollution); "Clear chat" only clears the current conversation — **long-term memory is untouched**.
- Open it from the palette: "**More → View AI memory**".

**Knowledge base (feed the AI your specs)** — the AI learns your project conventions

- Put your **design standards, drawing notes, calculation rules and project conventions** into `user-knowledge\` (palette: "**More → Open knowledge folder**").
- Say **"organize the knowledge base"** once: the AI extracts text, builds the index `_index.md`, then **cites it on demand** — no more pasting the same rules again.
- Supports md / txt / csv / json / log / Word / Excel / PPT / PDF; **scanned PDFs go through Windows OCR** automatically.
- Global (not per project), separate from drawing data.

---

### ★ Model-provider list upgraded

- The palette's **provider / model dropdowns are fully refreshed**: **9 providers** built in — **DeepSeek / OpenAI / Qwen / Kimi / Zhipu GLM / MiniMax / OpenRouter / xAI / Custom**.
- Model lists updated to each provider's latest flagship models (e.g. DeepSeek `deepseek-flash` / `deepseek-v4-pro`, OpenAI `gpt-6.1-sol`, Qwen `qwen3.8-max`, Kimi `kimi-k3`, Zhipu `glm-5.3`, MiniMax `M3`, xAI `grok-4.7`…).
- **Models are pulled live**: the dropdown fetches the provider's `/models`, so the palette is authoritative (no more hard-coded stale names).
- Just re-pick a model after switching providers; the key is stored only in your local `config.json`. The palette and the user guide are updated to match.
- A new **Clear** button in the config area: one click wipes the saved provider / model / key (with a confirmation) — useful when switching providers or keys.

---

### 1. Security: approval-gate bypass fixed

- Palette-AI tools that modify the drawing (move/rotate/scale/copy/boolean...) could previously **bypass approval**. Fixed: these tools now **require an approval token**; read-only tools are unaffected.

### 2. Calculations: full unit support (any units)

Previously hydraulic/detention/slope/sight-distance math assumed **imperial** in its formula constants and defaults, so **metric drawings silently produced wrong results**. Now:

- **Unit-aware formula constants**: Manning full-flow (SI 1.0 / feet 1.486 - was fixed at 1.0, i.e. imperial was off by 1.486x), gravity for Froude/orifice (9.81 / 32.2).
- **Defaults converted by drawing units** (min cover depth, velocities, bottom width, freeboard, sampling/station intervals...): imperial ≈ unchanged, metric now correct.
- **Outputs labelled by unit**: volume (m3/ft3/acre-ft), orifice diameter (mm/in), velocity/flow labels.
- **Auto-detects drawing units** (`Insunits`), with an explicit override: pass `units = auto|metric|imperial`.
- Dimensionless ratios/times (slope ratios, storm duration, reaction time) unchanged.

### 3. Fixes (consistency / reliability)

- **English UI**: the write-file target path showed a developer absolute path → now shows the real workspace.
- **Image read** no longer errors under certain params.
- **Hung request** on an inbox reply endpoint → fixed.
- `listSelectedTexts` always returned 0 (snapshot format change not followed) → fixed.
- **Selection-snapshot path / palette relay URL / MCP port** now resolved from config (no more lost/offset connections after a port change).
- **Intent parsing**: fixed false label matches from missing word boundaries (e.g. "stop 5" mis-read as topElevation).
- **Required fields** now derived from tool schemas (removes a drift).

### 4. Internal: de-dup / cleanup

- Point schemas, route-param mapping, required-field tables **consolidated to a single source**; dead code and 10 one-off scripts removed; several mojibake comments fixed.
- New regression tests: approval coverage, units, intent word-boundary, i18n description parity, packaging-manifest cross-check.

### Known issues (still open in v1.8.3)

> This is the **re-issue of v1.8.2**; the known-issue list is **unchanged** (only a manual fix + re-publish). Full ledger (design limits / backlog / resolved) in `KNOWN-ISSUES.md`.

1. High tone levels (>= 24) unstable (2-12 recommended).
2. Scanned OCR depends on the system OCR language pack; cap 60 pages/run.
3. Watchdog cannot unlock a modal dialog (close it / restart CAD).
4. `render` is still a posted command (no render API in 25.0.58).
5. `trim / extend / overkill` duplicate their `*Geo` counterparts - removal undecided.
6. ~12 command methods have no public API (no replacement).
7. No automated test suite / CI yet.
8. Civil 3D 2024 is not supported (.NET Framework 4.8).
9. Timeout behaviour of some heavy operations (when CAD is not idle for a long time) still needs real-machine verification.

### Downloads

`new-acad-setup-v1.8.3.exe` / `new-acad-v1.8.3.zip` / `new-acad.bundle.zip`
**Civil 3D 2025 / 2026** (.NET 8). **Civil 3D 2024 is NOT supported.**
