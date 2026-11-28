# =====================================================================
#  Fabrique l'exécutable et l'installateur de Keyce'sCode (Membre 5)
#  À lancer depuis la racine du dépôt :  .\installer\build-installer.ps1
# =====================================================================

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 1. Publier l'application : un seul .exe autonome (.NET inclus, rien à installer en plus)
#    ReadyToRun : le code est déjà compilé pour Windows, l'application démarre plus vite
Write-Host "1/2  Publication de l'application..." -ForegroundColor Cyan
if (Test-Path publish) { Remove-Item -Recurse -Force publish }

dotnet publish src\KeycesCode.App\KeycesCode.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -o publish

if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }
Write-Host "     -> publish\KeycesCode.App.exe" -ForegroundColor Green

# 2. Créer l'installateur avec Inno Setup (s'il est installé)
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "Inno Setup n'est pas installé : seul l'exécutable a été créé." -ForegroundColor Yellow
    Write-Host "Pour l'installer :  winget install JRSoftware.InnoSetup"
    exit 0
}

Write-Host "2/2  Création de l'installateur..." -ForegroundColor Cyan
& $iscc "installer\KeycesCode.iss"
if ($LASTEXITCODE -ne 0) { throw "La création de l'installateur a échoué." }

Write-Host "     -> installer\Output\KeycesCode-Setup-1.0.0.exe" -ForegroundColor Green
