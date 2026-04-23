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

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.EnableExperimentalHomeInjection)
        {
            return Task.CompletedTask;
        }

        lock (SyncLock)
        {
            if (_registrationSucceeded)
            {
                return Task.CompletedTask;
            }
        }

        try
        {
            JObject payload = new();
            payload.Add("id", "d316d401-b0e6-4618-95a0-ba897f59547f");
            payload.Add("fileNamePattern", "index.html");
            payload.Add("callbackAssembly", GetType().Assembly.FullName);
            payload.Add("callbackClass", typeof(TransformationPatches).FullName);
            payload.Add("callbackMethod", nameof(TransformationPatches.IndexHtml));

            Assembly? fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            Type? pluginInterfaceType = fileTransformationAssembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            MethodInfo? registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");
            if (registerMethod is null)
            {
                return Task.CompletedTask;
            }

            registerMethod.Invoke(null, [payload]);

            lock (SyncLock)
            {
                _registrationSucceeded = true;
            }
        }
        catch
        {
            // Do not block server startup if transformation registration fails.
            // Keep retrying on future task runs until registration succeeds.
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
#if NET9_0
                Type = TaskTriggerInfoType.StartupTrigger
#else
                Type = TaskTriggerInfo.TriggerStartup
#endif
            }
        ];
    }
}
