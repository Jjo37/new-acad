# switch-runtime.ps1 -- switch the installed plug-in binary between the net8 and net10 builds
#
#   powershell -File tools\switch-runtime.ps1 -Runtime status
#   powershell -File tools\switch-runtime.ps1 -Runtime net10     (run as Administrator)
#   powershell -File tools\switch-runtime.ps1 -Runtime net8
#
# 2026-11-10: every installer ships both builds (Bro decision). install.ps1 picks one by host release
# line (R25.x -> net8, R26.0+ -> net10). This script flips that choice in place, so a user
# can answer "does 2027 accept the net8 build?" without reinstalling.
#
# What it touches: HKLM Applications\HankBridge "Loader" (the real load path) and the
# *hank-dll-path* line inside Hank.lsp (the LISP fallback). Nothing else.

param([ValidateSet('status', 'net8', 'net10')][string]$Runtime = 'status')

$ErrorActionPreference = 'Stop'
$ROOT = Split-Path $PSScriptRoot -Parent
$net8 = Join-Path $ROOT 'plugin\AcBridge-v24\Civil3DMcpPlugin.dll'
$net10 = Join-Path $ROOT 'plugin\AcBridge-v24\net10\Civil3DMcpPlugin.dll'

function Get-HostInfo {
  $probe = Join-Path $ROOT 'tools\find-civil3d.ps1'
  if (-not (Test-Path $probe)) { return $null }
  try { return (& $probe | ConvertFrom-Json) } catch { return $null }
}

$c3d = Get-HostInfo
if (-not $c3d -or -not $c3d.found) { throw 'no AutoCAD / Civil 3D host detected (registry HKLM\SOFTWARE\Autodesk\AutoCAD)' }

$appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($c3d.rName)\$($c3d.productKey)\Applications\HankBridge"
$current = if (Test-Path $appKey) { (Get-ItemProperty $appKey -Name Loader -ErrorAction SilentlyContinue).Loader } else { '' }

if ($Runtime -eq 'status') {
  Write-Host ("host        : " + $c3d.kind + ' ' + $c3d.year + ' (' + $c3d.rName + ')')
  Write-Host ("registry    : " + $appKey)
  Write-Host ("Loader      : " + $(if ($current) { $current } else { '(not registered)' }))
  Write-Host ("net8 build  : " + $(if (Test-Path $net8) { 'present' } else { 'MISSING' }))
  Write-Host ("net10 build : " + $(if (Test-Path $net10) { 'present' } else { 'MISSING' }))
  $which = if ($current -match '\\net10\\') { 'net10' } elseif ($current) { 'net8' } else { 'unknown' }
  Write-Host ("active      : " + $which)
  exit 0
}

$dll = if ($Runtime -eq 'net10') { $net10 } else { $net8 }
if (-not (Test-Path $dll)) { throw "build not found: $dll" }

$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'switching needs Administrator (it writes HKLM). Re-run from an elevated prompt.' }

# 1. registry Loader (the path AutoCAD actually loads)
if (-not (Test-Path $appKey)) { New-Item -Path $appKey -Force | Out-Null }
New-ItemProperty -Path $appKey -Name Loader   -Value $dll -PropertyType String -Force | Out-Null
New-ItemProperty -Path $appKey -Name Managed  -Value 1    -PropertyType DWord  -Force | Out-Null
New-ItemProperty -Path $appKey -Name LoadCtl  -Value 1    -PropertyType DWord  -Force | Out-Null
New-ItemProperty -Path $appKey -Name Desc     -Value 'new-acad Hank palette plugin (Civil3D MCP bridge, runtime-switched)' -PropertyType String -Force | Out-Null

# 2. Hank.lsp fallback (same DLL path, LISP side)
$hank = Join-Path $c3d.supportDir 'Hank.lsp'
if (Test-Path $hank) {
  $txt = [System.IO.File]::ReadAllText($hank)
  $fwd = $dll -replace '\\', '/'
  $txt = [regex]::Replace($txt, '\(setq \*hank-dll-path\* "[^"]*"\)', '(setq *hank-dll-path* "' + $fwd + '")')
  [System.IO.File]::WriteAllText($hank, $txt, (New-Object System.Text.UTF8Encoding $false))
  Write-Host ("Hank.lsp    : " + $hank)
}

# 3. TRUSTEDPATHS for the folder we just pointed at
$trDir = Split-Path $dll -Parent
foreach ($prof in Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($c3d.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue) {
  $vk = "$($prof.PSPath)\Variables"
  if (-not (Test-Path $vk)) { continue }
  $cur = (Get-ItemProperty $vk -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
  if ($null -eq $cur) { $cur = '' }
  if ($cur -notmatch [regex]::Escape($trDir)) {
    $sep = if ($cur -and -not $cur.EndsWith(';')) { ';' } else { '' }
    Set-ItemProperty $vk -Name TRUSTEDPATHS -Value ($cur + $sep + $trDir + ';')
  }
}

Write-Host ("switched to : " + $Runtime)
Write-Host ("Loader      : " + $dll)
Write-Host 'restart AutoCAD / Civil 3D for the change to take effect.'
