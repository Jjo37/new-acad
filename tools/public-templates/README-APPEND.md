---

## 许可与致谢（License & Credits）

- 本项目采用 **MIT License** —— 见 [`LICENSE`](LICENSE)
- **上游致谢**：Civil3D-mcp（MIT License，https://github.com/Sacred-G/Civil3D-mcp）。
  本仓库在 `server/mcp/` 引用其开源实现，其许可证原文保留于 `server/mcp/LICENSE`。
- 第三方组件与依赖清单：见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
- 本项目**非 Autodesk 官方产品**，与 Autodesk 无隶属或背书关系；使用需自备正版 AutoCAD / Civil 3D。

## 关于本仓库（公开版说明）

- 本仓库是**免费分发版（release）的完整源码**，AI 能力走 relay 内置 LLM 代理，
  自带 API Key 即可运行（DeepSeek / OpenAI / 通义 / Kimi / 智谱 / MiniMax / OpenRouter / xAI / 自定义）。
- **不含**：`config.json`（本地配置与密钥，永不入库）、`dist/`、安装包产物（`*.exe` / `*.zip`）、
  运行时数据（`exchange/`）、开发内部文档。
- 编译：插件用 `build-plugin.ps1`（不要裸 `dotnet build -o`，会产出 4KB 空壳 DLL）；
  安装包用 `build-installer.ps1`（需 Inno Setup）。

## 快速开始

```
① 安装正版 AutoCAD / Civil 3D（2024 / 2025 / 2026）
② 双击 install.bat（自动绕过执行策略）或右键 install.ps1 → 用 PowerShell 运行
③ 打开 C3D（Hank.lsp 自动 NETLOAD 插件）
④ 面板里填入自己的 LLM API Key，直接下指令
```
