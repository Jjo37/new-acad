# port-check.ps1 — new-acad 端口一键检测（双击 port-check.bat 或右键"使用 PowerShell 运行"）
# 检测: 8080(插件) / 19876(relay) / 3000(MCP) / 18789(Gateway, 本机开发用)
# 输出: 每端口 监听状态 + 占用进程；自动给结论（谁在线/谁被抢/怎么修）
# 2026-09-21: 输出双语（跟随 config.json 的 locale，auto=系统语言），见 tools\locale.ps1
$ErrorActionPreference = "SilentlyContinue"
# 脚本位于 <根>\tools\，项目根 = 脚本目录的父级
$ROOT = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { "D:\new-acad" }

. (Join-Path $PSScriptRoot "locale.ps1")
Initialize-NaLocale $ROOT | Out-Null

Write-Host ""
Write-Host (T "===== new-acad 端口检测 =====" "===== new-acad Port Check =====") -ForegroundColor Cyan

# 读取 config.json 配置的端口（插件也读这个文件，改这里全对齐）
$cfg = @{}
if (Test-Path "$ROOT\config.json") {
  try { $cfg = Get-Content "$ROOT\config.json" -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Write-Host ("  " + (T "[注意] config.json 解析失败: $_" "[warn] config.json parse failed: $_")) -ForegroundColor Yellow }
}
$cfgBridge = if ($cfg.bridgePort) { $cfg.bridgePort } else { 8080 }
$cfgRelay  = if ($cfg.relayPort)  { $cfg.relayPort  } else { 19876 }
$cfgMcp    = if ($cfg.mcpPort)    { $cfg.mcpPort    } else { 3000 }

Write-Host ((T "  配置(config.json): 插件 {0} | Relay {1} | MCP {2}" "  Config (config.json): Plugin {0} | Relay {1} | MCP {2}") -f $cfgBridge, $cfgRelay, $cfgMcp) -ForegroundColor Gray

# 端口定义: 名称 / 端口 / 期望进程（判断"被抢"）
$ports = @(
  @{ Name = (T "C3D 插件" "C3D Plugin"); Port = [int]$cfgBridge; Expect = @('acad','civil3d') },
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
    $procName = if ($proc) { $proc.ProcessName } else { (T "未知" "unknown") }
    $expected = $procName -in $p.Expect
    if ($expected) {
      Write-Host ((T "  [OK]  {0,-6} {1,-12} 监听中  PID {2} ({3})" "  [OK]  {0,-6} {1,-12} listening  PID {2} ({3})") -f $p.Port, $p.Name, $conn.OwningProcess, $procName) -ForegroundColor Green
    } else {
      $allOk = $false
      Write-Host ((T "  [!!]  {0,-6} {1,-12} 被 {2} 占用 (PID {3}) —— 不是本服务进程，可能端口被抢！" "  [!!]  {0,-6} {1,-12} occupied by {2} (PID {3}) - not our service, the port may have been taken!") -f $p.Port, $p.Name, $procName, $conn.OwningProcess) -ForegroundColor Red
    }
  } else {
    # 未监听: 试连确认（能连上=异常占用；连不上=空闲）
    $client = New-Object System.Net.Sockets.TcpClient
    try {
      $iar = $client.BeginConnect("127.0.0.1", $p.Port, $null, $null)
      $ok = $iar.AsyncWaitHandle.WaitOne(300, $false)
      if ($ok -and $client.Connected) {
        $allOk = $false
        Write-Host ((T "  [!!]  {0,-6} {1,-12} 端口被占用(非监听态) —— 有程序占着但未正常监听" "  [!!]  {0,-6} {1,-12} port in use (not listening) - something holds it without a normal listener") -f $p.Port, $p.Name) -ForegroundColor Red
      } else {
        Write-Host ((T "  [--]  {0,-6} {1,-12} 未启动（空闲，服务没跑属正常）" "  [--]  {0,-6} {1,-12} not started (free; normal when the service is off)") -f $p.Port, $p.Name) -ForegroundColor DarkGray
      }
    } catch {
      Write-Host ((T "  [--]  {0,-6} {1,-12} 未启动（空闲）" "  [--]  {0,-6} {1,-12} not started (free)") -f $p.Port, $p.Name) -ForegroundColor DarkGray
    } finally {
      $client.Close()
    }
  }
}

Write-Host ""
Write-Host (T "===== 结论 =====" "===== Verdict =====") -ForegroundColor Cyan
# 2026-09-21 fix: 原来写的是 `-LocalPort [int]$cfgBridge` —— PowerShell 在**命令参数位置**不做 [int]
# 转换（会当字面量传给参数 → 参数转换报错 → 计数恒为 0），导致结论永远误判成"C3D 未开"。
# 先落到变量（语言表达式位置才会真正转换）再用。
$bridgePort = [int]$cfgBridge
$relayPort  = [int]$cfgRelay
$pluginUp = @(Get-NetTCPConnection -LocalPort $bridgePort -State Listen -ErrorAction SilentlyContinue).Count -gt 0
$relayUp  = @(Get-NetTCPConnection -LocalPort $relayPort  -State Listen -ErrorAction SilentlyContinue).Count -gt 0
if ($allOk) {
  if ($pluginUp -and $relayUp) {
    Write-Host (T "  全部正常：插件 + Relay 在线，可以正常使用。" "  All good: plugin + Relay online, ready to use.") -ForegroundColor Green
  } elseif (-not $pluginUp) {
    Write-Host (T "  正常：Relay 在线；C3D 当前未开，插件未启动（打开 C3D 即自动加载）。" "  OK: Relay online; C3D is not running, plugin not loaded (it loads automatically when C3D starts).") -ForegroundColor Green
  } else {
    Write-Host (T "  正常：插件在线；Relay 未启动（检查 relay-launcher 是否在运行）。" "  OK: plugin online; Relay not running (check whether relay-launcher is running).") -ForegroundColor Green
  }
} else {
  Write-Host (T "  发现问题端口（红色 !!），按下面处理：" "  Problems found (red !!). Fix them as follows:") -ForegroundColor Yellow
  Write-Host (T "  1. 端口被其他程序占用 → 关闭该程序，或双击 tools\port-config.bat 改端口" "  1. Port taken by another program -> close it, or run tools\port-config.bat to change ports") -ForegroundColor Yellow
  Write-Host (T "  2. 端口空闲但服务没起 → 开 C3D（插件）/ 启动 relay（relay-launcher）" "  2. Port free but service down -> start C3D (plugin) / start relay (relay-launcher)") -ForegroundColor Yellow
  Write-Host (T "  3. 改完端口必须重启 C3D + relay 才生效" "  3. After changing ports you must restart C3D + relay for the change to take effect") -ForegroundColor Yellow
}
Write-Host ""
