namespace Jellyfin.Plugin.JellyTrends.Model;

/// <summary>
/// The trending charts returned to the web client.
/// </summary>
public sealed class TrendingResult
{
    /// <summary>
    /// Gets or sets the provider that produced this result, for display in the settings page.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    public List<TrendingEntry> Movies { get; set; } = [];

    public List<TrendingEntry> Shows { get; set; } = [];
}
