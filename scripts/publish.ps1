# Builds a self-contained Windows build of mi2o Gaming Center.
# The target PC does not need .NET installed.
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Runtime win-x86
#
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SingleFile
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

Write-Host ""
Write-Host "Done. Copy the folder below to the gaming center laptop and run mi2oGamingCenter.exe:" -ForegroundColor Green
Write-Host "  $out"
