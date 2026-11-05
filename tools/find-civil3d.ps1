# find-civil3d.ps1 — 探测 Autodesk 宿主（Civil 3D 或纯 AutoCAD）
#
# 2026-10-27 (v1.11.0-fix): 原来是"只认 Civil 3D"——纯 AutoCAD 机器上 found=false，
# 安装器于是跳过自动加载，用户装了插件却什么都不会发生。现在按宿主类型返回：
#   kind = "civil3d" | "autocad"
# 优先选 Civil 3D（功能是 AutoCAD 的超集），没有 Civil 3D 才回落到纯 AutoCAD。
# 保留原有 JSON 字段（found/supported/year/installDir/rName/productKey/productName/supportDir），
# 新增 kind / candidates[].kind —— 旧调用方不受影响。
#
# 支持范围：2025 / 2026（R25.x，.NET 8）。2024 及更早是 .NET Framework 4.8，插件加载不了。

$result = [ordered]@{
  found = $false; supported = $false; year = $null; installDir = $null; rName = $null;
  productKey = $null; productName = $null; supportDir = $null; kind = $null; candidates = @()
}

function Get-SupportDir([string]$kind, [string]$year, [string]$rName) {
  if ($kind -eq 'civil3d') {
    # Civil 3D 的漫游目录：%APPDATA%\Autodesk\C3D <year>\<lang>\Support
    $supRoot = Join-Path $env:APPDATA "Autodesk\C3D $year"
    foreach ($lg in @('chs', 'enu', 'cht')) {
      $c = Join-Path $supRoot "$lg\Support"
      if (Test-Path $c) { return $c }
    }
    return (Join-Path $supRoot 'chs\Support')
  }
  # 纯 AutoCAD 的漫游目录：%APPDATA%\Autodesk\AutoCAD <year>\<rName>\<lang>\Support
  $roots = @(
    (Join-Path $env:APPDATA "Autodesk\AutoCAD $year\$rName"),
    (Join-Path $env:APPDATA "Autodesk\AutoCAD $year")
  )
  foreach ($root in $roots) {
    foreach ($lg in @('chs', 'enu', 'cht')) {
      $c = Join-Path $root "$lg\Support"
      if (Test-Path $c) { return $c }
    }
  }
  return (Join-Path $env:APPDATA "Autodesk\AutoCAD $year\$rName\chs\Support")
}

$acadRoot = "HKLM:\SOFTWARE\Autodesk\AutoCAD"
if (Test-Path $acadRoot) {
  foreach ($rKey in Get-ChildItem $acadRoot) {
    $rName = $rKey.PSChildName  # 形如 R25.0
    foreach ($acadKey in Get-ChildItem $rKey.PSPath -ErrorAction SilentlyContinue) {
      if ($acadKey.PSChildName -notmatch '^ACAD-') { continue }
      $props = Get-ItemProperty $acadKey.PSPath -ErrorAction SilentlyContinue
      if (-not $props -or -not $props.ProductName) { continue }

      # 年份：优先 ProductName 里的四位年份，兜底 R 版本号（R25.0 -> 2025）
      $year = $null
      if ($props.ProductName -match '(\d{4})') { $year = $matches[1] }
      if (-not $year -and $rName -match '^R(\d+)') { $year = [string](2000 + [int]$matches[1]) }

      $isCivil = $props.ProductName -match 'Civil 3D'
      $kind = $(if ($isCivil) { 'civil3d' } else { 'autocad' })

      $installDir = $props.AcadLocation
      if ($installDir -and -not $installDir.EndsWith('\')) { $installDir += '\' }

      $result.candidates += [pscustomobject]@{
        year        = $year
        installDir  = $installDir
        rName       = $rName
        productKey  = $acadKey.PSChildName
        productName = $props.ProductName
        supportDir  = (Get-SupportDir -kind $kind -year $year -rName $rName)
        kind        = $kind
      }
    }
  }
}

# 多版本共存时优先受支持的最高版本；同等条件下 Civil 3D 优先（功能超集）。
$supportedCands = $result.candidates | Where-Object { $_.year -and [int]$_.year -ge 2025 }
$picked = $supportedCands | Where-Object { $_.kind -eq 'civil3d' } | Sort-Object { [int]$_.year } -Descending | Select-Object -First 1
if (-not $picked) {
  $picked = $supportedCands | Where-Object { $_.kind -eq 'autocad' } | Sort-Object { [int]$_.year } -Descending | Select-Object -First 1
}
$isSupported = $true
if (-not $picked) {
  $picked = $result.candidates | Sort-Object { [int]$_.year } -Descending | Select-Object -First 1
  $isSupported = $false
}

if ($picked) {
  $result.found = $true
  $result.supported = $isSupported
  $result.year = $picked.year
  $result.installDir = $picked.installDir
  $result.rName = $picked.rName
  $result.productName = $picked.productName
  $result.productKey = $picked.productKey
  $result.supportDir = $picked.supportDir
  $result.kind = $picked.kind
}

$result | ConvertTo-Json
