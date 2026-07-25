<#
.SYNOPSIS
    Packages the currently built mod (from .\make.cmd all) into a zip and
    publishes it as a GitHub prerelease that CNLauncher can pick up.

.PARAMETER Tag
    Release tag, e.g. "test-2026-07-26". Must be unique on the repo.

.PARAMETER Notes
    Optional release notes / changelog text.
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Notes = "Test build."
)

$ErrorActionPreference = "Stop"
$repo = "DoGyAUT/crystallized-nexus"
$root = $PSScriptRoot

if (-not (Test-Path "$root\engine\bin\OpenRA.exe")) {
    Write-Host "Engine build not found - run '.\make.cmd all' first." -ForegroundColor Red
    exit 1
}

$stagingDir = Join-Path $env:TEMP "cn-release-$Tag"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path "$stagingDir\engine\bin" -Force | Out-Null
New-Item -ItemType Directory -Path "$stagingDir\mods" -Force | Out-Null

Write-Host "Staging release files..."
Copy-Item "$root\engine\bin\*" "$stagingDir\engine\bin\" -Recurse -Force
Copy-Item "$root\engine\VERSION" "$stagingDir\engine\VERSION" -Force
Copy-Item "$root\mods\cn" "$stagingDir\mods\cn" -Recurse -Force

$zipPath = Join-Path $env:TEMP "cn-release-$Tag.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Zipping..."
Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Publishing GitHub prerelease $Tag..."
gh release create $Tag $zipPath --repo $repo --title $Tag --notes $Notes --prerelease

Remove-Item $stagingDir -Recurse -Force
Remove-Item $zipPath -Force

Write-Host "Done. Testers running CNLauncher will pick this up automatically." -ForegroundColor Green
