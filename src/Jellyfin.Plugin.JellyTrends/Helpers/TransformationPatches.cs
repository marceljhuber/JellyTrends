using System.Reflection;
using Jellyfin.Plugin.JellyTrends.Model;

namespace Jellyfin.Plugin.JellyTrends.Helpers;

public static class TransformationPatches
{
    public static string IndexHtml(PatchRequestPayload payload)
    {
        string original = payload.Contents ?? string.Empty;

        if (!Plugin.Instance.Configuration.Enabled || !Plugin.Instance.Configuration.EnableExperimentalHomeInjection)
        {
            return original;
        }

        try
        {
            if (original.Contains("/JellyTrends/assets/jellytrends.js", StringComparison.OrdinalIgnoreCase))
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
                return original + importedHtml;
            }

            return original.Insert(headCloseIndex, importedHtml);
        }
        catch
        {
            return original;
        }
    }
}
