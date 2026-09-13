# 新机器部署包 — 文件筛选

> 目标: 最小可用部署包
> 原则: 只放运行必需的文件，源码/编译/调试文件一概不发

---

## ✅ 必须保留（发给别人）

| 文件 | 大小 | 说明 |
|------|------|------|
| `plugin/AcBridge-v24/Civil3DMcpPlugin.dll` | 713KB | C3D 插件核心（328方法） |
| `plugin/AcBridge-v24/*.json` | ~1KB | .NET 运行时配置 |
| `plugin/Hank.lsp` | 0.4KB | C3D 自动加载脚本 |
| `server/panel-relay.js` | ~15KB | 面板中转服务器 |
| `server/sacred-mcp.js` | 0.7KB | MCP 服务器启动器 |
| `server/mcp/` | ~30MB | MCP 服务器（build + node_modules） |
| `install.ps1` | 7.3KB | 一键部署脚本 |
| `ONBOARDING.md` | 8.6KB | AI 上手指南 |
| `config.json` | 0.2KB | 配置 |

## 🟡 建议保留

| 文件 | 理由 |
|------|------|
| `BOOTSTRAP.md` | 架构手册 |
| `README.md` | 项目简介 |
| `workflow-guide.md` | 用户工作流说明 |
| `knowledge/api-inventory.md` | 功能清单 |

## ❌ 部署不需要

| 文件/目录 | 理由 |
|-----------|------|
| `plugin/AcBridge-v24/src/` | C# 源码，目标机器不编译 |
| `plugin/AcBridge-v24/C_References/` | Autodesk 引用 DLL（版权问题） |
| `DASHBOARD.md` / `ROADMAP.md` | 开发进度，用户不需要 |
| `knowledge/补全计划.md` | 开发计划 |
| `.git/` `.gitignore` | Git 数据 |
| `build-plugin.bat` | 编译脚本，部署不编译 |
| `tools/` 中除 BAT 外的文件 | 辅助工具 |

## 核心部署包

约 **35MB**（DLL + MCP + Node.js + 配置 + 文档）
