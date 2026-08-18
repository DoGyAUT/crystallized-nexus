<#
.SYNOPSIS
    Cuts a new launcher build by pushing a "launcher-v*" tag, which triggers the repo's
    Launcher GitHub Actions workflow (builds Windows/Linux/macOS binaries and publishes
    them as a full release). Installed launchers pick the new version up on next start
    and offer to update themselves.

    This is separate from create-test-release.ps1 on purpose: the launcher changes rarely,
    the game ships often, and their versions should not be tied together.

.PARAMETER Version
    Launcher version, "X.Y.Z". The tag becomes "launcher-v<Version>" and the build reports
    exactly this version, so it must increase for existing launchers to offer the update.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "Version must be X.Y.Z (for example 2.1.0)." -ForegroundColor Red
    exit 1
}

$tag = "launcher-v$Version"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

Push-Location $repoRoot
try {
    git tag $tag
    git push origin $tag
}
finally {
    Pop-Location
}

Write-Host "Pushed tag '$tag' - launcher workflow starting on GitHub Actions." -ForegroundColor Green
Write-Host "Track it with: gh run list --repo DoGyAUT/crystallized-nexus --workflow launcher.yml --limit 1"
