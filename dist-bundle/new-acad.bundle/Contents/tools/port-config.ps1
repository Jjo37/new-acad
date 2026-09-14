# port-config.ps1 — new-acad 端口配置向导（双击 port-config.bat 或右键"使用 PowerShell 运行"）
# 改 config.json 的 bridgePort/relayPort/mcpPort —— 插件/relay 都读这个文件，改一处全对齐
# 改完必须重启 C3D（插件启动时绑定端口）+ 重启 relay 才生效
$ErrorActionPreference = "Stop"
$ROOT = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { "D:\new-acad" }
$CONFIG = "$ROOT\config.json"

Write-Host ""
Write-Host "===== new-acad 端口配置向导 =====" -ForegroundColor Cyan
Write-Host "配置文件: $CONFIG" -ForegroundColor Gray
Write-Host "说明: 插件 / Relay / MCP 都从 config.json 读端口，改这里一处即可。" -ForegroundColor Gray
Write-Host "      改完必须【重启 C3D + 重启 relay】才生效。" -ForegroundColor Gray
Write-Host ""

# 读当前配置（不存在则用默认值）
$cfg = $null
if (Test-Path $CONFIG) {
  try { $cfg = Get-Content $CONFIG -Raw -Encoding UTF8 | ConvertFrom-Json } catch {
    Write-Host "  [错误] config.json 解析失败: $_" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
  }
} else {
  Write-Host "  [注意] 未找到 config.json，将以默认值创建" -ForegroundColor Yellow
}
if (-not $cfg) { $cfg = [pscustomobject]@{} }

$curBridge = if ($cfg.bridgePort) { [int]$cfg.bridgePort } else { 8080 }
$curRelay  = if ($cfg.relayPort)  { [int]$cfg.relayPort  } else { 19876 }
$curMcp    = if ($cfg.mcpPort)    { [int]$cfg.mcpPort    } else { 3000 }

Write-Host ("  当前: 插件(C3D)={0}  Relay={1}  MCP={2}" -f $curBridge, $curRelay, $curMcp) -ForegroundColor White

function Read-Port([string]$label, [int]$current) {
  while ($true) {
    $input = Read-Host ("  {0} 端口 [当前 {1}，回车保持不变]:" -f $label, $current)
    if ([string]::IsNullOrWhiteSpace($input)) { return $current }
    if ($input -notmatch '^\d+$' -or [int]$input -lt 1 -or [int]$input -gt 65535) {
      Write-Host "    端口必须是 1~65535 的数字" -ForegroundColor Yellow
      continue
    }
    $p = [int]$input
    # 占用检测（警告但允许，用户可能就要换走被占端口）
    $listening = @(Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
    if ($listening.Count -gt 0 -and $p -ne $current) {
      $proc = Get-Process -Id $listening[0].OwningProcess -ErrorAction SilentlyContinue
      $pn = if ($proc) { $proc.ProcessName } else { "未知" }
      $ans = Read-Host ("    端口 {0} 已被 {1} (PID {2}) 占用！仍要使用吗? [y/N]" -f $p, $pn, $listening[0].OwningProcess)
      if ($ans -notmatch '^[yY]') { continue }
    }
    return $p
  }
}

$newBridge = Read-Port "插件(C3D)" $curBridge
$newRelay  = Read-Port "Relay"     $curRelay
$newMcp    = Read-Port "MCP"       $curMcp

if ($newBridge -eq $curBridge -and $newRelay -eq $curRelay -and $newMcp -eq $curMcp) {
  Write-Host ""
  Write-Host "端口未变化，无需保存。" -ForegroundColor Gray
  Read-Host "按回车退出"
  exit 0
}

# 写回 config.json（保留其他所有字段）
$cfg.bridgePort = $newBridge
$cfg.relayPort  = $newRelay
$cfg.mcpPort    = $newMcp
try {
  $json = $cfg | ConvertTo-Json -Depth 6
  # PowerShell 5.1 的 ConvertTo-Json 会转义非 ASCII 为 \uXXXX（可正常解析，无碍）
  [System.IO.File]::WriteAllText($CONFIG, $json, (New-Object System.Text.UTF8Encoding $true))
  Write-Host ""
  Write-Host ("  [OK] 已保存: 插件={0}  Relay={1}  MCP={2}" -f $newBridge, $newRelay, $newMcp) -ForegroundColor Green
  Write-Host ""
  Write-Host "  ===== 下一步（必须做）=====" -ForegroundColor Cyan
  Write-Host "  1. 关闭 C3D 重新打开 —— 插件启动时读取新端口" -ForegroundColor White
  Write-Host "  2. 重启 relay（关闭 relay 窗口或重启电脑；守护模式会自动拉起）" -ForegroundColor White
  Write-Host "  3. 双击 tools\port-check.bat 确认新端口正常" -ForegroundColor White
} catch {
  Write-Host "  [错误] 保存失败: $_" -ForegroundColor Red
}
Write-Host ""
Read-Host "按回车退出"
