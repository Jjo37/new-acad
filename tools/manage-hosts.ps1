<#
.SYNOPSIS
  new-acad 宿主管理 —— 后续加装到本机其他 AutoCAD / Civil 3D 版本，或只卸载某一个版本
.DESCRIPTION
  安装向导只配了当时选中的宿主。之后想加装到别的版本、或者只想从某个版本摘掉插件，
  用这个脚本（不重跑 install.ps1，不动 relay / AI 配置 / 安装目录）。

  -List        只看本机宿主和各版本接线状态
  -Add 2       把插件接到第 2 个版本（可写 1,3）
  -Remove 2    只从第 2 个版本摘掉（Hank.lsp / acad.lsp 行 / Applications 注册）
  不带参数      交互菜单
.EXAMPLE
  tools\manage-hosts.bat
  powershell -ExecutionPolicy Bypass -File tools\manage-hosts.ps1 -Remove 2
.NOTES
  2026-11-24 新建。写 HKLM 需要管理员；卸载/加装只影响所选宿主。
  接线实现与 install.ps1 共用 tools/host-wiring.ps1，不另写一份。
#>
param([string]$Add = "", [string]$Remove = "", [switch]$List)

$ErrorActionPreference = "Stop"
$ROOT = Split-Path -Parent $PSScriptRoot
$script:NewAcadRoot = $ROOT
. (Join-Path $PSScriptRoot "host-wiring.ps1")

$script:PASS = 0; $script:WARN = 0
function Ok { param($m) $script:PASS++; Write-Host "[OK] $m" -ForegroundColor Green }
function Wn { param($m) $script:WARN++; Write-Host "[WARN] $m" -ForegroundColor Yellow }

$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$script:HostList = @(Get-NewAcadHosts -Root $ROOT)
if ($script:HostList.Count -eq 0) {
  Write-Host "未检测到 Autodesk AutoCAD / Civil 3D（注册表 HKLM\SOFTWARE\Autodesk\AutoCAD 下无产品）。" -ForegroundColor Yellow
  exit 1
}

function Show-Hosts {
  Write-Host ""
  Write-Host "本机 Autodesk 宿主：" -ForegroundColor Cyan
  $i = 0
  foreach ($h in $script:HostList) {
    $i++
    $st = Get-HostWiringStatus $h
    $seg = Get-HostSegmentName $h
    $state = if ($st.wired) { "已接线" } elseif ($st.registry -or $st.hankLsp) { "半接线(异常)" } else { "未接线" }
    $sup = if ($h.supported -eq $false) { "  [不支持: 需 2025+]" } else { "" }
    Write-Host ("  [{0}] {1}  ({2})  段={3}  状态={4}{5}" -f $i, $h.productName, $h.rName, $seg, $state, $sup) -ForegroundColor Gray
  }
  Write-Host ""
}

function Invoke-Add([string]$sel) {
  if (-not $isAdmin) { Wn "写注册表需要管理员权限 —— 请右键以管理员身份运行"; return }
  $targets = @(Resolve-HostSelection -AllHosts $script:HostList -Selection $sel)
  if ($targets.Count -eq 0) { Wn "没有选中任何宿主（编号 1-$($script:HostList.Count)）"; return }
  foreach ($h in $targets) {
    if ($h.supported -eq $false) {
      Wn "$($h.productName) 版本不受支持（插件是 .NET 8/10 构建，2024 及更早的 AutoCAD 加载不了），仍按选择写入"
    }
    Install-HostAutoload $h
  }
}

function Invoke-Remove([string]$sel) {
  if (-not $isAdmin) { Wn "写注册表需要管理员权限 —— 请右键以管理员身份运行"; return }
  $targets = @(Resolve-HostSelection -AllHosts $script:HostList -Selection $sel)
  if ($targets.Count -eq 0) { Wn "没有选中任何宿主（编号 1-$($script:HostList.Count)）"; return }
  foreach ($h in $targets) { Remove-HostWiring $h | Out-Null }
}

if ($List) {
  Show-Hosts
  exit 0
}

if ($Add) { Invoke-Add $Add }
elseif ($Remove) { Invoke-Remove $Remove }
else {
  Write-Host "new-acad 宿主管理" -ForegroundColor Cyan
  Write-Host "  加装 / 单版本卸载只动所选宿主，不碰 relay、AI 配置和安装目录"
  while ($true) {
    Show-Hosts
    Write-Host "  a <编号> = 加装到该版本    r <编号> = 只卸载该版本    q = 退出" -ForegroundColor Yellow
    $cmd = (Read-Host "  操作").Trim()
    if (-not $cmd -or $cmd -match '^[qQ]$') { break }
    if ($cmd -match '^[aA]\s*(.+)$') { Invoke-Add $matches[1].Trim(); continue }
    if ($cmd -match '^[rR]\s*(.+)$') { Invoke-Remove $matches[1].Trim(); continue }
    Write-Host "  看不懂: $cmd（示例：a 2 / r 1 / q）" -ForegroundColor Darkyellow
  }
}

Write-Host ""
Write-Host ("结果: $script:PASS 项完成, $script:WARN 项警告") -ForegroundColor Cyan
if ($script:WARN -gt 0) { exit 1 } else { exit 0 }
