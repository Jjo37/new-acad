<#
.SYNOPSIS
  find-civil3d.ps1 — 注册表检测已安装的 Civil 3D 版本
.DESCRIPTION
  扫描 HKLM\SOFTWARE\Autodesk\AutoCAD\R* 下所有 ACAD-* 键，
  通过 ProductName 含 "Civil 3D" 判断，返回版本年份/安装目录/支持目录。
  输出 JSON，供 install.ps1 / build-plugin.ps1 调用。
.EXAMPLE
  $c3d = tools\find-civil3d.ps1 | ConvertFrom-Json
  if ($c3d.found) { $c3d.year; $c3d.installDir; $c3d.supportDir }
#>

$result = [ordered]@{ found = $false; year = $null; installDir = $null; rName = $null; productKey = $null; productName = $null; supportDir = $null }

$acadRoot = "HKLM:\SOFTWARE\Autodesk\AutoCAD"
if (Test-Path $acadRoot) {
  foreach ($rKey in Get-ChildItem $acadRoot) {
    $rName = $rKey.PSChildName  # 形如 R25.0
    foreach ($acadKey in Get-ChildItem $rKey.PSPath -ErrorAction SilentlyContinue) {
      if ($acadKey.PSChildName -notmatch '^ACAD-') { continue }
      $props = Get-ItemProperty $acadKey.PSPath -ErrorAction SilentlyContinue
      if (-not $props -or -not $props.ProductName) { continue }
      if ($props.ProductName -match 'Civil 3D') {
        # 年份：优先从 ProductName 提取（"Civil 3D 2025"），兜底 R 版本号（R25.0 → 2025）
        $year = $null
        if ($props.ProductName -match '(\d{4})') { $year = $matches[1] }
        if (-not $year -and $rName -match '^R(\d+)') { $year = [string](2000 + [int]$matches[1]) }
        $installDir = $props.AcadLocation
        if ($installDir -and -not $installDir.EndsWith('\')) { $installDir += '\' }
        $result.found = $true
        $result.year = $year
        $result.installDir = $installDir
        $result.rName = $rName
        $result.productName = $props.ProductName
        $result.productKey = $acadKey.PSChildName
        $result.supportDir = "$env:APPDATA\Autodesk\C3D $year\chs\Support"
        break
      }
    }
    if ($result.found) { break }
  }
}

$result | ConvertTo-Json
