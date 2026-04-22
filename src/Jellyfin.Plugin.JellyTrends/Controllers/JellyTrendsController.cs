using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyTrends.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTrends.Controllers;

[Route("JellyTrends")]
public sealed class JellyTrendsController : ControllerBase
{
    private static readonly HttpClient HttpClient = new();

    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    private static string _cacheKey = string.Empty;

    private static DateTimeOffset _cacheValidUntil = DateTimeOffset.MinValue;

    private static TrendingResponse? _cachedResponse;

    [HttpGet("assets/{file}")]
    public ActionResult GetAsset([FromRoute] string file)
    {
        string resourceName = "Jellyfin.Plugin.JellyTrends.Web." + file;
        Stream? fileStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (fileStream is null)
        {
            return NotFound();
        }

        string fileContents = new StreamReader(fileStream).ReadToEnd();
        string extension = Path.GetExtension(file).ToLowerInvariant();
        string contentType = extension switch
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
            Enabled = config.Enabled,
            MaxDisplayItems = Clamp(config.MaxDisplayItems, 1, 25),
            StrictYearMatch = config.StrictYearMatch
        });
    }

    [HttpGet("trending")]
    public async Task<ActionResult<TrendingResponse>> GetTrending(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        if (!config.Enabled)
        {
            return Ok(new TrendingResponse());
        }

        string country = string.IsNullOrWhiteSpace(config.CountryCode) ? "us" : config.CountryCode.Trim().ToLowerInvariant();
        int movieLimit = Clamp(config.MovieFeedLimit, 10, 200);
        int showLimit = Clamp(config.ShowFeedLimit, 10, 200);
        int cacheDurationMinutes = Clamp(config.CacheDurationMinutes, 5, 1440);
        string key = $"{country}:{movieLimit}:{showLimit}";

        if (_cachedResponse is not null && _cacheKey == key && _cacheValidUntil > DateTimeOffset.UtcNow)
        {
            return Ok(_cachedResponse);
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedResponse is not null && _cacheKey == key && _cacheValidUntil > DateTimeOffset.UtcNow)
            {
                return Ok(_cachedResponse);
            }

            TrendingResponse response;

            try
            {
                Task<List<TrendingEntry>> moviesTask = FetchFeedAsync(
                    $"https://rss.applemarketingtools.com/api/v2/{country}/movies/top-movies/{movieLimit}/movies.json",
                    cancellationToken);

                Task<List<TrendingEntry>> showsTask = FetchFeedAsync(
                    $"https://rss.applemarketingtools.com/api/v2/{country}/tv-shows/top-tv-shows/{showLimit}/tv-shows.json",
                    cancellationToken);

                await Task.WhenAll(moviesTask, showsTask).ConfigureAwait(false);

                response = new TrendingResponse
                {
                    Source = "Apple Marketing RSS",
                    Movies = moviesTask.Result,
                    Shows = showsTask.Result
                };
            }
            catch
            {
                response = new TrendingResponse
                {
                    Source = "Built-in fallback list",
                    Movies = GetFallbackMovies(),
                    Shows = GetFallbackShows()
                };
            }

            _cachedResponse = response;
            _cacheKey = key;
            _cacheValidUntil = DateTimeOffset.UtcNow.AddMinutes(cacheDurationMinutes);

            return Ok(_cachedResponse);
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static async Task<List<TrendingEntry>> FetchFeedAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        AppleFeedResponse? payload = await JsonSerializer.DeserializeAsync<AppleFeedResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        List<TrendingEntry> results = [];
        if (payload?.Feed?.Results is null)
        {
            return results;
        }

        int rank = 1;
        foreach (AppleFeedItem item in payload.Feed.Results)
        {
            int? year = null;
            if (DateTime.TryParse(item.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedDate))
            {
                year = parsedDate.Year;
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                results.Add(new TrendingEntry
                {
                    Rank = rank,
                    Title = item.Name.Trim(),
                    Year = year
                });
                rank++;
            }
        }

        return results;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static List<TrendingEntry> GetFallbackMovies()
    {
        string[] titles =
        [
            "Dune: Part Two", "Oppenheimer", "Barbie", "Poor Things", "Civil War",
            "The Batman", "Top Gun: Maverick", "The Holdovers", "Killers of the Flower Moon", "The Iron Claw",
            "Godzilla Minus One", "The Killer", "Napoleon", "The Beekeeper", "The Creator",
            "Everything Everywhere All at Once", "Spider-Man: Across the Spider-Verse", "John Wick: Chapter 4", "Wonka", "The Fall Guy"
        ];

        return ToRankedEntries(titles);
    }

    private static List<TrendingEntry> GetFallbackShows()
    {
        string[] titles =
        [
            "Shogun", "Fallout", "The Last of Us", "House of the Dragon", "The Bear",
            "The Gentlemen", "True Detective", "Severance", "The Boys", "3 Body Problem",
            "The Penguin", "The White Lotus", "Silo", "Dark", "Breaking Bad",
            "The Office", "Stranger Things", "The Mandalorian", "Andor", "Squid Game"
        ];

        return ToRankedEntries(titles);
    }

    private static List<TrendingEntry> ToRankedEntries(IEnumerable<string> titles)
    {
        List<TrendingEntry> entries = [];
        int rank = 1;
        foreach (string title in titles)
        {
            entries.Add(new TrendingEntry
            {
                Rank = rank,
                Title = title,
                Year = null
            });
            rank++;
        }

        return entries;
    }

    public sealed class ClientConfigResponse
    {
        public bool Enabled { get; set; }

        public int MaxDisplayItems { get; set; }

        public bool StrictYearMatch { get; set; }
    }

    public sealed class TrendingResponse
    {
        public string Source { get; set; } = string.Empty;

        public List<TrendingEntry> Movies { get; set; } = [];

        public List<TrendingEntry> Shows { get; set; } = [];
    }

    public sealed class TrendingEntry
    {
        public int Rank { get; set; }

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }
    }

    public sealed class AppleFeedResponse
    {
        public AppleFeed? Feed { get; set; }
    }

    public sealed class AppleFeed
    {
        public List<AppleFeedItem>? Results { get; set; }
    }

    public sealed class AppleFeedItem
    {
        public string Name { get; set; } = string.Empty;

        public string ReleaseDate { get; set; } = string.Empty;
    }
}
