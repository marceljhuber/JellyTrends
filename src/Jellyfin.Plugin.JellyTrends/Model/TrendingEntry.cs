namespace Jellyfin.Plugin.JellyTrends.Model;

/// <summary>
/// A single title from an online trending chart.
/// </summary>
public sealed class TrendingEntry
{
    /// <summary>
    /// Gets or sets the 1-based position in the online chart. This is kept as-is when the
    /// entry is matched against the library so the UI can show the real online number.
    /// </summary>
    public int Rank { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? ImdbId { get; set; }

    public string? TmdbId { get; set; }

    public string? TvdbId { get; set; }
}
