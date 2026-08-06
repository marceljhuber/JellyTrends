# JellyTrends

JellyTrends adds Netflix-style trending rows to the Jellyfin home screen, showing only the
titles you actually own — and keeping each title's real position in the online chart.

![JellyTrends Banner](assets/jellytrends-banner.png)

## Screenshot

![JellyTrends Home Example](assets/jellytrends-home-example.png)

If *Dune: Prophecy* sits at #36 worldwide and it is the sixth title you own from that chart,
the badge still reads `#36`. That is the point of the rows: they tell you where a title
ranks online, not where it ranks inside your shelf.

## What It Does

- Adds **Top N Movies In Your Library** and **Top N Shows In Your Library** rows to Home.
- Pulls trending charts from TMDB, Trakt, or the keyless Cinemeta catalog.
- Matches charts to your library by IMDb / TMDB / TVDB id first, then by title and year.
- Keeps the online chart position on the rank badge (toggleable).
- Row size is configurable — 10 is the default, anything from 1 to 50 works.
- Caches results so Home stays fast after the first render.

## Requirements

- Jellyfin **10.11.0 or newer** (built and tested against 10.11.11).
- The [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  plugin, which is what lets JellyTrends inject the rows into the web client.

## Client Support

The rows are injected into `jellyfin-web`, so every client that renders the server's web
bundle gets them:

| Client | Supported | Notes |
| --- | --- | --- |
| Jellyfin Web (browser) | Yes | |
| Jellyfin for Android | Yes | The official app hosts `jellyfin-web` in a WebView |
| Jellyfin for iOS | Yes | Same WebView architecture |
| Jellyfin Media Player (Windows / macOS / Linux) | Yes | Loads the server's web client |
| Jellyfin for Android TV | No | Native UI, cannot be injected into |
| Swiftfin, Findroid, Roku, Kodi | No | Native UIs |

Serving Jellyfin from a sub path (`https://example.com/jellyfin/`) is handled — the injected
bootstrap resolves the server root at runtime.

## Install

1. Open Jellyfin Dashboard → `Plugins` → `Repositories`.
2. Add a repository:
   - Name: `JellyTrends`
   - URL: `https://raw.githubusercontent.com/marceljhuber/JellyTrends/master/repo/manifest.json`
3. Install **File Transformation** if you have not already.
4. Refresh the catalog, install `JellyTrends`, then restart Jellyfin.

A restart is required after enabling the rows: the injection is registered by a startup task.

## Trending Sources

JellyTrends tries sources in order and uses the first one that returns data, so a fresh
install works with no signup at all.

| Source | Credential | What it ranks |
| --- | --- | --- |
| **TMDB** | Free API key | Genuine "trending this week" (or today) |
| **Trakt** | Free Client ID | How many people are watching right now |
| **Cinemeta** | None | General popularity — the keyless default |

- **TMDB key:** themoviedb.org → Settings → API. Both the v3 API key and the v4 "API Read
  Access Token" are accepted; JellyTrends detects which one you pasted.
- **Trakt Client ID:** trakt.tv/oauth/applications → create an app, copy the Client ID.

With `Automatic` selected, TMDB is preferred, then Trakt, then Cinemeta. If a key is wrong or
a source is down, JellyTrends falls through to the next one rather than showing nothing. Use
the **Test source** button on the settings page to see which source answered and what it
returned.

Cinemeta is a real chart with full IMDb and TMDB id coverage, but it ranks by overall
popularity, so classics surface alongside new releases. Add a TMDB key if you want the rows
to track what is actually trending this week.

## Settings

Dashboard → Plugins → JellyTrends.

| Setting | Default | What it does |
| --- | --- | --- |
| Enable plugin | on | Master switch |
| Show trending rows on Home | on | Turns the web injection on or off |
| Source | Automatic | Which chart provider to use |
| TMDB API key | empty | Unlocks TMDB trending |
| Trakt Client ID | empty | Unlocks Trakt trending |
| Trending window | This week | `week` or `day`, TMDB only |
| Titles per row | 10 | 1–50; the row headings follow this number |
| Show the online chart position | on | Off renumbers the badges 1..N |
| Card size scale | 100% | Card width |
| Text size scale | 100% | Heading and label size |
| Movie / Show chart depth | 100 | How far down the chart to look for titles you own |
| Strict year match | off | Title fallback also requires a matching year |
| Cache duration | 180 min | How long charts are reused |

If a row comes up short on a small library, raise the chart depth — the plugin can only show
titles you actually have.

## Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /JellyTrends/config` | Display settings for the web client |
| `GET /JellyTrends/trending` | Current charts, cached |
| `POST /JellyTrends/test` | Refetches and reports which source answered |
| `GET /JellyTrends/assets/{file}` | Serves the injected JS and CSS |

## For Maintainers

Build (Jellyfin 10.11 targets `net9.0`, so a .NET 9 SDK is required):

```powershell
dotnet build JellyTrends.sln -c Release
```

Create the release zip and refresh the manifest:

```powershell
./scripts/New-Release.ps1 -Version 0.2.0.0 -JellyfinVersion 10.11.11 -Owner marceljhuber -Repository JellyTrends -UseRawRepoZip $true
```

The script writes `dist/Release-<jellyfin-version>.zip` and `repo/manifest.json`. `targetAbi`
is pinned to the start of the minor line (`10.11.0.0`) so the plugin installs on every patch
release of it.
