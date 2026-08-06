using System.Reflection;
using Jellyfin.Plugin.JellyTrends.Configuration;
using Jellyfin.Plugin.JellyTrends.Model;
using Jellyfin.Plugin.JellyTrends.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTrends.Controllers;

[Route("JellyTrends")]
public sealed class JellyTrendsController : ControllerBase
{
    [HttpGet("assets/{file}")]
    public ActionResult GetAsset([FromRoute] string file)
    {
        // Guard against path traversal: only the files embedded under Web/ are servable.
        if (file.Contains('/', StringComparison.Ordinal)
            || file.Contains('\\', StringComparison.Ordinal)
            || file.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        string resourceName = "Jellyfin.Plugin.JellyTrends.Web." + file;
        using Stream? fileStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (fileStream is null)
        {
            return NotFound();
        }

        using StreamReader reader = new(fileStream);
        string fileContents = reader.ReadToEnd();
        string contentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".js" => "text/javascript",
            ".css" => "text/css",
            _ => "text/plain"
        };

        return Content(fileContents, contentType);
    }

    [HttpGet("config")]
    public ActionResult<ClientConfigResponse> GetClientConfig()
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        return Ok(new ClientConfigResponse
        {
            Enabled = config.Enabled && config.EnableHomeRows,
            MaxDisplayItems = Clamp(config.MaxDisplayItems, 1, 50),
            StrictYearMatch = config.StrictYearMatch,
            ShowOnlineRank = config.ShowOnlineRank,
            CardScalePercent = Clamp(config.CardScalePercent, 60, 180),
            TextScalePercent = Clamp(config.TextScalePercent, 70, 180)
        });
    }

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

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public sealed class ClientConfigResponse
    {
        public bool Enabled { get; set; }

        public int MaxDisplayItems { get; set; }

        public bool StrictYearMatch { get; set; }

        public bool ShowOnlineRank { get; set; }

        public int CardScalePercent { get; set; }

        public int TextScalePercent { get; set; }
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
