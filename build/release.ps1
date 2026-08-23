<#
.SYNOPSIS
    Builds a Windows release of EfAssist: a self-contained publish, packed by Velopack into an
    installer, a portable zip, and a delta package against the previous release.

.DESCRIPTION
    Run from anywhere; paths are resolved relative to the repository root.

    The output lands in `releases/`. Velopack needs the *previous* releases in that folder to build
    a delta package — if it is empty, it produces a full release only, which still installs and
    still updates, just with a larger download.

    Nothing is signed. Windows SmartScreen will warn on first run of an unsigned installer until the
    download reputation builds. Add `--signParams` to the `vpk pack` call below if a code-signing
    certificate ever appears.

.PARAMETER Version
    The version to release. Defaults to the <Version> in EfAssist.App.csproj, which is the one
    place the release number is written down.

.PARAMETER Upload
    Publish the result to GitHub Releases as well. Requires a token with `repo` scope, in -Token or
    in the GITHUB_TOKEN environment variable.

.PARAMETER Token
    GitHub token for -Upload. Falls back to $env:GITHUB_TOKEN.

.PARAMETER Draft
    With -Upload, leave the GitHub release as a draft instead of publishing it.

.EXAMPLE
    ./build/release.ps1
    Build 1.0.0 (or whatever the csproj says) into releases/.

.EXAMPLE
    ./build/release.ps1 -Version 1.1.0 -Upload
    Build and publish to GitHub Releases, which is where the in-app updater looks.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Upload,
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repo 'src/EfAssist.App/EfAssist.App.csproj'
$publishDir = Join-Path $repo 'artifacts/publish/win-x64'
$releaseDir = Join-Path $repo 'releases'
$repoUrl = 'https://github.com/matthewtoghill/EfAssist'
$runtime = 'win-x64'

if (-not $Version) {
    $Version = (Select-Xml -Path $appProject -XPath '/Project/PropertyGroup/Version').Node.InnerText |
        Select-Object -First 1
    if (-not $Version) {
        throw "No -Version given and no <Version> found in $appProject."
    }
}

Write-Host "Releasing EfAssist $Version ($runtime)" -ForegroundColor Cyan

# A stale publish directory would be packed verbatim, shipping files that are no longer built.
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

# Tests gate the release. A broken build is not worth packaging.
dotnet test (Join-Path $repo 'EfAssist.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing packaged." }

# Self-contained so the installer works on a machine with no .NET runtime. Not PublishSingleFile:
# Velopack packs a directory and applies binary deltas to the files in it, which a single bundled
# exe defeats — every update would download the whole application again.
# Not PublishTrimmed either: Avalonia's XAML loading and the toolkit's generated code are
# trim-hostile, and the failure mode is a crash in a shipped binary rather than a build error.
dotnet publish $appProject `
    -c Release `
    -r $runtime `
    --self-contained `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

dotnet vpk pack `
    --packId EfAssist `
    --packVersion $Version `
    --packDir $publishDir `
    --packTitle EfAssist `
    --packAuthors 'Matthew Toghill' `
    --mainExe EfAssist.exe `
    --icon (Join-Path $repo 'src/EfAssist.App/Assets/app-logo.ico') `
    --runtime $runtime `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "Packaged into $releaseDir" -ForegroundColor Green

if (-not $Upload) {
    Write-Host "Not uploaded. Re-run with -Upload to publish to GitHub Releases." -ForegroundColor Yellow
    return
}

if (-not $Token) {
    throw "-Upload needs a GitHub token, in -Token or GITHUB_TOKEN."
}

# vpk takes --publish as a value, not as a flag, so it cannot be a PowerShell switch colon-binding.
$publishRelease = if ($Draft) { 'false' } else { 'true' }

# --merge lets a re-run add to an existing release for the same tag rather than failing outright.
dotnet vpk upload github `
    --repoUrl $repoUrl `
    --token $Token `
    --outputDir $releaseDir `
    --tag "v$Version" `
    --releaseName "EfAssist $Version" `
    --merge `
    --publish $publishRelease
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }

Write-Host "Published v$Version to $repoUrl/releases" -ForegroundColor Green
