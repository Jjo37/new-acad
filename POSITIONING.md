# POSITIONING — new-acad 是什么、不是什么

> 本文回答一个问题：**为什么这不是"又一个给 AI 接 CAD 的项目"？**
> 面向：技术评审 / 集成方 / 想抄的人。

---

## 一句话

**new-acad 是一个内置 AI 工程助手**：把一个懂 Civil 3D 工程实践的 Agent 装进 AutoCAD / Civil 3D，用户只需要一个自己的模型 Key。

## 不是什么（先说清楚）

| 常见误解 | 事实 |
|---|---|
| "这是个 MCP 项目" | **不是。** MCP 只是对外兼容出口。我们自己的面板与 Agent **一次都不走 MCP** |
| "就是把 398 个 CAD 方法包成工具给 AI" | **不是。** 对模型只暴露 **2 个工具**（`cadCall` / `mcpCall`），398 个方法一个都不直接给模型 |
| "必须接你们的云 / 装某个客户端" | **不是。** 默认 `llm` 模式自带 LLM 代理，OpenAI 兼容，**零外部平台依赖** |
| "AI 会瞎调方法" | **设计上禁止。** 规则强制先 `listMethods` 查实时清单，且失败 ≥3 次必须停手报 `[MISSING]` |

---

## 三层结构（按重要性，不是按组件）

### 1. 大脑：内置独立 Agent —— 这是产品本身

- `server/llm-agent.js`：自带 OpenAI 兼容 LLM 代理，**不依赖任何外部平台**
- `server/compact.js`：上下文压缩 —— 长任务不会把 token 撑爆（实测 26KB → 9KB）
- `server/memory/agent-memory.md`：长期记忆，按关键词注入相关分节，不全量灌
- 用户动作只有一步：**填自己的 API Key**（面板内即可，保存即生效）

> 竞品要么必须接它的云，要么必须装 Claude Desktop / Cursor。我们把"大脑"做进产品里。

### 2. 神经：TCP 常驻通道 —— 这是工程底盘

面板 / Agent → relay → 插件 **TCP JSON-RPC :8080**（常驻、双向）。为**真实 CAD 任务**设计，不是为"工具调用"设计：

| 通道 | 场景 | 为什么不能用"一问一答"的方式 |
|---|---|---|
| **job 通道** | 廊道重建、批量属性读取、曲面体积、DEM 导入、质检报告 | 这些要跑几十秒到几分钟。返回 `{jobId, state:"running"}` 排队执行，**不占用 CAD 命令上下文、不撞 25s 超时** |
| **文件通道** | >500 条高程点等大批量数据 | 走 CSV 一次导入（`addSurfacePointsFromFile`），**不走对话**（否则烧轮次） |
| **吞吐约束** | 多个同类对象 | **批量方法 / 数组参数优先**，禁止逐个调用 |

> MCP 的"一问一答工具"模型在长事务、超时、状态保持上先天吃亏 —— 所以它在我们这里只是插口。

### 3. 插口：MCP —— 这是兼容出口

- **274** 个 MCP 工具（`:3000`），Claude Desktop / Cursor 等任何 MCP 客户端都能接
- **我们自己的面板和 Agent 不走它**
- 定位：让别人能接进来，不是我们的引擎

---

## 决策链 —— 抄不走的那部分

工具面只有一个入口：`cadCall(method, params)`。**方法怎么选、任务走哪条通道，由一层工程判断决定**，而不是把 398 个方法丢给模型让它猜。

| 判断 | 规则 | 代码位置 |
|---|---|---|
| **先查再调** | 不确定方法名 → 先 `listMethods`（支持 `filter` 秒回，带缓存）；**禁止猜方法名** | `llm-agent.js` SOP / `cad-tools.js` `listMethods` 特殊处理 |
| **复杂度识别** | 命中 工程量/曲面/廊道/放样/管网/报告/批量 等关键词，或选中 ≥20 个对象 → **先输出 1-2 行计划**（处理方式 + 预计轮数） | `llm-agent.js` `COMPLEX_KEYWORDS` / `panel-relay.js` `isBulk` + `planNote` |
| **选通道** | 单个 → 直接调；多个同类 → **批量方法/数组参数**；>500 数据 → **文件通道**；重操作 → **job 通道** | `llm-agent.js` 批量优先硬约束 |
| **拥塞处理** | `HOST_BUSY` / `JOB_RUNNING` → **禁止连续重试同一方法**（会耗尽轮次）；改走不吃 CAD 上下文的路径（`getJobStatus` / `listMethods` / 读缓存）或询问用户 | `llm-agent.js` SOP |
| **诚实边界** | 同一方法失败 ≥3 次 → 停手，报 `[MISSING]`；拿不准 → 问用户 | `llm-agent.js` 护栏 |

**别人的做法**：给模型 274 个工具，让它自己猜该用哪个。
**我们的做法**：给模型一套工程判断，只给它一个入口。

---

## 对比

| | 常见做法 | new-acad |
|---|---|---|
| 谁在"想" | 外部平台 / 客户端 | **产品内置** |
| 执行通道 | MCP 工具一问一答 | **TCP 常驻 + job 通道 + 文件通道** |
| 工具怎么用 | 全部暴露给模型 | **决策链选通道，只暴露 `cadCall`** |
| 长任务 | 超时 / 失败 | **job 排队，不占命令上下文** |
| 用户要什么 | 平台账号 / 客户端 | **一个自己的 Key** |
| 界面语言 | 单语 | **中 / 英**（安装向导选什么就是什么，面板可随时切） |

---

## 数字口径（实时为准）

| 项 | 数字 | 来源 |
|---|---|---|
| 插件 TCP 方法 | **398** | `listMethods` 实时 count |
| MCP 工具 | **274** | `GET :3000/health` |
| 对模型暴露的工具 | **2** | `cad-tools.js`（`cadCall` / `mcpCall`） |
| 支持的 C3D | 2024 / 2025 / 2026 | 注册表自动检测 |

---
---

# POSITIONING — what new-acad is, and what it is not

> This document answers one question: **why is this not "yet another project that wires an AI to CAD"?**
> Audience: technical reviewers, integrators, people who want to copy it.

## In one line

**new-acad is an in-app AI engineering assistant**: an agent that understands Civil 3D engineering practice, living inside AutoCAD / Civil 3D. The user only needs to bring a model key.

## What it is NOT

| Common assumption | Reality |
|---|---|
| "It's an MCP project" | **No.** MCP is only a compatibility exit. Our own palette and agent **never go through MCP** |
| "It just wraps 398 CAD methods as tools for the AI" | **No.** The model sees exactly **2 tools** (`cadCall` / `mcpCall`). None of the 398 methods is handed to it directly |
| "You must connect to their cloud / install some client" | **No.** The default `llm` mode ships its own LLM proxy (OpenAI-compatible) — **zero external platform dependency** |
| "The AI will call methods at random" | **Forbidden by design.** Rules force a live `listMethods` lookup first, and after 3 failures of the same method the agent must stop and report `[MISSING]` |

## The three layers (by importance, not by component)

### 1. Brain — the built-in standalone agent (this is the product)

- `server/llm-agent.js`: its own OpenAI-compatible LLM proxy — **no external platform**
- `server/compact.js`: context compaction, so long tasks don't blow up the token budget (measured 26KB → 9KB)
- `server/memory/agent-memory.md`: long-term memory, keyword-injected by section instead of dumped wholesale
- The user does exactly one thing: **paste their own API key** (in the palette, effective on save)

### 2. Nerves — the persistent TCP channel (this is the engineering chassis)

Palette / agent → relay → plugin **TCP JSON-RPC :8080** (persistent, bidirectional). Built for **real CAD work**, not for "tool calls":

| Channel | For | Why request/response cannot do it |
|---|---|---|
| **job channel** | Corridor rebuilds, bulk property reads, surface volumes, DEM import, QC reports | These run from tens of seconds to minutes. They return `{jobId, state:"running"}` and run queued — **without holding the CAD command context and without hitting the 25 s timeout** |
| **file channel** | Large batches (>500 records) | CSV import in one shot (`addSurfacePointsFromFile`) — **not through the chat**, which would burn turns |
| **throughput rule** | Many objects of the same kind | **Batch methods / array parameters first**; calling one by one is forbidden |

### 3. Socket — MCP (the compatibility exit)

- **274** MCP tools (`:3000`), usable from any MCP client (Claude Desktop, Cursor, …)
- **Our own palette and agent don't use them**
- It exists so others can plug in — it is not our engine

## The decision chain — the part you cannot copy

There is exactly one tool entry point: `cadCall(method, params)`. **Which method to use and which channel a task takes is decided by a layer of engineering judgement** — not by handing 398 methods to a model and hoping.

| Judgement | Rule | Where |
|---|---|---|
| **Look up before calling** | Unsure of a method name → call `listMethods` first (with `filter`, cached); **guessing method names is forbidden** | `llm-agent.js` SOP / `cad-tools.js` |
| **Complexity check** | Complex keywords (quantities / surfaces / corridors / grading / pipe networks / reports / bulk) or ≥20 selected objects → **print a 1-2 line plan first** (approach + expected turns) | `llm-agent.js` `COMPLEX_KEYWORDS`, `panel-relay.js` `isBulk` + `planNote` |
| **Channel choice** | Single → direct; many of a kind → **batch/array**; >500 records → **file channel**; heavy ops → **job channel** | `llm-agent.js` bulk-first hard rule |
| **Congestion** | `HOST_BUSY` / `JOB_RUNNING` → **no repeated retries of the same method** (burns turns); switch to a path that doesn't need the CAD context, or ask the user | `llm-agent.js` SOP |
| **Honest boundary** | Same method failing ≥3 times → stop and report `[MISSING]`; when unsure → ask | `llm-agent.js` guardrails |

**Others**: hand the model 274 tools and let it guess.
**Us**: hand the model a set of engineering judgements, and one entry point.

## Numbers (live source of truth)

| Item | Value | Source |
|---|---|---|
| Plugin TCP methods | **398** | live `listMethods` count |
| MCP tools | **274** | `GET :3000/health` |
| Tools exposed to the model | **2** | `cad-tools.js` (`cadCall` / `mcpCall`) |
| Supported Civil 3D | 2024 / 2025 / 2026 | registry auto-detect |
