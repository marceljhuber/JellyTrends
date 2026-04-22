using System.Globalization;
using System.Reflection;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTrends.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTrends.Controllers;

[Route("JellyTrends")]
public sealed class JellyTrendsController : ControllerBase
{
    private static readonly HttpClient HttpClient = new();

    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> ImdbLookupCache = new(StringComparer.Ordinal);
    private static readonly string[] ExtraCountries = ["us", "gb", "de", "fr", "es", "it", "ca", "au", "jp", "br"];

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
                int moviePool = Math.Max(movieLimit * 6, 300);
                int showPool = Math.Max(showLimit * 8, 500);

                Task<List<TrendingEntry>> moviesTask = FetchCinemetaTopAsync(false, moviePool, cancellationToken);
                Task<List<TrendingEntry>> showsTask = FetchCinemetaTopAsync(true, showPool, cancellationToken);

                await Task.WhenAll(moviesTask, showsTask).ConfigureAwait(false);

                List<TrendingEntry> movies = moviesTask.Result;
                List<TrendingEntry> shows = showsTask.Result;

                if (movies.Count < movieLimit || shows.Count < showLimit)
                {
                    Task<List<TrendingEntry>> appleMoviesTask = FetchCombinedFeedAsync(country, movieLimit, false, cancellationToken);
                    Task<List<TrendingEntry>> appleShowsTask = FetchCombinedFeedAsync(country, showLimit, true, cancellationToken);
                    await Task.WhenAll(appleMoviesTask, appleShowsTask).ConfigureAwait(false);

                    movies = MergeTrends(movies, appleMoviesTask.Result);
                    shows = MergeTrends(shows, appleShowsTask.Result);
                }

                response = new TrendingResponse
                {
                    Source = "Cinemeta top lists + Apple RSS backup",
                    Movies = movies,
                    Shows = shows
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

    private static async Task<List<TrendingEntry>> FetchCombinedFeedAsync(string primaryCountry, int limit, bool isShow, CancellationToken cancellationToken)
    {
        List<string> countries = [primaryCountry];
        foreach (string country in ExtraCountries)
        {
            if (!countries.Contains(country, StringComparer.OrdinalIgnoreCase))
            {
                countries.Add(country);
            }
        }

        Dictionary<string, (TrendingEntry Entry, int Score)> merged = new(StringComparer.Ordinal);

        for (int index = 0; index < countries.Count; index++)
        {
            string country = countries[index];
            string url = isShow
                ? $"https://rss.applemarketingtools.com/api/v2/{country}/tv-shows/top-tv-shows/{limit}/tv-shows.json"
                : $"https://rss.applemarketingtools.com/api/v2/{country}/movies/top-movies/{limit}/movies.json";

            List<TrendingEntry> entries;
            try
            {
                entries = await FetchFeedAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (TrendingEntry entry in entries)
            {
                string key = BuildMergeKey(entry.Title, entry.Year);
                int score = (index * 10_000) + entry.Rank;

                if (!merged.TryGetValue(key, out (TrendingEntry Entry, int Score) existing) || score < existing.Score)
                {
                    merged[key] = (entry, score);
                }
            }
        }

        List<TrendingEntry> mergedOrdered = merged
            .OrderBy(x => x.Value.Score)
            .Select(x => x.Value.Entry)
            .ToList();

        for (int i = 0; i < mergedOrdered.Count; i++)
        {
            mergedOrdered[i].Rank = i + 1;
        }

        await EnrichWithImdbIdsAsync(mergedOrdered, isShow, cancellationToken).ConfigureAwait(false);
        return mergedOrdered;
    }

    private static async Task<List<TrendingEntry>> FetchCinemetaTopAsync(bool isShow, int limit, CancellationToken cancellationToken)
    {
        int pageSize = 100;
        List<TrendingEntry> all = [];

        for (int skip = 0; all.Count < limit && skip <= 1000; skip += pageSize)
        {
            string type = isShow ? "series" : "movie";
            string url = skip == 0
                ? $"https://v3-cinemeta.strem.io/catalog/{type}/top.json"
                : $"https://v3-cinemeta.strem.io/catalog/{type}/top/skip={skip}.json";

            List<TrendingEntry> page;
            try
            {
                page = await FetchCinemetaPageAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);

            if (page.Count < pageSize)
            {
                break;
            }
        }

        List<TrendingEntry> deduped = MergeTrends([], all);
        if (deduped.Count > limit)
        {
            deduped = deduped.Take(limit).ToList();
        }

        return deduped;
    }

    private static async Task<List<TrendingEntry>> FetchCinemetaPageAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        CinemetaResponse? payload = await JsonSerializer.DeserializeAsync<CinemetaResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        List<TrendingEntry> items = [];
        if (payload?.Metas is null)
        {
            return items;
        }

        int rank = 1;
        foreach (CinemetaMeta meta in payload.Metas)
        {
            if (string.IsNullOrWhiteSpace(meta.Name))
            {
                continue;
            }

            int? year = TryParseYear(meta.Year) ?? TryParseYear(meta.Released);
            string? imdbId = null;
            if (!string.IsNullOrWhiteSpace(meta.ImdbId) && meta.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                imdbId = meta.ImdbId;
            }
            else if (!string.IsNullOrWhiteSpace(meta.Id) && meta.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                imdbId = meta.Id;
            }

            items.Add(new TrendingEntry
            {
                Rank = rank,
                Title = meta.Name.Trim(),
                Year = year,
                ImdbId = imdbId
            });

            rank++;
        }

        return items;
    }

    private static int? TryParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = Regex.Match(text, "(19|20)\\d{2}");
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Value, CultureInfo.InvariantCulture, out int year) ? year : null;
    }

    private static List<TrendingEntry> MergeTrends(List<TrendingEntry> primary, List<TrendingEntry> secondary)
    {
        List<TrendingEntry> merged = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Append(IEnumerable<TrendingEntry> entries)
        {
            foreach (TrendingEntry entry in entries)
            {
                string key = !string.IsNullOrWhiteSpace(entry.ImdbId)
                    ? "imdb:" + entry.ImdbId
                    : BuildMergeKey(entry.Title, entry.Year);

                if (seen.Add(key))
                {
                    merged.Add(entry);
                }
            }
        }

        Append(primary);
        Append(secondary);

        for (int i = 0; i < merged.Count; i++)
        {
            merged[i].Rank = i + 1;
        }

        return merged;
    }

    private static string BuildMergeKey(string title, int? year)
    {
        return $"{NormalizeTitle(title)}|{year?.ToString(CultureInfo.InvariantCulture) ?? ""}";
    }

    private static string NormalizeTitle(string title)
    {
        string normalized = title.ToLowerInvariant();
        normalized = normalized.Normalize(NormalizationForm.FormD);
        normalized = Regex.Replace(normalized, "[\\u0300-\\u036f]", string.Empty);
        normalized = normalized.Replace("&", " and ", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, "[^a-z0-9 ]", " ");
        normalized = Regex.Replace(normalized, "\\b(the|a|an)\\b", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        return normalized;
    }

    private static async Task EnrichWithImdbIdsAsync(List<TrendingEntry> entries, bool isShow, CancellationToken cancellationToken)
    {
        int enrichLimit = Math.Min(entries.Count, 200);
        for (int i = 0; i < enrichLimit; i++)
        {
            TrendingEntry entry = entries[i];
            if (!string.IsNullOrWhiteSpace(entry.ImdbId))
            {
                continue;
            }

            string cacheKey = $"{(isShow ? "show" : "movie")}|{NormalizeTitle(entry.Title)}|{entry.Year?.ToString(CultureInfo.InvariantCulture) ?? ""}";
            if (ImdbLookupCache.TryGetValue(cacheKey, out string? cachedId))
            {
                entry.ImdbId = cachedId;
                continue;
            }

            string? imdbId = await ResolveImdbIdAsync(entry.Title, entry.Year, isShow, cancellationToken).ConfigureAwait(false);

            ImdbLookupCache[cacheKey] = imdbId ?? string.Empty;
            entry.ImdbId = imdbId;
        }
    }

    private static async Task<string?> ResolveImdbIdAsync(string title, int? year, bool isShow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string firstChar = Uri.EscapeDataString(title.Trim()[0].ToString().ToLowerInvariant());
        string encodedTitle = Uri.EscapeDataString(title.Trim());
        string url = $"https://v2.sg.media-imdb.com/suggestion/{firstChar}/{encodedTitle}.json";

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            ImdbSuggestionResponse? payload = await JsonSerializer.DeserializeAsync<ImdbSuggestionResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (payload?.Results is null)
            {
                return null;
            }

            string normalizedTitle = NormalizeTitle(title);
            ImdbSuggestionItem? best = payload.Results
                .Where(x => IsTypeMatch(x, isShow))
                .OrderBy(x => NormalizeTitle(x.Label) == normalizedTitle ? 0 : 1)
                .ThenBy(x => year.HasValue && x.Year.HasValue ? Math.Abs(x.Year.Value - year.Value) : 999)
                .FirstOrDefault();

            if (best is null || string.IsNullOrWhiteSpace(best.Id))
            {
                return null;
            }

            return best.Id;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTypeMatch(ImdbSuggestionItem item, bool isShow)
    {
        string qid = item.Qid?.ToLowerInvariant() ?? string.Empty;
        string q = item.Q?.ToLowerInvariant() ?? string.Empty;

        if (isShow)
        {
            return qid.Contains("tv", StringComparison.Ordinal) || q.Contains("tv", StringComparison.Ordinal) || q.Contains("series", StringComparison.Ordinal);
        }

        return !qid.Contains("tv", StringComparison.Ordinal) && !q.Contains("tv", StringComparison.Ordinal);
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
                Year = null,
                ImdbId = title == "The Batman" ? "tt1877830" : null
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

        public string? ImdbId { get; set; }
    }

    public sealed class ImdbSuggestionResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("d")]
        public List<ImdbSuggestionItem>? Results { get; set; }
    }

    public sealed class ImdbSuggestionItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("l")]
        public string Label { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("y")]
        public int? Year { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("q")]
        public string? Q { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("qid")]
        public string? Qid { get; set; }
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

    public sealed class CinemetaResponse
    {
        public List<CinemetaMeta>? Metas { get; set; }
    }

    public sealed class CinemetaMeta
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        public string? Year { get; set; }

        public string? Released { get; set; }
    }
}
