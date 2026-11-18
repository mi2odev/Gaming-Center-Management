# Builds the Windows installer: publish\installer\mi2oGamingCenter-Setup-<version>.exe
# Needs Inno Setup 6 (free): https://jrsoftware.org/isdl.php  (or: winget install JRSoftware.InnoSetup)
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# A release build without the seller's public key would refuse every license key.
$pubFile = Join-Path $root "src\GamingCenter.App\Licensing\LicensePublicKey.cs"
if ((Get-Content $pubFile -Raw) -match 'Value = "";') {
    throw "No license public key in the app yet. Run once:  dotnet run --project tools\LicenseTool -- init"
}

& (Join-Path $PSScriptRoot "publish.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup)" }

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version

Write-Host "Building installer v$version ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "/DSourceDir=$root\publish\win-x64" (Join-Path $root "installer\mi2oGamingCenter.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

Write-Host ""
Write-Host "Installer ready:" -ForegroundColor Green
Write-Host "  $root\publish\installer\mi2oGamingCenter-Setup-$version.exe"
