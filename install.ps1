<#
.SYNOPSIS
  new-acad 一键部署脚本 — 环境检查 + 自动加载 + MCP/Relay 自启
.NOTES
  2026-08-03 重写：修复编码（原文件 UTF-8 被 GBK 误读成乱码），逻辑不变
  2026-09-11: C3D 路径改注册表动态检测（tools/find-civil3d.ps1），支持 2024/2025/2026
#>

$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = "D:\new-acad" }
$PLUGIN_DLL = "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.dll"
$STARTUP = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
# P3: 优先用包内 node.exe（便携版），否则用 PATH 里的 node
$NODE = if (Test-Path "$ROOT\node.exe") { "$ROOT\node.exe" } else { "node" }

$PASS = 0; $FAIL = 0; $WARN = 0
function Ok  { $script:PASS++; "[PASS] $args" }
function Ng  { $script:FAIL++; "[FAIL] $args" }
function Wn  { $script:WARN++; "[WARN] $args" }

# P2.5: 统一配置入口 — 端口/密钥从 config.json 读（环境变量可覆盖）
$CONFIG = "$ROOT\config.json"
$cfg = [pscustomobject]@{ bridgePort=8080; mcpPort=3000; relayPort=19876; relayToken=""; relaySessionKey=""; locale="auto"; relayMode="llm"; llm=[pscustomobject]@{ provider=""; baseUrl=""; apiKey=""; model=""; maxTurns=20; timeoutMs=120000 } }
if (Test-Path $CONFIG) {
  try { $cfg = Get-Content $CONFIG -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Wn "config.json 解析失败: $_" }
}

Write-Host "`n===== new-acad 环境检查 =====`n" -ForegroundColor Cyan

# ---------- 1. 目录完整性 ----------
Write-Host "--- 1/9 目录完整性 ---" -ForegroundColor Yellow
if (Test-Path $ROOT) { Ok "项目目录: $ROOT" } else { Ng "项目目录不存在"; exit 1 }
if (Test-Path $PLUGIN_DLL) { Ok "插件 DLL: $((Get-Item $PLUGIN_DLL).Length) 字节" } else { Ng "插件 DLL 不存在" }
if (Test-Path "$ROOT\server\panel-relay.js") { Ok "Relay: panel-relay.js" } else { Ng "Relay 缺失" }
if (Test-Path "$ROOT\server\sacred-mcp.js") { Ok "MCP 启动器: sacred-mcp.js" } else { Ng "MCP 启动器缺失" }
if (Test-Path "$ROOT\server\mcp\build\index.js") { Ok "MCP 核心: mcp/build/index.js" } else { Ng "MCP 核心缺失" }

# 1.5 运行时写权限（2026-08-25 安装器适配：Program Files 场景下 relay/面板需写 exchange/ 和 config.json）
# 以管理员运行时给 $ROOT 加 Users 修改权限，标准用户也能写运行时文件；zip 解压到用户目录时无副作用
if ((New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  try {
    icacls $ROOT /grant "*S-1-5-32-545:(OI)(CI)M" /T /Q | Out-Null
    Ok "运行时写权限: Users 组已获得 $ROOT 修改权限（exchange/config 可写）"
  } catch { Wn "运行时写权限设置失败: $_" }
}

# ---------- 2. C3D 是否安装（P2: 注册表动态检测）----------
Write-Host "`n--- 2/9 C3D 安装检查 ---" -ForegroundColor Yellow
try { $c3d = & "$ROOT\tools\find-civil3d.ps1" | ConvertFrom-Json } catch { $c3d = [pscustomobject]@{ found = $false }; Wn "C3D 检测脚本异常: $_" } # 2026-08-14 P2-4
if ($c3d.found) {
  Ok "C3D $($c3d.year) 安装: $($c3d.installDir)"
  if (Test-Path "$($c3d.installDir)acad.exe") { Ok "acad.exe 就绪" } else { Wn "acad.exe 未找到（目录存在但无 acad.exe）" }
} else {
  Wn "未检测到 C3D（注册表 HKLM\SOFTWARE\Autodesk\AutoCAD 下无 Civil 3D 产品），用户需手动安装"
}

# ---------- 3. accoreconsole ----------
Write-Host "`n--- 3/9 accoreconsole ---" -ForegroundColor Yellow
if ($c3d.found -and (Test-Path "$($c3d.installDir)accoreconsole.exe")) {
  Ok "accoreconsole 就绪"
} else {
  Wn "accoreconsole 未找到（非必需，部分功能受限）"
}

# ---------- 4. Node.js ----------
Write-Host "`n--- 4/9 Node.js ---" -ForegroundColor Yellow
try {
  $nodeVer = & $NODE --version
  $nodeMajor = [int]((($nodeVer -replace '^v','') -split '\.')[0])  # 2026-08-14 P1-4: 修 v16.20.2->16202 误判
  if ($nodeMajor -ge 18) { Ok "Node.js $nodeVer" } else { Wn "Node.js $nodeVer（建议 v18+）" }
} catch { Ng "Node.js 未安装，请先安装: https://nodejs.org" }

# ---------- 5. .NET SDK ----------
Write-Host "`n--- 5/9 .NET SDK ---" -ForegroundColor Yellow
try {
  $dotnetVer = dotnet --version
  if ($dotnetVer -ge 6) { Ok ".NET SDK $dotnetVer" } else { Wn ".NET SDK $dotnetVer（建议 v6+）" }
} catch { Wn ".NET SDK 未安装（非必需，仅 C# 源码才需要）" }
# 5b. .NET 8 Desktop Runtime 检查（2026-08-14 P0: 插件是 net8.0-windows, 异机演示前置; AutoCAD 2025 可能自带）
try {
  $desktop8 = dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 8\.'
  if ($desktop8) { Ok ".NET 8 Desktop Runtime 就绪: $($desktop8[0].Line.Trim())" } else { Wn ".NET 8 Desktop Runtime 未检测到（插件需要 net8.0-windows；若 AutoCAD/C3D 2025 自带可忽略，否则安装 https://dotnet.microsoft.com/download/dotnet/8.0）" }
} catch { Wn ".NET Runtime 检测失败: $_" }

# ---------- 6. 端口 8080 (C3D 插件) ----------
Write-Host "`n--- 6/9 C3D 插件 (:8080) ---" -ForegroundColor Yellow
try {
  $tcp = [System.Net.Sockets.TcpClient]::new()
  $tcp.ConnectAsync('127.0.0.1', $cfg.bridgePort).Wait(3000) | Out-Null
  if ($tcp.Connected) { $tcp.Close(); Ok "C3D 插件在线 (:$($cfg.bridgePort))" } else { Wn "C3D 插件未响应（需打开 C3D）" }
} catch { Wn "C3D 插件未连接" }

# 启动后台服务: 直启 node.exe（不用 powershell -NoExit 宿主，避免卸载时句柄占用）
function Start-BackgroundNode($scriptPath) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $NODE
  $psi.Arguments = "`"$scriptPath`""
  $psi.WorkingDirectory = (Split-Path $scriptPath -Parent)
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  try { [System.Diagnostics.Process]::Start($psi) | Out-Null; return $true } catch { return $false }
}

# 可靠健康检测: 查端口监听（不依赖 HTTP 响应解析，PS5.1 兼容）
function Test-PortListening($port) {
  try {
    return [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
  } catch { return $false }
}

# ---------- 7. 端口 3000 (Sacred MCP) ----------
Write-Host "`n--- 7/9 Sacred MCP (:3000) ---" -ForegroundColor Yellow
if (Test-PortListening $cfg.mcpPort) {
  # 在线: 尝试取工具数（失败不影响，端口在就是活）
  try {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$($cfg.mcpPort)/health" -TimeoutSec 3 -UseBasicParsing
    $j = $r.Content | ConvertFrom-Json
    Ok "Sacred MCP 在线, $($j.registeredTools) 工具"
  } catch { Ok "Sacred MCP 在线 (:3000)" }
} else {
  Wn "MCP 未运行，正在尝试启动..."
  if (Start-BackgroundNode "$ROOT\server\sacred-mcp.js") {
    Start-Sleep 4
    if (Test-PortListening $cfg.mcpPort) {
      # 自愈成功: FAIL 回退（Bug5 修复）
      if ($script:FAIL -gt 0) { $script:FAIL-- }
      try {
        $r2 = Invoke-WebRequest -Uri "http://127.0.0.1:$($cfg.mcpPort)/health" -TimeoutSec 3 -UseBasicParsing
        $j2 = $r2.Content | ConvertFrom-Json
        Ok "MCP 已启动, $($j2.registeredTools) 工具"
      } catch { Ok "MCP 已启动 (:3000)" }
    } else { Ng "MCP 启动后仍无响应（检查 server\mcp\package.json 是否存在）" }
  } else { Ng "MCP 启动失败（检查 server\mcp\package.json 是否存在）" }
}

# ---------- 8. 端口 19876 (Panel Relay) ----------
Write-Host "`n--- 8/9 Panel Relay (:19876) ---" -ForegroundColor Yellow
if (Test-PortListening $cfg.relayPort) {
  Ok "Panel Relay 在线 (:19876)"
} else {
  Wn "Relay 未运行，正在启动..."
  if (Start-BackgroundNode "$ROOT\server\relay-launcher.js") {  # 2026-08-07: 改守护启动器, 崩溃自动重启
    Start-Sleep 3
    if (Test-PortListening $cfg.relayPort) { Ok "Panel Relay 已启动 (:19876)" }
    else { Ng "Relay 启动失败（检查 server\llm-agent.js / file-tools.js 等模块是否齐全）" }
  } else { Ng "Relay 启动失败" }
}

# ---------- 9. 自动加载设置 ----------
Write-Host "`n--- 9/9 自动加载设置 ---" -ForegroundColor Yellow

# 9a. Panel Relay 开机自启（2026-08-10: vbs 隐藏窗口 + 计划任务，替代启动文件夹 lnk）
# Relay 支持两种模式:
#   - 接 AI 网关: 设置 RELAY_TOKEN + RELAY_SESSION_KEY 环境变量
#   - 内置 LLM 模式（默认）: 仅用 inbox/reply 通用接口，任何 AI 都能用
# 旧版启动文件夹快捷方式清理（避免重复启动 + 修复历史空壳 lnk）
$lnk = "$STARTUP\AcBridge-Relay.lnk"
if (Test-Path $lnk) {
  try { Remove-Item $lnk -Force; Ok "Relay 开机自启: 已移除旧快捷方式" } catch { Wn "旧快捷方式清理失败: $_" }
}
# vbs 隐藏启动器（按当前安装路径重新生成，保证路径正确）
$vbs = "$ROOT\server\start-relay-hidden.vbs"
try {
  $vbsContent = @'
Set fso = CreateObject("Scripting.FileSystemObject")
Set ws = CreateObject("Wscript.Shell")
base = fso.GetParentFolderName(WScript.ScriptFullName)
root = fso.GetParentFolderName(base)
node = fso.BuildPath(root, "node.exe")
If Not fso.FileExists(node) Then node = "node"
cmd = """" & node & """" & " """ & base & "\relay-launcher.js"""
ws.Run cmd, 0, False
'@
  [System.IO.File]::WriteAllText($vbs, $vbsContent, (New-Object System.Text.UTF8Encoding $false))
  $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$vbs`""
  $task = Get-ScheduledTask -TaskName 'AcBridge-Relay' -ErrorAction SilentlyContinue
  if ($task) {
    # 重装到新目录时刷新 Action 指向（任务已存在则更新）
    Set-ScheduledTask -TaskName 'AcBridge-Relay' -Action $action | Out-Null
    Ok "Relay 开机自启: 计划任务已注册(路径已刷新)"
  } else {
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName 'AcBridge-Relay' -Description 'new-acad relay 守护 launcher（登录自启）' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Ok "Relay 开机自启: 计划任务已创建"
  }
} catch { Wn "Relay 开机自启注册失败: $_" }

# 注：Sacred MCP 不做开机自启（方案 B）——由 C3D 插件首次加载时懒启动（探测 :3000 未监听则拉起），
#     避免不用 C3D 时白占 ~108MB。手动启动：server\start-mcp.bat

# 9a.5 发布版说明（内置 LLM 模式 — 面板 AI 由 relay 直接调用，无外部依赖）
Write-Host "`n--- 9b. AI 模式说明 ---" -ForegroundColor Yellow
Write-Host "  发布版使用内置 LLM 模式：面板内可直接选模型 + 填 API Key" -ForegroundColor Gray
Write-Host "  配置入口：C3D 面板 → 顶部模型区（服务商/模型/API Key/保存），保存即生效" -ForegroundColor Gray
Write-Host "  或手动编辑 config.json 的 llm 段（provider/baseUrl/apiKey/model）" -ForegroundColor Gray
if (-not $cfg.llm.apiKey) {
  Wn "尚未配置 AI：开 C3D 后在面板顶部填 API Key，或手动编辑 config.json"
} else {
  Ok "AI 已配置: $($cfg.llm.provider)/$($cfg.llm.model)（key 已存在，仅本机）"
}

# 9b. C3D 自动加载插件 (Hank.lsp -> acad.lsp)  [P2: 路径动态化 + Hank.lsp 模板生成]
if (-not $c3d.found) {
  Wn "C3D 未检测到，跳过自动加载配置"
} else {
  $c3dSupport = $c3d.supportDir
  $acadLspPath = "$c3dSupport\acad.lsp"
  $hankLspTarget = "$c3dSupport\Hank.lsp"

  # 确保支持目录存在
  if (-not (Test-Path $c3dSupport)) {
    Wn "C3D 支持目录不存在: $c3dSupport（版本可能不是 $($c3d.year)，需手动确认）"
  } else {
    # 生成 Hank.lsp（注入实际 DLL 路径，异机可用）
    $dllPath = "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.dll"
    $dllForward = $dllPath -replace '\\','/'
    $hankContent = @"
;; Hank.lsp - auto-show Hank palette (DLL auto-loaded via Applications registry)
;; Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm') - ASCII only
;; NETLOAD omitted: double NETLOAD triggers 2nd Initialize, StartServer port conflict kills palette show

(if (not *hank-plugins-loaded*)
  (progn
    (setq *hank-plugins-loaded* T)
    ;; NETLOAD with path arg (verified). -APPLOAD does NOT exist in C3D 2025.
    ;; STARTUP=0 ensures document exists so acad.lsp runs. Palette shows via Initialize.
    (setq *hank-dll-path* "$dllForward")
    (if (findfile *hank-dll-path*)
      (progn
        (vl-catch-all-apply
          (function (lambda () (command "NETLOAD" *hank-dll-path*))))
        (vl-catch-all-apply
          (function (lambda () (command "_.HANK_SHOW"))))
        (princ "\n[HANK] plugin loaded, palette shown")
      )
      (princ (strcat "\n[HANK] DLL not found: " *hank-dll-path*))
    )
  )
)
(princ)
"@
    Set-Content $hankLspTarget $hankContent -Encoding ASCII
    Ok "Hank.lsp 已生成: $hankLspTarget"

    # 检查/创建 acad.lsp（加入 Hank.lsp 加载）
    if (Test-Path $acadLspPath) {
      $acadContent = Get-Content $acadLspPath -Raw
      if ($acadContent -match 'Hank\.lsp') {
        Ok "acad.lsp 已包含 Hank.lsp 加载"
      } else {
        # 2026-08-25: 弃用 Add-Content（PS5.1 按 GBK 重写 UTF-8 文件会把 acad.lsp 损坏成混合编码，AutoCAD 解析报"输入的字符串有缺陷"）
        [System.IO.File]::AppendAllText($acadLspPath, "`r`n(load ""Hank.lsp"")(princ)", (New-Object System.Text.UTF8Encoding $false))
        Ok "acad.lsp 已追加 Hank.lsp 加载"
      }
    } else {
      Set-Content $acadLspPath "(load ""Hank.lsp"")(princ)" -Encoding Default
      Ok "acad.lsp 已创建（自动加载 Hank.lsp）"
    }
  }

  # 9c. Applications 注册表自动加载（2026-08-25: 主加载通道, 不依赖 acad.lsp 时序）
  # DLL 由 AutoCAD 原生机制加载 → IExtensionApplication.Initialize → TCP 服务 + 面板全自动
  if ($c3d.productKey) {
    try {
      $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($c3d.rName)\$($c3d.productKey)\Applications\HankBridge"
      if (-not (Test-Path $appKey)) { New-Item -Path $appKey -Force | Out-Null }
      New-ItemProperty -Path $appKey -Name "Loader" -Value "$ROOT\plugin\AcBridge-v24\Civil3DMcpPlugin.dll" -PropertyType String -Force | Out-Null
      New-ItemProperty -Path $appKey -Name "Managed" -Value 1 -PropertyType DWord -Force | Out-Null
      New-ItemProperty -Path $appKey -Name "LoadCtl" -Value 1 -PropertyType DWord -Force | Out-Null
      New-ItemProperty -Path $appKey -Name "Desc" -Value "new-acad Hank palette plugin (Civil3D MCP bridge)" -PropertyType String -Force | Out-Null
      Ok "Applications 自动加载: $appKey"
    } catch { Wn "Applications 自动加载注册失败: $_" }
  } else { Wn "未取得产品键, 跳过 Applications 自动加载注册" }

  # 9d. STARTUP=0（2026-08-25: 启动直接建图, 保证 acad.lsp 执行——开始页无文档时 acad.lsp 不加载导致插件不自动加载）
  try {
    foreach ($prof in Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($c3d.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue) {
      $varKey = "$($prof.PSPath)\Variables"
      if (Test-Path $varKey) { New-ItemProperty -Path $varKey -Name STARTUP -Value 0 -PropertyType DWord -Force | Out-Null }
    }
    Ok "STARTUP=0 已设置（启动直接建图, acad.lsp 必然执行）"
  } catch { Wn "STARTUP=0 设置失败: $_" }

  # 2026-08-16: TRUSTEDPATHS 写入——插件目录加入可信路径，NETLOAD 不再弹"未签名可执行文件"安全框
  # （实测：全新签名 DLL + TRUSTEDPATHS 含插件目录 → C3D 重启无弹窗，8080 直接监听，两次确认）
  $pluginDir = "$ROOT\plugin\AcBridge-v24"
  try {
    $profilesKey = "HKCU:\Software\Autodesk\AutoCAD\$($c3d.rName)\ACAD-*\Profiles\*"
    $profiles = Get-ChildItem $profilesKey -ErrorAction SilentlyContinue
    $touched = 0
    foreach ($profile in $profiles) {
      $varKey = "$($profile.PSPath)\Variables"
      if (-not (Test-Path $varKey)) { continue }
      $cur = (Get-ItemProperty $varKey -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
      if ($null -eq $cur) { $cur = "" }
      if ($cur -match [regex]::Escape($pluginDir)) {
        Ok "TRUSTEDPATHS 已含插件目录: $($profile.PSChildName)"
      } else {
        $sep = if ($cur -and -not $cur.EndsWith(';')) { ';' } else { '' }
        Set-ItemProperty $varKey -Name TRUSTEDPATHS -Value ($cur + $sep + $pluginDir + ';')
        Ok "TRUSTEDPATHS 已加入插件目录: $($profile.PSChildName)"
        $touched++
      }
    }
    if ($touched -eq 0 -and $profiles.Count -eq 0) { Wn "TRUSTEDPATHS: 未找到 C3D profile（跳过）" }
  } catch {
    Wn "TRUSTEDPATHS 写入失败: $_"
  }
}
# ---------- 10. AI 客户端自动配置（P3.5: 装完即用，用户无需手动配 MCP）----------
Write-Host "`n--- 10/10 AI 客户端自动配置 ---" -ForegroundColor Yellow
$mcpEntry = @{
  civil3d = @{
    command = "$NODE"
    args = @("$ROOT\server\sacred-mcp.js")  # cwd=mcp（上游 help/env 兼容）
  }
}
$aiConfigured = @()

function Add-McpServer {
  param([string]$CfgPath, [string]$ClientName)
  if (-not $CfgPath) { return }
  try {
    # 读取现有配置（无则新建）
    $existing = $null
    if (Test-Path $CfgPath) {
      $existing = Get-Content $CfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    if (-not $existing) { $existing = [pscustomobject]@{ mcpServers = @{} } }
    if (-not $existing.mcpServers) { $existing | Add-Member -NotePropertyName mcpServers -NotePropertyValue @{} }
    # 备份原文件
    if (Test-Path $CfgPath) { Copy-Item $CfgPath "$CfgPath.bak" -Force }
    # 合并 mcpServers（PSObject 属性赋值；空 hashtable 跳过避免序列化泄漏内部成员）
    $serverMap = @{}
    $ms = $existing.mcpServers
    if ($ms -and $ms -isnot [hashtable]) {
      foreach ($prop in $ms.PSObject.Properties) { $serverMap[$prop.Name] = $prop.Value }
    }
    foreach ($k in $mcpEntry.Keys) { $serverMap[$k] = $mcpEntry[$k] }
    $existing.mcpServers = $serverMap
    # 写回（无 BOM，JSON 规范不允许 BOM）
    $dir = Split-Path $CfgPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $json = $existing | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($CfgPath, $json, (New-Object System.Text.UTF8Encoding $false))
    $script:aiConfigured += $ClientName
    Ok "$ClientName 已自动配置 MCP ($CfgPath)"
  } catch {
    Wn "$ClientName 配置失败: $($_.Exception.Message)"
  }
}

# 10a. Claude Desktop
$claudeCfg = "$env:APPDATA\Claude\claude_desktop_config.json"
$claudeInstalled = (Test-Path "$env:LOCALAPPDATA\AnthropicClaude\claude.exe") -or (Test-Path "$env:APPDATA\Claude")
if ($claudeInstalled) {
  Add-McpServer -CfgPath $claudeCfg -ClientName "Claude Desktop"
} else {
  Wn "Claude Desktop 未安装（跳过）"
}

# 10b. Cursor
$cursorCfg = "$env:USERPROFILE\.cursor\mcp.json"
$cursorInstalled = (Test-Path "$env:LOCALAPPDATA\Programs\Cursor\Cursor.exe") -or (Test-Path "$env:USERPROFILE\.cursor")
if ($cursorInstalled) {
  Add-McpServer -CfgPath $cursorCfg -ClientName "Cursor"
} else {
  Wn "Cursor 未安装（跳过）"
}

if ($aiConfigured.Count -gt 0) {
  Write-Host "  已自动配置: $($aiConfigured -join ', ') — 打开对应 AI 客户端即可直接使用（需重启客户端生效）" -ForegroundColor Cyan
} else {
  Write-Host "  未检测到 AI 客户端。手动配置方法见 使用手册.html 第四章" -ForegroundColor Gray
}


# ---------- 11. AI 配置向导（可选，S3: 发布版形态3 — relay 内置 LLM）----------
Write-Host "`n--- 11/11 AI 配置（可选）---" -ForegroundColor Yellow
$aiCfg = $cfg.llm
$doConfig = $true
if ($aiCfg -and $aiCfg.apiKey) {
  Write-Host "  已配置 AI: $($aiCfg.provider)/$($aiCfg.model)" -ForegroundColor Green
  $ch = Read-Host "  [1] 重新配置  [0] 跳过"
  if ($ch -ne "1") { $doConfig = $false }
}
if ($doConfig) {
  Write-Host "  选择服务商:" -ForegroundColor Yellow
  Write-Host "    [1] DeepSeek      [2] OpenAI       [3] 通义千问(Qwen)" -ForegroundColor Gray
  Write-Host "    [4] Kimi(Moonshot) [5] 智谱GLM      [6] MiniMax" -ForegroundColor Gray
  Write-Host "    [7] OpenRouter    [8] xAI          [9] 自定义" -ForegroundColor Gray
  Write-Host "    [0] 跳过（不配置，面板将提示未配置 AI）" -ForegroundColor Gray
  $prov = Read-Host "  请输入编号"
  $baseUrl = ""; $model = ""; $providerName = ""
  switch ($prov) {
    "1" { $providerName="deepseek";  $baseUrl="https://api.deepseek.com/v1";  Write-Host "    模型: [1] v4-flash [2] v4-pro [3] deepseek-chat [4] deepseek-reasoner" -ForegroundColor Gray; $m = Read-Host "    选择模型(默认 3)"; if ($m -eq "1") {$model="deepseek-v4-flash"} elseif ($m -eq "2") {$model="deepseek-v4-pro"} elseif ($m -eq "4") {$model="deepseek-reasoner"} else {$model="deepseek-chat"} }
    "2" { $providerName="openai";   $baseUrl="https://api.openai.com/v1";     $model = Read-Host "    模型名(默认 gpt-4o-mini)"; if (-not $model) { $model="gpt-4o-mini" } }
    "3" { $providerName="qwen";     $baseUrl="https://dashscope.aliyuncs.com/compatible-mode/v1"; $model = Read-Host "    模型名(默认 qwen-plus)"; if (-not $model) { $model="qwen-plus" } }
    "4" { $providerName="kimi";     $baseUrl="https://api.moonshot.cn/v1";    $model = Read-Host "    模型名(默认 moonshot-v1-8k)"; if (-not $model) { $model="moonshot-v1-8k" } }
    "5" { $providerName="zhipu";    $baseUrl="https://open.bigmodel.cn/api/paas/v4"; $model = Read-Host "    模型名(默认 glm-4-flash)"; if (-not $model) { $model="glm-4-flash" } }
    "6" { $providerName="minimax";  $baseUrl="https://api.minimax.chat/v1";   $model = Read-Host "    模型名(默认 abab6.5s-chat)"; if (-not $model) { $model="abab6.5s-chat" } }
    "7" { $providerName="openrouter";$baseUrl="https://openrouter.ai/api/v1"; $model = Read-Host "    模型名(默认 deepseek/deepseek-chat)"; if (-not $model) { $model="deepseek/deepseek-chat" } }
    "8" { $providerName="xai";      $baseUrl="https://api.x.ai/v1";           $model = Read-Host "    模型名(默认 grok-2-latest)"; if (-not $model) { $model="grok-2-latest" } }
    "9" { $providerName="custom";   $baseUrl = Read-Host "    API Base URL(OpenAI 兼容)"; $model = Read-Host "    模型名" }
    default { Write-Host "  跳过 AI 配置" -ForegroundColor Gray }
  }
  if ($baseUrl -and $model) {
    $key = Read-Host "  输入 API Key"
    if ($key) {
      $llmObj = [pscustomobject]@{ provider=$providerName; baseUrl=$baseUrl; apiKey=$key; model=$model; maxTurns=20; timeoutMs=120000 }
      if ($cfg.llm) { $cfg.llm = $llmObj } else { $cfg | Add-Member -NotePropertyName llm -NotePropertyValue $llmObj -Force }
      $cfg | Add-Member -NotePropertyName relayMode -NotePropertyValue "llm" -Force
      $json = $cfg | ConvertTo-Json -Depth 6
      [System.IO.File]::WriteAllText($CONFIG, $json, (New-Object System.Text.UTF8Encoding $false))
      Ok "AI 已配置: $providerName/$model（key 已写入 config.json，仅存本机）"
      Write-Host "  ⚠️ key 只存本机 config.json，请勿将 config.json 随包分发" -ForegroundColor Yellow
      Write-Host "  （面板内也可随时修改模型/key，保存即生效）" -ForegroundColor Gray
    } else {
      Wn "未输入 API Key，跳过 AI 配置（可稍后手动编辑 config.json 的 llm 段）"
    }
  }
}

# ---------- 总结 ----------
Write-Host "`n========================================" -ForegroundColor Cyan
$total = $PASS + $FAIL + $WARN
Write-Host "结果: $PASS/通过  $FAIL/失败  $WARN/警告（共 $total 项）" -ForegroundColor $(if ($FAIL -gt 0) {"Red"} elseif ($WARN -gt 0) {"Yellow"} else {"Green"})
Write-Host "========================================`n" -ForegroundColor Cyan

if ($FAIL -gt 0) {
  Write-Host "有 $FAIL 项失败，请修复后重新运行本脚本" -ForegroundColor Red
  exit 1
}
