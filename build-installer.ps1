<#
.SYNOPSIS
  build-installer.ps1 — 用 Inno Setup 编译一键安装器 setup.exe
.DESCRIPTION
  1. 校验 dist/（build-dist.ps1 产物）存在
  2. 从 package.json 读版本号，生成 installer.iss
  3. ISCC.exe 编译 → new-acad-setup-v<版本>.exe
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build-installer.ps1
.NOTES
  2026-08-25 新建：安装器 = Inno 外壳(文件部署/快捷方式/卸载) + install.ps1(环境配置向导)
  前置：先跑 build-dist.ps1 生成 dist/
#>
$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
if (-not $ROOT) { $ROOT = "D:\new-acad" }

# ---------- 0. 定位 ISCC.exe ----------
$candidates = @(
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$ISCC = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ISCC) { Write-Host "[FAIL] 未找到 ISCC.exe，请先安装 Inno Setup 6" -ForegroundColor Red; exit 1 }
Write-Host "[OK] ISCC: $ISCC" -ForegroundColor Green

# ---------- 1. 校验 dist/ ----------
$DIST = "$ROOT\dist"
if (-not (Test-Path "$DIST\install.ps1")) { Write-Host "[FAIL] dist/ 不完整（缺 install.ps1），先跑 build-dist.ps1" -ForegroundColor Red; exit 1 }
Write-Host "[OK] dist/: $DIST" -ForegroundColor Green

# ---------- 2. 版本号 ----------
$VERSION = try { (Get-Content "$ROOT\package.json" -Raw | ConvertFrom-Json).version } catch { "1.3.0" }
Write-Host "===== 编译安装器 v$VERSION =====" -ForegroundColor Cyan

# ---------- 3. 生成 installer.iss ----------
$ISS = @"
; new-acad 安装器定义（由 build-installer.ps1 自动生成，勿手改）
#define MyAppName "new-acad AI 助手"
#define MyAppVersion "$VERSION"
#define MyAppPublisher "new-acad"
#define MyAppExeName "使用手册.html"

[Setup]
AppId={{8A3C9E21-2F5B-4D6E-9C1A-AC0D81D62026}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\new-acad
DefaultGroupName=new-acad
DisableProgramGroupPage=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#SourcePath}
OutputBaseFilename=new-acad-setup-v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=new-acad {#MyAppVersion}
UninstallDisplayIcon={app}\node.exe
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}tools\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourcePath}\dist\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "uninstall.ps1"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -Locale {language}"; WorkingDir: "{app}"; StatusMsg: "运行环境配置向导（检查 C3D/注册自启/配置 AI）..."; Flags: postinstall skipifsilent; Description: "运行环境配置向导（推荐：检查 C3D + 注册自启 + 配置 AI）"
Filename: "{app}\使用手册.html"; Flags: postinstall skipifsilent shellexec; Description: "查看使用手册"

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-pre.ps1"""; RunOnceId: "NewAcadCleanup"; Flags: runhidden

[Icons]
Name: "{group}\使用手册"; Filename: "{app}\使用手册.html"
Name: "{group}\卸载 new-acad"; Filename: "{uninstallexe}"

[UninstallDelete]
Type: files; Name: "{app}\server\start-relay-hidden.vbs"
Type: files; Name: "{app}\server\*.log"
Type: files; Name: "{app}\server\*.lock"
Type: files; Name: "{app}\relay-inbox.json"
Type: filesandordirs; Name: "{app}\exchange"
"@
$ISS_PATH = "$ROOT\installer.iss"
[System.IO.File]::WriteAllText($ISS_PATH, $ISS, (New-Object System.Text.UTF8Encoding $false))
Write-Host "[OK] 生成 installer.iss" -ForegroundColor Green

# ---------- 4. ISCC 编译 ----------
& $ISCC $ISS_PATH
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] ISCC 编译失败 (code $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }

$OUT = "$ROOT\new-acad-setup-v$VERSION.exe"
if (Test-Path $OUT) {
  $sz = (Get-Item $OUT).Length
  Write-Host "[OK] 安装器: $OUT ($([math]::Round($sz/1MB,1)) MB)" -ForegroundColor Green
} else {
  Write-Host "[FAIL] 未找到输出文件 $OUT" -ForegroundColor Red; exit 1
}

# 2026-09-11: 编完安装器后自动并入同目录 new-acad-v<版本>.zip（异机一步到位；先跑 build-dist.ps1 -Zip）
$zipPath = "$ROOT\new-acad-v$VERSION.zip"
if (Test-Path $zipPath) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zipEntry = "new-acad-setup-v$VERSION.exe"
  $za = [System.IO.Compression.ZipFile]::Open($zipPath, 'Update')
  try {
    $old = $za.GetEntry($zipEntry)
    if ($old) { $old.Delete() }
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $OUT, $zipEntry)
    Write-Host "[OK] 安装器已并入 zip: $zipPath" -ForegroundColor Green
  } finally { $za.Dispose() }
} else {
  Write-Host "[WARN] 未找到 $zipPath —— 安装器未并入 zip（先跑 build-dist.ps1 -Zip）" -ForegroundColor Yellow
}
