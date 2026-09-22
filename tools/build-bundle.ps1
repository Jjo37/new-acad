# build-bundle.ps1 - assemble the Autodesk Autoloader .bundle for the store build
# Layout (matches PluginRuntime.ProjectRoot(): DLL parent's parent == bundle Contents\):
#   new-acad.bundle\PackageContents.xml
#   new-acad.bundle\Contents\Windows\   <- plugin DLL (+deps.json, runtimeconfig.json)
#   new-acad.bundle\Contents\Help\      <- user guide (index.html) + icon
#   new-acad.bundle\Contents\           <- node.exe, server\, knowledge\, tools\, config.json
# Usage: powershell -File build-bundle.ps1 [-Root <release repo>] [-Out <bundle dir>]
param(
  [string]$Root = "D:\new-acad-release",
  [string]$Out  = "D:\new-acad-release\dist-bundle"
)
$ErrorActionPreference = 'Stop'
$dist = Join-Path $Root 'dist'
if (-not (Test-Path $dist)) { throw "dist not found: $dist (run build-dist.ps1 first)" }
$bundle = Join-Path $Out 'new-acad.bundle'
$contents = Join-Path $bundle 'Contents'

Write-Host "[1/5] clean $Out"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $contents 'Windows'), (Join-Path $contents 'Help') | Out-Null

Write-Host "[2/5] Contents\Windows  <- plugin DLL"
Get-ChildItem (Join-Path $dist 'plugin\AcBridge-v24') -File | Where-Object { $_.Extension -in @('.dll','.json') } | ForEach-Object {
  Copy-Item $_.FullName (Join-Path $contents 'Windows') -Force
}

Write-Host "[3/5] Contents\  <- node.exe + server + knowledge + tools + config"
Copy-Item (Join-Path $dist 'node.exe') $contents -Force
foreach ($d in @('server','knowledge','tools')) {
  $src = Join-Path $dist $d
  if (Test-Path $src) { Copy-Item $src $contents -Recurse -Force }
}
Copy-Item (Join-Path $dist 'config.json') $contents -Force
Copy-Item (Join-Path $dist 'workflow-guide.md') (Join-Path $contents 'Help') -Force

Write-Host "[4/5] Contents\Help <- user guide + icon + readme"
$manual = Get-ChildItem $dist -Filter *.html -File | Select-Object -First 1
if ($manual) { Copy-Item $manual.FullName (Join-Path $contents 'Help\index.html') -Force ; Write-Host ("      guide <- " + $manual.Name) }
else { Write-Host "      WARN: user guide not found, writing a stub" ; '<html><body>new-acad - see https://github.com/Jjo37/new-acad</body></html>' | Set-Content (Join-Path $contents 'Help\index.html') -Encoding UTF8 }
$readmeTxt = Join-Path $dist 'README.txt'
if (Test-Path $readmeTxt) { Copy-Item $readmeTxt (Join-Path $contents 'Help\README.txt') -Force }
$icon = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\public-templates\icon.ico'
if (Test-Path $icon) { Copy-Item $icon (Join-Path $contents 'Help\icon.ico') -Force } else { Write-Host "      NOTE: no icon.ico (optional)" }

Write-Host "[5/5] PackageContents.xml"
$pcSrc = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\bundle\PackageContents.xml'
if (-not (Test-Path $pcSrc)) { throw "PackageContents.xml source not found: $pcSrc" }
Copy-Item $pcSrc (Join-Path $bundle 'PackageContents.xml') -Force
$storeReadme = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\bundle\INSTALL-README.txt'
if (Test-Path $storeReadme) { Copy-Item $storeReadme (Join-Path $bundle 'README-FIRST.txt') -Force ; Copy-Item $storeReadme (Join-Path $contents 'Help\INSTALL-README.txt') -Force }

# report
function Size($p) { $s = (Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum; $n = (Get-ChildItem $p -Recurse -File | Measure-Object).Count; "{0:N1} MB / {1} files" -f ($s/1MB), $n }
Write-Host ""
Write-Host ("bundle : " + $bundle)
Write-Host ("total  : " + (Size $bundle))
Write-Host ("Windows: " + (Size (Join-Path $contents 'Windows')))
Write-Host ("server : " + (Size (Join-Path $contents 'server')))
Write-Host ("Help   : " + (Size (Join-Path $contents 'Help')))
Write-Host ("node   : " + [math]::Round((Get-Item (Join-Path $contents 'node.exe')).Length/1MB,1) + " MB")
# sanity checks
$dll = Join-Path $contents 'Windows\Civil3DMcpPlugin.dll'
if (-not (Test-Path $dll)) { throw "DLL missing in Contents\Windows" }
$relay = Join-Path $contents 'server\panel-relay.js'
if (-not (Test-Path $relay)) { throw "server\panel-relay.js missing (ProjectRoot would not resolve)" }
Write-Host "[OK] layout sanity checks passed"

# produce the submission artifact (zip of the .bundle folder)
Write-Host "[6/6] zip"
$zipPath = Join-Path $Out 'new-acad.bundle.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $bundle -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host ("  zip: " + $zipPath + "  " + [math]::Round((Get-Item $zipPath).Length/1MB,1) + " MB")
