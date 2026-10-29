param()
# ocr-check.ps1 —— 检查本机 Windows OCR 语言包（扫描件 PDF OCR 需要），供 install 预检 / 手动排查
# 退出码：0 有可用语言包；1 无语言包；2 无法读取引擎（非 Windows/WinRT 不可用）
$ErrorActionPreference = 'Stop'
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
  [Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType = WindowsRuntime] | Out-Null
  $langs = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
} catch {
  Write-Host "[ocr-check] 无法读取 OCR 引擎（非 Windows 或 WinRT 不可用）: $($_.Exception.Message)" -ForegroundColor Yellow
  exit 2
}
if ($langs.Count -eq 0) {
  Write-Host "[ocr-check] ✗ 未安装任何 OCR 语言包 —— 扫描件 PDF 无法识别。" -ForegroundColor Yellow
  Write-Host "            修复：设置 → 时间和语言 → 语言和区域 → 选语言 → 语言选项 → 添加「光学字符识别」组件。" -ForegroundColor Gray
  exit 1
}
Write-Host ("[ocr-check] ✓ 可用 OCR 语言: " + ($langs -join ", ")) -ForegroundColor Green
$zh = @($langs | Where-Object { $_ -like "zh*" })
if ($zh.Count -gt 0) { Write-Host ("[ocr-check] 中文识别: 已具备（" + ($zh -join ", ") + "）") -ForegroundColor Green }
else { Write-Host "[ocr-check] 提示：未装中文 OCR 组件，中文扫描件将按英文识别（质量差）。建议添加中文「光学字符识别」。" -ForegroundColor Yellow }
exit 0
