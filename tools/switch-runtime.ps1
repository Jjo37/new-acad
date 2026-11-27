# switch-runtime.ps1 -- switch the installed plug-in binary between the net8 and net10 builds
#
#   powershell -File tools\switch-runtime.ps1 -Runtime status
#   powershell -File tools\switch-runtime.ps1 -Runtime net10                 (run as Administrator)
#   powershell -File tools\switch-runtime.ps1 -Runtime net8 -Hosts 1,2
#
# 2026-11-10: every installer ships both builds. install.ps1 picks one per host release line
# (R25.x -> net8, R26.0+ -> net10). This script flips that choice in place, so a user can answer
# "does 2027 accept the net8 build?" without reinstalling.
#
# 2026-11-19: multi-host. The machine may have several AutoCAD / Civil 3D versions, each with its
# own Applications\HankBridge key and its own Hank.lsp. Default -Hosts all switches every host
# that is registered by the installer; pass -Hosts 1,3 to touch only those (see -Runtime status
# for the numbering).
#
# What it touches: HKLM Applications\HankBridge "Loader" (the real load path), the
# *hank-dll-path* line inside Hank.lsp (the LISP fallback), and TRUSTEDPATHS for that host.

param(
  [ValidateSet('status', 'net8', 'net10')][string]$Runtime = 'status',
  [string]$Hosts = 'all'
)

$ErrorActionPreference = 'Stop'
$ROOT = Split-Path $PSScriptRoot -Parent
$net8 = Join-Path $ROOT 'plugin\AcBridge-v24\Civil3DMcpPlugin.dll'
$net10 = Join-Path $ROOT 'plugin\AcBridge-v24\net10\Civil3DMcpPlugin.dll'

function Get-HostList {
  $probe = Join-Path $ROOT 'tools\find-civil3d.ps1'
  if (-not (Test-Path $probe)) { return @() }
  try { $p = & $probe | ConvertFrom-Json } catch { return @() }
  $list = @($p.candidates | Where-Object { $_.installDir -and $_.productKey })
  if ($list.Count -eq 0 -and $p.found) { $list = @($p) }
  return @($list | Sort-Object @{ Expression = { if ($_.kind -eq 'civil3d') { 0 } else { 1 } } }, @{ Expression = { [int]$_.year }; Descending = $true })
}

function Get-Loader([object]$h) {
  $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge"
  if (Test-Path $appKey) { return [string](Get-ItemProperty $appKey -Name Loader -ErrorAction SilentlyContinue).Loader }
  return ''
}

$all = Get-HostList
if ($all.Count -eq 0) { throw 'no AutoCAD / Civil 3D host detected (registry HKLM\SOFTWARE\Autodesk\AutoCAD)' }

# resolve -Hosts: all | 0 (none) | 1,3
$targets = @()
if ($Hosts -eq 'all' -or -not $Hosts) {
  $targets = @($all)
} elseif ($Hosts -ne '0' -and $Hosts -ne 'none') {
  foreach ($tok in ($Hosts -split '[,;\s]+' | Where-Object { $_ })) {
    $n = 0
    if ([int]::TryParse($tok, [ref]$n) -and $n -ge 1 -and $n -le $all.Count) { $targets += $all[$n - 1] }
    else { Write-Host ("ignore invalid host index: " + $tok) -ForegroundColor Yellow }
  }
}

if ($Runtime -eq 'status') {
  $i = 0
  foreach ($h in $all) {
    $i++
    $loader = Get-Loader $h
    $active = if ($loader -match '\\net10\\') { 'net10' } elseif ($loader) { 'net8' } else { '(not registered)' }
    $recommended = if ($h.segment) { $h.segment } else { if ($h.rName -match '^R(\d+)\.' -and [int]$matches[1] -ge 26) { 'net10' } else { 'net8' } }
    Write-Host ("[" + $i + "] " + $h.productName + " (" + $h.rName + ")")
    Write-Host ("    apps key : HKLM\SOFTWARE\Autodesk\AutoCAD\" + $h.rName + "\" + $h.productKey + "\Applications\HankBridge")
    Write-Host ("    Loader   : " + $(if ($loader) { $loader } else { '(not registered)' }))
    Write-Host ("    active   : " + $active + "   recommended for this host: " + $recommended)
  }
  Write-Host ("net8 build  : " + $(if (Test-Path $net8) { 'present' } else { 'MISSING' }))
  Write-Host ("net10 build : " + $(if (Test-Path $net10) { 'present' } else { 'MISSING' }))
  exit 0
}

$dll = if ($Runtime -eq 'net10') { $net10 } else { $net8 }
if (-not (Test-Path $dll)) { throw "build not found: $dll" }

$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'switching needs Administrator (it writes HKLM). Re-run from an elevated prompt.' }
if ($targets.Count -eq 0) { Write-Host 'no host selected - nothing to do'; exit 0 }

$trDir = Split-Path $dll -Parent
$fwd = $dll -replace '\\', '/'

foreach ($h in $targets) {
  $tag = "[" + $h.productName + " / " + $h.rName + "]"

  # 1. registry Loader (the path AutoCAD actually loads)
  $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge"
  if (-not (Test-Path $appKey)) { New-Item -Path $appKey -Force | Out-Null }
  New-ItemProperty -Path $appKey -Name Loader   -Value $dll -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $appKey -Name Managed  -Value 1    -PropertyType DWord  -Force | Out-Null
  New-ItemProperty -Path $appKey -Name LoadCtl  -Value 1    -PropertyType DWord  -Force | Out-Null
  New-ItemProperty -Path $appKey -Name Desc     -Value 'new-acad Hank palette plugin (Civil3D MCP bridge, runtime-switched)' -PropertyType String -Force | Out-Null
  Write-Host ($tag + " Loader -> " + $dll)

  # 2. Hank.lsp fallback (same DLL path, LISP side)
  $hank = if ($h.supportDir) { Join-Path $h.supportDir 'Hank.lsp' } else { '' }
  if ($hank -and (Test-Path $hank)) {
    $txt = [System.IO.File]::ReadAllText($hank)
    $txt = [regex]::Replace($txt, '\(setq \*hank-dll-path\* "[^"]*"\)', '(setq *hank-dll-path* "' + $fwd + '")')
    [System.IO.File]::WriteAllText($hank, $txt, (New-Object System.Text.UTF8Encoding $false))
    Write-Host ($tag + " Hank.lsp -> " + $hank)
  } else {
    Write-Host ($tag + " Hank.lsp not found (Applications registry still points at the new build)") -ForegroundColor Yellow
  }

  # 3. TRUSTEDPATHS for the folder we just pointed at
  foreach ($prof in Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($h.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue) {
    $vk = "$($prof.PSPath)\Variables"
    if (-not (Test-Path $vk)) { continue }
    $cur = (Get-ItemProperty $vk -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
    if ($null -eq $cur) { $cur = '' }
    if ($cur -notmatch [regex]::Escape($trDir)) {
      $sep = if ($cur -and -not $cur.EndsWith(';')) { ';' } else { '' }
      Set-ItemProperty $vk -Name TRUSTEDPATHS -Value ($cur + $sep + $trDir + ';')
    }
  }
}

Write-Host ("switched to : " + $Runtime + " (" + $targets.Count + " host(s))")
Write-Host 'restart AutoCAD / Civil 3D for the change to take effect.'
