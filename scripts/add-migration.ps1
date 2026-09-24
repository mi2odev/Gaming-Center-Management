# Creates a new EF Core migration after you change an entity or GamingCenterDbContext.
#
#   powershell -ExecutionPolicy Bypass -File scripts\add-migration.ps1 AddLoyaltyPoints
#
# The app applies pending migrations automatically at startup (DbInitializer.MigrateAsync),
# so there is no separate "update database" step on the gaming center laptop.
param([Parameter(Mandatory = $true)][string]$Name)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet tool restore
    dotnet ef migrations add $Name `
        --project src\GamingCenter.Infrastructure `
        --startup-project src\GamingCenter.Infrastructure `
        --output-dir Migrations
}
finally {
    Pop-Location
}
