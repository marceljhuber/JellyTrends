# JellyTrends Repository Install (One Link)

1. Push this repository to GitHub as `https://github.com/<you>/JellyTrends`.
2. Run:

```powershell
./scripts/New-Release.ps1 -Version 0.1.0.0 -Owner <you> -Repository JellyTrends
```

3. Create a GitHub release with tag `0.1.0.0` and upload `dist/Release-10.10.7.zip`.
4. Commit and push `repo/manifest.json`.
5. In Jellyfin Dashboard -> Plugins -> Repositories, add:
   - Repository Name: `JellyTrends`
   - Repository URL: `https://raw.githubusercontent.com/<you>/JellyTrends/main/repo/manifest.json`

After that, JellyTrends is installable directly from the plugin catalog.
