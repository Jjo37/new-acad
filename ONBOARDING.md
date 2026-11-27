# ONBOARDING.md — 快速上手（AI / 新接手）

> 更新：2026-10-15（收缩去重：只讲「连哪、怎么调、出事怎么修」；架构与方法清单不在本文件）

## 0. 一句话

new-acad = 让 AI 直接操作 AutoCAD / Civil 3D。
链路：`面板 → relay :19876 → AI → /tcp 桥 → C3D 插件 :8080`。

## 1. 环境检测（先做）

| 检查 | 方式 | 失败处理 |
|------|------|----------|
| ① 插件在线 | `POST http://127.0.0.1:19876/tcp` body `{"jsonrpc":"2.0","id":1,"method":"getCivil3DHealth","params":{}}` | 让用户开 C3D 并 `NETLOAD` 插件 DLL |
| ② relay 在线 | `GET http://127.0.0.1:19876/health` | `node server/relay-launcher.js` |
| ③ MCP 在线（可选） | `GET http://127.0.0.1:3000/health` | `node server/sacred-mcp.js`（有 /tcp 桥时非必需） |

## 2. 调插件的铁律

- **必须走 /tcp 桥**：`POST http://127.0.0.1:19876/tcp`（JSON-RPC）。**禁止**直接 HTTP 打 8080（会把队列卡死）；**禁止**用 `executeCommand` 传交互命令（白名单拦截）。
- **先 `listMethods` 拿真实方法名**，别猜。任务级说法（"统计地块面积并出报告"/"大图层导出坐标表"）先调 **`findRecipes`**：它按意图返回内置配方（有序步骤 + 已知坑）和本项目跑通过的历史记录，步骤里的 `method` 可直接喂给 `cadCall`。
- 优先级：TCP 方法（快、无审批）> MCP 工具（`POST :3000/execute`，C3D 专业域）> `executeCommand`（万能但无参数校验）。
- **批量优先**：复数对象→批量方法（handles 数组）；>500 条→文件通道（`addSurfacePointsFromFile` 等）；禁止逐个调用烧轮次。
- **碰到 `CIVIL3D.NOT_SUPPORTED` 不要换参数硬试**：地块创建/删除、地块线滑移（调整面积）、放坡组编辑在 Civil 3D 托管 API 里没有入口，方法会回 `NOT_SUPPORTED` 并在消息里给出界面操作指路。照消息把界面步骤告诉用户即可；地块的读取、面积/周长、报告、改名与换样式仍然可用。

## 3. 出问题

| 现象 | 解决 |
|------|------|
| 开着 C3D 但 :8080 不通 | C3D 命令行 `NETLOAD <根>\plugin\AcBridge-v24\Civil3DMcpPlugin.dll` |
| 选中图元读不到 | ESC 取消选择 → 重新框选 → 再发（或先调 `saveSelection` 重捕） |
| DLL 更新后调不到新方法 | 关 C3D → `build-plugin.bat` → 重启 C3D |
| 要排查"AI 做了什么" | 设 `CIVIL3D_MCP_LOG_LEVEL=debug` 重启 C3D → 看 `%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log` |

## 4. 关键路径

根 `<安装目录>\` · 插件 DLL `plugin\AcBridge-v24\Civil3DMcpPlugin.dll` · relay `server\panel-relay.js`（日志 `server\relay.log`） · 记忆 `server\memory\agent-memory.md` · 选择集缓存 `exchange\_out\_sel.json`。

## 5. 深入（去重后各归其位）

- **描摹（图片→CAD）** → `knowledge/image-to-cad.md`
