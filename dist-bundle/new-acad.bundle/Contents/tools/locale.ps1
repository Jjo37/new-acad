# locale.ps1 — new-acad 脚本共用语言助手（2026-09-21）
# 目的: 面板切英文后，外部 .ps1 脚本的输出也跟着英文
# 规则: 读 config.json 的 locale（auto = 跟随系统 UI 语言）；所有用户可见文案走 T("中文","English")
# 用法:
#   . (Join-Path $PSScriptRoot "locale.ps1")
#   Initialize-NaLocale $ROOT | Out-Null
#   Write-Host (T "中文文案" "English text")
# 注意: 本文件必须存为 UTF-8 带 BOM（PowerShell 5.1 中文环境）

$Global:NaLang = 'zh-CN'

function Initialize-NaLocale {
  param([string]$Root)
  $loc = ''
  if ($Root) {
    $cfgPath = Join-Path $Root 'config.json'
    if (Test-Path $cfgPath) {
      try {
        $c = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($c.locale) { $loc = [string]$c.locale }
      } catch { }
    }
  }
  if ([string]::IsNullOrWhiteSpace($loc) -or $loc.Trim().ToLower() -eq 'auto') {
    try { $loc = [System.Globalization.CultureInfo]::CurrentUICulture.Name } catch { $loc = 'zh-CN' }
  }
  if ($loc -match '^(?i)zh') { $Global:NaLang = 'zh-CN' } else { $Global:NaLang = 'en-US' }
  return $Global:NaLang
}

function T {
  param([string]$zh, [string]$en)
  if ($Global:NaLang -eq 'en-US') { return $en } else { return $zh }
}
