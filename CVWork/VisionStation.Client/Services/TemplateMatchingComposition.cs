using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

namespace VisionStation.Client.Services;

/// <summary>
/// Application composition root for the neutral template-matching contracts.
/// </summary>
internal sealed class TemplateMatchingComposition
{
    internal TemplateMatchingComposition(
        RuntimePaths runtimePaths,
        HalconRuntimeConfiguration configuration,
        IAppLogService appLog)
    {
        ArgumentNullException.ThrowIfNull(runtimePaths);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(appLog);

        Store = new FileTemplateModelStore(runtimePaths);
        TemplateMatchingRuntime runtime = HalconTemplateMatchingFactory.Create(
            Store,
            configuration,
            new AppLogDiagnosticSink(appLog));
        Service = runtime.Service;
        Resources = runtime.Resources;
    }

    internal ITemplateMatchingService Service { get; }

    internal ITemplateModelStore Store { get; }

    internal ITemplateModelResourceManager Resources { get; }

    private sealed class AppLogDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        private readonly IAppLogService _appLog;

        public AppLogDiagnosticSink(IAppLogService appLog)
        {
            _appLog = appLog;
        }

        public void Warning(string source, string message)
        {
            _appLog.Warning(source, message);
        }

        public void Error(string source, string message)
        {
            _appLog.Error(source, message);
        }
    }
}
