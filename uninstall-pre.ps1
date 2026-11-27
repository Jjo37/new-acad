# new-acad 卸载预清理（Inno Setup [UninstallRun] 调用，静默）
# 停 relay/MCP 进程 + 删开机自启计划任务 + 还原 C3D 自动加载（Hank.lsp/acad.lsp）
#
# 2026-11-24: 加 -Hosts 参数
#   （无参数）= 原有全量清理（Inno 卸载走这条路，行为不变）
#   -Hosts "1,3" = 只摘掉这几个宿主的接线，relay / 计划任务 / 安装目录一律不动
#   入口：tools\manage-hosts.bat（或 manage-hosts.ps1 -Remove 1,3）
param([string]$Hosts = "")

$ErrorActionPreference = "SilentlyContinue"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = Split-Path -Parent $MyInvocation.MyCommand.Path }

# host-wiring.ps1 提供 Get-NewAcadHosts / Resolve-HostSelection / Remove-HostWiring（单一真相）
$script:NewAcadRoot = $ROOT
. (Join-Path $ROOT "tools\host-wiring.ps1")
function Ok { param($m) Write-Host $m }
function Wn { param($m) Write-Host $m }

$fullUninstall = [string]::IsNullOrWhiteSpace($Hosts)
$allHosts = @(Get-NewAcadHosts -Root $ROOT)
$selected = if ($fullUninstall) { $allHosts } else { @(Resolve-HostSelection -AllHosts $allHosts -Selection $Hosts) }

if ($fullUninstall) {
  # 0. 2026-11-10: 清掉早期 net10 包可能留下的 bundle 副本 / 改名残留
  foreach ($b in @(
      (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle"),
      (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle"),
      (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle.disabled-by-net10-preview"),
      (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle.disabled-by-net10-preview"))) {
    if (Test-Path $b) { Remove-Item $b -Recurse -Force -ErrorAction SilentlyContinue }
  }
  # 被早期 net10 包改名停用的正式 bundle，还原回来
  foreach ($base in @((Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"), (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins"))) {
    $off = Join-Path $base "new-acad.bundle.disabled-by-net10-preview"
    if (Test-Path $off) { try { Rename-Item -Path $off -NewName "new-acad.bundle" -ErrorAction Stop } catch { } }
  }
  # 1. 停 relay / MCP / launcher（按命令行匹配，避免误杀其他 node 进程）
  Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object {
    $_.CommandLine -match 'panel-relay|sacred-mcp|relay-launcher'
  } | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force
  }
  # 等句柄释放，避免 Inno 删文件时因进程未完全退出而残留（2026-08-25 实测：不等待会残留 vbs）
  Start-Sleep -Milliseconds 500

  # 2. 删开机自启计划任务（全局资源，只在全量卸载时动）
  schtasks /Delete /TN "AcBridge-Relay" /F 2>$null
}

# 3. 逐宿主摘接线（Applications 注册 + Hank.lsp + acad.lsp 加载行）
foreach ($h in $selected) {
  if (-not $h.productKey) { continue }
  Remove-HostWiring $h | Out-Null
  Write-Host ("host wiring removed: " + $h.rName + " " + $h.productKey + " [" + $h.productName + "]")
}
