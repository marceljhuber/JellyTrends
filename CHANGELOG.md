# Changelog

## 0.3.3

**Fixed**

- The arrow buttons were connected before Jellyfin had upgraded the scroller next to them, which
  logged `addScrollEventListener is not a function` in the console. They are now added once the
  scroller is ready.

## 0.3.2

Layout and load-time fixes, verified against a live Jellyfin 10.11 in a real browser.

**Fixed**

- The `#N` chart badge sat top-left, on top of resolution/quality tags added by other plugins.
  It is now top-right.
- Rows were not aligned with Jellyfin's own sections: the cards started at the screen edge while
  the heading was inset. Rows now use the exact markup of Jellyfin's home sections, so the
  inset, card size (205 px at 1600 px wide), arrow buttons and scrolling are Jellyfin's own and
  line up with "My Media" and the rest.
- Native scroller detection used `customElements.get`, but Jellyfin 10.11 does not register
  its scroller that way, so the native scroller was never used. Detection now looks for a
  rendered Jellyfin scroller on the page.

**Changed — performance**

- The last rows are kept in localStorage (per user, up to 12 hours), so a cold page load paints
  immediately and the network answer only repaints if it differs.
- Rows are drawn together with Jellyfin's own sections instead of earlier or later (they now
  appear within about 60 ms of them), and startup polling is 150 ms instead of 500 ms.
- Posters are requested with the same size and quality as Jellyfin's own portrait cards
  (`fillHeight=372&fillWidth=248&quality=96`), so the server's resized-image cache is shared.

## 0.3.1

**Fixed**

- 0.3.0 broke the web client's login page and other lazy-loaded screens. File Transformation
  matches `index.html` as a regex, so it also passed JS chunks such as
  `session-login-index-html.<hash>.chunk.js` to the plugin, which appended its bootstrap
  `<script>` markup to the JavaScript and caused `Unexpected token '<'`. Only real HTML
  documents (starting with a doctype or `<html`) are modified now. **If you installed 0.3.0,
  update immediately.**

## 0.3.0

Reliability, native look, and multi-version support.

**Security**

- `/JellyTrends/rows` and `/JellyTrends/trending` were reachable without signing in. An
  unauthenticated request ran the library query unscoped and returned titles from every
  library. Both now require a signed-in user, and `/JellyTrends/test` requires an
  administrator.

**Fixed**

- Cards could fail to open: the rows were rebuilt whenever Jellyfin touched the home page,
  which could replace a card between mouse-down and mouse-up. Rows are now only redrawn when
  their content changed, and card clicks navigate directly.
- Rows could stay missing after a quick navigation: an in-flight render was dropped and nothing
  retried it.
- After signing out and into another account, the previous account's rows could be shown.
  Cached rows are now tied to the user.
- The assembly reported version `0.1.7.3` regardless of release. It now follows the project
  version.
- A newly added title now appears on the next home load instead of after the 5 minute cache.
- File Transformation loading after JellyTrends left the injection unregistered until the next
  restart. Registration now polls for it, and no longer depends on the rows setting, so
  toggling it needs no restart.

**Changed**

- Rows use Jellyfin's native `emby-scroller` (arrow buttons, wheel, touch and remote focus)
  with native section headers and a year line under each title. A plain CSS scroller is the
  fallback when the native one is unavailable or fails to size the cards.
- Posters carry Jellyfin's image tag (browser-cacheable) and lazy-load.
- Chart fetching is stale-while-revalidate, retries transient failures once, uses a 12 second
  timeout (was 30), and backs off for two minutes after a total failure.
- Asset URLs are versioned so updates are picked up immediately.
- Row headings are configurable.
- Jellyfin 10.10 (net8.0) is supported again with its own build; the release script produces a
  zip per line. Source also compiles against Jellyfin 12.0 (net10.0).
- `scripts/release.py` replaces the PowerShell script; added the GPL-3.0 license.

## 0.2.1.0

Performance pass, native styling, and a navigation fix.

**Fixed**

- Clicking a card did not always open the item. Jellyfin's own cards link to
  `#/details?id=<id>&serverId=<serverId>`; the `serverId` was missing, so the details route
  could not resolve which server the item belonged to.

**Changed — performance**

- Chart-to-library matching moved from the browser to the server. The client used to request
  the entire library (both movies and series, `Limit: 50000`) on every home load and index it
  locally, normalising every title three ways. Jellyfin does not expose provider-id filtering
  over HTTP, so that download was the only way to do it client side. The plugin now reads the
  library in process and returns just the rows to draw.
- Config and rows collapsed into a single `GET /JellyTrends/rows` request. A render used to
  cost three round trips before anything could be drawn.
- Chart pages are fetched concurrently instead of one after another. A depth-100 Cinemeta
  fetch measures ~140 ms. A failed page no longer takes the whole chart down with it.
- The injected JS and CSS are read from the assembly once, held in memory, and served with an
  ETag and `Cache-Control`, so repeat loads answer 304.
- Matched rows are cached per user for five minutes, short enough that newly added titles
  appear without waiting out the chart cache.
- The DOM observer now watches `#homeTab` rather than `document.body` with `subtree`. Media
  Bar's slideshow lives on `document.body` and mutates continuously, so the old observer woke
  on every slide transition, progress tick and image load.
- Cards are assembled in a `DocumentFragment` so the browser lays out once per row.

**Changed — appearance**

- Rows now render with Jellyfin's own classes (`verticalSection`, `sectionTitle-cards`,
  `card overflowPortraitCard`, `cardBox`, `cardScalable`, `cardPadder-overflowPortrait`,
  `cardImageContainer`, `cardText`), so they inherit native card sizing, spacing, hover and
  typography and match sections like Continue Watching. The stylesheet now only supplies what
  Jellyfin does not: the horizontal scroller, the rank badge and the size scaling.
- `GET /JellyTrends/config` is gone, replaced by `/rows`. `/trending` is kept for diagnostics.

## 0.2.0.1

**Fixed**

- Rows rendered as blank space when [Media Bar](https://github.com/IAmParadox27/jellyfin-plugin-media-bar)
  was installed. Media Bar offsets `.homeSectionsContainer` with `top: 65vh` to clear its
  full-bleed slideshow; 0.2.0.0 mounted the rows outside that container, so they missed the
  offset and rendered underneath the slideshow. Rows now mount inside the container again and
  the MutationObserver re-attaches them after Jellyfin rebuilds the sections.

## 0.2.0.0

Rebuild of the trending pipeline, retargeted to Jellyfin 10.11.

**Fixed**

- The Apple RSS chart feed used as a fallback returns 404 on both
  `rss.applemarketingtools.com` and `rss.marketingtools.apple.com`. Removed.
- Cinemeta paging dropped half of every chart: the catalog serves 50 entries per page, but
  the skip offset advanced by 100.
- Cinemeta types `tvdb_id` as either a number or a string. Strict deserialization threw on
  the first mistyped series, failing the whole fetch and leaving the shows row empty.
- The library query sent `ProductionYear` and `ImageTags` in `Fields`. Neither is a valid
  `ItemFields` value and Jellyfin 10.11 rejects the entire request with HTTP 400. Both are
  returned by default anyway.
- Ranks came from IMDb keyword seeding rather than any chart, so positions reflected which
  fixed keywords happened to hit.
- The asset endpoint did not reject path traversal.

**Added**

- Provider chain with TMDB, Trakt and Cinemeta. Sources are tried in order and fall through
  on failure, so a server with no credentials still gets charts from keyless Cinemeta.
- TMDB accepts both the v3 API key and the v4 API Read Access Token, detected automatically.
- `POST /JellyTrends/test` and a **Test source** button that report which source answered.
- Configurable row size (1–50). Row headings follow the number.
- **Show the online chart position** toggle. On by default: a title at #37 worldwide keeps
  the `#37` badge even when it is the third one you own.
- Sub path support — the injected bootstrap resolves the server root at runtime, so
  `https://example.com/jellyfin/` works.

**Changed**

- Retargeted to Jellyfin 10.11.11 with `targetAbi` `10.11.0.0`, so the plugin installs on
  every patch of the 10.11 line. 10.10 is no longer served by the manifest.
- Removed the hardcoded fallback title list. Presenting a stale 2024 list as "trending" is
  worse than showing nothing; the last known charts are served instead.
- `EnableExperimentalHomeInjection` renamed to `EnableHomeRows` and now defaults to on.
- Removed the unused `CountryCode` setting, which only fed the dead Apple feed.
- The manifest is written without a UTF-8 BOM.

## 0.1.7.x and earlier

See the git history.
