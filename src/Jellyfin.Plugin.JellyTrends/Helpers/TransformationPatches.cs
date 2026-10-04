using System.Reflection;
using Jellyfin.Plugin.JellyTrends.Model;

namespace Jellyfin.Plugin.JellyTrends.Helpers;

public static class TransformationPatches
{
    public static string IndexHtml(PatchRequestPayload payload)
    {
        string original = payload.Contents ?? string.Empty;

        if (!Plugin.Instance.Configuration.Enabled || !Plugin.Instance.Configuration.EnableHomeRows)
        {
            return original;
        }

        try
        {
            if (original.Contains("JellyTrends bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.JellyTrends.Inject.index.html");
            if (stream is null)
            {
                return original;
            }

            using TextReader reader = new StreamReader(stream);
            string importedHtml = reader.ReadToEnd();

            int headCloseIndex = original.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headCloseIndex < 0)
            {
                return original + importedHtml.Replace("__JT_VERSION__", Plugin.Instance.Version.ToString(), StringComparison.Ordinal);
            }

            // The version in the asset URLs busts browser and WebView caches on upgrade, so a
            // stale script can never run against a newer server.
            importedHtml = importedHtml.Replace(
                "__JT_VERSION__",
                Plugin.Instance.Version.ToString(),
                StringComparison.Ordinal);

            return original.Insert(headCloseIndex, importedHtml);
        }
        catch
        {
            return original;
        }
    }
}
