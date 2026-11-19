# Builds a self-contained Windows build of mi2o Gaming Center.
# The target PC does not need .NET installed.
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Runtime win-x86
#
# The folder build obfuscates GamingCenter.Application.dll (license checks) with Obfuscar,
# then runs the tests again against the obfuscated file. -NoObfuscate skips that.
#
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SingleFile,
    [switch]$NoObfuscate
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\$Runtime"

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root "tests\GamingCenter.Tests") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed - not publishing." }

Write-Host "Publishing to $out ..." -ForegroundColor Cyan
$publishArgs = @(
    "publish", (Join-Path $root "src\GamingCenter.App\GamingCenter.App.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishReadyToRun=true",
    "-o", $out
)
if ($SingleFile) {
    $publishArgs += "-p:PublishSingleFile=true"
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
}
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if ($SingleFile -and -not $NoObfuscate) {
    Write-Warning "Single-file builds are not obfuscated. Use the folder build (or the installer) for customers."
}
elseif (-not $NoObfuscate) {
    Write-Host "Obfuscating the license code..." -ForegroundColor Cyan
    dotnet tool restore | Out-Null
    $obfOut = Join-Path $root "publish\obfuscar-$Runtime"   # Mapping.txt stays here, never shipped
    if (Test-Path $obfOut) { Remove-Item $obfOut -Recurse -Force }
    $cfg = Join-Path $obfOut "obfuscar.xml"
    New-Item -ItemType Directory -Force $obfOut | Out-Null
    (Get-Content (Join-Path $root "installer\obfuscar.xml") -Raw).Replace("{IN}", $out).Replace("{OUT}", $obfOut) | Set-Content $cfg
    dotnet tool run obfuscar.console $cfg
    if ($LASTEXITCODE -ne 0) { throw "Obfuscation failed." }
    Copy-Item (Join-Path $obfOut "GamingCenter.Application.dll") $out -Force

    # Same tests, now running on the obfuscated file, so a broken obfuscation never ships.
    $testBin = Join-Path $root "tests\GamingCenter.Tests\bin\$Configuration\net10.0"
    Copy-Item (Join-Path $obfOut "GamingCenter.Application.dll") $testBin -Force
    dotnet test (Join-Path $root "tests\GamingCenter.Tests") -c $Configuration --no-build
    $failed = $LASTEXITCODE -ne 0
    Remove-Item (Join-Path $testBin "GamingCenter.Application.dll") -Force   # next build restores the normal one
    if ($failed) { throw "Tests failed on the obfuscated build - not publishing." }
}

Write-Host ""
Write-Host "Done. Copy the folder below to the gaming center laptop and run mi2oGamingCenter.exe:" -ForegroundColor Green
Write-Host "  $out"
