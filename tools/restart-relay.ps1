# restart-relay.ps1 — 让 relay 按 config.json 的 relayPort 重新起来（2026-11-05）
#
# 为什么需要它：relay 只在启动那一刻读 config.json。改了 relayPort 却不重启 relay，
# 实际还在旧端口上跑，而面板/插件按 config 连新端口 -> SYN_SENT 一直重试 -> 用户看到「连不上」。
# 这个脚本先问「运行态在哪个端口」（server\.relay-runtime.json + 试连兜底），再优雅重启、等新端口起来。
#
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools\restart-relay.ps1 [-TimeoutSec 20] [-Quiet]
# 退出码: 0 = 已在目标端口 / 已切过去; 1 = 失败（打印原因）

param(
  [int]$TimeoutSec = 20,
  [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ROOT = Split-Path $PSScriptRoot -Parent
$CONFIG = Join-Path $ROOT 'config.json'
$RUNTIME = Join-Path $ROOT 'server\.relay-runtime.json'

function Say([string]$msg, [string]$color = 'Gray') {
  if (-not $Quiet) { Write-Host $msg -ForegroundColor $color }
}

# .NET 对「没在听」的端口要约 2s 才抛 ConnectionRefused（2026-11-05 实测），预算给够。
function Test-Tcp([int]$port) {
  $client = New-Object System.Net.Sockets.TcpClient
  try {
    $task = $client.ConnectAsync([System.Net.IPAddress]::Loopback, $port)
    if (-not $task.Wait(2500)) { return $false }
    return $client.Connected
  } catch {
    return $false
  } finally {
    $client.Dispose()
  }
}

function Get-ConfigRelayPort {
  if (-not (Test-Path $CONFIG)) { throw "找不到 config.json: $CONFIG" }
  $cfg = Get-Content $CONFIG -Raw -Encoding UTF8 | ConvertFrom-Json
  $p = 0
  if ($cfg.relayPort) { $p = [int]$cfg.relayPort }
  if ($p -le 0 -or $p -gt 65535) { $p = 19876 }
  return $p
}

function Get-RuntimeRelayPort {
  # 运行态端口：relay 启动时落盘（2026-11-05 新增）。文件过期/丢失就返回 0。
  if (-not (Test-Path $RUNTIME)) { return 0 }
  try {
    $j = Get-Content $RUNTIME -Raw -Encoding UTF8 | ConvertFrom-Json
    $p = [int]$j.port
    if ($p -gt 0 -and $p -le 65535) { return $p }
  } catch {
    return 0
  }
  return 0
}

function Invoke-RelayRestart([int]$port) {
  try {
    Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/restart" -f $port) -Method POST -UseBasicParsing -TimeoutSec 5 | Out-Null
    return $true
  } catch {
    Say ("  [relay] POST :{0}/restart 失败: {1}" -f $port, $_.Exception.Message) 'Yellow'
    return $false
  }
}

$target = Get-ConfigRelayPort
Say ("  [relay] config.json 的 relayPort = {0}" -f $target) 'Gray'

$runtime = Get-RuntimeRelayPort
$live = 0
if ($runtime -gt 0 -and (Test-Tcp $runtime)) {
  $live = $runtime
} elseif (Test-Tcp $target) {
  $live = $target
}

if ($live -eq $target) {
  Say ("  [OK] relay 已经在 :{0} 跑，无需重启" -f $target) 'Green'
  exit 0
}

if ($live -eq 0) {
  Say ("  [!] :{0} 上没有 relay 在听。relay 没起来？" -f $target) 'Yellow'
  Say ("      启动方式：node server\relay-launcher.js（守护会在 3s 后拉起 relay）") 'Gray'
  exit 1
}

Say ("  [relay] 运行态在 :{0}，config 要 :{1} -> 重启 relay" -f $live, $target) 'Yellow'
if (-not (Invoke-RelayRestart $live)) {
  Say ("  [FAIL] 没能在 :{0} 上触发重启（relay 可能已经退出，或不是本项目的 relay）" -f $live) 'Red'
  exit 1
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 500
  if (Test-Tcp $target) {
    if ($live -eq $target -or -not (Test-Tcp $live)) {
      Say ("  [OK] relay 已切到 :{0}（原 :{1} 已释放）" -f $target, $live) 'Green'
      exit 0
    }
  }
}

Say ("  [FAIL] 等了 {0}s，:{1} 还没起来。看 server\relay-launcher.log" -f $TimeoutSec, $target) 'Red'
exit 1
