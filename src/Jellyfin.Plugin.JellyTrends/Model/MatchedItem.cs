namespace Jellyfin.Plugin.JellyTrends.Model;

/// <summary>
/// A chart entry that exists in the caller's library, reduced to what a card needs to draw.
/// </summary>
public sealed class MatchedItem
{
    /// <summary>
    /// Gets or sets the position in the online chart, not the position within the row.
    /// </summary>
    public int Rank { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int? ProductionYear { get; set; }

    public bool HasPrimaryImage { get; set; }
}
