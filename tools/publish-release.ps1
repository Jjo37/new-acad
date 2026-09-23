# publish-release.ps1 -- create a GitHub release with assets the immutable-release-safe way.
# Flow: create DRAFT -> upload assets -> publish. (An already-published release rejects asset uploads
# with HTTP 422 "Cannot upload assets to an immutable release".)
#
# Credentials: read from the OS credential store via `git credential fill` (never written to disk,
# never printed). Requires a previously stored GitHub credential for https://github.com.
#
# ASCII-only script body: PowerShell 5.1 reads .ps1 as GBK on zh-CN Windows, so keep Chinese text
# (release notes) in a UTF-8 file passed via -NotesFile.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File publish-release.ps1 -Repo Jjo37/new-acad -Tag v1.3.4 `
#       -Name "new-acad v1.3.4" -NotesFile .\release-notes.md `
#       -Assets D:\new-acad-release\new-acad-setup-v1.3.4.exe, D:\new-acad-release\new-acad-v1.3.4.zip
param(
  [Parameter(Mandatory = $true)][string]$Repo,
  [Parameter(Mandatory = $true)][string]$Tag,
  [string]$Name = "",
  [Parameter(Mandatory = $true)][string]$NotesFile,
  [Parameter(Mandatory = $true)][string[]]$Assets,
  [string]$TargetCommitish = "",
  [switch]$KeepDraft,
  [switch]$LegacyUpload
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Name) { $Name = $Tag }
if (-not (Test-Path $NotesFile)) { throw "notes file not found: $NotesFile" }
$notes = [IO.File]::ReadAllText($NotesFile)
$Api = "https://api.github.com/repos/$Repo"
$Up  = "https://uploads.github.com/repos/$Repo"

$cred  = ("protocol=https`nhost=github.com`n`n" | git credential fill 2>$null) -join "`n"
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value
if (-not $token) { throw 'no stored GitHub credential (run a git push first, or set one via git credential approve)' }
Write-Host "credential loaded (len $($token.Length))"

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromHours(2)
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('new-acad-release-script')
$client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

function Api {
  param([string]$Method, [string]$Url, $BodyObj)
  $m = New-Object System.Net.Http.HttpMethod($Method.ToUpper())
  $req = New-Object System.Net.Http.HttpRequestMessage($m, $Url)
  if ($null -ne $BodyObj) {
    $json = $BodyObj | ConvertTo-Json -Compress -Depth 6
    $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
  }
  $resp = $client.SendAsync($req).Result
  $txt  = $resp.Content.ReadAsStringAsync().Result
  if (-not $resp.IsSuccessStatusCode) { throw ("HTTP {0} on {1} {2} : {3}" -f [int]$resp.StatusCode, $Method, $Url, $txt) }
  if ([string]::IsNullOrWhiteSpace($txt)) { return $null }
  return ($txt | ConvertFrom-Json)
}

# fail early if the tag is unusable (immutable releases burn tag names permanently)
Write-Host "--- checks ---"
$existing = Api 'GET' "$Api/releases?per_page=50" $null
foreach ($x in $existing) { if ($x.tag_name -eq $Tag) { throw "release for tag $Tag already exists: $($x.html_url)" } }

$body = @{ tag_name = $Tag; name = $Name; body = $notes; draft = $true }
if ($TargetCommitish) { $body.target_commitish = $TargetCommitish }
$rel = Api 'POST' "$Api/releases" $body
Write-Host ("draft created: id={0} tag={1}" -f $rel.id, $rel.tag_name)

# 2026-09-14 footgun fix: passing "a,b" (one quoted string) used to be treated as a SINGLE path and
# silently skipped -> published a release with 0 assets. Now split every element on commas and FAIL LOUDLY
# if an asset is missing. Never skip silently.
$assetList = @()
foreach ($a in $Assets) {
  foreach ($one in ([string]$a).Split(',')) {
    $t = $one.Trim()
    if ($t) { $assetList += $t }
  }
}
if ($assetList.Count -eq 0) { throw 'no assets to upload' }

# 2026-09-15: upload with curl instead of HttpClient.
# HttpClient hangs silently when the connection half-dies (observed: 64.5 MB with zero progress for
# 30+ minutes). curl aborts a stalled transfer (--speed-limit/--speed-time) and retries
# (--retry/--retry-all-errors); the token travels in a stdin config (-K -), so it never lands on the
# command line or on disk. -LegacyUpload keeps the old HttpClient path as a fallback.
$curl = $null
if (-not $LegacyUpload) { $curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source }
$loginCfg = "header = `"Authorization: Bearer $token`"`nheader = `"X-GitHub-Api-Version: 2022-11-28`"`n"
$expect = @{}

foreach ($f in $assetList) {
  if (-not (Test-Path $f)) { throw ("asset not found: " + $f) }
  $name = Split-Path $f -Leaf
  $localSize = (Get-Item $f).Length
  $expect[$name] = $localSize
  Write-Host ("uploading {0} ({1} MB) ..." -f $name, [math]::Round($localSize / 1MB, 1))
  $sw = [Diagnostics.Stopwatch]::StartNew()
  if ($curl) {
    $url = "$Up/releases/{0}/assets?name={1}" -f $rel.id, $name
    $outJson = Join-Path $env:TEMP ("gh_asset_" + $name + ".json")
    $loginCfg | & $curl -sS --fail-with-body --retry 8 --retry-delay 10 --retry-all-errors --speed-limit 4096 --speed-time 60 --max-time 2400 --progress-bar -X POST $url -H "Content-Type: application/octet-stream" --data-binary "@$f" -K - -o $outJson -w "http=%{http_code} bytes=%{size_upload} secs=%{time_total} speed=%{speed_upload}`n"
    $sw.Stop()
    if ($LASTEXITCODE -eq 0) {
      Write-Host ("  OK {0} in {1:N0}s" -f $name, $sw.Elapsed.TotalSeconds)
    } else {
      Write-Host ("  FAIL {0} : curl exit {1}" -f $name, $LASTEXITCODE)
      if (Test-Path $outJson) { Write-Host ("       " + (Get-Content $outJson -Raw)) }
      throw ("asset upload failed: " + $name)
    }
  } else {
    $fs = [IO.File]::OpenRead($f)
    $content = New-Object System.Net.Http.StreamContent($fs)
    $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
    $resp = $client.PostAsync(("$Up/releases/{0}/assets?name={1}" -f $rel.id, $name), $content).Result
    $sw.Stop()
    $txt = $resp.Content.ReadAsStringAsync().Result
    if ($resp.IsSuccessStatusCode) {
      $a = $txt | ConvertFrom-Json
      Write-Host ("  OK {0} -> {1} MB in {2:N1}s" -f $name, [math]::Round($a.size / 1MB, 1), $sw.Elapsed.TotalSeconds)
    } else {
      Write-Host ("  FAIL {0} : HTTP {1} {2}" -f $name, [int]$resp.StatusCode, $txt)
      $fs.Dispose()
      throw ("asset upload failed: " + $name)
    }
    $fs.Dispose()
  }
}

# 2026-09-15: verify every asset is uploaded and matches the local size BEFORE publishing.
# A published release is immutable: a bad asset would force a new tag.
$chk = Api 'GET' ("$Api/releases/{0}" -f $rel.id) $null
$problems = @()
foreach ($name in $expect.Keys) {
  $found = $null
  foreach ($a in $chk.assets) { if ($a.name -eq $name) { $found = $a } }
  if (-not $found) { $problems += ("missing: " + $name); continue }
  if ($found.state -ne 'uploaded') { $problems += ("{0}: state={1}" -f $name, $found.state) }
  elseif ([int64]$found.size -ne [int64]$expect[$name]) { $problems += ("{0}: size {1} != local {2}" -f $name, $found.size, $expect[$name]) }
}
if ($problems.Count -gt 0) {
  Write-Host "ASSET VERIFY FAILED:"
  $problems | ForEach-Object { Write-Host ("   " + $_) }
  throw ("not publishing; draft kept: " + $rel.html_url)
} else {
  Write-Host ("assets verified: {0} uploaded" -f $expect.Keys.Count)
}

if ($KeepDraft) {
  Write-Host "draft kept (not published). Publish later: PATCH $Api/releases/$($rel.id) {draft:false}"
} else {
  $pub = Api 'PATCH' "$Api/releases/$($rel.id)" @{ draft = $false }
  Write-Host ("PUBLISHED: {0}" -f $pub.html_url)
}

Write-Host "--- final ---"
foreach ($x in (Api 'GET' "$Api/releases?per_page=10" $null)) {
  Write-Host ("{0} | draft={1} | assets={2}" -f $x.tag_name, $x.draft, ($x.assets | Measure-Object).Count)
  foreach ($a in $x.assets) { Write-Host ("   - {0} ({1} MB) {2}" -f $a.name, [math]::Round($a.size / 1MB, 1), $a.browser_download_url) }
}
$client.Dispose()
