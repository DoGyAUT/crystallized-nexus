<#
.SYNOPSIS
    Cuts a new test build by pushing a "playtest-*" tag, which triggers the
    repo's Release Packaging GitHub Actions workflow (builds Windows/Linux/
    macOS packages on CI and publishes them as a prerelease). CNLauncher
    picks up whatever is the newest release automatically.

.PARAMETER Tag
    Release tag. Must start with "playtest-" or "release-" to trigger the
    workflow (see .github/workflows/packaging.yml). Defaults to
    "playtest-<today's date>".
#>
param(
    [string]$Tag = "playtest-$(Get-Date -Format 'yyyyMMdd')"
)

$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^(playtest|release)-') {
    Write-Host "Tag must start with 'playtest-' or 'release-' to trigger the packaging workflow." -ForegroundColor Red
    exit 1
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repoRoot
try {
    git tag $Tag
    git push origin $Tag
}
finally {
    Pop-Location
}

Write-Host "Pushed tag '$Tag' - packaging workflow starting on GitHub Actions (~10 min)." -ForegroundColor Green
Write-Host "Track it with: gh run list --repo DoGyAUT/crystallized-nexus --workflow packaging.yml --limit 1"
