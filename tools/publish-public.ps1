# publish-public.ps1 -- build the public GitHub snapshot from the release line
# ASCII-only comments on purpose (PowerShell 5.1 reads .ps1 as GBK on zh-CN Windows).
#
# 2026-10-24 policy (Bro): developer / internal docs stay local and NEVER ship in the public
# snapshot. Before this the script only deleted 3 files, so docs/adr, i18n, KNOWN-ISSUES,
# POSITIONING, tools/public-templates and the agent memory file were all published.
# Now it is a whitelist: code + user-facing docs only, everything else is pruned and asserted.
param(
  [string]$Rel = "D:\new-acad-release",
  [string]$Out = "D:\new-acad-public",
  [string]$Tag = "",   # REQUIRED: snapshot tag, e.g. v1.7.0-release. The old default
                                     # (first release was published immutable, then deleted ->
                                     #  GitHub refuses to reuse that tag name ever again)
  [switch]$SkipGit,
  [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$tpl  = Join-Path $here "public-templates"

Write-Host "[1/7] snapshot: $Rel -> $Out"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
robocopy $Rel $Out /E `
  /XD .git dist dist-bundle node_modules exchange obj bin build-tmp tmp reports user-knowledge `
  /XF *.exe *.zip *.log config.json installer.iss *.dll *.pdb *.lock *.tmp *.bak `
  /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed, code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

# 2026-09-28: -Tag is now REQUIRED. The old default (v1.3.3-release) plus "git tag -f"
if ([string]::IsNullOrWhiteSpace($Tag)) {
  throw "Pass -Tag explicitly, e.g. -Tag v1.7.0-release (tags are not recyclable; never reuse the historical default)"
}

Write-Host "[2/7] drop internal doc trees"
foreach ($d in @("docs", "i18n", "marketplace", "tools\public-templates", "tools\archive", "user-knowledge")) {
  $p = Join-Path $Out $d
  if (Test-Path $p) { Remove-Item $p -Recurse -Force; Write-Host "      - $d/" }
}

Write-Host "[3/7] prune docs to the public whitelist"
# Root user docs. Any root *.html counts (the shipped manual, ASCII-only keep rule so this
# script never has to embed a non-ASCII file name).
$keepRoot = @("README.md", "README_EN.md", "ONBOARDING.md", "PRIVACY.md", "workflow-guide.md", "LICENSE", "THIRD-PARTY-NOTICES.md")
# User-facing knowledge only. API inventories / SAC guides / method specs are developer docs.
$keepKnowledge = @("image-to-cad.md", "image-to-cad.en.md")
$keepServerMemory = @("knowledge.md")

$removed = @()
$kept = @()
Get-ChildItem $Out -Recurse -File -Include *.md, *.html | ForEach-Object {
  $rel = $_.FullName.Substring($Out.Length + 1)
  $dir = Split-Path $rel -Parent
  $name = $_.Name
  $keep = $false
  if (-not $dir -and ($keepRoot -contains $name -or $name -like "*.html")) { $keep = $true }
  elseif ($dir -eq "knowledge" -and $keepKnowledge -contains $name) { $keep = $true }
  elseif ($rel -eq "server\memory\knowledge.md") { $keep = $true }
  # READMEs inside the code tree explain how to build/test that module -> contributor docs, keep
  elseif ($name -eq "README.md" -and ($rel -like "server\*" -or $rel -like "plugin\*")) { $keep = $true }
  if ($keep) { $kept += $rel } else { $removed += $rel; Remove-Item $_.FullName -Force }
}
Write-Host "      kept $($kept.Count) docs, removed $($removed.Count)"
foreach ($r in $removed) { Write-Host "      - $r" }

Write-Host "[4/7] assert nothing internal survived"
$forbiddenFiles = @("BOOTSTRAP.md", "DASHBOARD.md", "ROADMAP.md", "KNOWN-ISSUES.md", "VERIFICATION.md",
                    "POSITIONING.md", "AUDIT-STANDARD.md", "glossary.md", "agent-memory.md",
                    "api-inventory.md", "c3d-api-refs.md", "civil3d-objects.md",
                    "method-description-spec.md", "sac-xaml-guide.md", "sac-xaml-guide.en.md")
$forbiddenDirs = @("docs", "i18n", "marketplace", "tools\public-templates", "tools\archive")
$bad = @()
foreach ($f in $forbiddenFiles) {
  $hit = Get-ChildItem $Out -Recurse -File -Filter $f -ErrorAction SilentlyContinue
  foreach ($h in @($hit)) { $bad += "internal doc survived: $($h.FullName)" }
}
foreach ($d in $forbiddenDirs) {
  if (Test-Path (Join-Path $Out $d)) { $bad += "internal dir survived: $d" }
}
# empty dirs left behind by the prune
Get-ChildItem $Out -Recurse -Directory | Sort-Object FullName -Descending | ForEach-Object {
  if (-not (Get-ChildItem $_.FullName -Force)) { Remove-Item $_.FullName -Force }
}
if ($bad.Count -gt 0) {
  Write-Host "WHITELIST ASSERTION FAILED:"
  $bad | ForEach-Object { Write-Host "   $_" }
  if (-not $DryRun) { throw "whitelist assertion failed" }
}

Write-Host "[5/7] license + third-party notices"
Copy-Item (Join-Path $tpl "LICENSE") (Join-Path $Out "LICENSE") -Force
Copy-Item (Join-Path $tpl "THIRD-PARTY-NOTICES.md") (Join-Path $Out "THIRD-PARTY-NOTICES.md") -Force
Copy-Item (Join-Path $tpl ".gitattributes") (Join-Path $Out ".gitattributes") -Force

Write-Host "[6/7] sanitize docs"
node (Join-Path $tpl "sanitize-docs.js") $Out $tpl
if ($LASTEXITCODE -ne 0) { throw "sanitize-docs failed" }

Write-Host "[7/7] leak check"
$leaks = @()
if (Test-Path (Join-Path $Out "config.json")) { $leaks += "config.json present" }
$hits = Get-ChildItem $Out -Recurse -File -Include *.js,*.ts,*.json,*.md,*.cs,*.ps1,*.bat,*.lsp,*.html,*.xaml |
  Select-String -Pattern 'sk-[A-Za-z0-9]{16,}' -List -ErrorAction SilentlyContinue
foreach ($h in @($hits)) { $leaks += "api key pattern: $($h.Path)" }
Get-ChildItem $Out -Recurse -File -Include *.exe,*.zip | ForEach-Object { $leaks += "binary: $($_.FullName)" }
if ($leaks.Count -gt 0) {
  Write-Host "LEAK CHECK FAILED:"
  $leaks | ForEach-Object { Write-Host "   $_" }
  if (-not $DryRun) { throw "leak check failed" }
} else { Write-Host "      clean" }

if ($SkipGit -or $DryRun) { Write-Host "done (no git)"; exit 0 }

Push-Location $Out
try {
  git init -b main | Out-Null
  # 2026-09-22: after git init the remote may be gone -> re-add origin (otherwise push says
  # "origin does not appear to be a git repository")
  $remotes = @(git remote)
  if ($remotes -notcontains "origin") {
    git remote add origin https://github.com/Jjo37/new-acad.git
    Write-Host "[fix] origin remote restored"
  }
  # inherit identity from the release repo (source stays ASCII-only; the name may not be)
  $uname = (git -C $Rel config user.name)
  $uemail = (git -C $Rel config user.email)
  if ([string]::IsNullOrWhiteSpace($uname)) { $uname = "Jjo" }
  if ([string]::IsNullOrWhiteSpace($uemail)) { $uemail = "167265596+Jjo37@users.noreply.github.com" }
  git config user.name $uname
  git config user.email $uemail
  git add -A
  $files = (git diff --cached --name-only | Measure-Object).Count
  git commit -q -m "release: new-acad $Tag public snapshot (MIT)"
  if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
  git tag -f $Tag | Out-Null
  Write-Host "      committed $files files"
} finally { Pop-Location }
Write-Host "ready: $Out"
