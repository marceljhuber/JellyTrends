param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "0.2.0.0",

    [Parameter(Mandatory = $false)]
    [string]$JellyfinVersion = "10.11.11",

    [Parameter(Mandatory = $false)]
    [string]$TargetAbi = "",

    [Parameter(Mandatory = $false)]
    [string]$Owner = "marceljhuber",

    [Parameter(Mandatory = $false)]
    [string]$Repository = "JellyTrends",

    [Parameter(Mandatory = $false)]
    [bool]$UseRawRepoZip = $true
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Jellyfin.Plugin.JellyTrends/Jellyfin.Plugin.JellyTrends.csproj"
$publishRoot = Join-Path $root "artifacts/release"
$zipRoot = Join-Path $root "dist"
$zipName = "Release-$JellyfinVersion.zip"
$zipPath = Join-Path $zipRoot $zipName
$manifestPath = Join-Path $root "repo/manifest.json"
$framework = if ($JellyfinVersion.StartsWith("10.11")) { "net9.0" } else { "net8.0" }

if ([string]::IsNullOrWhiteSpace($TargetAbi)) {
    # Target the first release of the minor line so the plugin installs on every patch
    # release of it, not just on the exact version it was compiled against.
    $abiParts = $JellyfinVersion.Split(".")
    $TargetAbi = "$($abiParts[0]).$($abiParts[1]).0.0"
}

if (!(Test-Path $publishRoot)) { New-Item -Path $publishRoot -ItemType Directory | Out-Null }
if (!(Test-Path $zipRoot)) { New-Item -Path $zipRoot -ItemType Directory | Out-Null }

Write-Host "Building plugin..."
dotnet publish $project -c Release -f $framework -o $publishRoot -p:JellyfinVersion=$JellyfinVersion

$filesToKeep = @("Jellyfin.Plugin.JellyTrends.dll", "Jellyfin.Plugin.JellyTrends.pdb")
Get-ChildItem -Path $publishRoot -File | Where-Object { $filesToKeep -notcontains $_.Name } | Remove-Item -Force

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Write-Host "Creating release zip..."
Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath

$checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToUpperInvariant()
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
if ($UseRawRepoZip) {
    $sourceUrl = "https://raw.githubusercontent.com/$Owner/$Repository/master/dist/$zipName"
}
else {
    $sourceUrl = "https://github.com/$Owner/$Repository/releases/download/$Version/$zipName"
}

$manifestEntry = @{
    guid = "5e4f95f0-df85-4ef4-a73c-30afde8be5f9"
    name = "JellyTrends"
    overview = "Netflix-style trending rows built from your own library"
    description = "Pulls trending movie and show charts from TMDB, Trakt or the keyless Cinemeta catalog, matches them against your library by IMDb/TMDB/TVDB id, and renders Top N rows that keep each title's real online chart position. Row size is configurable. Works on Jellyfin Web, the Android and iOS apps, and Jellyfin Media Player."
    imageUrl = "https://raw.githubusercontent.com/$Owner/$Repository/master/assets/jellytrends-banner.png"
    owner = $Owner
    category = "General"
    versions = @(
        @{
            version = $Version
            changelog = "Release $Version"
            targetAbi = $TargetAbi
            sourceUrl = $sourceUrl
            checksum = $checksum
            timestamp = $timestamp
            dependencies = @(
                "5e87cc92-571a-4d8d-8d98-d2d4147f9f90"
            )
        }
    )
}

Write-Host "Writing manifest..."
$manifestJson = "[" + ($manifestEntry | ConvertTo-Json -Depth 8) + "]"
$manifestJson | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "Done"
Write-Host "Zip: $zipPath"
Write-Host "MD5: $checksum"
Write-Host "Manifest: $manifestPath"
