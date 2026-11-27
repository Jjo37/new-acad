<#
.SYNOPSIS
  宿主自动加载接线（dot-source 使用，不单独运行）
.DESCRIPTION
  2026-11-24 从 install.ps1 抽出。

  为什么抽：用户需要
    ① 后续把插件加装到本机其他 AutoCAD / Civil 3D 版本；
    ② 只卸载某一个版本，保留其他版本；
  这两件事读写的都是同一套东西：HKLM\...\Applications\HankBridge + <Support>\Hank.lsp + acad.lsp 加载行。
  逻辑只放一处，才不会出现第二份漂移。

  调用方需先定义 Ok / Wn 两个输出函数（install.ps1 带 PASS/FAIL 计数，manage-hosts.ps1 是轻量版）。
  可选：调用方设 $script:NewAcadRoot = <安装根>；不设则按本文件位置推断（tools/ 的父目录）。
#>

function Get-NewAcadRoot {
  if ($script:NewAcadRoot) { return $script:NewAcadRoot }
  return (Split-Path $PSScriptRoot -Parent)   # tools/ 的父目录 = 安装根
}

# 本机所有 Autodesk 宿主（AutoCAD + Civil 3D），Civil 3D 优先、年份降序
function Get-NewAcadHosts {
  param([string]$Root = "")
  if (-not $Root) { $Root = Get-NewAcadRoot }
  $probe = $null
  try { $probe = & (Join-Path $Root 'tools\find-civil3d.ps1') | ConvertFrom-Json } catch { return @() }
  if (-not $probe) { return @() }
  $all = @($probe.candidates | Where-Object { $_.installDir -and $_.productKey })
  if ($all.Count -eq 0 -and $probe.found) { $all = @($probe) }   # 旧探测脚本只回单宿主时兜底
  return @($all | Sort-Object @{ Expression = { if ($_.kind -eq 'civil3d') { 0 } else { 1 } } }, @{ Expression = { [int]$_.year }; Descending = $true })
}

# 插件段：R25.x（2025/2026）-> net8；R26.0+（2027 及以后）-> net10（包里没有 net10 段则回落 net8）
function Get-HostSegmentName([object]$h) {
  $series = 25
  if ($h.rName -match '^R(\d+)\.') { $series = [int]$matches[1] }
  $net10 = Join-Path (Get-NewAcadRoot) 'plugin\AcBridge-v24\net10\Civil3DMcpPlugin.dll'
  if ($series -ge 26 -and (Test-Path $net10)) { return 'net10' }
  return 'net8'
}

function Get-HostPluginDll([object]$h) {
  $pluginRoot = Join-Path (Get-NewAcadRoot) 'plugin\AcBridge-v24'
  if ((Get-HostSegmentName $h) -eq 'net10') { return (Join-Path $pluginRoot 'net10\Civil3DMcpPlugin.dll') }
  return (Join-Path $pluginRoot 'Civil3DMcpPlugin.dll')
}

# 选择解析：""/"all" = 全部受支持宿主（否则全部）；"none"/"0" = 空；"1,3" = 按编号
function Resolve-HostSelection {
  param([object[]]$AllHosts, [string]$Selection)
  $supported = @($AllHosts | Where-Object { $_.supported -ne $false })
  $fallback = if ($supported.Count -gt 0) { $supported } else { @($AllHosts) }
  if (-not $Selection -or $Selection -eq 'all') { return $fallback }
  if ($Selection -eq 'none' -or $Selection -eq '0') { return @() }
  $picked = @()
  foreach ($tok in ($Selection -split '[,;\s]+' | Where-Object { $_ })) {
    $n = 0
    if ([int]::TryParse($tok, [ref]$n) -and $n -ge 1 -and $n -le $AllHosts.Count) {
      $picked += $AllHosts[$n - 1]
    } else { Wn "忽略无效选择: $tok" }
  }
  return @($picked | Select-Object -Unique)
}

# 当前接线状态（注册表 + Hank.lsp 两处都在才算 wired）
function Get-HostWiringStatus([object]$h) {
  $reg = $false
  if ($h.productKey) { $reg = Test-Path "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge" }
  $hank = $false
  if ($h.supportDir) { $hank = Test-Path (Join-Path $h.supportDir 'Hank.lsp') }
  return [pscustomobject]@{ registry = $reg; hankLsp = $hank; wired = ($reg -and $hank) }
}

# 给单个宿主接线：Hank.lsp + acad.lsp + Applications 注册表 + STARTUP=0 + TRUSTEDPATHS
function Install-HostAutoload([object]$h) {
  $pluginRoot = Join-Path (Get-NewAcadRoot) 'plugin\AcBridge-v24'
  $hostDll = Get-HostPluginDll $h
  $segName = Get-HostSegmentName $h
  $tag = "[$($h.productName) / $($h.rName)]"
  Write-Host ("  $tag 插件段: " + $segName) -ForegroundColor Gray
  $c3dSupport = $h.supportDir
  if (-not $c3dSupport) { Wn "$tag 支持目录未知，跳过自动加载"; return }
  $acadLspPath = "$c3dSupport\acad.lsp"
  $hankLspTarget = "$c3dSupport\Hank.lsp"
  if (-not (Test-Path $c3dSupport)) {
    try { New-Item -ItemType Directory -Path $c3dSupport -Force | Out-Null; Ok "$tag 已创建支持目录" }
    catch { Wn "$tag 支持目录无法创建: $c3dSupport"; return }
  }

  # Hank.lsp（注入本宿主的 DLL 路径）
  $dllForward = $hostDll -replace '\\','/'
  $hankContent = @"
;; Hank.lsp - auto-show Hank palette (DLL auto-loaded via Applications registry)
;; Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm') - ASCII only
;; Host: $($h.productName) / $($h.rName) / plugin segment $segName
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
        ;; 2026-11-24: F1 contextual help (Design and Make Marketplace requirement).
        ;; setfunhelp is the standard AutoCAD mechanism: command line -> F1 opens the manual.
        ;; root = three levels up from <root>/plugin/AcBridge-v24/Civil3DMcpPlugin.dll
        (vl-catch-all-apply
          (function (lambda ()
            (if (and (boundp '*hank-dll-path*) *hank-dll-path*)
              (progn
                (setq *hank-root*
                  (vl-filename-directory
                    (vl-filename-directory
                      (vl-filename-directory *hank-dll-path*))))
                (setq *hank-help* (strcat *hank-root* "/help.html"))
                (if (findfile *hank-help*)
                  (progn
                    (setfunhelp "HANK_SHOW" *hank-help*)
                    (setfunhelp "C3DMCPSTART" *hank-help*)
                    (setfunhelp "C3DMCPSTOP" *hank-help*)
                    (setfunhelp "C3DMCPSTATUS" *hank-help*)
                    (princ "\n[HANK] F1 help registered")
                  )
                  (princ "\n[HANK] help.html not found (F1 help skipped)")
                )
              )
            )
          ))
        )
      )
      (princ (strcat "\n[HANK] DLL not found: " *hank-dll-path*))
    )
  )
)
(princ)
"@
  Set-Content $hankLspTarget $hankContent -Encoding ASCII
  Ok "$tag Hank.lsp 已生成"

  if (Test-Path $acadLspPath) {
    $acadContent = Get-Content $acadLspPath -Raw
    if ($acadContent -match 'Hank\.lsp') { Ok "$tag acad.lsp 已包含 Hank.lsp 加载" }
    else {
      [System.IO.File]::AppendAllText($acadLspPath, "`r`n(load `"Hank.lsp`")(princ)", (New-Object System.Text.UTF8Encoding $false))
      Ok "$tag acad.lsp 已追加 Hank.lsp 加载"
    }
  } else {
    Set-Content $acadLspPath "(load `"Hank.lsp`")(princ)" -Encoding Default
    Ok "$tag acad.lsp 已创建（自动加载 Hank.lsp）"
  }

  # Applications 注册表自动加载（主加载通道）
  try {
    $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge"
    if (-not (Test-Path $appKey)) { New-Item -Path $appKey -Force | Out-Null }
    New-ItemProperty -Path $appKey -Name "Loader" -Value $hostDll -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $appKey -Name "Managed" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $appKey -Name "LoadCtl" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $appKey -Name "Desc" -Value "new-acad Hank palette plugin (Civil3D MCP bridge)" -PropertyType String -Force | Out-Null
    Ok "$tag Applications 自动加载: $appKey"
  } catch { Wn "$tag Applications 自动加载注册失败: $_" }

  # STARTUP=0（开始页无文档时 acad.lsp 不加载）
  try {
    foreach ($prof in Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($h.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue) {
      $varKey = "$($prof.PSPath)\Variables"
      if (Test-Path $varKey) { New-ItemProperty -Path $varKey -Name STARTUP -Value 0 -PropertyType DWord -Force | Out-Null }
    }
    Ok "$tag STARTUP=0 已设置"
  } catch { Wn "$tag STARTUP=0 设置失败: $_" }

  # TRUSTEDPATHS：插件根 + 本段 DLL 目录（net10 在子目录里）
  try {
    $trustDirs = @($pluginRoot, (Split-Path $hostDll -Parent)) | Select-Object -Unique
    $profiles = Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($h.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue
    $touched = 0
    foreach ($profile in $profiles) {
      $varKey = "$($profile.PSPath)\Variables"
      if (-not (Test-Path $varKey)) { continue }
      $cur = (Get-ItemProperty $varKey -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
      if ($null -eq $cur) { $cur = "" }
      $add = @()
      foreach ($d in $trustDirs) { if ($cur -notmatch [regex]::Escape($d)) { $add += $d } }
      if ($add.Count -gt 0) {
        $sep = if ($cur -and -not $cur.EndsWith(';')) { ';' } else { '' }
        Set-ItemProperty $varKey -Name TRUSTEDPATHS -Value (($cur + $sep + ($add -join ';')) + ';')
        $touched++
      }
    }
    if ($touched -gt 0) { Ok "$tag TRUSTEDPATHS 已加入插件目录（$touched 个 profile）" }
    elseif ($profiles.Count -eq 0) { Wn "$tag TRUSTEDPATHS: 未找到 profile（跳过）" }
    else { Ok "$tag TRUSTEDPATHS 已含插件目录" }
  } catch { Wn "$tag TRUSTEDPATHS 写入失败: $_" }
}

# 摘掉单个宿主的接线（只动这个宿主，不碰 relay / 计划任务 / 安装目录）
function Remove-HostWiring([object]$h) {
  $tag = "[$($h.productName) / $($h.rName)]"
  $removed = @()

  if ($h.productKey) {
    $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge"
    if (Test-Path $appKey) {
      try { Remove-Item $appKey -Recurse -Force; $removed += 'Applications'; Ok "$tag 已移除自动加载注册" }
      catch { Wn "$tag 注册表删除失败（需要管理员权限）: $_" }
    }
  }

  if ($h.supportDir) {
    $hankPath = Join-Path $h.supportDir 'Hank.lsp'
    if (Test-Path $hankPath) {
      try { Remove-Item $hankPath -Force; $removed += 'Hank.lsp'; Ok "$tag 已移除 Hank.lsp" }
      catch { Wn "$tag Hank.lsp 删除失败: $_" }
    }
    $acadPath = Join-Path $h.supportDir 'acad.lsp'
    if (Test-Path $acadPath) {
      $content = [System.IO.File]::ReadAllText($acadPath)
      if ($content -match 'Hank\.lsp') {
        $new = $content -replace '(?m)^\s*\(load "Hank\.lsp"\)\s*\(princ\)\s*$', ''
        $new = $new -replace '(?m)^\s*\(load "Hank\.lsp"\)\s*$', ''
        try {
          if ($new.Trim()) {
            [System.IO.File]::WriteAllText($acadPath, $new, (New-Object System.Text.UTF8Encoding $false))
            Ok "$tag acad.lsp 已移除 Hank.lsp 加载行"
          } else {
            Remove-Item $acadPath -Force
            Ok "$tag acad.lsp 只含本插件加载，已删除整个文件"
          }
          $removed += 'acad.lsp'
        } catch { Wn "$tag acad.lsp 改写失败: $_" }
      }
    }
  }

  if ($removed.Count -eq 0) { Ok "$tag 本来就没有接线（无需清理）" }
  return $removed
}
