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

            // File Transformation matches "index.html" as a regex, so it also hands this
            // callback JS chunks such as "session-login-index-html.<hash>.chunk.js". Appending
            // markup to those breaks the whole web client, so only a real HTML document with a
            // head is ever modified.
            string head = original.TrimStart();
            bool isDocument = head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
            int headCloseIndex = original.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (!isDocument || headCloseIndex < 0)
            {
                return original;
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
