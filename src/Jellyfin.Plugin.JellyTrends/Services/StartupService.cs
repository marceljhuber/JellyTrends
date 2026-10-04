using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Jellyfin.Plugin.JellyTrends.Helpers;
using MediaBrowser.Model.Tasks;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyTrends.Services;

public sealed class StartupService : IScheduledTask
{
    private static readonly object SyncLock = new();
    private static bool _registrationSucceeded;

    public string Name => "JellyTrends Startup";

    public string Key => "Jellyfin.Plugin.JellyTrends.Startup";

    public string Description => "Registers JellyTrends file transformations";

    public string Category => "Startup Services";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Registered regardless of EnableHomeRows: the transformation itself checks the setting
        // on every request, so toggling it takes effect without a restart.
        lock (SyncLock)
        {
            if (_registrationSucceeded)
            {
                return;
            }
        }

        // File Transformation may finish loading after this plugin, so poll for it rather than
        // trying once and giving up until the next restart.
        for (int attempt = 0; attempt < 24 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (TryRegister())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRegister()
    {
        try
        {
            Assembly? fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            Type? pluginInterfaceType = fileTransformationAssembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            MethodInfo? registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");
            if (registerMethod is null)
            {
                return false;
            }

            JObject payload = new()
            {
                { "id", "d316d401-b0e6-4618-95a0-ba897f59547f" },
                { "fileNamePattern", "index.html" },
                { "callbackAssembly", GetType().Assembly.FullName },
                { "callbackClass", typeof(TransformationPatches).FullName },
                { "callbackMethod", nameof(TransformationPatches.IndexHtml) }
            };

            registerMethod.Invoke(null, [payload]);

            lock (SyncLock)
            {
                _registrationSucceeded = true;
            }

            return true;
        }
        catch
        {
            // Never block server startup; the next poll tries again.
            return false;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
#if NET9_0_OR_GREATER
                Type = TaskTriggerInfoType.StartupTrigger
#else
                Type = TaskTriggerInfo.TriggerStartup
#endif
            }
        ];
    }
}
