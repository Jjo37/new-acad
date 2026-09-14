<#
.SYNOPSIS
  build-dist.ps1 — 生成可分发的绿色版安装包（P3 打包）
.DESCRIPTION
  1. 收集运行必需文件到 dist/
  2. 复制 node.exe（便携版，部署机无需装 Node）
  3. 可选: Compress-Archive 压缩成 zip
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build-dist.ps1          # 只生成 dist/
  powershell -ExecutionPolicy Bypass -File build-dist.ps1 -Zip      # 生成 dist/ + new-acad-v<版本>.zip
.NOTES
  部署流程: 解压 → 双击 install.ps1（右键"使用 PowerShell 运行"）→ 开 C3D
  打包内容遵循 knowledge/deployment-package.md
#>
param([switch]$Zip)

$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = "D:\new-acad" }
# 版本号从 package.json 读取，避免打包时忘改
$VERSION = try { (Get-Content "$ROOT\package.json" -Raw | ConvertFrom-Json).version } catch { "0.0.0" }
$DIST = "$ROOT\dist"

Write-Host "===== new-acad 打包 v$VERSION ====="

# ---------- 0. 检查必需文件 ----------
$need = @(
  "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.dll",
  "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.deps.json",
  "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.runtimeconfig.json",
  "$ROOT\server\panel-relay.js",
  "$ROOT\server\llm-agent.js",
  "$ROOT\server\compact.js",   # 2026-08-14: 上下文压缩模块(llm-agent 依赖, 漏打包会 require 失败)
  "$ROOT\server\cad-tools.js",
  "$ROOT\server\file-tools.js",
  "$ROOT\server\relay-launcher.js",
  "$ROOT\server\sacred-mcp.js",
  "$ROOT\server\mcp\build\index.js",
  "$ROOT\server\mcp\package.json",
  "$ROOT\server\mcp\LICENSE",
  "$ROOT\install.ps1",
  "$ROOT\config.json",
  "$ROOT\tools\find-civil3d.ps1",
  "$ROOT\tools\clean-runtime.ps1"
)
foreach ($f in $need) {
  if (-not (Test-Path $f)) { Write-Host "[FAIL] 缺少: $f" -ForegroundColor Red; exit 1 }
}

# ---------- 0.5 本地模块完整性校验（防漏包：扫描 require('./x') 是否在源存在） ----------
$srcServerJs = Get-ChildItem "$ROOT\server" -Filter "*.js" -File
$missingMod = @()
foreach ($js in $srcServerJs) {
  $content = Get-Content $js.FullName -Raw -ErrorAction SilentlyContinue
  if (-not $content) { continue }
  # 匹配 require('./xxx') 或 require("./xxx")
  $reqs = [regex]::Matches($content, 'require([''"](.{1,2}/[^''"]+)[''"])')
  foreach ($m in $reqs) {
    $rel = $m.Groups[1].Value -replace '^./', ''
    $cand = Join-Path $ROOT "server$rel"
    if (-not (Test-Path $cand)) { $missingMod += "$($js.Name) -> $rel" }
  }
}
if ($missingMod.Count -gt 0) {
  Write-Host "[FAIL] 本地模块缺失（relay 将无法启动）:" -ForegroundColor Red
  $missingMod | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  exit 1
}
Write-Host "[OK] server 本地模块完整性校验通过" -ForegroundColor Green


# P0: install.ps1 路径引用防呆校验（防"反斜杠丢失"类 bug：$ROOT\server 变 $ROOTserver）
# 1) 抓 "$ROOT 后直接跟字母（应为反斜杠）→ 反斜杠丢失，直接 FAIL
$badRefs = Select-String -Path "$ROOT\install.ps1" -Pattern '\"\$ROOT[A-Za-z]' -AllMatches
if ($badRefs) {
  Write-Host "[FAIL] install.ps1 存在反斜杠丢失的路径引用（\$ROOT 后直接跟字母）:" -ForegroundColor Red
  $badRefs | ForEach-Object { Write-Host "  行 $($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
  exit 1
}
Write-Host "[OK] install.ps1 路径引用无反斜杠丢失" -ForegroundColor Green

# ---------- 1. 重建 dist ----------
if (Test-Path $DIST) { Remove-Item $DIST -Recurse -Force }
New-Item -ItemType Directory -Path $DIST | Out-Null
New-Item -ItemType Directory -Path "$DIST\server\mcp" | Out-Null
New-Item -ItemType Directory -Path "$DIST\plugin\AcBridge-v24" | Out-Null
New-Item -ItemType Directory -Path "$DIST\tools" | Out-Null
New-Item -ItemType Directory -Path "$DIST\knowledge" | Out-Null
Write-Host "[OK] dist/ 已创建"

# ---------- 2. 复制文件 ----------
Copy-Item "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.dll" "$DIST\plugin\AcBridge-v24\" -Force
Copy-Item "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.deps.json" "$DIST\plugin\AcBridge-v24\" -Force
Copy-Item "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.runtimeconfig.json" "$DIST\plugin\AcBridge-v24\" -Force
Copy-Item "$ROOT\server\panel-relay.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\llm-agent.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\compact.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\cad-tools.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\file-tools.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\relay-launcher.js" "$DIST\server\" -Force
Copy-Item "$ROOT\server\memory" "$DIST\server\memory" -Recurse -Force  # 2026-08-07: 长期记忆初始骨架
Copy-Item "$ROOT\server\sacred-mcp.js" "$DIST\server\" -Force
Copy-Item "$ROOT\install.ps1" "$DIST\" -Force
# 安全加固: 进包的 config.json 必须是干净模板（强制清空 relayToken/relaySessionKey），
# 绝不复制本机可能已配置密钥的 config.json，防止网关 token 随包泄漏
$distCfg = @{
  bridgePort = 8080
  mcpPort = 3000
  relayPort = 19876
  locale = "auto"
  relayToken = ""
  relaySessionKey = ""
  relayMode = "llm"
  llm = @{ provider=""; baseUrl=""; apiKey=""; model=""; maxTurns=20; timeoutMs=120000 }
}
[System.IO.File]::WriteAllText("$DIST\config.json", ($distCfg | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))
Write-Host "[OK] config.json 已生成（干净模板，无密钥）"
Copy-Item "$ROOT\tools\find-civil3d.ps1" "$DIST\tools\" -Force
Copy-Item "$ROOT\tools\clean-runtime.ps1" "$DIST\tools\" -Force
# 2026-08-13: 端口诊断/配置工具（用户端口问题自服务）
Copy-Item "$ROOT\tools\port-check.ps1" "$DIST\tools\" -Force
Copy-Item "$ROOT\tools\port-check.bat" "$DIST\tools\" -Force
Copy-Item "$ROOT\tools\port-config.ps1" "$DIST\tools\" -Force
Copy-Item "$ROOT\tools\port-config.bat" "$DIST\tools\" -Force
Copy-Item "$ROOT\uninstall.ps1" "$DIST\" -Force
Copy-Item "$ROOT\uninstall-pre.ps1" "$DIST\" -Force  # 2026-08-25: Inno uninstall pre-cleanup (stop node/task, restore acad.lsp)
Copy-Item "$ROOT\使用手册.html" "$DIST\" -Force
# 2026-08-17: AI 生成 SAC 部件必需（knowledge.md 指引 AI 读 guide + 模板）+ 简版手册
Copy-Item "$ROOT\knowledge\sac-xaml-guide.md" "$DIST\knowledge\" -Force
Copy-Item "$ROOT\knowledge\sac-templates" "$DIST\knowledge\sac-templates" -Recurse -Force
Copy-Item "$ROOT\knowledge\ai-sac-subassembly-plan.md" "$DIST\knowledge\" -Force
Copy-Item "$ROOT\workflow-guide.md" "$DIST\" -Force
Copy-Item "$ROOT\server\mcp\build" "$DIST\server\mcp\build" -Recurse -Force
# 关键修复: version.js 启动时读取 ../package.json，缺失会导致 MCP 开箱即崩（P0-3）
Copy-Item "$ROOT\server\mcp\package.json" "$DIST\server\mcp\package.json" -Force
# 2026-09-11: 补随包 LICENSE（MIT 二次开发合规硬要求，之前漏拷）
Copy-Item "$ROOT\server\mcp\LICENSE" "$DIST\server\mcp\LICENSE" -Force
# node_modules 瘦身: 发布包只带运行时依赖（devDependencies 如 typescript/vitest 不进包）
# 打包前 prune，打包后恢复（保证开发环境完整）
Write-Host "[..] node_modules 瘦身（npm prune --omit=dev）..."
pushd "$ROOT\server\mcp"
try {
  & npm prune --omit=dev --no-audit --no-fund 2>$null | Out-Null
} catch { Write-Host "[WARN] npm prune 失败: $_" -ForegroundColor Yellow }
Copy-Item "$ROOT\server\mcp\node_modules" "$DIST\server\mcp\node_modules" -Recurse -Force
Write-Host "[OK] node_modules 已复制（仅运行时依赖）"
try {
  & npm install --no-audit --no-fund 2>$null | Out-Null
  Write-Host "[OK] 开发依赖已恢复"
} catch { Write-Host "[WARN] npm install 恢复失败: $_" -ForegroundColor Yellow }
popd
Write-Host "[OK] 项目文件已复制"

# ---------- 3. Node 便携版 ----------
$nodeSrc = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $nodeSrc) { Write-Host "[FAIL] 本机无 node，无法打包便携版"; exit 1 }
Copy-Item $nodeSrc "$DIST\node.exe" -Force
Write-Host "[OK] node.exe 已复制 ($([math]::Round((Get-Item $DIST\node.exe).Length/1MB,1)) MB)"

# ---------- 4. 使用说明 ----------
$readme = @"
new-acad v$VERSION — Civil 3D AI 数据桥（绿色版）
==========================================

【安装步骤】
1. 解压本包到任意目录（建议 D:\new-acad，路径不含中文）
2. 双击 install.bat（推荐，自动绕过 PowerShell 执行策略）
   或右键 install.ps1 → "使用 PowerShell 运行"
3. 看到"结果: N/通过 0/失败 0/警告"即安装成功
4. 打开 Civil 3D → 插件自动加载 → 命令行输入 HANK_SHOW 看面板

【使用手册】
详细图文说明请打开 使用手册.html（安装步骤/日常使用/AI 接入/常见问题）

【环境要求】
- Windows 10/11
- Civil 3D 2024 / 2025 / 2026（自动检测版本）
- 无需安装 Node.js（已内置 node.exe）

【AI 对接】
- AI 配置：开 C3D 后在面板顶部选模型 + 填 API Key（保存即生效，无需改文件）
- 其他 AI 客户端（Claude/Cursor）由 install.ps1 自动配置 MCP

【自带工具】
- MCP 服务器: 双击 server\start-mcp.bat（或由 install.ps1 启动）
- 面板中转: 双击 server\start-relay.bat（开机自启已注册）

【卸载】
- 双击 uninstall.bat（一键卸载：移除自动加载 + 自启 + 停进程 + 清理运行态 + 可选删目录，目录自动延迟清理）
- 或运行 uninstall.ps1（右键"使用 PowerShell 运行"）
- 或手动: 删除本目录 + %APPDATA%\Autodesk\C3D <年份>\chs\Support\ 下的 acad.lsp/Hank.lsp + 计划任务 AcBridge-Relay（任务计划程序）
"@
# UTF-8 with BOM（兼容中文 Windows 与 UTF-8 环境）
[System.IO.File]::WriteAllText("$DIST\README.txt", $readme, (New-Object System.Text.UTF8Encoding $true))
Write-Host "[OK] README.txt 已生成 (UTF-8 BOM)"

# ---------- 5. 启动快捷 bat + install.bat ----------
# 2026-08-07: relay 改由守护启动器拉起 (崩溃自动重启 + 日志落盘), 不再直启
Set-Content "$DIST\server\start-relay.bat" "@echo off`r`ncd /d %~dp0..`r`nnode.exe server\relay-launcher.js`r`npause" -Encoding ASCII
Set-Content "$DIST\server\start-mcp.bat" "@echo off`r`ncd /d %~dp0..`r`nnode.exe server\sacred-mcp.js`r`npause" -Encoding ASCII
# install.bat: 一键安装入口，绕过 PowerShell 执行策略（P1-6）
# title 用英文（bat 中文需 GBK 编码，非中文系统会乱码，英文最稳）
Set-Content "$DIST\install.bat" "@echo off`r`ntitle new-acad $VERSION installer`r`ncd /d %~dp0`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`"`r`necho.`r`npause" -Encoding ASCII
# 2026-08-13: uninstall.bat 一键卸载入口（对称 install.bat）
# 末尾延迟自删: ps1 删目录被本 bat 占用时, cmd 释放句柄后自动 rd（卸载窗口需用户关闭）
Set-Content "$DIST\uninstall.bat" "@echo off`r`ntitle new-acad $VERSION uninstaller`r`ncd /d %~dp0`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall.ps1`"`r`necho.`r`npause" -Encoding ASCII
Write-Host "[OK] 启动脚本 + install.bat/uninstall.bat 已生成"

# ---------- 6. 大小统计 ----------
$total = (Get-ChildItem $DIST -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "[OK] dist/ 总大小: $([math]::Round($total/1MB,1)) MB" -ForegroundColor Green

# ---------- 7. 压缩 ----------
if ($Zip) {
  $zipPath = "$ROOT\new-acad-v$VERSION.zip"
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  Compress-Archive -Path "$DIST\*" -DestinationPath $zipPath -CompressionLevel Optimal
  $zipSize = (Get-Item $zipPath).Length
  Write-Host "[OK] 压缩包: $zipPath ($([math]::Round($zipSize/1MB,1)) MB)" -ForegroundColor Green
}

Write-Host "`n打包完成。部署: 解压 dist/ → install.ps1 → 开 C3D" -ForegroundColor Cyan
