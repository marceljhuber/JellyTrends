# Changelog

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
