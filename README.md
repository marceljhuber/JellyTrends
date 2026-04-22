# JellyTrends

JellyTrends is a Jellyfin plugin that adds two home-screen rows:

- Top 10 Movies in your library based on a current public Top chart.
- Top 10 Shows in your library based on a current public Top chart.

The plugin fetches up to 100 movie and 100 TV entries from the Apple Marketing RSS feeds,
matches them against local library titles, and shows ranked results with badges (`#1`, `#2`, ...).

## Notes

- JellyTrends uses the File Transformation plugin approach to inject its web assets into `index.html`.
- If the transform plugin is missing, the backend still runs but the home UI will not auto-inject.

## Build

```powershell
dotnet build JellyTrends.sln -c Release
```

## Easy Repository Install

This project already includes a Jellyfin repository manifest at `repo/manifest.json` and a release helper script.

Use this flow to make install as easy as: add repo URL -> install from catalog.

1. Publish this repository to GitHub.
2. Run `./scripts/New-Release.ps1 -Version 0.1.0.0 -Owner <you> -Repository JellyTrends`.
3. Create GitHub release `0.1.0.0` and upload `dist/Release-10.10.7.zip`.
4. Commit/push updated `repo/manifest.json`.
5. In Jellyfin, add repository URL:

`https://raw.githubusercontent.com/<you>/JellyTrends/main/repo/manifest.json`

Then install JellyTrends like any normal plugin repository entry.
