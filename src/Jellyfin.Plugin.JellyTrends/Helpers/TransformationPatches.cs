using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTrends.Model;

namespace Jellyfin.Plugin.JellyTrends.Helpers;

public static class TransformationPatches
{
    public static string IndexHtml(PatchRequestPayload payload)
    {
        if (!Plugin.Instance.Configuration.Enabled)
        {
            return payload.Contents ?? string.Empty;
        }

        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.JellyTrends.Inject.index.html")!;
        using TextReader reader = new StreamReader(stream);

        string importedHtml = reader.ReadToEnd();
        return Regex.Replace(payload.Contents ?? string.Empty, "(</head>)", importedHtml + "$1", RegexOptions.IgnoreCase);
    }
}
