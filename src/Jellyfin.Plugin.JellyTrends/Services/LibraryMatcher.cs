using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrends.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTrends.Services;

/// <summary>
/// Resolves online chart entries to items in a user's library.
/// </summary>
/// <remarks>
/// This runs on the server rather than in the browser on purpose. Jellyfin does not expose
/// provider-id filtering through the HTTP item API, so a client doing this work has to pull
/// the whole library down and index it locally. In process we read the same items straight
/// from the library manager, so the client only ever receives the handful of rows it draws.
/// </remarks>
public static class LibraryMatcher
{
    private static readonly char[] IdSeparators = ['|', ',', ';', ' '];
    private static readonly Regex DiacriticsRegex = new("[\\u0300-\\u036f]", RegexOptions.Compiled);
    private static readonly Regex NonAlphanumericRegex = new("[^a-z0-9 ]", RegexOptions.Compiled);
    private static readonly Regex LeadingArticleRegex = new("\\b(the|a|an)\\b", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex YearRegex = new("(19|20)\\d{2}", RegexOptions.Compiled);

    /// <summary>
    /// Picks the first <paramref name="maxItems"/> chart entries the user actually owns.
    /// </summary>
    public static List<MatchedItem> Match(
        ILibraryManager libraryManager,
        AuthorizationInfo? auth,
        IReadOnlyList<TrendingEntry> entries,
        bool isShow,
        int maxItems,
        bool strictYearMatch)
    {
        List<MatchedItem> matches = [];
        if (entries.Count == 0 || maxItems <= 0)
        {
            return matches;
        }

        LibraryIndex index = BuildIndex(libraryManager, auth, isShow);
        if (index.IsEmpty)
        {
            return matches;
        }

        HashSet<Guid> used = [];

        foreach (TrendingEntry entry in entries)
        {
            BaseItem? item = index.Find(entry, strictYearMatch);
            if (item is null || !used.Add(item.Id))
            {
                continue;
            }

            matches.Add(new MatchedItem
            {
                // Carry the online position through untouched so the badge can show where the
                // title ranks on the chart, not where it ranks among the ones you happen to own.
                Rank = entry.Rank,
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = item.Name ?? string.Empty,
                ProductionYear = item.ProductionYear,
                HasPrimaryImage = item.ImageInfos.Any(image => image.Type == ImageType.Primary)
            });

            if (matches.Count >= maxItems)
            {
                break;
            }
        }

        return matches;
    }

    private static LibraryIndex BuildIndex(ILibraryManager libraryManager, AuthorizationInfo? auth, bool isShow)
    {
        InternalItemsQuery query = new()
        {
            Recursive = true,
            IncludeItemTypes = [isShow ? BaseItemKind.Series : BaseItemKind.Movie],

            // Scoping to the caller keeps library access rules intact: a user must never be
            // shown a title they cannot otherwise see. The User entity changed namespace
            // between 10.10 and 10.11, so it is assigned without naming the type.
            User = auth?.User
        };

        LibraryIndex index = new();
        foreach (BaseItem item in libraryManager.GetItemList(query))
        {
            index.Add(item);
        }

        return index;
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string normalized = title.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        normalized = DiacriticsRegex.Replace(normalized, string.Empty);
        normalized = normalized.Replace("&", " and ", StringComparison.Ordinal);
        normalized = NonAlphanumericRegex.Replace(normalized, " ");
        normalized = LeadingArticleRegex.Replace(normalized, " ");
        return WhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private sealed class LibraryIndex
    {
        private readonly Dictionary<string, BaseItem> _byProvider = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BaseItem> _byTitleYear = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BaseItem>> _byTitle = new(StringComparer.Ordinal);

        public bool IsEmpty => _byProvider.Count == 0 && _byTitle.Count == 0;

        public void Add(BaseItem item)
        {
            foreach (KeyValuePair<string, string> provider in item.ProviderIds)
            {
                if (string.IsNullOrWhiteSpace(provider.Value))
                {
                    continue;
                }

                // A few providers store several ids in one value, e.g. "tt123|tt456".
                foreach (string id in provider.Value.Split(IdSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    _byProvider.TryAdd(provider.Key + ":" + id.Trim(), item);
                }
            }

            foreach (string? candidate in new[] { item.Name, item.OriginalTitle })
            {
                string title = NormalizeTitle(candidate);
                if (title.Length == 0)
                {
                    continue;
                }

                if (!_byTitle.TryGetValue(title, out List<BaseItem>? bucket))
                {
                    bucket = [];
                    _byTitle[title] = bucket;
                }

                bucket.Add(item);

                if (item.ProductionYear.HasValue)
                {
                    _byTitleYear.TryAdd(BuildTitleYearKey(title, item.ProductionYear.Value), item);
                }
            }
        }

        public BaseItem? Find(TrendingEntry entry, bool strictYearMatch)
        {
            BaseItem? item = FindByProvider("Imdb", entry.ImdbId)
                ?? FindByProvider("Tmdb", entry.TmdbId)
                ?? FindByProvider("Tvdb", entry.TvdbId);

            if (item is not null)
            {
                return item;
            }

            string title = NormalizeTitle(entry.Title);
            if (title.Length == 0)
            {
                return null;
            }

            if (entry.Year.HasValue && _byTitleYear.TryGetValue(BuildTitleYearKey(title, entry.Year.Value), out BaseItem? exact))
            {
                return exact;
            }

            if (strictYearMatch || !_byTitle.TryGetValue(title, out List<BaseItem>? candidates) || candidates.Count == 0)
            {
                return null;
            }

            if (!entry.Year.HasValue)
            {
                return candidates[0];
            }

            BaseItem? closest = null;
            int closestDelta = int.MaxValue;
            foreach (BaseItem candidate in candidates)
            {
                if (!candidate.ProductionYear.HasValue)
                {
                    continue;
                }

                int delta = Math.Abs(candidate.ProductionYear.Value - entry.Year.Value);
                if (delta < closestDelta)
                {
                    closestDelta = delta;
                    closest = candidate;
                }
            }

            return closestDelta <= 4 ? closest : candidates[0];
        }

        private BaseItem? FindByProvider(string provider, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _byProvider.TryGetValue(provider + ":" + id.Trim(), out BaseItem? item) ? item : null;
        }

        private static string BuildTitleYearKey(string title, int year)
        {
            return title + "|" + year.ToString(CultureInfo.InvariantCulture);
        }
    }
}
