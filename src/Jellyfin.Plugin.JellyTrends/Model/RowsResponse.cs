namespace Jellyfin.Plugin.JellyTrends.Model;

/// <summary>
/// Everything the web client needs for one render, in a single response.
/// </summary>
/// <remarks>
/// Display settings travel with the rows deliberately: splitting them across endpoints cost
/// an extra round trip before the client could draw anything.
/// </remarks>
public sealed class RowsResponse
{
    public bool Enabled { get; set; }

    public int MaxDisplayItems { get; set; }

    public bool ShowOnlineRank { get; set; }

    public int CardScalePercent { get; set; }

    public int TextScalePercent { get; set; }

    /// <summary>
    /// Gets or sets the provider that produced the charts, for diagnostics.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    public List<MatchedItem> Movies { get; set; } = [];

    public List<MatchedItem> Shows { get; set; } = [];
}
