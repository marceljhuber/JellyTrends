using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrends.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool EnableHomeRows { get; set; } = true;

    /// <summary>
    /// Gets or sets the preferred trending source: auto, tmdb, trakt or cinemeta.
    /// </summary>
    public string TrendingSource { get; set; } = "auto";

    public string TmdbApiKey { get; set; } = string.Empty;

    public string TraktClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the trending window used by TMDB: day or week.
    /// </summary>
    public string TrendingWindow { get; set; } = "week";

    public int MovieFeedLimit { get; set; } = 100;

    public int ShowFeedLimit { get; set; } = 100;

    public int MaxDisplayItems { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether badges show the position in the online
    /// chart instead of the position inside the rendered row.
    /// </summary>
    public bool ShowOnlineRank { get; set; } = true;

    public int CardScalePercent { get; set; } = 100;

    public int TextScalePercent { get; set; } = 100;

    public int CacheDurationMinutes { get; set; } = 180;

    public bool StrictYearMatch { get; set; }
}
