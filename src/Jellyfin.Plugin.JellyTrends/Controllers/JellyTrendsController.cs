using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Jellyfin.Plugin.JellyTrends.Configuration;
using Jellyfin.Plugin.JellyTrends.Model;
using Jellyfin.Plugin.JellyTrends.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyTrends.Controllers;

[Route("JellyTrends")]
public sealed class JellyTrendsController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, CachedAsset?> AssetCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedRows> RowsCache = new(StringComparer.Ordinal);

    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;

    public JellyTrendsController(ILibraryManager libraryManager, IAuthorizationContext authorizationContext)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Serves the injected script and stylesheet.
    /// </summary>
    /// <remarks>
    /// Both are read out of the assembly once and then held in memory with a content hash as
    /// the ETag, so repeat page loads answer with 304 instead of re-reading and re-sending.
    /// </remarks>
    [HttpGet("assets/{file}")]
    public ActionResult GetAsset([FromRoute] string file)
    {
        // Only the files embedded under Web/ are servable.
        if (file.Contains('/', StringComparison.Ordinal)
            || file.Contains('\\', StringComparison.Ordinal)
            || file.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        CachedAsset? asset = AssetCache.GetOrAdd(file, LoadAsset);
        if (asset is null)
        {
            AssetCache.TryRemove(file, out _);
            return NotFound();
        }

        Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600, must-revalidate";
        Response.Headers[HeaderNames.ETag] = asset.ETag;

        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out Microsoft.Extensions.Primitives.StringValues existing)
            && existing.ToString().Contains(asset.ETag, StringComparison.Ordinal))
        {
            return StatusCode(304);
        }

        return File(asset.Content, asset.ContentType);
    }

    /// <summary>
    /// Returns the rows for the calling user together with the display settings.
    /// </summary>
    [HttpGet("rows")]
    public async Task<ActionResult<RowsResponse>> GetRows(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        RowsResponse response = new()
        {
            Enabled = config.Enabled && config.EnableHomeRows,
            MaxDisplayItems = Clamp(config.MaxDisplayItems, 1, 50),
            ShowOnlineRank = config.ShowOnlineRank,
            CardScalePercent = Clamp(config.CardScalePercent, 60, 180),
            TextScalePercent = Clamp(config.TextScalePercent, 70, 180)
        };

        if (!response.Enabled)
        {
            return Ok(response);
        }

        AuthorizationInfo auth = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        TrendingResult trending = await TrendingService.GetTrendingAsync(config, cancellationToken).ConfigureAwait(false);
        response.Source = trending.Source;

        string cacheKey = string.Join(
            '|',
            auth.UserId.ToString("N", CultureInfo.InvariantCulture),
            trending.Source,
            trending.Movies.Count.ToString(CultureInfo.InvariantCulture),
            trending.Shows.Count.ToString(CultureInfo.InvariantCulture),
            response.MaxDisplayItems.ToString(CultureInfo.InvariantCulture),
            config.StrictYearMatch ? "1" : "0");

        if (RowsCache.TryGetValue(cacheKey, out CachedRows? cached) && cached.ValidUntil > DateTimeOffset.UtcNow)
        {
            response.Movies = cached.Movies;
            response.Shows = cached.Shows;
            return Ok(response);
        }

        response.Movies = LibraryMatcher.Match(_libraryManager, auth, trending.Movies, false, response.MaxDisplayItems, config.StrictYearMatch);
        response.Shows = LibraryMatcher.Match(_libraryManager, auth, trending.Shows, true, response.MaxDisplayItems, config.StrictYearMatch);

        // Held only briefly: the charts change slowly but the library can change at any time,
        // so a newly added title should show up without waiting out the chart cache.
        RowsCache[cacheKey] = new CachedRows
        {
            Movies = response.Movies,
            Shows = response.Shows,
            ValidUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        PruneRowsCache();
        return Ok(response);
    }

    /// <summary>
    /// Returns the raw online charts, before library matching. Useful for diagnosing whether
    /// an empty row means "source is down" or "you own none of these".
    /// </summary>
    [HttpGet("trending")]
    public async Task<ActionResult<TrendingResult>> GetTrending(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        if (!config.Enabled)
        {
            return Ok(new TrendingResult { Source = "disabled" });
        }

        return Ok(await TrendingService.GetTrendingAsync(config, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Refetches the charts and reports what the configured source returned. Used by the
    /// settings page so an API key can be verified without waiting for the cache to expire.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<SourceTestResponse>> TestSource(CancellationToken cancellationToken)
    {
        TrendingService.InvalidateCache();
        RowsCache.Clear();

        TrendingResult result = await TrendingService.GetTrendingAsync(Plugin.Instance.Configuration, cancellationToken).ConfigureAwait(false);

        return Ok(new SourceTestResponse
        {
            Source = result.Source,
            MovieCount = result.Movies.Count,
            ShowCount = result.Shows.Count,
            TopMovie = result.Movies.FirstOrDefault()?.Title ?? string.Empty,
            TopShow = result.Shows.FirstOrDefault()?.Title ?? string.Empty
        });
    }

    private static CachedAsset? LoadAsset(string file)
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.JellyTrends.Web." + file);
        if (stream is null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] content = buffer.ToArray();

        return new CachedAsset
        {
            Content = content,
            ETag = "\"" + Convert.ToHexString(SHA256.HashData(content))[..16] + "\"",
            ContentType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "text/plain; charset=utf-8"
            }
        };
    }

    /// <summary>
    /// Drops expired entries so the cache cannot grow without bound on a busy server.
    /// </summary>
    private static void PruneRowsCache()
    {
        if (RowsCache.Count < 64)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, CachedRows> entry in RowsCache)
        {
            if (entry.Value.ValidUntil <= now)
            {
                RowsCache.TryRemove(entry.Key, out _);
            }
        }
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private sealed class CachedAsset
    {
        public byte[] Content { get; init; } = [];

        public string ETag { get; init; } = string.Empty;

        public string ContentType { get; init; } = string.Empty;
    }

    private sealed class CachedRows
    {
        public List<MatchedItem> Movies { get; init; } = [];

        public List<MatchedItem> Shows { get; init; } = [];

        public DateTimeOffset ValidUntil { get; init; }
    }

    public sealed class SourceTestResponse
    {
        public string Source { get; set; } = string.Empty;

        public int MovieCount { get; set; }

        public int ShowCount { get; set; }

        public string TopMovie { get; set; } = string.Empty;

        public string TopShow { get; set; } = string.Empty;
    }
}
