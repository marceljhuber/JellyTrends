# JellyTrends Repository Install (One Link)

## For Jellyfin Users

1. Open Jellyfin Dashboard -> `Plugins` -> `Repositories`.
2. Add:
   - Name: `JellyTrends`
   - URL: `https://raw.githubusercontent.com/marceljhuber/JellyTrends/master/repo/manifest.json`
3. Refresh plugins, install JellyTrends, restart Jellyfin.

## For Repository Maintainers

1. Push this repository to GitHub as `https://github.com/<you>/JellyTrends`.
2. Place banner image at `assets/jellytrends-banner.png`.
3. Run:

```powershell
./scripts/New-Release.ps1 -Version 0.1.6.1 -JellyfinVersion 10.11.7 -Owner <you> -Repository JellyTrends -UseRawRepoZip $true
```

4. Commit and push `dist/Release-<version>.zip` and `repo/manifest.json`.

After that, JellyTrends is installable directly from the Jellyfin plugin catalog.
