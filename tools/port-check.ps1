# port-check.ps1 — new-acad 端口一键检测（双击 port-check.bat 或右键"使用 PowerShell 运行"）
# 检测: 8080(插件) / 19876(relay) / 3000(MCP) / 18789(Gateway, 本机开发用)
# 输出: 每端口 监听状态 + 占用进程；自动给结论（谁在线/谁被抢/怎么修）
$ErrorActionPreference = "SilentlyContinue"
# 脚本位于 <根>\tools\，项目根 = 脚本目录的父级
$ROOT = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { "D:\new-acad" }

Write-Host ""
Write-Host "===== new-acad 端口检测 =====" -ForegroundColor Cyan

# 读取 config.json 配置的端口（插件也读这个文件，改这里全对齐）
$cfg = @{}
if (Test-Path "$ROOT\config.json") {
  try { $cfg = Get-Content "$ROOT\config.json" -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Write-Host "  [注意] config.json 解析失败: $_" -ForegroundColor Yellow }
}
$cfgBridge = if ($cfg.bridgePort) { $cfg.bridgePort } else { 8080 }
$cfgRelay  = if ($cfg.relayPort)  { $cfg.relayPort  } else { 19876 }
$cfgMcp    = if ($cfg.mcpPort)    { $cfg.mcpPort    } else { 3000 }

Write-Host ("  配置(config.json): 插件 {0} | Relay {1} | MCP {2}" -f $cfgBridge, $cfgRelay, $cfgMcp) -ForegroundColor Gray

# 端口定义: 名称 / 端口 / 期望进程（判断"被抢"）
$ports = @(
  @{ Name = "C3D 插件"; Port = [int]$cfgBridge; Expect = @('acad','civil3d') },
  @{ Name = "Relay";    Port = [int]$cfgRelay;  Expect = @('node') },
  @{ Name = "MCP";      Port = [int]$cfgMcp;    Expect = @('node') },
  @{ Name = "Gateway";  Port = 18789;           Expect = @('node') }
)

$allOk = $true
foreach ($p in $ports) {
  $listening = @(Get-NetTCPConnection -LocalPort $p.Port -State Listen -ErrorAction SilentlyContinue)
  if ($listening.Count -gt 0) {
    $conn = $listening[0]
    $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    $procName = if ($proc) { $proc.ProcessName } else { "未知" }
    $expected = $procName -in $p.Expect
    if ($expected) {
      Write-Host ("  [OK]  {0,-6} {1,-10} 监听中  PID {2} ({3})" -f $p.Port, $p.Name, $conn.OwningProcess, $procName) -ForegroundColor Green
    } else {
      $allOk = $false
      Write-Host ("  [!!]  {0,-6} {1,-10} 被 {2} 占用 (PID {3}) —— 不是本服务进程，可能端口被抢！" -f $p.Port, $p.Name, $procName, $conn.OwningProcess) -ForegroundColor Red
    }
  } else {
    # 未监听: 试连确认（能连上=异常占用；连不上=空闲）
    $client = New-Object System.Net.Sockets.TcpClient
    try {
      $iar = $client.BeginConnect("127.0.0.1", $p.Port, $null, $null)
      $ok = $iar.AsyncWaitHandle.WaitOne(300, $false)
      if ($ok -and $client.Connected) {
        $allOk = $false
        Write-Host ("  [!!]  {0,-6} {1,-10} 端口被占用(非监听态) —— 有程序占着但未正常监听" -f $p.Port, $p.Name) -ForegroundColor Red
      } else {
        Write-Host ("  [--]  {0,-6} {1,-10} 未启动（空闲，服务没跑属正常）" -f $p.Port, $p.Name) -ForegroundColor DarkGray
      }
    } catch {
      Write-Host ("  [--]  {0,-6} {1,-10} 未启动（空闲）" -f $p.Port, $p.Name) -ForegroundColor DarkGray
    } finally {
      $client.Close()
    }
  }
}

Write-Host ""
Write-Host "===== 结论 =====" -ForegroundColor Cyan
$pluginUp = @(Get-NetTCPConnection -LocalPort [int]$cfgBridge -State Listen -ErrorAction SilentlyContinue).Count -gt 0
$relayUp   = @(Get-NetTCPConnection -LocalPort [int]$cfgRelay  -State Listen -ErrorAction SilentlyContinue).Count -gt 0
if ($allOk) {
  if ($pluginUp -and $relayUp) {
    Write-Host "  全部正常：插件 + Relay 在线，可以正常使用。" -ForegroundColor Green
  } elseif (-not $pluginUp) {
    Write-Host "  正常：Relay 在线；C3D 当前未开，插件未启动（打开 C3D 即自动加载）。" -ForegroundColor Green
  } else {
    Write-Host "  正常：插件在线；Relay 未启动（检查 relay-launcher 是否在运行）。" -ForegroundColor Green
  }
} else {
  Write-Host "  发现问题端口（红色 !!），按下面处理：" -ForegroundColor Yellow
  Write-Host "  1. 端口被其他程序占用 → 关闭该程序，或双击 tools\port-config.bat 改端口" -ForegroundColor Yellow
  Write-Host "  2. 端口空闲但服务没起 → 开 C3D（插件）/ 启动 relay（relay-launcher）" -ForegroundColor Yellow
  Write-Host "  3. 改完端口必须重启 C3D + relay 才生效" -ForegroundColor Yellow
}
Write-Host ""
