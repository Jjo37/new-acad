# ONBOARDING.md — AI 上手指南

> 你是一个新接手的 AI agent。读完这个文件你就知道环境什么样、去哪连工具、出了事怎么修。
> 第一件事：按 §4 跑环境检测。
> 更新: 2026-09-14（同步 v1.5.0 架构：内置 AI Agent 在 server/、AI 调插件走 relay `/tcp` 桥 → 面板 TCP :8080、配置统一走 config.json、MCP 为对外兼容出口）

---

## 1. 项目是什么

**new-acad** 是一个让 AI 直接操作 AutoCAD / Civil 3D 的项目。

架构：`面板 → relay :19876 → AI（内置 LLM 代理，或自建 OpenClaw 网关）→ /tcp 桥 → C3D 插件 :8080`

## 2. 能做什么

读选中图元、删/移/旋转/缩放/改颜色、画线画圆、3D 实体创建与布尔运算、C3D 全套（曲面/路线/廊道/管网/放坡）、图层管理、块操作、标注、工程量/质检。

完整功能清单: `GET http://127.0.0.1:3000/tools`（270 个 MCP 工具）或 `<安装目录>\knowledge\api-inventory.md`（TCP 方法数以 listMethods 实时清单为准，或直接调 `listMethods`）。

## 3. 项目结构

```
<安装目录>\
├── ONBOARDING.md             ← 这个文件，AI 必读
├── install.bat / install.ps1   一键安装（bat 自动绕过执行策略）
├── config.json               统一配置入口（relayToken/relaySessionKey/端口，本地敏感不入库）
├── plugin/
│   ├── AcBridge-v24/
│   │   ├── Civil3DMcpPlugin.dll   ← C3D 插件 (:8080)
│   │   └── src/                   ← C# 源码
│   └── Hank.lsp                   ← C3D 自动加载脚本
├── server/
│   ├── panel-relay.js        面板中转 (:19876) + /tcp 桥
│   ├── relay-launcher.js     启动器
│   ├── sacred-mcp.js         MCP 启动器 (:3000)
│   └── mcp/                  MCP 服务器
│       ├── build/            编译产物（部署用）
│       └── src/              TS 源码（npm run build 可重建 270 工具）
├── tools/                    测试与辅助工具
├── knowledge/api-inventory.md  完整 API 清单
└── exchange/_out/_sel.json   选中图元缓存
```

### relay 配置（config.json）

```json
{ "bridgePort": 8080, "mcpPort": 3000, "relayPort": 19876,
  "relayToken": "", "relaySessionKey": "", "locale": "auto" }
```

- `relayToken`/`relaySessionKey`：对接 OpenClaw 用（install.ps1 检测到 openclaw.json 时自动写入；也可 setx RELAY_TOKEN 覆盖）
- `locale`：界面语言 —— `auto`（跟随系统 UI 语言）/ `zh-CN` / `en-US`。安装向导选的语言会写入此项；面板配置区「界面语言」下拉（自动/中文/English）可随时切换（写盘即时生效）
- 环境变量优先级高于 config.json
- relay 未配 token 时跑通用模式（面板消息排队落盘，AI 可走 /inbox 接口）

## 4. 环境检测（登录后第一件事）

依次检查以下 3 项，全部通过再开始干活。

### ✅ ① C3D 插件是否在线（TCP :8080）

```
检查方式: 调 getCivil3DHealth
推荐路径: POST http://127.0.0.1:19876/tcp  {"jsonrpc":"2.0","id":1,"method":"getCivil3DHealth","params":{}}
成功: → {"result":{"connected":true,...}}
失败: → 连接被拒 → 提示用户"请打开 Civil 3D"
```

### ✅ ② Panel Relay (:19876) 是否在线

```
检查方式: GET http://127.0.0.1:19876/health
成功: → {"ok":true,"gateway":true,...}
失败: → 跑 node <安装目录>\server\panel-relay.js（或 relay-launcher.js）
```

### ✅ ③ MCP 服务器 (:3000) 是否在线（可选，有 /tcp 桥时非必需）

```
检查方式: GET http://127.0.0.1:3000/health
失败: → 跑 node <安装目录>\server\sacred-mcp.js
```

## 5. 工具调用方式

### ⚠️ 铁律：调 C3D 插件必须走 /tcp 桥

```
POST http://127.0.0.1:19876/tcp
Content-Type: application/json

{"jsonrpc":"2.0","id":1,"method":"createCircle","params":{"center":[0,0],"diameter":50}}
```

- **禁止**直接 HTTP 打 8080（插件只认 TCP JSON-RPC，HTTP 会把队列卡死）
- **禁止**用 executeCommand 传交互命令（_BOX/_PLINE/_CIRCLE 等被白名单拦截）

### 方法调用优先级

```
① 先调 listMethods 拿真实方法清单（别再猜方法名！）
② 有 TCP 方法（见 api-inventory.md）→ 走 /tcp 桥（快、无审批）
③ 有 MCP 工具 → POST :3000/execute（C3D 专业域）
④ executeCommand → 万能，但无参数校验
```

### 批量优先规则（2026-08-07 数据管道框架，必须遵守）

```
① 复数对象 → 用批量方法（setEntitiesColor/moveEntities/deleteEntities，handles 数组一次处理 N 个）
② 数据 >500 条 → 走文件通道（addSurfacePointsFromFile / importCogoPointsFromFile / exportSelectionToFile）
③ 禁止逐个调用烧轮次
④ 大规模任务（选中≥20 或含 csv/批量/大量关键词）先出计划：方式+预计轮数，再动手
⑤ 工具循环第 8 轮强制提示改批量
```

### ❌ 不可用/慎用方法（2026-08-05 实测，别再调）

| 方法 | 状态 | 替代 |
|------|------|------|
| loftSolid | ✅ 已重写为真 API（2026-08-11 CreateLoftedSolid） | 直接调（crossSections≥2 闭合曲线） |
| extrude / revolve / sweep / slice / interfere（英文名） | 不存在 | 用 extrudeSolid / revolveSolid / sweepSolid / sliceSolid / interferenceCheck |
| stationToPoint | 不存在 | 用 alignmentStationToPoint（参数 name + station，station 是数字！） |
| 廊道区域编辑（addCorridorRegion/deleteCorridorRegion） | ✅ 已修（2026-08-05：BaselineRegions.Add/RemoveAt 强类型） | addCorridorRegion 支持 regionName 可选参数 |
| 廊道目标映射写（setCorridorTargetMappings 的写部分） | 🟡 读可用（GetTargets），写需构造 SubassemblyTargetInfoCollection | 暂标注，复杂场景手动 |
| 放坡组（GradingGroup 系列） | ❌ 托管 API 无此类型（底层 C++ AeccDbGradingGroup 未封装） | 仅能命令操作，AI 不可调 |
| 地块编辑（AdjustLotLine/SlideAngle/GetBoundary/GetVertices） | ❌ Parcel 无此方法（仅面积标签类 API） | 面积查询可用（Area 属性/GetUsageArea） |
| executeCommand（交互命令） | 白名单拦截 | 用专用方法 |
| getSelection 返回 docName | 多图纸场景：先 getDrawingInfo 对比当前文档 | 不一致 → 让用户重新框选（或调 saveSelection 重捕） |

### 常用 TCP 方法速查

| 方法 | 参数 | 说明 |
|------|------|------|
| getCivil3DHealth | — | 插件在线检测 |
| getSelection | — | 读选中图元 handle |
| getEntityInfo | handle | 查图元属性（含 Solid3d 体积/块引用） |
| createCircle | center:[x,y], diameter | 画圆（handle 为十六进制） |
| createPolyline | points:[{x,y}], closed:1/0 | 画多段线 |
| createBox/Cylinder/Sphere/Cone/Wedge/Torus | x,y,z + 尺寸 | 3D 实体 |
| extrudeSolid | handle, height, taperAngle | 闭合曲线拉伸成体 |
| booleanUnion / booleanSubtract | handles[] / mainHandle+toolHandle | 3D 布尔 |
| createBlock / writeBlock / attachXref | — | 块操作 |
| moveEntity / copyEntity / deleteEntity | handle + 位移 | 编辑 |
| runLisp | lisp 表达式 | 投递执行 LISP（**2026-08-07 已修复：SendStringToExecute 投递，无返回值通道，适合操作类脚本；查询类用专用方法**） |
| addSurfacePointsFromFile | name, filePath, delimiter?, hasHeader?, xCol?, yCol?, zCol?, skipRows? | **文件通道**：任意文本格式批量加曲面点（delimiter: auto/comma/space/tab/semicolon，AI 判断格式传参） |
| importCogoPointsFromFile | format, filePath | **文件通道**：从文件导入 Cogo 点 |
| exportSelectionToFile | path | **文件通道**：选中集完整几何写 JSON（已存在拒绝） |

**注意**：createLineSegment 返回字段是 `lineId`（不是 handle）；joinEntities 不支持纯 Line 组合（用 Polyline）。

## 6. 面板通信

面板消息 → relay → **CAD 会话**（agent:main:cad:panel）→ AI 处理 → relay 自动 SSE 推回面板。

**你不需要手动 POST /reply**——relay 自动捕获 CAD 会话的 AI 回复推给面板。

**记忆检索化（2026-08-07）**：长期记忆 `server/memory/agent-memory.md` 分节存储，消息按关键词匹配注入相关节（不全量注入）；readMemory 拉全节索引，按需读全文。

**清理接口（2026-08-07）**：`POST /clear` body `{scope: chat|logs|cache|reports|all}`（默认 chat）——chat=对话、logs=日志、cache=缓存文件、reports=MISSING 报告、all=全部；**长期记忆无清理入口（用户不可清）**，export/import 用户文件永不清理。面板「更多清理」菜单对应此接口。

CAD 会话规则：
1. 回复极简短，直接给结果
2. 调插件走 /tcp 桥
3. 不需要说"正在操作..."之类的话
4. 多步独立操作并行发

## 7. 常见问题处理

| 现象 | 解决 |
|------|------|
| C3D 开着但 :8080 不通 | C3D 命令行 NETLOAD `<安装目录>\plugin\AcBridge-v24\Civil3DMcpPlugin.dll` |
| 选中图元读不到 | 面板发送时已自动捕获（2026-08-05 起）；仍读不到 → ESC 取消选择 → 重新框选 → 再发消息（或先调 saveSelection 重捕再 getSelection） |
| relay 没启动 | `node <安装目录>\server\relay-launcher.js` |
| DLL 更新后调不到新方法 | 关 C3D → build-plugin.bat → 重启 C3D |
| 插件日志 | `C:\Users\<用户>\AppData\Local\Civil3DMcpPlugin\plugin.log`（成功调用默认不落盘） |
| 开 Debug 日志 | 设环境变量 `CIVIL3D_MCP_LOG_LEVEL=debug` 后重启 C3D → 成功调用也记录（排查“AI 做了什么”用） |

## 8. 关键文件路径速查

| 路径 | 说明 |
|------|------|
| <安装目录>\ | 项目根目录 |
| <安装目录>\knowledge\api-inventory.md | 完整 API 清单 |
| <安装目录>\plugin\AcBridge-v24\Civil3DMcpPlugin.dll | C3D 插件 DLL |
| <安装目录>\server\panel-relay.js | 面板中转 + /tcp 桥 |
| <安装目录>\server\relay.log | relay 日志 |
| <安装目录>\server\memory\agent-memory.md | AI 长期记忆（分节存储，消息按关键词匹配注入） |
| <安装目录>\exchange\_out\_sel.json | 选择集缓存 |
| %LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log | C3D 插件日志 |

## 9. 插件编译

```bash
build-plugin.bat          # 或 npm run build:plugin
```

编译产物输出到 `plugin/AcBridge-v24/Civil3DMcpPlugin.dll`。**C3D 关着才能替换**（DLL 被锁）。
