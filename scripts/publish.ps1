$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $projectRoot "bin\publish\win-x64"

Push-Location $projectRoot
dotnet publish -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputDir
Pop-Location

Write-Host ""
Write-Host "Publication terminée." -ForegroundColor Green
Write-Host "Exécutable autonome : $outputDir\SpaceManager.exe"
Write-Host "Ce fichier inclut le runtime .NET (~140 Mo) et peut être copié seul."
