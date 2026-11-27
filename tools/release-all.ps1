# release-all.ps1 -- one command for the whole open-source release pipeline.
#
# Codifies the 6-step flow used for v1.7.4 (2026-10-11). Every step aborts loudly on failure.
#
#   1 preflight   version format / notes file present / ISCC present / repo cleanliness
#   2 bump        package.json -> tools/sync-version.js -> --check must pass
#   3 build+gate  build-plugin.ps1, tools/release-gate.js (must exit 0)
#   4 sync        tools/sync-dev-release.ps1 -Apply  (+ .cs parity check dev vs release)
#   4b evidence   tools/check-host-evidence.js --require (plain-AutoCAD proof from the other machine)
#   5 artifacts   build-dist.ps1 -Zip, build-installer.ps1, tools/build-bundle.ps1
#   6 audit       unpack the portable zip: hashes / no source in package / no secrets
#   7 publish     publish-public.ps1 -Tag <tag> + git push main/tag + publish-release.ps1
#                 ONLY with -Publish (a tag is immutable -> explicit opt-in required)
#
# Usage:
#   powershell -File tools\release-all.ps1 -Version 1.7.5 -DryRun   # validate inputs, print the plan
#   powershell -File tools\release-all.ps1 -Version 1.7.5           # local steps 1-6 (nothing external)
#   powershell -File tools\release-all.ps1 -Version 1.7.5 -Publish  # + public snapshot/push + GitHub release
#
# Notes:
#   - The release notes file is REQUIRED and never generated: a human must write and review it
#     (numbers verified, limitations listed). Default: tools/public-templates/release-notes-<ver>.md
#   - ASCII-only on purpose: PowerShell 5.1 reads .ps1 as GBK on zh-CN Windows.
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Tag = "",
  [string]$NotesFile = "",
  [string]$Dev = "D:\new-acad",
  [string]$Rel = "D:\new-acad-release",
  [string]$Pub = "D:\new-acad-public",
  [string]$Repo = "Jjo37/new-acad",
  # 2026-10-27: a corrected build keeps the version number and only adds an artifact suffix (e.g. fix3)
  [string]$ArtifactSuffix = "",
  [string]$ReleaseName = "",
  # 2026-10-27: red-line files require the real-host tier; pass -WithCad with Civil 3D running
  [switch]$WithCad,
  # 2026-10-27: plain AutoCAD lives on another machine; its evidence file is required to publish
  [switch]$SkipHostEvidence,
  [switch]$Publish,
  [switch]$DryRun,
  [switch]$NoCommit,
  [switch]$StopCad
)
$ErrorActionPreference = 'Stop'

function Say($m)  { Write-Host "[release] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[release] $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[release] ABORT: $m" -ForegroundColor Red; exit 1 }
function Step($name, [scriptblock]$body) {
  Say "== $name"
  & $body   # helpers abort on failure; do not re-check a stale LASTEXITCODE here
}
function RunNode($tool, [string[]]$a = @()) {
  Say ("  node " + (Split-Path $tool -Leaf) + " " + ($a -join ' '))
  & node $tool @a
  if ($LASTEXITCODE -ne 0) { Die ("node " + (Split-Path $tool -Leaf) + " exit " + $LASTEXITCODE) }
}
function RunPs($file, [string[]]$a = @()) {
  Say ("  powershell -File " + $file + " " + ($a -join ' '))
  & powershell -NoProfile -ExecutionPolicy Bypass -File $file @a
  if ($LASTEXITCODE -ne 0) { Die ("powershell " + $file + " exit " + $LASTEXITCODE) }
}
function GitRun($dir, [string[]]$a) {
  Push-Location $dir
  try {
    & git @a
    if ($LASTEXITCODE -ne 0) { Die ("git " + ($a -join ' ') + " in " + $dir + " exit " + $LASTEXITCODE) }
  } finally { Pop-Location }
}
function DirtyCount($dir) {
  Push-Location $dir
  try { return @(& git status --short 2>$null).Count } finally { Pop-Location }
}
function CsCount($dir) {
  return @(Get-ChildItem (Join-Path $dir 'plugin\AcBridge-v24\src') -Filter *.cs -ErrorAction SilentlyContinue).Count
}

# ---------- 1. preflight ----------
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die "version must look like 1.2.3 (got '$Version')" }
if (-not $Tag) { $Tag = "v$Version-release" }
$SUFFIX = if ($ArtifactSuffix) { "-" + $ArtifactSuffix } else { "" }
$ARTIFACT = "v$Version$SUFFIX"
if (-not $ReleaseName) { $ReleaseName = if ($ArtifactSuffix) { "new-acad v$Version ($ArtifactSuffix)" } else { "new-acad v$Version" } }
if (-not $NotesFile) { $NotesFile = Join-Path $Dev "tools\public-templates\release-notes-$Version.md" }
if (-not (Test-Path $NotesFile)) { Die "release notes not found: $NotesFile  (write + review it first; it is never auto-generated)" }
foreach ($p in @($Dev, $Rel, $Pub)) { if (-not (Test-Path $p)) { Die "path not found: $p" } }
$iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe', "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Die "ISCC.exe (Inno Setup 6) not found - build-installer.ps1 will fail" }
$acad = @(Get-Process acad -ErrorAction SilentlyContinue)
if ($acad.Count -gt 0) {
  if ($StopCad) { Warn "stopping Civil 3D so build-plugin can refresh the Autoloader bundle"; $acad | Stop-Process -Force; Start-Sleep -Seconds 4 }
  else { Warn "Civil 3D is running: build-plugin.ps1 cannot replace the Autoloader bundle (local artifacts are still fine). Use -StopCad to close it." }
}
Say "version=$Version tag=$Tag"
Say "notes=$NotesFile"
foreach ($r in @(
    [pscustomobject]@{ Path = $Dev; Label = 'dev' },
    [pscustomobject]@{ Path = $Rel; Label = 'release' },
    [pscustomobject]@{ Path = $Pub; Label = 'public' }
  )) {
  $n = DirtyCount $r.Path
  if ($n -gt 0) { Warn ("{0} tree has {1} uncommitted change(s): {2}" -f $r.Label, $n, $r.Path) }
}

$commitDev  = if ($NoCommit) { ' [not committed: -NoCommit]' } else { ', then commit dev' }
$commitRel  = if ($NoCommit) { ' [not committed: -NoCommit]' } else { ', then commit release' }
$publishLine = if ($Publish) {
  "7 publish   publish-public.ps1 -Tag $Tag ; git push main + tag ; publish-release.ps1 -Repo $Repo -Tag $Tag (IMMUTABLE)"
} else {
  "7 publish   SKIPPED (this run stops after the audit; pass -Publish to snapshot/push/create the release)"
}
$plan = @(
  ("2 bump      package.json -> $Version, then tools/sync-version.js (--check must pass)" + $commitDev),
  "3 build     build-plugin.ps1 + tools/release-gate.js (must exit 0)",
  ("4 sync      tools/sync-dev-release.ps1 -Apply (plugin src must travel) + .cs parity check" + $commitRel),
  ("5 artifacts build-dist.ps1 -Zip ; build-installer.ps1 ; tools/build-bundle.ps1   (in $Rel)"),
  ("5b evidence check-host-evidence.js --require (plain-AutoCAD proof from the other machine; -SkipHostEvidence to bypass)"),
  "6 audit     unpack the zip -> hashes / no source in package / no secrets",
  $publishLine
)
Say "plan:"
$plan | ForEach-Object { Warn ("   " + $_) }
if ($DryRun) { Say "DRY RUN - nothing was modified."; exit 0 }

# ---------- 2. bump ----------
Step "2 bump to $Version" {
  $pkg = Join-Path $Dev 'package.json'
  $txt = [System.IO.File]::ReadAllText($pkg, [System.Text.Encoding]::UTF8)
  $new = [regex]::Replace($txt, '"version"\s*:\s*"\d+\.\d+\.\d+"', ('"version": "' + $Version + '"'), 1)
  if ($new -ne $txt) { [System.IO.File]::WriteAllText($pkg, $new, (New-Object System.Text.UTF8Encoding($false))); Say "  package.json -> $Version" }
  else { Say "  package.json already $Version" }
  RunNode (Join-Path $Dev 'tools\sync-version.js')
  RunNode (Join-Path $Dev 'tools\sync-version.js') @('--check')
}
if (-not $NoCommit) {
  if ((DirtyCount $Dev) -gt 0) { GitRun $Dev @('add', '-A'); GitRun $Dev @('commit', '-m', "release: v$Version bump + release notes") }
}

# ---------- 3. build + gate ----------
Step "3 build plugin + release gate" {
  $buildArgs = @()
  if ($acad.Count -gt 0) { $buildArgs += '-AllowBundleLocked' }
  RunPs (Join-Path $Dev 'build-plugin.ps1') $buildArgs
  $gateArgs = @()
  if ($WithCad) {
    $gateArgs += '--with-cad'
    # 2026-10-27: CAD sitting on the Start page makes every live regression fail with
    # CIVIL3D.NO_DRAWING. Open a drawing before the gate instead of relying on a human.
    RunNode (Join-Path $Dev 'tools\ensure-drawing.js')
  }
  RunNode (Join-Path $Dev 'tools\release-gate.js') $gateArgs
}

# ---------- 4. sync dev -> release ----------
Step "4 sync dev -> release" {
  RunPs (Join-Path $Dev 'tools\sync-dev-release.ps1') @('-Apply', '-From', $Dev, '-To', $Rel)
  $csDev = CsCount $Dev; $csRel = CsCount $Rel
  Say "  plugin src .cs: dev=$csDev release=$csRel"
  if ($csDev -ne $csRel) { Die "plugin source parity FAILED (dev $csDev vs release $csRel) - the sync must carry src/" }
}
if (-not $NoCommit) {
  if ((DirtyCount $Rel) -gt 0) { GitRun $Rel @('add', '-A'); GitRun $Rel @('commit', '-m', "release: v$Version sync + artifacts") }
}

# ---------- 5. artifacts ----------
Step "5 build artifacts in release" {
  $distArgs = @('-Zip')
  $instArgs = @()
  if ($ArtifactSuffix) { $distArgs += @('-ArtifactSuffix', $ArtifactSuffix); $instArgs += @('-ArtifactSuffix', $ArtifactSuffix) }
  # 2026-11-23 P35: hand the dev-side plugin source commit to the package.
  # The installed tree has no .git, so off-machine evidence cells must carry it explicitly.
  $pluginCommit = ""
  try { $pluginCommit = (& git -C $Dev log -1 --format=%H -- plugin/AcBridge-v24/src).Trim() } catch { }
  if ($pluginCommit) { $env:NEW_ACAD_PLUGIN_COMMIT = $pluginCommit; Say ("  plugin commit = " + $pluginCommit.Substring(0,8)) }
  RunPs (Join-Path $Rel 'build-dist.ps1') $distArgs
  RunPs (Join-Path $Rel 'build-installer.ps1') $instArgs
  RunPs (Join-Path $Rel 'tools\build-bundle.ps1') @('-Root', $Rel)
}
$setup  = Join-Path $Rel ("new-acad-setup-" + $ARTIFACT + ".exe")
$zip    = Join-Path $Rel ("new-acad-" + $ARTIFACT + ".zip")
# 2026-10-19: package cleanliness check (personal config / user data / dev docs / tool whitelist / leaked keys)
$bundleContents = Join-Path $Rel 'dist-bundle\new-acad.bundle\Contents'
if (Test-Path $bundleContents) {
  RunNode (Join-Path $Dev 'tools\check-package-clean.js') @($bundleContents)
  # 2026-10-19: package must also RUN, not just be clean (missing modules shipped twice)
  RunNode (Join-Path $Dev 'tools\check-package-runtime.js') @($bundleContents)
  RunNode (Join-Path $Dev 'tools\check-package-runtime.js') @((Join-Path $Rel 'dist'))
  RunNode (Join-Path $Dev 'tools\smoke-packaged-relay.js') @('--from', (Join-Path $Rel 'dist'))
}
$bundle = Join-Path $Rel 'dist-bundle\new-acad.bundle.zip'
foreach ($f in @($setup, $zip, $bundle)) { if (-not (Test-Path $f)) { Die "artifact missing: $f" } }
$szSetup = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$szZip   = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$szBnd   = [math]::Round((Get-Item $bundle).Length / 1MB, 1)
Say "  setup ${szSetup}MB / zip ${szZip}MB / bundle ${szBnd}MB"
if ($szSetup -lt 20) { Die "installer looks too small (${szSetup}MB)" }
if ($szZip -lt 50)   { Die "portable zip looks too small (${szZip}MB) - did build-installer merge the setup into it?" }
if ($szBnd -lt 30)   { Die "bundle zip looks too small (${szBnd}MB)" }

# ---------- 5z. commit the rebuilt dist-bundle (Bro 2026-10-28: v1.11.0-fix3 and the next
# version must be treated the same way -- the bundle is the release snapshot in the repo, so it
# gets committed with the artifacts. Otherwise every release leaves uncommitted churn behind
# (2026-10-27 left 5 modified files). Keep this file ASCII-only: PS 5.1 reads a BOM-less file as
# ANSI/GBK, and non-ASCII bytes break parsing (that is why check-ps-ascii exists).
if (-not $NoCommit) {
  Push-Location $Rel
  try { $bundleDirty = @(& git status --porcelain 'dist-bundle' 2>$null).Count } finally { Pop-Location }
  if ($bundleDirty -gt 0) {
    GitRun $Rel @('add', 'dist-bundle')
    GitRun $Rel @('commit', '-m', "release: rebuild dist-bundle for v$Version")
    Say "  dist-bundle rebuilt and committed ($bundleDirty files)"
  } else {
    Say "  dist-bundle already up to date"
  }
}

# ---------- 5a. clean-install smoke (local, no installer/registry) ----------
# Unzip the artifact we just built, assert the package structure, start the packaged relay with
# the packaged node.exe, and require the packaged DLL to be byte-identical to the release build.
Step "5a clean-install smoke" {
  RunNode (Join-Path $Dev 'tools\clean-install-smoke.js') @('--zip', $zip)
}
# ---------- 5b. plain-AutoCAD evidence (required) ----------
# The plain-AutoCAD host is a different machine, so this release cannot run that smoke here.
# tools/smoke-plain-acad.js --evidence writes a JSON bound to the plugin DLL hash; that file
# must live in docs/audit/host-evidence/ or the release refuses to continue.
Step "5b plain-AutoCAD evidence" {
  if ($SkipHostEvidence) {
    Warn "  SKIPPED by -SkipHostEvidence - this build is NOT verified on plain AutoCAD"
  } else {
    $packagedDll = Join-Path $Rel 'plugin\AcBridge-v24\Civil3DMcpPlugin.dll'
    RunNode (Join-Path $Dev 'tools\check-host-evidence.js') @('--require', '--dll', $packagedDll)
  }
}

# ---------- 6. audit the portable package ----------
Step "6 audit the packaged zip" {
  $tmp = Join-Path $env:TEMP ("newacad-audit-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
  try {
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    foreach ($rel2 in @('plugin\AcBridge-v24\Civil3DMcpPlugin.dll', 'server\panel-relay.js', 'server\file-tools.js')) {
      $pa = Join-Path $tmp $rel2
      if (-not (Test-Path $pa)) { Die "package missing: $rel2" }
      $ha = (Get-FileHash $pa -Algorithm MD5).Hash
      $hb = (Get-FileHash (Join-Path $Rel $rel2) -Algorithm MD5).Hash
      if ($ha -ne $hb) { Die "package mismatch: $rel2" }
    }
    Say "  hashes match the release build"
    if (Test-Path (Join-Path $tmp 'plugin\AcBridge-v24\src')) { Die "package contains plugin source (should not)" }
    $cs = @(Get-ChildItem $tmp -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue).Count
    if ($cs -gt 0) { Die "package contains $cs .cs file(s) (should not)" }
    $sec = @(Get-ChildItem $tmp -Recurse -File -Include *.pem, *.b64, *.pfx -ErrorAction SilentlyContinue).Count
    if ($sec -gt 0) { Die "package contains $sec key/cert file(s)" }
    $cfg = Join-Path $tmp 'config.json'
    if (Test-Path $cfg) {
      $c = [System.IO.File]::ReadAllText($cfg, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
      if ($c.relayToken -or $c.relaySessionKey -or $c.llm.apiKey) { Die "config.json ships with a key/token set" }
    }
    $dll = Join-Path $tmp 'plugin\AcBridge-v24\Civil3DMcpPlugin.dll'
    $fv = (Get-Item $dll).VersionInfo.FileVersion
    Say ("  DLL FileVersion " + $fv)
    # 2026-10-23: the pre-packaging gate can only see the PREVIOUS dist, so the version
    # invariant lives here -- on the package that is actually about to ship.
    if (-not $fv.StartsWith($Version)) { Die "packaged DLL FileVersion $fv != release $Version" }
    $distPath = Join-Path $Rel 'dist'
    RunNode (Join-Path $Dev 'tools\check-package-contents.js') @('--strict', '--dist', $distPath)

    # 2026-10-24: the docs inside the package must be the docs in the release tree. A stale manual
    # is user-visible: it names the download file and the version the reader is looking at, and it
    # used to say v1.10.0 while the package was v1.11.0 (nobody noticed for a whole release).
    # 2026-11-27: the package carries the manual twice on purpose - the CHS-named manual (Chinese filename, what
    # the user sees in Explorer) and help.html (ASCII alias, because Hank.lsp registers HANK_SHOW ->
    # help.html and LISP paths with CJK characters are a portability hazard). The rule is therefore:
    # 1 or 2 html files in the package, ALL byte-identical, and the release tree manual must be the
    # same bytes. (The old "exactly 1" rule fired only now because the alias is new in this release.)
    $pkgHtml = @(Get-ChildItem $tmp -File -Filter *.html -ErrorAction SilentlyContinue)
    $relHtml = @(Get-ChildItem $Rel -File -Filter *.html -ErrorAction SilentlyContinue)
    if ($pkgHtml.Count -lt 1 -or $pkgHtml.Count -gt 2) { Die "package html manuals: expected 1-2 (manual + help.html alias), found $($pkgHtml.Count)" }
    if ($relHtml.Count -ne 1) { Die "release tree should carry exactly 1 html manual, found $($relHtml.Count)" }
    $pkgHashes = @($pkgHtml | ForEach-Object { (Get-FileHash $_.FullName -Algorithm MD5).Hash } | Select-Object -Unique)
    if ($pkgHashes.Count -ne 1) { Die "packaged html manuals differ from each other (help.html must be the same manual)" }
    if ($pkgHashes[0] -ne (Get-FileHash $relHtml[0].FullName -Algorithm MD5).Hash) {
      Die "packaged manual differs from the release-tree manual (stale dist? rebuild artifacts)"
    }
    $pkgGuide = Join-Path $tmp 'workflow-guide.md'
    if (Test-Path $pkgGuide) {
      if ((Get-FileHash $pkgGuide -Algorithm MD5).Hash -ne (Get-FileHash (Join-Path $Rel 'workflow-guide.md') -Algorithm MD5).Hash) {
        Die "packaged workflow-guide.md differs from the release tree"
      }
    }
    Say "  packaged docs match the release tree"
    Say "  no source / no keys / clean config"
  } finally { if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } }
}

# ---------- 7. publish ----------
if (-not $Publish) {
  Warn "steps 1-6 done; NOT publishing. Re-run with -Publish to snapshot/push/create the release (tag $Tag is immutable)."
  Say "local artifacts:"
  Say "  $setup"
  Say "  $zip"
  Say "  $bundle"
  exit 0
}
Step "7 preflight (tag not already used by a release)" {
  & node (Join-Path $Dev 'tools\check-release-tag.js') --repo $Repo --tag $Tag
  if ($LASTEXITCODE -eq 3) { Die "tag $Tag already used by a release (immutable -> not reusable). Bump the version and retry; never delete a published release." }
}

Step "7a public snapshot" {
  RunPs (Join-Path $Dev 'tools\publish-public.ps1') @('-Tag', $Tag)
  $cs = @(Get-ChildItem (Join-Path $Pub 'plugin\AcBridge-v24\src') -Filter *.cs -ErrorAction SilentlyContinue).Count
  if ($cs -eq 0) { Die "public snapshot has no plugin source (.cs)" }
  $bin = @(Get-ChildItem $Pub -Recurse -File -Include *.dll, *.exe, *.zip -ErrorAction SilentlyContinue).Count
  if ($bin -gt 0) { Die "public snapshot contains $bin binary file(s)" }
  Say "  snapshot ok: $cs .cs, no binaries"
}
Step "7b push public" {
  GitRun $Pub @('push', '-u', 'origin', 'main', '--force')
  GitRun $Pub @('push', '-f', 'origin', $Tag)
  Warn "  if a push dies on pack transfer (Connection was reset), retry with tools/push-via-api.js"
}
Step "7c GitHub release" {
  # 2026-10-12: `-File` argument binding does NOT expand an array or consume multiple values,
  # so `-Assets a b c` to the child process bound only the first path (v1.7.5 was published by
  # hand because of this). Call the script in-process so @(...) binds to [string[]].
  # 2026-10-30 (Bro, permanent): the release carries the INSTALLER ONLY. The portable zip and the
  # autoloader bundle are still built + audited locally (steps 5-6), but never uploaded as assets.
  try {
    & (Join-Path $Dev 'tools\publish-release.ps1') -Repo $Repo -Tag $Tag -Name $ReleaseName -NotesFile $NotesFile -Assets @($setup)
  } catch { Die ("publish-release failed: " + $_.Exception.Message) }
}


# 2026-10-27: the release body must round-trip as UTF-8. Windows PowerShell 5.1 sends a
# string body (Invoke-RestMethod -Body <string>) as ASCII, which turned every Chinese
# character into a question mark on v1.11.0-fix3. set-release-body.js is the safe path.
Step "7d verify release body encoding" {
  RunNode (Join-Path $Dev 'tools\set-release-body.js') @('--repo', $Repo, '--tag', $Tag, '--notes', $NotesFile, '--check')
}
Say "DONE. release: https://github.com/$Repo/releases/tag/$Tag"
