<#
.SYNOPSIS
  build-plugin.ps1 — 自动检测 C3D 版本并编译插件（P2 跨版本适配）
.DESCRIPTION
  1. 注册表检测 C3D 安装（find-civil3d.ps1）
  2. 从安装目录自动收集 6 个引用 DLL 到 C_References
  3. dotnet build + 复制产物到插件目录
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build-plugin.ps1
.NOTES
  DLL 位置映射（AutoCAD 2025 布局）:
    accoremgd.dll / acdbmgd.dll / acmgd.dll  → <installDir>\
    AecBaseMgd.dll                           → <installDir>\ACA\
    AeccDbMgd.dll / AeccPressurePipesMgd.dll → <installDir>\C3D\
#>

$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = "D:\new-acad" }
$srcDir = "$ROOT\plugin\AcBridge-v24\src"
$refsDir = "$ROOT\plugin\AcBridge-v24\C_References"

Write-Host "===== new-acad 插件编译 (P2 自动版本适配) ====="

# ---------- 1. 检测 C3D ----------
$c3d = & "$ROOT\tools\find-civil3d.ps1" | ConvertFrom-Json
if (-not $c3d.found) {
  Write-Host "[FAIL] 未检测到 Civil 3D（注册表 HKLM\SOFTWARE\Autodesk\AutoCAD 下无 Civil 3D 产品）" -ForegroundColor Red
  Write-Host "       如已安装请检查注册表，或手动放置引用 DLL 到 plugin\AcBridge-v24\C_References" -ForegroundColor Yellow
  exit 1
}
Write-Host "[OK] 检测到 C3D $($c3d.year) : $($c3d.productName)" -ForegroundColor Green
Write-Host "     安装目录: $($c3d.installDir)"

# ---------- 2. 收集引用 DLL ----------
$refMap = @{
  "accoremgd.dll"           = "$($c3d.installDir)accoremgd.dll"
  "AcDbMgd.dll"             = "$($c3d.installDir)acdbmgd.dll"
  "acmgd.dll"               = "$($c3d.installDir)acmgd.dll"
  "AecBaseMgd.dll"          = "$($c3d.installDir)ACA\AecBaseMgd.dll"
  "AeccDbMgd.dll"           = "$($c3d.installDir)C3D\AeccDbMgd.dll"
  "AeccPressurePipesMgd.dll" = "$($c3d.installDir)C3D\AeccPressurePipesMgd.dll"
}
if (-not (Test-Path $refsDir)) { New-Item -ItemType Directory -Path $refsDir | Out-Null }
$missing = @()
foreach ($name in $refMap.Keys) {
  $src = $refMap[$name]
  if (Test-Path $src) {
    $needCopy = $true
    if (Test-Path "$refsDir\$name") {
      $srcTime = (Get-Item $src).LastWriteTime
      $dstTime = (Get-Item "$refsDir\$name").LastWriteTime
      $needCopy = $srcTime -gt $dstTime.AddMinutes(1)
    }
    if ($needCopy) {
      Copy-Item $src "$refsDir\$name" -Force
      Write-Host "  引用更新: $name"
    }
  } else {
    $missing += "$name"
  }
}
if ($missing.Count -gt 0) {
  Write-Host "[WARN] 缺少引用 DLL（编译将失败，需手动补齐）: $($missing -join ', ')" -ForegroundColor Yellow
  Write-Host "       来源版本: $($c3d.year) @ $($c3d.installDir)" -ForegroundColor Yellow
}

# ---------- 3. 编译 ----------
Push-Location $srcDir
try {
  # 2026-09-11: dotnet 会往 stderr 写告警/进度；$ErrorActionPreference=Stop 下 2>&1 会把原生 stderr 当停止错误（NativeCommandError）→ 临时放宽
  $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { dotnet build -c Release 2>&1 | Write-Host } finally { $ErrorActionPreference = $prevEap }
  if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] 编译失败，见上方错误" -ForegroundColor Red
    exit 1
  }
  # P4 防回归（2026-08-25）: 描述参数一致性校验，严重 > 0 则 FAIL（放复制前, 不受 DLL 锁影响）
  $node = if (Test-Path "$ROOT\node.exe") { "$ROOT\node.exe" } else { "node" }
  & $node "$ROOT\tools\scan-desc-check.js" --fail-on-severe
  if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] 描述参数一致性校验未过，请修复 CommandDispatcher.cs 描述后重新编译" -ForegroundColor Red
    exit 1
  }
  Copy-Item "bin\Release\net8.0-windows\Civil3DMcpPlugin.dll" "..\Civil3DMcpPlugin.dll" -Force
  Copy-Item "bin\Release\net8.0-windows\Civil3DMcpPlugin.deps.json" "..\" -Force -ErrorAction SilentlyContinue
  Copy-Item "bin\Release\net8.0-windows\Civil3DMcpPlugin.runtimeconfig.json" "..\" -Force -ErrorAction SilentlyContinue
  $size = (Get-Item "..\Civil3DMcpPlugin.dll").Length
  Write-Host "[OK] 编译成功，DLL 已更新 ($size 字节) — 针对 C3D $($c3d.year)" -ForegroundColor Green
  Write-Host "     提示: C3D 开着时 DLL 被锁，需关 C3D 才能替换" -ForegroundColor Yellow
} finally {
  Pop-Location
}
