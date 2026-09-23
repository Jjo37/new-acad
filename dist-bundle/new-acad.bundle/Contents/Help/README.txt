new-acad v1.6.2 — Civil 3D AI assistant（portable / 绿色版）
==============================================================

【中文】
1) 解压本包到任意目录（建议路径不含中文，如 D:\new-acad）
2) 双击 install.bat（推荐，自动绕过执行策略）；或右键 install.ps1 →「使用 PowerShell 运行」
3) 看到「结果: N/通过 0/失败 0/警告」即安装成功
4) 打开 Civil 3D → 插件自动加载 → 命令行输入 HANK_SHOW 调出面板
5) 面板顶部：选服务商 + 选模型 + 填你自己的 API Key → 保存 → 开始用

界面语言：安装向导可选 中文 / English；装完也能在面板配置区「界面语言」下拉随时切换
详细说明：打开 使用手册.html（中英双语，右上角切换）

【English】
1) Unzip anywhere (avoid non-ASCII paths, e.g. D:\new-acad)
2) Double-click install.bat (recommended) or right-click install.ps1 -> "Run with PowerShell"
3) "N passed / 0 failed / 0 warnings" means success
4) Open Civil 3D -> the plugin loads itself -> type HANK_SHOW to bring up the palette
5) In the palette: pick provider + model + paste YOUR OWN API key -> Save -> go

UI language: choose Chinese / English in the setup wizard, or switch any time with the "Language"
dropdown in the palette config area.
Full guide: open 使用手册.html (Chinese + English, toggle at the top right)

【要求 / Requirements】
- Windows 10 / 11
- Civil 3D 2024 / 2025 / 2026（自动检测 / auto-detected）
- 无需安装 Node.js（已内置 node.exe）/ No Node.js needed (node.exe is bundled)

【填 Key / API key】
- 面板内配置（推荐）/ configure inside the palette (recommended)
- 或手动编辑 config.json 的 llm 段 / or edit the "llm" section of config.json
- Key 只存本机，请勿把 config.json 随包分发 / the key stays local — never ship config.json

【卸载 / Uninstall】
- 双击 uninstall.bat 或运行 uninstall.ps1 / double-click uninstall.bat, or run uninstall.ps1
- 或手动：删除本目录 + 删除 %APPDATA%\Autodesk\C3D <年份>\chs\Support\ 下的 acad.lsp 与 Hank.lsp
  + 删除计划任务 AcBridge-Relay
  Manual: delete this folder + acad.lsp / Hank.lsp under %APPDATA%\Autodesk\C3D <year>\chs\Support\
  + the AcBridge-Relay scheduled task