using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTrends.Configuration;
using Jellyfin.Plugin.JellyTrends.Model;

namespace Jellyfin.Plugin.JellyTrends.Services;

/// <summary>
/// Fetches trending charts from the configured online source.
/// </summary>
/// <remarks>
/// Providers are tried in order and the first one that returns usable data wins, so a
/// server without any API key still gets charts from the keyless Cinemeta catalog.
/// </remarks>
public static class TrendingService
{
    private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
    private const string TraktBaseUrl = "https://api.trakt.tv";
    private const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";

    // Cinemeta serves 50 entries per catalog page and the skip offset moves in the same step.
    private const int CinemetaPageSize = 50;

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    /// <summary>
    /// Cinemeta types some ids inconsistently (tvdb_id comes back as a number for some
    /// titles and as a string for others), so numbers are read from either form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static TrendingResult? _cachedResult;
    private static string _cacheKey = string.Empty;
    private static DateTimeOffset _cacheValidUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Gets the trending charts, honouring the configured cache duration.
    /// </summary>
    public static async Task<TrendingResult> GetTrendingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        int movieLimit = Clamp(config.MovieFeedLimit, 10, 500);
        int showLimit = Clamp(config.ShowFeedLimit, 10, 500);
        int cacheMinutes = Clamp(config.CacheDurationMinutes, 5, 1440);
        string window = NormalizeWindow(config.TrendingWindow);
        string key = string.Join(
            '|',
            NormalizeSource(config.TrendingSource),
            window,
            movieLimit.ToString(CultureInfo.InvariantCulture),
            showLimit.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(config.TmdbApiKey) ? "0" : "1",
            string.IsNullOrWhiteSpace(config.TraktClientId) ? "0" : "1");

        if (TryGetFresh(key, out TrendingResult? fresh))
        {
            return fresh;
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key, out TrendingResult? refreshed))
            {
                return refreshed;
            }

            foreach (string provider in ResolveProviderChain(config))
            {
                TrendingResult result;
                try
                {
                    result = await FetchFromProviderAsync(provider, config, window, movieLimit, showLimit, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (result.Movies.Count == 0 && result.Shows.Count == 0)
                {
                    continue;
                }

                _cachedResult = result;
                _cacheKey = key;
                _cacheValidUntil = DateTimeOffset.UtcNow.AddMinutes(cacheMinutes);
                return result;
            }

            // Every provider failed. Serving the last known charts beats serving nothing,
            // so keep the stale entry alive rather than dropping the rows entirely.
            if (_cachedResult is not null)
            {
                _cacheValidUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                return _cachedResult;
            }

            return new TrendingResult { Source = "unavailable" };
        }
        finally
        {
            CacheLock.Release();
        }
    }

    /// <summary>
    /// Drops the cached charts so the next request refetches.
    /// </summary>
    public static void InvalidateCache()
    {
        _cacheValidUntil = DateTimeOffset.MinValue;
    }

    private static bool TryGetFresh(string key, out TrendingResult result)
    {
        if (_cachedResult is not null && _cacheKey == key && _cacheValidUntil > DateTimeOffset.UtcNow)
        {
            result = _cachedResult;
            return true;
        }

        result = null!;
        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Some of the upstream endpoints reject requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyTrends/1.0 (+https://github.com/marceljhuber/JellyTrends)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Builds the ordered list of providers to try. Cinemeta always terminates the chain
    /// because it needs no credentials.
    /// </summary>
    private static List<string> ResolveProviderChain(PluginConfiguration config)
    {
        bool hasTmdb = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        bool hasTrakt = !string.IsNullOrWhiteSpace(config.TraktClientId);
        string requested = NormalizeSource(config.TrendingSource);

        List<string> chain = [];

        void Add(string provider)
        {
            if (!chain.Contains(provider, StringComparer.Ordinal))
            {
                chain.Add(provider);
            }
        }

        switch (requested)
        {
            case "tmdb" when hasTmdb:
                Add("tmdb");
                break;
            case "trakt" when hasTrakt:
                Add("trakt");
                break;
            case "cinemeta":
                Add("cinemeta");
                return chain;
            default:
                if (hasTmdb)
                {
                    Add("tmdb");
                }

                if (hasTrakt)
                {
                    Add("trakt");
                }

                break;
        }

        Add("cinemeta");
        return chain;
    }

    private static async Task<TrendingResult> FetchFromProviderAsync(
        string provider,
        PluginConfiguration config,
        string window,
        int movieLimit,
        int showLimit,
        CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case "tmdb":
            {
                string key = config.TmdbApiKey.Trim();
                Task<List<TrendingEntry>> movies = FetchTmdbAsync(key, window, false, movieLimit, cancellationToken);
                Task<List<TrendingEntry>> shows = FetchTmdbAsync(key, window, true, showLimit, cancellationToken);
                await Task.WhenAll(movies, shows).ConfigureAwait(false);
                return new TrendingResult
                {
                    Source = $"TMDB trending ({window})",
                    Movies = movies.Result,
                    Shows = shows.Result
                };
            }

            case "trakt":
            {
                string clientId = config.TraktClientId.Trim();
                Task<List<TrendingEntry>> movies = FetchTraktAsync(clientId, false, movieLimit, cancellationToken);
                Task<List<TrendingEntry>> shows = FetchTraktAsync(clientId, true, showLimit, cancellationToken);
                await Task.WhenAll(movies, shows).ConfigureAwait(false);
                return new TrendingResult
                {
                    Source = "Trakt trending",
                    Movies = movies.Result,
                    Shows = shows.Result
                };
            }

            default:
            {
                Task<List<TrendingEntry>> movies = FetchCinemetaAsync(false, movieLimit, cancellationToken);
                Task<List<TrendingEntry>> shows = FetchCinemetaAsync(true, showLimit, cancellationToken);
                await Task.WhenAll(movies, shows).ConfigureAwait(false);
                return new TrendingResult
                {
                    Source = "Cinemeta popular (no API key)",
                    Movies = movies.Result,
                    Shows = shows.Result
                };
            }
        }
    }

    private static async Task<List<TrendingEntry>> FetchTmdbAsync(string apiKey, string window, bool isShow, int limit, CancellationToken cancellationToken)
    {
        string kind = isShow ? "tv" : "movie";
        List<TrendingEntry> entries = [];

        // TMDB hands out two different credentials. The v3 key goes in the query string,
        // while the v4 "API Read Access Token" is a JWT and must be sent as a bearer token.
        bool isReadAccessToken = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
        Action<HttpRequestMessage>? authorize = isReadAccessToken
            ? request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey)
            : null;

        // TMDB serves 20 per page. The page count is known up front, so they are fetched
        // concurrently instead of paying one round trip per page.
        int pageCount = Math.Min(25, ((limit - 1) / 20) + 1);
        Task<TmdbPage?>[] pages = new Task<TmdbPage?>[pageCount];
        for (int page = 1; page <= pageCount; page++)
        {
            string query = $"page={page.ToString(CultureInfo.InvariantCulture)}";
            if (!isReadAccessToken)
            {
                query += $"&api_key={Uri.EscapeDataString(apiKey)}";
            }

            pages[page - 1] = GetJsonOrNullAsync<TmdbPage>($"{TmdbBaseUrl}/trending/{kind}/{window}?{query}", authorize, cancellationToken);
        }

        TmdbPage?[] results = await Task.WhenAll(pages).ConfigureAwait(false);

        foreach (TmdbPage? payload in results)
        {
            // Stop at the first gap so chart positions stay in order.
            if (payload?.Results is null || payload.Results.Count == 0)
            {
                break;
            }

            foreach (TmdbItem item in payload.Results)
            {
                string? title = isShow ? item.Name : item.Title;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                entries.Add(new TrendingEntry
                {
                    Title = title.Trim(),
                    Year = TryParseYear(isShow ? item.FirstAirDate : item.ReleaseDate),
                    TmdbId = item.Id > 0 ? item.Id.ToString(CultureInfo.InvariantCulture) : null
                });
            }
        }

        if (entries.Count == 0)
        {
            // Nothing came back at all, which means the credential or the endpoint is bad.
            // Surface it so the provider chain moves on to the next source.
            throw new InvalidOperationException("TMDB returned no usable trending entries.");
        }

        return Finalize(entries, limit);
    }

    private static async Task<List<TrendingEntry>> FetchTraktAsync(string clientId, bool isShow, int limit, CancellationToken cancellationToken)
    {
        string kind = isShow ? "shows" : "movies";
        List<TrendingEntry> entries = [];

        void ApplyHeaders(HttpRequestMessage request)
        {
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", clientId);
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        }

        // Trakt caps a page at 100 items, so deeper charts need more than one request.
        for (int page = 1; entries.Count < limit && page <= 5; page++)
        {
            int pageSize = Math.Min(100, limit);
            string url = $"{TraktBaseUrl}/{kind}/trending?page={page.ToString(CultureInfo.InvariantCulture)}&limit={pageSize.ToString(CultureInfo.InvariantCulture)}";
            List<TraktTrendingItem>? payload = await GetJsonAsync<List<TraktTrendingItem>>(url, ApplyHeaders, cancellationToken).ConfigureAwait(false);
            if (payload is null || payload.Count == 0)
            {
                break;
            }

            foreach (TraktTrendingItem wrapper in payload)
            {
                TraktMedia? media = isShow ? wrapper.Show : wrapper.Movie;
                if (media is null || string.IsNullOrWhiteSpace(media.Title))
                {
                    continue;
                }

                entries.Add(new TrendingEntry
                {
                    Title = media.Title.Trim(),
                    Year = media.Year,
                    ImdbId = media.Ids?.Imdb,
                    TmdbId = media.Ids?.Tmdb?.ToString(CultureInfo.InvariantCulture),
                    TvdbId = media.Ids?.Tvdb?.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (payload.Count < pageSize)
            {
                break;
            }
        }

        return Finalize(entries, limit);
    }

    private static async Task<List<TrendingEntry>> FetchCinemetaAsync(bool isShow, int limit, CancellationToken cancellationToken)
    {
        string type = isShow ? "series" : "movie";
        List<TrendingEntry> entries = [];

        // Page offsets are known from the requested depth, so the pages are fetched together.
        int pageCount = Math.Min(21, ((limit - 1) / CinemetaPageSize) + 1);
        Task<CinemetaResponse?>[] pages = new Task<CinemetaResponse?>[pageCount];
        for (int page = 0; page < pageCount; page++)
        {
            int skip = page * CinemetaPageSize;
            string url = skip == 0
                ? $"{CinemetaBaseUrl}/catalog/{type}/top.json"
                : $"{CinemetaBaseUrl}/catalog/{type}/top/skip={skip.ToString(CultureInfo.InvariantCulture)}.json";

            pages[page] = GetJsonOrNullAsync<CinemetaResponse>(url, null, cancellationToken);
        }

        CinemetaResponse?[] results = await Task.WhenAll(pages).ConfigureAwait(false);

        foreach (CinemetaResponse? payload in results)
        {
            // Stop at the first gap so chart positions stay in order.
            if (payload?.Metas is null || payload.Metas.Count == 0)
            {
                break;
            }

            foreach (CinemetaMeta meta in payload.Metas)
            {
                if (string.IsNullOrWhiteSpace(meta.Name))
                {
                    continue;
                }

                string? imdbId = FirstImdbId(meta.ImdbId, meta.Id);
                entries.Add(new TrendingEntry
                {
                    Title = meta.Name.Trim(),
                    Year = TryParseYear(meta.Year) ?? TryParseYear(meta.Released),
                    ImdbId = imdbId,
                    TmdbId = meta.MoviedbId?.ToString(CultureInfo.InvariantCulture),
                    TvdbId = meta.TvdbId?.ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        return Finalize(entries, limit);
    }

    /// <summary>
    /// Fetches a page, returning null instead of throwing when it fails.
    /// </summary>
    /// <remarks>
    /// Pages are requested concurrently, so a single bad page must not take the whole chart
    /// down with it. Callers stop at the first null to keep chart positions contiguous.
    /// </remarks>
    private static async Task<T?> GetJsonOrNullAsync<T>(string url, Action<HttpRequestMessage>? configureRequest, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await GetJsonAsync<T>(url, configureRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> GetJsonAsync<T>(string url, Action<HttpRequestMessage>? configureRequest, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        configureRequest?.Invoke(request);

        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes duplicates, truncates to the requested depth and numbers the chart from 1.
    /// The resulting <see cref="TrendingEntry.Rank"/> is the online position shown on the cards.
    /// </summary>
    private static List<TrendingEntry> Finalize(List<TrendingEntry> entries, int limit)
    {
        List<TrendingEntry> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrendingEntry entry in entries)
        {
            string key = !string.IsNullOrWhiteSpace(entry.ImdbId)
                ? "imdb:" + entry.ImdbId
                : !string.IsNullOrWhiteSpace(entry.TmdbId)
                    ? "tmdb:" + entry.TmdbId
                    : $"title:{entry.Title}|{entry.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";

            if (!seen.Add(key))
            {
                continue;
            }

            entry.Rank = result.Count + 1;
            result.Add(entry);

            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    private static string? FirstImdbId(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
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

    private static string NormalizeSource(string? source)
    {
        string value = (source ?? "auto").Trim().ToLowerInvariant();
        return value is "tmdb" or "trakt" or "cinemeta" ? value : "auto";
    }

    private static string NormalizeWindow(string? window)
    {
        return (window ?? "week").Trim().Equals("day", StringComparison.OrdinalIgnoreCase) ? "day" : "week";
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private sealed class TmdbPage
    {
        [JsonPropertyName("results")]
        public List<TmdbItem>? Results { get; set; }

        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }
    }

    private sealed class TmdbItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }
    }

    private sealed class TraktTrendingItem
    {
        [JsonPropertyName("movie")]
        public TraktMedia? Movie { get; set; }

        [JsonPropertyName("show")]
        public TraktMedia? Show { get; set; }
    }

    private sealed class TraktMedia
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("ids")]
        public TraktIds? Ids { get; set; }
    }

    private sealed class TraktIds
    {
        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("tmdb")]
        public int? Tmdb { get; set; }

        [JsonPropertyName("tvdb")]
        public int? Tvdb { get; set; }
    }

    private sealed class CinemetaResponse
    {
        [JsonPropertyName("metas")]
        public List<CinemetaMeta>? Metas { get; set; }
    }

    private sealed class CinemetaMeta
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("moviedb_id")]
        public int? MoviedbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("year")]
        public string? Year { get; set; }

        [JsonPropertyName("released")]
        public string? Released { get; set; }
    }
}
