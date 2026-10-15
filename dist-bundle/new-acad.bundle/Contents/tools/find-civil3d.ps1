$result = [ordered]@{ found = $false; supported = $false; year = $null; installDir = $null; rName = $null; productKey = $null; productName = $null; supportDir = $null; candidates = @() }

$acadRoot = "HKLM:\SOFTWARE\Autodesk\AutoCAD"
if (Test-Path $acadRoot) {
  foreach ($rKey in Get-ChildItem $acadRoot) {
    $rName = $rKey.PSChildName  # 形如 R25.0
    foreach ($acadKey in Get-ChildItem $rKey.PSPath -ErrorAction SilentlyContinue) {
      if ($acadKey.PSChildName -notmatch '^ACAD-') { continue }
      $props = Get-ItemProperty $acadKey.PSPath -ErrorAction SilentlyContinue
      if (-not $props -or -not $props.ProductName) { continue }
      if ($props.ProductName -match 'Civil 3D') {
        # 年份：优先从 ProductName 提取（"Civil 3D 2025"），兜底 R 版本号（R25.0 -> 2025）
        $year = $null
        if ($props.ProductName -match '(\d{4})') { $year = $matches[1] }
        if (-not $year -and $rName -match '^R(\d+)') { $year = [string](2000 + [int]$matches[1]) }
        $installDir = $props.AcadLocation
        if ($installDir -and -not $installDir.EndsWith('\')) { $installDir += '\' }
        # 2026-10-15: 支持目录语言按实际存在的探测（chs/enu/cht），不再写死 chs（英文版 C3D 是 enu）
        $supRoot = Join-Path $env:APPDATA "Autodesk\C3D $year"
        $supDir = $null
        foreach ($lg in @('chs','enu','cht')) { $c = Join-Path $supRoot "$lg\Support"; if (Test-Path $c) { $supDir = $c; break } }
        if (-not $supDir) { $supDir = Join-Path $supRoot 'chs\Support' }
        $result.candidates += [pscustomobject]@{ year = $year; installDir = $installDir; rName = $rName; productKey = $acadKey.PSChildName; productName = $props.ProductName; supportDir = $supDir }
      }
    }
  }
}

# 2026-09-28: 本插件为 net8.0 构建，仅 2025/2026（R25.x）可用；2024 及更早是 .NET Framework 4.8，装上也加载不了。
# 多版本共存时优先选受支持的最高版本（旧实现按 R 键字母序 break，机器同时有 2024/2025 会误选 2024）。
$picked = $result.candidates | Where-Object { $_.year -and [int]$_.year -ge 2025 } | Sort-Object { [int]$_.year } -Descending | Select-Object -First 1
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
}

$result | ConvertTo-Json
