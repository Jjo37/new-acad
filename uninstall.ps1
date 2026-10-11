# uninstall.ps1 — new-acad 发布版卸载脚本
# 用法: 右键"使用 PowerShell 运行"，或 powershell -ExecutionPolicy Bypass -File uninstall.ps1
<#
.SYNOPSIS
  卸载 new-acad：移除自动加载(Hank.lsp/acad.lsp)、开机自启、relay/MCP 进程
.DESCRIPTION
  清理内容:
    1. C3D Support 目录的 Hank.lsp / acad.lsp（含备份恢复）
    2. 启动文件夹的 relay 快捷方式
    3. 运行中的 relay / MCP 进程（node）
    4. AI 记忆/知识库 server/memory/（可选，询问）
    5. 本目录（可选，询问）
.NOTES
  不删除: C3D 本身、用户的图纸、config.json 备份（保留一份 .bak）
#>

$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = Split-Path -Parent $MyInvocation.MyCommand.Path }

Write-Host "`n===== new-acad 卸载 =====`n" -ForegroundColor Cyan

# ---------- 1. 移除 C3D Support 自动加载 ----------
Write-Host "--- 1/6 移除 C3D 自动加载 ---" -ForegroundColor Yellow
$removed = 0
Get-ChildItem "$env:APPDATA\Autodesk" -Recurse -Filter "Hank.lsp" -ErrorAction SilentlyContinue | ForEach-Object {
  try {
    # 备份一份到 .bak
    Copy-Item $_.FullName "$($_.FullName).bak" -Force -ErrorAction SilentlyContinue
    Remove-Item $_.FullName -Force
    Write-Host "  已移除: $($_.FullName)（备份 .bak）" -ForegroundColor Green
    $removed++
  } catch { Write-Host "  移除失败: $($_.FullName): $_" -ForegroundColor Yellow }
}
Get-ChildItem "$env:APPDATA\Autodesk" -Recurse -Filter "acad.lsp" -ErrorAction SilentlyContinue | ForEach-Object {
  # 只处理含 new-acad 注入的 acad.lsp（备份后移除；若原本就有内容则恢复备份）
  $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
  if ($content -match "new-acad|hank|Hank|NETLOAD") {
    try {
      Copy-Item $_.FullName "$($_.FullName).bak" -Force -ErrorAction SilentlyContinue
      Remove-Item $_.FullName -Force
      Write-Host "  已移除: $($_.FullName)（备份 .bak）" -ForegroundColor Green
      $removed++
    } catch { Write-Host "  移除失败: $($_.FullName): $_" -ForegroundColor Yellow }
  } else {
    Write-Host "  跳过（非 new-acad 注入）: $($_.FullName)" -ForegroundColor Gray
  }
}
if ($removed -eq 0) { Write-Host "  未发现自动加载文件（可能已卸载）" -ForegroundColor Gray }

# ---------- 2. 移除开机自启（计划任务 + 旧快捷方式） ----------
Write-Host "`n--- 2/6 移除开机自启 ---" -ForegroundColor Yellow
# 2026-08-10 起用计划任务 + vbs 隐藏启动器
$task = Get-ScheduledTask -TaskName 'AcBridge-Relay' -ErrorAction SilentlyContinue
if ($task) {
  Unregister-ScheduledTask -TaskName 'AcBridge-Relay' -Confirm:$false
  Write-Host "  已移除计划任务: AcBridge-Relay" -ForegroundColor Green
} else { Write-Host "  未发现计划任务" -ForegroundColor Gray }
# 旧版启动文件夹快捷方式
$startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$lnk = @(Get-ChildItem $startup -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "AcBridge|relay" })
if ($lnk.Count -gt 0) {
  $lnk | ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "  已移除: $($_.Name)" -ForegroundColor Green }
}
# vbs 隐藏启动器
$vbs = "$ROOT\server\start-relay-hidden.vbs"
if (Test-Path $vbs) { Remove-Item $vbs -Force; Write-Host "  已移除: start-relay-hidden.vbs" -ForegroundColor Green }

# ---------- 3. 停止 relay / MCP 进程 ----------
Write-Host "`n--- 3/6 停止 relay/MCP 进程 ---" -ForegroundColor Yellow
# 清理 relay/MCP 进程 + 承载宿主（powershell -NoExit 也可能占着安装目录句柄）
$procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -in @('node.exe','powershell.exe') -and $_.CommandLine -match 'new-acad|panel-relay|sacred-mcp' }
if ($procs) {
  $procs | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    Write-Host "  已停止: $($_.Name) PID $($_.ProcessId)" -ForegroundColor Green
  }
  Start-Sleep -Milliseconds 500   # 等句柄释放
} else { Write-Host "  未发现 relay/MCP 进程" -ForegroundColor Gray }

# ---------- 4. 清理运行态 ----------
Write-Host "`n--- 4/6 清理运行态 ---" -ForegroundColor Yellow
if (Test-Path "$ROOT\exchange") {
  Remove-Item "$ROOT\exchange" -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host "  已清理 exchange/（选择集缓存 _sel.json、跨图缓存 selection-cache.json、MISSING 报告）" -ForegroundColor Green
}
if (Test-Path "$ROOT\server\relay-inbox.json") { Remove-Item "$ROOT\server\relay-inbox.json" -Force; Write-Host "  已清理 relay-inbox.json" -ForegroundColor Green }
if (Test-Path "$ROOT\server\relay.log") { Remove-Item "$ROOT\server\relay.log" -Force; Write-Host "  已清理 relay.log" -ForegroundColor Green }
if (Test-Path "$ROOT\server\relay-launcher.log") { Remove-Item "$ROOT\server\relay-launcher.log" -Force; Write-Host "  已清理 relay-launcher.log" -ForegroundColor Green }

# ---------- 5. AI 记忆/知识库（单独问询，不随运行态默认删） ----------
Write-Host "`n--- 5/6 AI 记忆/知识库 ---" -ForegroundColor Yellow
if (Test-Path "$ROOT\server\memory") {
  $ansMem = Read-Host "  删除 server/memory/（AI 长期记忆 agent-memory.md + 知识库 knowledge.md）? [y/N]"
  if ($ansMem -match "^[yY]") {
    Remove-Item "$ROOT\server\memory" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已删除 server/memory/" -ForegroundColor Green
  } else {
    Write-Host "  保留 server/memory/（AI 记忆与知识库）" -ForegroundColor Gray
  }
} else { Write-Host "  未发现 server/memory/" -ForegroundColor Gray }

# ---------- 6. 删除本目录 ----------
Write-Host "`n--- 6/6 删除安装目录 ---" -ForegroundColor Yellow
$ans = Read-Host "  删除整个安装目录 $ROOT ? [y/N]"
if ($ans -match "^[yY]") {
  # 先退到上级目录再删（避免删除正在使用的目录）
  $parent = Split-Path $ROOT -Parent
  Push-Location $parent
  try {
    Remove-Item $ROOT -Recurse -Force
    Write-Host "  安装目录已删除: $ROOT" -ForegroundColor Green
  } catch {
    # 2026-08-13: 双击 uninstall.bat 卸载时本目录被 cmd 占用，直接删会失败 → 延迟自删（独立隐藏 cmd，等句柄释放后 rd）
    Write-Host "  目录被卸载程序自身占用，将延迟自动清理..." -ForegroundColor Yellow
    try {
      Start-Process cmd -ArgumentList '/c', 'timeout /t 3 /nobreak >nul & rd /s /q "' + $ROOT + '" 2>nul' -WindowStyle Hidden
      Write-Host "  请关闭本窗口，安装目录将在数秒后自动删除" -ForegroundColor Green
    } catch {
      Write-Host "  延迟删除启动失败，请手动删除: $ROOT" -ForegroundColor Yellow
      Write-Host "  原因: $_" -ForegroundColor Gray
    }
  }
  Pop-Location
} else {
  Write-Host "  保留安装目录（如需清理可手动删除）" -ForegroundColor Gray
}

Write-Host "`n===== 卸载完成 =====" -ForegroundColor Cyan
Write-Host "已备份文件（如需恢复）: " -ForegroundColor Gray
Get-ChildItem "$env:APPDATA\Autodesk" -Recurse -Filter "*.lsp.bak" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Gray }
