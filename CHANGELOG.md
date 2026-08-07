# Changelog

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
