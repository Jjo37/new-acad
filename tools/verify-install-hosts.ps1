# verify-install-hosts.ps1 -- read-only consistency check for the per-host autoload wiring
#
# 2026-11-19: install.ps1 can now configure several AutoCAD / Civil 3D versions in one run.
# This verifier answers "which hosts are wired, and does the wiring point at a real DLL?".
# It writes nothing.
#
#   powershell -File tools\verify-install-hosts.ps1           # text report, exit 1 on FAIL
#   powershell -File tools\verify-install-hosts.ps1 -Json     # machine-readable
#
# Status per host:
#   registered   Applications\HankBridge key exists
#   not-installed  host detected but never configured (fine: user chose not to install)
#   FAIL         registered but Loader/Hank.lsp/TRUSTEDPATHS inconsistent

param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Json)

$ErrorActionPreference = 'Stop'
$rows = @()
$fails = @()

$probe = Join-Path $Root 'tools\find-civil3d.ps1'
if (-not (Test-Path $probe)) { Write-Host 'SKIP: find-civil3d.ps1 missing'; exit 0 }
try { $p = & $probe | ConvertFrom-Json } catch { Write-Host ('SKIP: host detection failed: ' + $_); exit 0 }
$hosts = @($p.candidates | Where-Object { $_.installDir -and $_.productKey })
if ($hosts.Count -eq 0 -and $p.found) { $hosts = @($p) }
if ($hosts.Count -eq 0) { Write-Host 'SKIP: no AutoCAD / Civil 3D host on this machine'; exit 0 }

foreach ($h in $hosts) {
  $appKey = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($h.rName)\$($h.productKey)\Applications\HankBridge"
  $row = [ordered]@{
    product = $h.productName
    rName = $h.rName
    kind = $h.kind
    status = 'not-installed'
    loader = ''
    segment = ''
    hankLsp = ''
    problems = @()
  }
  if (-not (Test-Path $appKey)) {
    $rows += [pscustomobject]$row
    continue
  }
  $row.status = 'registered'
  $loader = [string](Get-ItemProperty $appKey -Name Loader -ErrorAction SilentlyContinue).Loader
  $row.loader = $loader
  if (-not $loader) {
    $row.problems += 'Loader value missing'
  } elseif (-not (Test-Path $loader)) {
    $row.problems += ('Loader points at a missing file: ' + $loader)
  }
  $row.segment = if ($loader -match '\\net10\\') { 'net10' } elseif ($loader) { 'net8' } else { '' }

  $hank = if ($h.supportDir) { Join-Path $h.supportDir 'Hank.lsp' } else { '' }
  $row.hankLsp = $hank
  if (-not $hank -or -not (Test-Path $hank)) {
    $row.problems += 'Hank.lsp not found in the host support folder'
  } elseif ($loader) {
    $fwd = $loader -replace '\\', '/'
    $txt = [System.IO.File]::ReadAllText($hank)
    if ($txt -notmatch [regex]::Escape($fwd)) { $row.problems += 'Hank.lsp *hank-dll-path* does not match the registry Loader' }
  }

  if ($loader) {
    $dir = Split-Path $loader -Parent
    $profiles = @(Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$($h.rName)\ACAD-*\Profiles\*" -ErrorAction SilentlyContinue)
    foreach ($prof in $profiles) {
      $vk = "$($prof.PSPath)\Variables"
      if (-not (Test-Path $vk)) { continue }
      $cur = [string](Get-ItemProperty $vk -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
      if ($cur -notmatch [regex]::Escape($dir)) { $row.problems += ('TRUSTEDPATHS missing ' + $dir + ' in profile ' + $prof.PSChildName) }
    }
  }

  if ($row.problems.Count -gt 0) {
    $row.status = 'FAIL'
    $fails += ($h.productName + ' (' + $h.rName + '): ' + ($row.problems -join '; '))
  }
  $rows += [pscustomobject]$row
}

if ($Json) {
  [pscustomobject]@{ hosts = $rows; failed = $fails.Count; problems = $fails } | ConvertTo-Json -Depth 6
} else {
  foreach ($r in $rows) {
    Write-Host ('  ' + $r.status.PadRight(13) + ' ' + $r.product + ' (' + $r.rName + ')')
    if ($r.loader) { Write-Host ('                loader: ' + $r.loader) -ForegroundColor DarkGray }
    foreach ($pr in $r.problems) { Write-Host ('                x ' + $pr) -ForegroundColor Red }
  }
  if ($fails.Count -eq 0) { Write-Host ('install hosts OK - ' + $rows.Count + ' host(s) detected, ' + @($rows | Where-Object { $_.status -eq 'registered' }).Count + ' wired') }
  else { Write-Host ('install hosts: ' + $fails.Count + ' host(s) FAILED') -ForegroundColor Red }
}
exit $(if ($fails.Count -gt 0) { 1 } else { 0 })
