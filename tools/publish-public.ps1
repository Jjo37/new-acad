# publish-public.ps1 -- build the public GitHub snapshot from the release line
# ASCII-only comments on purpose (PowerShell 5.1 reads .ps1 as GBK on zh-CN Windows).
param(
  [string]$Rel = "D:\new-acad-release",
  [string]$Out = "D:\new-acad-public",
  [string]$Tag = "v1.3.3-release",   # NOTE: plain "v1.3.3" is permanently burned in this repo
                                     # (first release was published immutable, then deleted ->
                                     #  GitHub refuses to reuse that tag name ever again)
  [switch]$SkipGit,
  [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$tpl  = Join-Path $here "public-templates"

Write-Host "[1/6] snapshot: $Rel -> $Out"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
robocopy $Rel $Out /E `
  /XD .git dist node_modules exchange obj bin build-tmp tmp reports `
  /XF *.exe *.zip *.log config.json installer.iss *.dll *.pdb *.lock *.tmp *.bak `
  /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed, code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

Write-Host "[2/6] drop internal docs"
foreach ($f in @("DASHBOARD.md","BOOTSTRAP.md","ROADMAP.md")) {
  $p = Join-Path $Out $f
  if (Test-Path $p) { Remove-Item $p -Force }
}

Write-Host "[3/6] knowledge whitelist"
$keep = @("api-inventory.md","c3d-api-refs.md","civil3d-objects.md",
          "command-methods-assessment.md","deployment-package.md",
          "method-description-spec.md","sac-xaml-guide.md")
$kd = Join-Path $Out "knowledge"
if (Test-Path $kd) {
  Get-ChildItem $kd -File -Filter *.md |
    Where-Object { $keep -notcontains $_.Name } |
    ForEach-Object { Write-Host "      - $($_.Name)"; Remove-Item $_.FullName -Force }
}

Write-Host "[4/6] license + third-party notices"
Copy-Item (Join-Path $tpl "LICENSE") (Join-Path $Out "LICENSE") -Force
Copy-Item (Join-Path $tpl "THIRD-PARTY-NOTICES.md") (Join-Path $Out "THIRD-PARTY-NOTICES.md") -Force
Copy-Item (Join-Path $tpl ".gitattributes") (Join-Path $Out ".gitattributes") -Force

Write-Host "[5/6] sanitize docs"
node (Join-Path $tpl "sanitize-docs.js") $Out $tpl
if ($LASTEXITCODE -ne 0) { throw "sanitize-docs failed" }

Write-Host "[6/6] leak check"
$bad = @()
if (Test-Path (Join-Path $Out "config.json")) { $bad += "config.json present" }
$hits = Get-ChildItem $Out -Recurse -File -Include *.js,*.ts,*.json,*.md,*.cs,*.ps1,*.bat,*.lsp,*.html,*.xaml |
  Select-String -Pattern 'sk-[A-Za-z0-9]{16,}' -List -ErrorAction SilentlyContinue
foreach ($h in @($hits)) { $bad += "api key pattern: $($h.Path)" }
Get-ChildItem $Out -Recurse -File -Include *.exe,*.zip | ForEach-Object { $bad += "binary: $($_.FullName)" }
if ($bad.Count -gt 0) {
  Write-Host "LEAK CHECK FAILED:"
  $bad | ForEach-Object { Write-Host "   $_" }
  if (-not $DryRun) { throw "leak check failed" }
} else { Write-Host "      clean" }

if ($SkipGit -or $DryRun) { Write-Host "done (no git)"; exit 0 }

Push-Location $Out
try {
  git init -b main | Out-Null
  # 2026-09-22: git init 之后远端可能已丢失 → 无 origin 时补回（否则 push 报 origin does not appear to be a git repository）
  $remotes = @(git remote)
  if ($remotes -notcontains "origin") {
    git remote add origin https://github.com/Jjo37/new-acad.git
    Write-Host "[fix] 已补回 origin 远端"
  }
  git config user.name "Jjo 阿飞"
  git config user.email "167265596+Jjo37@users.noreply.github.com"
  git add -A
  $files = (git diff --cached --name-only | Measure-Object).Count
  git commit -q -m "release: new-acad $Tag public snapshot (MIT)"
  if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
  git tag -f $Tag | Out-Null
  Write-Host "      committed $files files"
} finally { Pop-Location }
Write-Host "ready: $Out"
