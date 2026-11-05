<#
.SYNOPSIS
  clean-runtime.ps1 — 清理 new-acad 运行产生的临时文件
.DESCRIPTION
  默认清理：选择集缓存(exchange\_out)、relay 日志（含守护日志）、跨图选择集缓存、AI 坐标记忆
  -All      ：连同导出/导入文件一起清（exchange\export、exchange\import 报告文件）
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File clean-runtime.ps1
  powershell -ExecutionPolicy Bypass -File clean-runtime.ps1 -All
.NOTES
  选择集缓存会在 C3D 框选图元时自动重建，删除无副作用。
  2026-09-21: 输出双语（跟随 config.json 的 locale，auto=系统语言），见 tools\locale.ps1
#>
param([switch]$All)

$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = "D:\new-acad" }
if ((Split-Path $ROOT -Leaf) -eq "tools") { $ROOT = Split-Path $ROOT -Parent }

. (Join-Path $PSScriptRoot "locale.ps1")
Initialize-NaLocale $ROOT | Out-Null

$cleaned = @()
$freed = 0L

function Clear-Dir([string]$dir) {
  if (Test-Path $dir) {
    $files = Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    Remove-Item "$dir\*" -Recurse -Force -ErrorAction SilentlyContinue
    $script:cleaned += $dir
    $script:freed += $size
  }
}

# 1. 选择集缓存（运行态，下次框选自动重建）
Clear-Dir "$ROOT\exchange\_out"

# 2. relay 日志（relay 运行中可能被占用，跳过不影响）
if (Test-Path "$ROOT\server\relay.log") {
  $size = (Get-Item "$ROOT\server\relay.log").Length
  try {
    Remove-Item "$ROOT\server\relay.log" -Force -ErrorAction Stop
    $cleaned += "$ROOT\server\relay.log"
    $freed += $size
  } catch {
    Write-Host (T "[跳过] relay.log 被占用（relay 正在运行），不影响使用" "[skipped] relay.log is in use (relay is running); harmless") -ForegroundColor Yellow
  }
}

# 2b. relay 守护日志（relay-launcher 落盘，launcher 运行中可能被占用，跳过不影响）
if (Test-Path "$ROOT\server\relay-launcher.log") {
  $size = (Get-Item "$ROOT\server\relay-launcher.log").Length
  try {
    Remove-Item "$ROOT\server\relay-launcher.log" -Force -ErrorAction Stop
    $cleaned += "$ROOT\server\relay-launcher.log"
    $freed += $size
  } catch {
    Write-Host (T "[跳过] relay-launcher.log 被占用（launcher 正在运行），不影响使用" "[skipped] relay-launcher.log is in use (launcher is running); harmless") -ForegroundColor Yellow
  }
}

# 3. AI 坐标记忆（面板要求清理时也会删）
if (Test-Path "$ROOT\exchange\remembered_coords.json") {
  $size = (Get-Item "$ROOT\exchange\remembered_coords.json").Length
  Remove-Item "$ROOT\exchange\remembered_coords.json" -Force -ErrorAction SilentlyContinue
  $cleaned += "$ROOT\exchange\remembered_coords.json"
  $freed += $size
}

# 3b. 跨图选择集缓存（运行态，下次操作自动重建）
if (Test-Path "$ROOT\exchange\selection-cache.json") {
  $size = (Get-Item "$ROOT\exchange\selection-cache.json").Length
  Remove-Item "$ROOT\exchange\selection-cache.json" -Force -ErrorAction SilentlyContinue
  $cleaned += "$ROOT\exchange\selection-cache.json"
  $freed += $size
}

# 4. 报告文件（仅 -All）
if ($All) {
  Clear-Dir "$ROOT\exchange\export"
  Clear-Dir "$ROOT\exchange\import"
}

Write-Host ""
Write-Host (T "===== 清理完成 =====" "===== Cleanup complete =====") -ForegroundColor Green
$cleaned | ForEach-Object { Write-Host "  [x] $_" }
Write-Host ((T "释放空间: {0:N1} KB" "Space freed: {0:N1} KB") -f ($freed / 1KB))
if (-not $All) {
  Write-Host (T "提示: 报告文件在 exchange\export，确认不再需要后加 -All 一起清理" "Tip: report files live in exchange\export; add -All to clean them too once you no longer need them") -ForegroundColor Yellow
}
