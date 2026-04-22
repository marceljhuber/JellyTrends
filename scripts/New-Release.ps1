param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "0.1.0.0",

    [Parameter(Mandatory = $false)]
    [string]$TargetAbi = "10.10.7.0",

    [Parameter(Mandatory = $false)]
    [string]$Owner = "YOUR_GITHUB_USER",

    [Parameter(Mandatory = $false)]
    [string]$Repository = "JellyTrends"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Jellyfin.Plugin.JellyTrends/Jellyfin.Plugin.JellyTrends.csproj"
$publishRoot = Join-Path $root "artifacts/release"
$zipRoot = Join-Path $root "dist"
$zipPath = Join-Path $zipRoot "Release-10.10.7.zip"
$manifestPath = Join-Path $root "repo/manifest.json"

if (!(Test-Path $publishRoot)) { New-Item -Path $publishRoot -ItemType Directory | Out-Null }
if (!(Test-Path $zipRoot)) { New-Item -Path $zipRoot -ItemType Directory | Out-Null }

Write-Host "Building plugin..."
dotnet publish $project -c Release -f net8.0 -o $publishRoot

$filesToKeep = @("Jellyfin.Plugin.JellyTrends.dll", "Jellyfin.Plugin.JellyTrends.pdb")
Get-ChildItem -Path $publishRoot -File | Where-Object { $filesToKeep -notcontains $_.Name } | Remove-Item -Force

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Write-Host "Creating release zip..."
Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath

$checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToUpperInvariant()
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$sourceUrl = "https://github.com/$Owner/$Repository/releases/download/$Version/Release-10.10.7.zip"

$manifestEntry = @{
    guid = "5e4f95f0-df85-4ef4-a73c-30afde8be5f9"
    name = "JellyTrends"
    overview = "Top 10 trending matches from your library"
    description = "Fetches Top charts for movies and TV shows, compares them with your Jellyfin library, and shows ranked Top 10 matches on the home screen."
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
