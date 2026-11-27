# run-host-probe.ps1 -- stage + run the plain-AutoCAD JIT probe (2026-10-27)
#
# Stage the plugin DLL and the AutoCAD-only managed references into the probe's own output
# directory, then run it. The probe force-JITs every AutoCAD-side handler in a process that
# deliberately has NO AeccDbMgd / AeccPressurePipesMgd / AecBaseMgd, and asserts
# listMethods >= 300.
#
# Staging matters: passing the dist DLL path as an argument makes dependency resolution use that
# directory, where accoremgd is missing, and the probe reports false failures.
#
# Usage: powershell -File tools\run-host-probe.ps1 [-Dev D:\new-acad]
# ASCII-only on purpose: PowerShell 5.1 reads .ps1 as GBK on zh-CN Windows.

param(
  [string]$Dev = "D:\new-acad"
)
$ErrorActionPreference = 'Stop'

$pluginDir = Join-Path $Dev 'plugin\AcBridge-v24'
$probeDir = Join-Path $pluginDir 'tests\HostModeProbe'
$refs = Join-Path $pluginDir 'C_References'
$proj = Join-Path $probeDir 'HostModeProbe.csproj'

if (-not (Test-Path $proj)) { Write-Host "[FAIL] probe project missing: $proj" -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $pluginDir 'Civil3DMcpPlugin.dll'))) { Write-Host "[FAIL] plugin DLL missing (build first)" -ForegroundColor Red; exit 1 }

Write-Host '[1/3] build probe'
& dotnet build $proj -c Release -v q --nologo | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { Write-Host '[FAIL] probe build failed' -ForegroundColor Red; exit 1 }

$out = Join-Path $probeDir 'bin\Release\net8.0-windows'
Write-Host '[2/3] stage plugin + AutoCAD-only refs (no Aecc)'
Copy-Item (Join-Path $pluginDir 'Civil3DMcpPlugin.dll') $out -Force
foreach ($d in @('SixLabors.ImageSharp.dll', 'UglyToad.PdfPig.dll', 'UglyToad.PdfPig.Core.dll', 'UglyToad.PdfPig.Fonts.dll', 'UglyToad.PdfPig.Tokenization.dll')) {
  $src = Join-Path $pluginDir $d
  if (Test-Path $src) { Copy-Item $src $out -Force }
}
foreach ($d in @('acdbmgd.dll', 'acmgd.dll', 'accoremgd.dll', 'acdbmgdbrep.dll')) {
  $src = Join-Path $refs $d
  if (-not (Test-Path $src)) { Write-Host ("[FAIL] reference missing: " + $src) -ForegroundColor Red; exit 1 }
  Copy-Item $src $out -Force
}
$leak = @(Get-ChildItem $out -Filter '*Aecc*' -ErrorAction SilentlyContinue)
if ($leak.Count -gt 0) { Write-Host ('[FAIL] Aecc* leaked into probe dir: ' + ($leak.Name -join ',')) -ForegroundColor Red; exit 1 }

Write-Host '[3/3] run probe'
$c3dDir = $null
try {
  $det = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Dev 'tools\find-civil3d.ps1') | ConvertFrom-Json
  if ($det.installDir) { $c3dDir = $det.installDir }
} catch { }
if ($c3dDir -and (Test-Path $c3dDir)) { $env:PATH = $c3dDir + ';' + $env:PATH }

& (Join-Path $out 'HostModeProbe.exe')
$code = $LASTEXITCODE
Write-Host ("probe exit=" + $code)
exit $code
