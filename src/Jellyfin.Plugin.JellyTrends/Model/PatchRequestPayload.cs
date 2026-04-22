using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrends.Model;

public sealed class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
