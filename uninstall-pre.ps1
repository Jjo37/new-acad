# new-acad 卸载预清理（Inno Setup [UninstallRun] 调用，静默）
# 停 relay/MCP 进程 + 删开机自启计划任务 + 还原 C3D 自动加载（Hank.lsp/acad.lsp）
$ErrorActionPreference = "SilentlyContinue"
$ROOT = $PSScriptRoot

# 0. 2026-11-10: 清掉旧 net10 预览包可能留下的 bundle 副本 / 改名残留
foreach ($b in @(
    (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle"),
    (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle"),
    (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle.disabled-by-net10-preview"),
    (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins\new-acad-net10.bundle.disabled-by-net10-preview"))) {
  if (Test-Path $b) { Remove-Item $b -Recurse -Force -ErrorAction SilentlyContinue }
}
# 被预览包改名停用的正式 bundle，还原回来
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

# 2. 删开机自启计划任务 + Applications 自动加载注册
schtasks /Delete /TN "AcBridge-Relay" /F 2>$null
$c3d0 = & "$ROOT\tools\find-civil3d.ps1" | ConvertFrom-Json
if ($c3d0.found -and $c3d0.productKey) {
  $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($c3d0.rName)\$($c3d0.productKey)\Applications\HankBridge"
  Remove-Item $appKey -Recurse -Force 2>$null
}

# 3. 还原 C3D 自动加载（Hank.lsp 删除 + acad.lsp 移除加载行；acad.lsp 仅剩该行则整个删除）
$c3d = & "$ROOT\tools\find-civil3d.ps1" | ConvertFrom-Json
if ($c3d.found -and $c3d.supportDir) {
  Remove-Item "$($c3d.supportDir)\Hank.lsp" -Force
  $acad = "$($c3d.supportDir)\acad.lsp"
  if (Test-Path $acad) {
    $content = [System.IO.File]::ReadAllText($acad)
    $content = $content -replace '(?m)^\s*\(load "Hank\.lsp"\)\s*\(princ\)\s*$', ''
    $content = $content -replace '(?m)^\s*\(load "Hank\.lsp"\)\s*$', ''
    if ($content.Trim()) { [System.IO.File]::WriteAllText($acad, $content, (New-Object System.Text.UTF8Encoding $false)) } else { Remove-Item $acad -Force }
  }
}
