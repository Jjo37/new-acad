# new-acad

> AutoCAD / Civil 3D 里的**内置 AI 工程助手**：自带大脑、直连 CAD，用户只需要一个自己的模型 Key

 <img width="2559" height="1518" alt="image" src="https://github.com/user-attachments/assets/7d0d09a2-1e50-424d-ba16-2508bd3ee6cc" />

**中文** | [English](README_EN.md)

不是"给 AI 加一堆 CAD 工具"，而是**把一个懂 C3D 工程实践的 Agent 装进 CAD 里**。
内置 Agent 对模型**只暴露 2 个工具**（调插件、调 MCP）—— **398 个 CAD 方法一个都不直接丢给模型**，由一层工程判断决定"这个任务该走哪条通道"。

| 层 | 是什么 | 为什么重要 |
|---|---|---|
| **大脑** | 内置独立 Agent：自带 LLM 代理 + 上下文压缩 + 长期记忆 | **不依赖任何外部平台** —— 不接我们的云、不装 Claude Desktop、不用网关；用户只填自己的 API Key |
| **神经** | 面板 ↔ 插件 **TCP 常驻通道**（含 **job 通道** / **文件通道**） | 为真实 CAD 任务设计：跑几分钟的廊道重建排队执行、不撞超时；>500 条数据走文件通道不走对话 |
| **插口** | **274** 个 MCP 工具（`:3000`） | 兼容出口：Claude Desktop / Cursor 等任何 MCP 客户端都能接。**我们自己的面板和 Agent 不走它** |

> **关于界面语言**：我是中国开发者，做这个插件时**满脑子都是中文用户怎么用，压根没想起"语言"这回事**。直到有人在安装包里选了 English、装完发现界面还是全中文，我才意识到这是个问题。
> 现在补上了（v1.4.0）：**安装向导里选什么语言，装完就是什么语言**，装完之后面板里也能随时切。
> 目前界面语言**只有「中文 / English」两套**，没有第三语言；「自动（跟随系统）」的规则是：中文系统 → 中文，其它语言 → 英文。

---

## 这是什么

new-acad 由三块拼成，各司其职：

| 组件 | 位置 | 干什么 |
|------|------|--------|
| **C3D 插件**（C#） | `plugin/AcBridge-v24/` | 跑在 AutoCAD / Civil 3D 进程内，执行真实的绘图与 C3D 建模 API；对外是 TCP JSON-RPC 服务（默认 `:8080`） |
| **面板 + relay**（Node） | `plugin/AcBridge-v24/src/HankPalette.cs`、`server/` | 面板内嵌在 C3D 里（调色板）；relay 是中枢：把你这边的自然语言转给 AI，再把 AI 的调用转成插件调用，然后把结果推回面板 |
| **MCP 服务器**（Node + TypeScript） | `server/mcp/` | 270 个 MCP 工具（默认 `:3000`），任何支持 MCP 的客户端都能直接接，不限于本项目的面板 |

### 两种 AI 模式

| 模式 | 说明 |
|------|------|
| `llm`（**默认**） | relay 自带 LLM 代理（OpenAI 兼容协议）。填自己的 API Key 就能跑，**不依赖任何外部平台**。安装向导里可选 DeepSeek / OpenAI / 通义 / Kimi / 智谱 / MiniMax / OpenRouter / xAI / 自定义 |
| `openclaw` | 把面板接到自建的 agent 平台或网关上（进阶用法，需要自己配 token / session） |

---

## 核心能力

| 类别 | 能做什么 |
|------|----------|
| **图元操作** | 读选中图元、查属性、按图层 / 块 / 类型 / 高程范围筛选；改颜色图层、移动、旋转、缩放、复制、镜像、删除；批量接口一次处理 N 个对象（`handles` 数组） |
| **绘图** | 直线、圆、圆弧、多段线、矩形、文字、标注、填充 |
| **三维** | 基本体（盒 / 柱 / 球 / 锥 / 楔 / 环）；拉伸、旋转、扫掠、放样（走真 API 链路）；NURBS 曲面创建与编辑、曲面加厚；布尔并 / 差（交集用双重差集模拟）、干涉检查；体积与质量属性 |
| **Civil 3D 专业域** | **曲面**（创建、批量加点，支持任意文本格式走文件通道）；**路线**（样式、标签集、桩号查询）；**纵断面 / 图框**（样式与带集）；**装配与子装配**（含 AI 生成 SAC 部件：写 XAML → 打包 `.pkt` → 导入）；**廊道**（一条龙创建、区域编辑、廊道曲面）；**放坡 / 管网 / 地块 / 汇水 / 交叉口 / 数据快捷方式**；工程量与造价估算 |
| **图层 / 块 / 外部参照** | 创建、改名、属性读写、写块、附着 Xref |
| **文件通道** | 读 `docx` / `xlsx` / `pptx` / `zip`，以及老格式 `doc` / `xls`（自带 OLE2 / BIFF8 解析，**零依赖、不需要装 Office**）；列目录；在工作区内写文件；开图 / 新建 / 保存图纸（半自动多图流程） |
| **选择集** | 发指令时自动快照当时的选中集；带文档名，切图纸不会读到上一张图的选中集 |
| **任务与记忆** | 多步任务计划、真中断取消、选择性清理；AI 长期记忆按关键词注入 |

---

## 架构

```
   面板（C#，内嵌 C3D 调色板）
      │  HTTP  POST :19876/send
      ▼
   relay（server/panel-relay.js）
      │  ┌─ llm 模式：内置 LLM 代理（OpenAI 兼容） → tool 循环
      │  └─ openclaw 模式：自建 agent 平台
      │  HTTP  POST :19876/tcp   （JSON-RPC 2.0 桥）
      ▼
   C3D 插件（Civil3DMcpPlugin.dll，TCP :8080）  ←→  AutoCAD / Civil 3D 数据库

   MCP 客户端 ──POST :3000/execute──▶ MCP 服务器（server/mcp，270 工具）
                                            └──▶ 同样落到插件
```

**端口**（都在 `config.json` 或环境变量里可改）：插件 `8080`｜MCP `3000`｜relay `19876`

---

## 环境要求

- **Windows**
- **AutoCAD / Civil 3D 2024 / 2025 / 2026**（正版授权，自行准备）
- 一个 LLM 的 **API Key**（默认模式下唯一需要你自己准备的东西）
- 运行环境（Node 等）由安装脚本自动处理

> 本项目 **非 Autodesk 官方产品**，与 Autodesk 无隶属或背书关系。

---

## 快速开始

> 不想自己编译？到 [Releases](../../releases) 页面下载 `setup.exe` 安装包或绿色版 zip。

```
① 确保已安装 Civil 3D（2024 / 2025 / 2026）
② 双击 install.bat（推荐；自动绕过 PowerShell 执行策略）
   或：右键 install.ps1 → 使用 PowerShell 运行
③ 安装向导第 11 步会让你选 LLM 服务商 + 填 API Key
④ 打开 Civil 3D —— Hank.lsp 会自动 NETLOAD 插件
⑤ 在面板里直接说话：「把选中的多段线整体抬高 0.5 米」
```

**卸载**

- 用安装包装的：Windows「应用和功能」里卸载 `new-acad`（或开始菜单「卸载 new-acad」，走 Inno 卸载器）
- 绿色版（解压 zip）：右键 `uninstall.ps1` → 使用 PowerShell 运行（清理自启任务、`Hank.lsp` 自动加载、残留进程）

---

## 给 AI Agent 用的交接文档

本仓库天然是「让 AI 替你操作 CAD」的项目，所以附带了一份给 AI 读的交接手册：

- [`ONBOARDING.md`](ONBOARDING.md) —— 环境自检（插件 / relay / MCP 三个端口）、工具调用姿势、常见故障处理、方法调用优先级。把你的 AI agent 指向这个文件，它就知道怎么开始。
- [`使用手册.html`](使用手册.html) —— 面向人的界面与操作说明
- [`knowledge/`](knowledge/) —— 技术资料：API 清单、Civil 3D 对象模型、C3D API 参考、SAC XAML 语法指南与模板

---

## 编译与打包

```powershell
# 编译插件（产物：plugin/AcBridge-v24/Civil3DMcpPlugin.dll）
# 注意：必须用这个脚本。裸 dotnet build -o 会产出 4KB 空壳 DLL（无任何类型，NETLOAD 静默不加载）
powershell -ExecutionPolicy Bypass -File build-plugin.ps1

# 编译需要 Autodesk 托管程序集（AcDbMgd / AecBaseMgd / AeccDbMgd 等）
# 放在 C_References/ 下（已 gitignore，不随仓库分发）

# 打包分发目录 / 安装器
powershell -ExecutionPolicy Bypass -File build-dist.ps1        # → dist/
powershell -ExecutionPolicy Bypass -File build-installer.ps1   # → 安装包（需 Inno Setup）
```

**C3D 开着时 DLL 被锁**，编译前请先关掉 Civil 3D。

---

## 项目结构

```
new-acad/
├── plugin/
│   ├── AcBridge-v24/
│   │   ├── src/            C# 源码（55 个文件，插件+面板）
│   │   └── Civil3DMcpPlugin.dll  ← 编译产物（未随仓库分发，需自行编译）
│   └── Hank.lsp            C3D 自动加载脚本（NETLOAD 插件）
├── server/
│   ├── panel-relay.js      面板中枢（:19876）+ /tcp JSON-RPC 桥
│   ├── relay-launcher.js   relay 守护启动器（崩溃自动重启 + 日志落盘）
│   ├── llm-agent.js        内置 LLM 代理（OpenAI 兼容 + tool 循环 + 上下文压缩）
│   ├── cad-tools.js        AI 可用的唯一工具入口
│   ├── file-tools.js       文件通道（读文档 / 工作区写 / 安全边界）
│   ├── sacred-mcp.js       MCP 服务器启动器（:3000）
│   └── mcp/                MCP 服务器本体（TS 源码 + 编译产物 + 270 工具）
├── tools/                  环境检测、端口检查、自测与扫描脚本
├── knowledge/              技术资料（API 清单 / 对象模型 / SAC 指南）
├── install.bat / install.ps1 / uninstall.ps1 / uninstall-pre.ps1
├── config.json             本地配置（端口 / token / LLM 设置）—— 已 gitignore，不入库
└── ONBOARDING.md           给 AI agent 的上手指南
```

---

## 已知限制

- 12 个方法底层走命令通道（无公开托管 API），带 25 秒超时兜底
- C3D 开着时插件 DLL 被锁，更新需关闭 C3D 后重新编译、重启
- 面板接收回复走 **SSE 实时推送**（`/replies/stream`），不是轮询；状态与执行进度同样实时推送（10 秒 `/status` 轮询仅作断线兜底）
- 少数 C3D 能力（如放坡组编辑、地块线编辑）托管 API 未封装，AI 暂不可调，见 `ONBOARDING.md`

---

## 许可与致谢

- 本项目采用 **MIT License** —— 见 [`LICENSE`](LICENSE)
- **上游致谢**：[Civil3D-mcp](https://github.com/Sacred-G/Civil3D-mcp)（MIT License）。
  本仓库在 `server/mcp/` 引用其开源实现，其许可证原文保留于 `server/mcp/LICENSE`。
- 第三方组件与依赖清单：见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
