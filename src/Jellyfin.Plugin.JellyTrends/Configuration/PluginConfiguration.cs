using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrends.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public string CountryCode { get; set; } = "us";

    public int MovieFeedLimit { get; set; } = 100;

    public int ShowFeedLimit { get; set; } = 100;

    public int MaxDisplayItems { get; set; } = 10;

    public int CacheDurationMinutes { get; set; } = 180;

    public bool StrictYearMatch { get; set; }
}
