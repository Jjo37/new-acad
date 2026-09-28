# sync-dev-release.ps1 — 把 dev 仓的可分发文件同步到 release 仓（显式清单，默认干跑）
#
# 用法：
#   powershell -File tools/sync-dev-release.ps1              # 干跑：只报告差异
#   powershell -File tools/sync-dev-release.ps1 -Apply       # 实际复制
#
# 原则：**绝不删除 release 仓里的东西**（产物、历史安装包等都保留）；只按清单覆盖/新增。
# 不入清单的东西（内部文档、源码、日志、锁文件、产物）永远不会被同步。
param(
  [string]$From = "D:\new-acad",
  [string]$To   = "D:\new-acad-release",
  [switch]$Apply
)

$ErrorActionPreference = 'Stop'

# ---- 1) 单文件清单（相对路径）----
$files = @(
  # 版本与用户文档
  'package.json',
  'README.md','ONBOARDING.md','POSITIONING.md','PRIVACY.md','workflow-guide.md','使用手册.html',
  # 运行脚本
  'install.ps1','uninstall.ps1','uninstall-pre.ps1','config.json','relay-launcher.js',
  # 构建脚本
  'build-dist.ps1','build-installer.ps1','build-plugin.ps1','build-plugin.bat','.gitignore',
  # 插件产物（只同步 DLL 三件套，不同步 src/）
  'plugin/Hank.lsp',
  'plugin/AcBridge-v24/Civil3DMcpPlugin.dll',
  'plugin/AcBridge-v24/Civil3DMcpPlugin.deps.json',
  'plugin/AcBridge-v24/Civil3DMcpPlugin.runtimeconfig.json',
  # 服务端运行时
  'server/panel-relay.js','server/llm-agent.js','server/compact.js','server/cad-tools.js',
  'server/file-tools.js','server/trace-image.js','server/relay-launcher.js','server/sacred-mcp.js',
  'server/memory/knowledge.md',
  # 知识库白名单（被运行时引用 + 面向用户）
  'knowledge/api-inventory.md','knowledge/c3d-api-refs.md','knowledge/civil3d-objects.md',
  'knowledge/image-to-cad.md','knowledge/method-description-spec.md','knowledge/sac-xaml-guide.md',
  # 发布工具
  'tools/build-bundle.ps1','tools/publish-public.ps1','tools/publish-release.ps1',
  'tools/find-civil3d.ps1','tools/locale.ps1','tools/clean-runtime.ps1',
  'tools/port-check.ps1','tools/port-check.bat','tools/port-config.ps1','tools/port-config.bat',
  'tools/sync-dev-release.ps1',
  'tools/scan-release-leaks.js','tools/push-via-api.js'
)

# ---- 2) 目录清单（递归，带排除）----
$dirs = @(
  'server',
  'knowledge/sac-templates',
  'server/mcp/build',
  'tools/public-templates',
  'tools/bundle'
)
# 目录同步时排除：产物/源码/日志/锁/依赖
$excludePatterns = @(
  '\.log$','\.lock$','-inbox\.json$','\.pdb$','\.zip$','\.exe$',
  '\\node_modules\\','\\bin\\','\\obj\\','\\src\\','\\C_References\\','\\\.vs\\','\\dist'
)

function Rel([string]$base, [string]$full) {
  return $full.Substring($base.Length).TrimStart('\','/') -replace '\\','/'
}
function Same([string]$a, [string]$b) {
  if (-not (Test-Path $a) -or -not (Test-Path $b)) { return $false }
  $ha = (Get-FileHash $a -Algorithm SHA1).Hash
  $hb = (Get-FileHash $b -Algorithm SHA1).Hash
  return ($ha -eq $hb)
}

$plan = @()
foreach ($f in $files) {
  $src = Join-Path $From $f
  if (-not (Test-Path $src)) { $plan += [pscustomobject]@{ Action='MISSING-IN-DEV'; Path=$f }; continue }
  $dst = Join-Path $To $f
  $action = if (-not (Test-Path $dst)) { 'NEW' } elseif (Same $src $dst) { 'SAME' } else { 'CHANGED' }
  $plan += [pscustomobject]@{ Action=$action; Path=$f }
}
foreach ($d in $dirs) {
  $srcDir = Join-Path $From $d
  if (-not (Test-Path $srcDir)) { $plan += [pscustomobject]@{ Action='MISSING-IN-DEV'; Path=$d }; continue }
  Get-ChildItem $srcDir -Recurse -File | ForEach-Object {
    $rel = Join-Path $d (Rel $srcDir $_.FullName)
    $skip = $false
    foreach ($p in $excludePatterns) { if ($_.FullName -match $p) { $skip = $true; break } }
    if ($skip) { return }
    $dst = Join-Path $To $rel
    $action = if (-not (Test-Path $dst)) { 'NEW' } elseif (Same $_.FullName $dst) { 'SAME' } else { 'CHANGED' }
    $plan += [pscustomobject]@{ Action=$action; Path=$rel }
  }
}

$toSync = $plan | Where-Object { $_.Action -in @('NEW','CHANGED') }
$mode = if ($Apply) { '（执行）' } else { '（干跑）' }
Write-Host "===== dev → release 同步$mode =====" -ForegroundColor Cyan
Write-Host ("清单文件 {0} 个 + 目录 {1} 个；需同步 {2}，相同 {3}，dev 缺失 {4}" -f `
  $files.Count, $dirs.Count, $toSync.Count, ($plan | Where-Object Action -eq 'SAME').Count, ($plan | Where-Object Action -eq 'MISSING-IN-DEV').Count)
if ($toSync.Count -gt 0) {
  Write-Host "`n-- 待同步 --" -ForegroundColor Yellow
  $toSync | Sort-Object Path | ForEach-Object { Write-Host ("  [{0}] {1}" -f $_.Action, $_.Path) }
}
$missing = $plan | Where-Object Action -eq 'MISSING-IN-DEV'
if ($missing.Count -gt 0) { Write-Host "`n-- dev 缺失（清单项不存在）--" -ForegroundColor Red; $missing | ForEach-Object { Write-Host ("  " + $_.Path) } }

if (-not $Apply) {
  Write-Host "`n干跑结束。加 -Apply 才会复制。" -ForegroundColor Cyan
  exit 0
}

foreach ($item in $toSync) {
  $src = Join-Path $From $item.Path
  $dst = Join-Path $To $item.Path
  $dstDir = Split-Path $dst -Parent
  if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
  Copy-Item $src $dst -Force
}
Write-Host ("`n[OK] 已同步 {0} 个文件到 {1}" -f $toSync.Count, $To) -ForegroundColor Green

# release 仓的记忆骨架必须中性（dev 副本含开发者个人偏好）
$mem = Join-Path $To 'server\memory\agent-memory.md'
if (Test-Path $mem) {
  $neutral = @'
# new-acad LLM Agent 长期记忆

<!-- 准入原则（最高优先）：只存"规则类 / 用户习惯 / 工具方法"。
     具体任务数据、图纸内容、一次性上下文（算量结果 / 选中集 / 某张图的处理）禁止写入——会污染记忆。
     由 AI 通过 writeMemory 覆盖式维护；写入前先 readMemory，保留仍有价值的旧条目。 -->

## 用户偏好
（空。由 AI 在对话中逐步积累。）

## 工具方法
（空。）
'@
  [System.IO.File]::WriteAllText($mem, $neutral, (New-Object System.Text.UTF8Encoding $true))
  Write-Host "[OK] release/server/memory/agent-memory.md 已置为中性骨架"
}
