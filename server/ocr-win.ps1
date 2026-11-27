param(
  [string]$Path = '',
  [string]$Out = '',
  [string]$Lang = '',
  [int]$MaxPages = 30,
  [int]$Width = 2000,
  [switch]$List
)
# 2026-10-14: OCR for scanned PDFs / images via built-in Windows WinRT (zero bundle).
# Windows.Data.Pdf renders pages; Windows.Media.Ocr recognizes. Runs under PowerShell 5.1 (WinRT-capable).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
$asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
function Await($op, $t) { $m = $asTaskGeneric.MakeGenericMethod($t); $task = $m.Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
function AwaitAction($act) { $task = $asTaskAction.Invoke($null, @($act)); $task.Wait(-1) | Out-Null }

[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType = WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null

$langs = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
if ($List) {
  if ($Out) { [System.IO.File]::WriteAllText($Out, ($langs -join "`n"), (New-Object System.Text.UTF8Encoding($false))) }
  Write-Output ($langs -join ',')
  exit 0
}
if (-not $Path -or -not $Out) { throw 'Path and Out are required (unless -List)' }
if ($langs.Count -eq 0) { throw 'OCR_UNAVAILABLE: no OCR recognizer languages installed on this machine' }

# language choice: explicit -> zh-Hans-CN -> zh-Hans -> en-US -> first
$pick = ''
if ($Lang -and ($langs -contains $Lang)) { $pick = $Lang }
elseif ($langs -contains 'zh-Hans-CN') { $pick = 'zh-Hans-CN' }
elseif ($langs -contains 'zh-Hans') { $pick = 'zh-Hans' }
elseif ($langs -contains 'en-US') { $pick = 'en-US' }
else { $pick = $langs[0] }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($pick)))
if (-not $engine) { throw ("OCR_UNAVAILABLE: no engine for '" + $pick + "' (available: " + ($langs -join ',') + ")") }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("[ocr] lang=$pick available=$($langs -join ',')")

$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])

function OcrBitmap($bmp) { return Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult]) }

if ($ext -eq '.pdf') {
  $doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
  $total = $doc.PageCount
  $limit = [Math]::Min($total, $MaxPages)
  [void]$sb.AppendLine("[ocr] pdf pages=$total rendered=$limit")
  for ($i = 0; $i -lt $limit; $i++) {
    $page = $doc.GetPage($i)
    $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $opts = New-Object Windows.Data.Pdf.PdfPageRenderOptions
    $opts.DestinationWidth = [uint32]$Width
    AwaitAction ($page.RenderToStreamAsync($stream, $opts))
    $stream.Seek(0)
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $r = OcrBitmap $bmp
    [void]$sb.AppendLine("===== page $($i + 1) =====")
    [void]$sb.AppendLine($r.Text)
    $page.Dispose()
  }
}
else {
  $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
  $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
  $bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
  $r = OcrBitmap $bmp
  [void]$sb.AppendLine("[ocr] image $($decoder.PixelWidth)x$($decoder.PixelHeight)")
  [void]$sb.AppendLine($r.Text)
}
[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("OK lang=" + $pick)
