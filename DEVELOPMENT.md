# Development notes

For anyone (including future me) picking this project up cold.

## How it works

- **Server** (`src/Jellyfin.Plugin.JellyTrends`): `TrendingService` fetches charts (TMDB, Trakt, Cinemeta;
  stale-while-revalidate, one retry, 2 min backoff). `LibraryMatcher` matches them to the caller's library by
  IMDb/TMDB/TVDB id, then title/year. `JellyTrendsController` exposes `GET /JellyTrends/rows` (signed-in users),
  `/trending` (signed-in), `POST /test` (admin) and the public `assets/{file}`.
- **Injection**: `StartupService` registers `TransformationPatches.IndexHtml` with the File Transformation plugin.
  The callback adds a tiny bootstrap that loads `jellytrends.js/css` (versioned URLs).
- **Client** (`Web/jellytrends.js`): fetches `/rows`, caches the last answer in localStorage, and mounts rows inside
  `.homeSectionsContainer` using Jellyfin's own section markup (`emby-scroller`, `emby-scrollbuttons`, `card
  overflowPortraitCard`). Plain CSS scroller is the fallback.

## Hard-won gotchas

1. **File Transformation's `fileNamePattern` is a regex.** `index.html` also matches JS chunks
   (`session-login-index-html.<hash>.chunk.js`). Never append to anything that is not a real HTML document
   (0.3.0 broke the login page this way; see CHANGELOG 0.3.1).
2. **`emby-scroller` is not a registered custom element in 10.11** (`customElements.get` is false). Detect it by a
   rendered `[is="emby-scroller"]` on the page. Add `emby-scrollbuttons` only after the scroller is upgraded,
   otherwise Jellyfin logs `addScrollEventListener is not a function`.
3. **Authenticate every data endpoint.** An unscoped library query returns every library.
4. **Match native posters**: `fillHeight=372&fillWidth=248&quality=96&tag=<ImageTag>` shares Jellyfin's image cache.
5. Jellyfin runs one binary per line: net8.0 for 10.10, net9.0 for 10.11, net10.0 for 12.x (compiles, untested).

## Build and test

```sh
dotnet build JellyTrends.sln -c Release                              # 10.11
dotnet build JellyTrends.sln -c Release -p:JellyfinVersion=10.10.7   # 10.10
dotnet build JellyTrends.sln -c Release -p:JellyfinVersion=12.0.0    # 12.x
```

API checks are not enough: 0.3.0 passed them and still broke the web client. Test in a real browser. Playwright
can serve the working-tree JS/CSS in place of the server's, so UI changes need no release or restart:

```python
ctx.route("**/JellyTrends/assets/*", lambda r: r.fulfill(status=200,
    body=open(WEB + ("jellytrends.js" if ".js" in r.request.url else "jellytrends.css"), "rb").read(),
    content_type="text/javascript" if ".js" in r.request.url else "text/css"))
```

Checks worth repeating after UI changes: rows paint with the native sections; first card `x` equals a native
portrait card's `x` (53 px at 1600 px wide) and width (205 px); arrows scroll; a card opens its details page and Back
restores the rows; no `pageerror`s; a JS chunk does not contain `JellyTrends bootstrap`.

## Release

```sh
# bump <PluginVersion> in the csproj, build.yaml, CHANGELOG.md
python scripts/release.py --version X.Y.Z --changelog "What changed"
git rm --cached the old dist/*.zip; git add -A && git add -f dist/*.zip
git commit && git push origin master
```

Versions are `X.Y.Z.<n>`: 0 = Jellyfin 10.10, 1 = 10.11, 2 = 12.x, so a newer server always prefers its own build.
`raw.githubusercontent.com` can lag a few minutes after a push; read the manifest with `gh api` when scripting.

## Open items

- Test the 12.x build on a Jellyfin 12 server, then add `12.0.0` to `--targets` and the manifest.
- Optional hover play overlay (native cards have one; ours link to the details page only).
- No automated tests yet; the matcher (`LibraryMatcher.LibraryIndex`) is the best first candidate.
- Retake the screenshots in `assets/` after UI changes.
