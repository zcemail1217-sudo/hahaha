using System.Runtime.ExceptionServices;
using VisionStation.Domain;

namespace VisionStation.Vision;

/// <summary>
/// Receives HALCON matching diagnostics without coupling the public factory to an application
/// logging framework.
/// </summary>
public interface ITemplateMatchingDiagnosticSink
{
    void Warning(string source, string message);

    void Error(string source, string message);
}

internal sealed class NullTemplateMatchingDiagnosticSink : ITemplateMatchingDiagnosticSink
{
    internal static NullTemplateMatchingDiagnosticSink Instance { get; } = new();

    private NullTemplateMatchingDiagnosticSink()
    {
    }

    public void Warning(string source, string message)
    {
    }

    public void Error(string source, string message)
    {
    }
}

/// <summary>
/// Provides the neutral services created by the HALCON template-matching composition root.
/// Native models, caches and worker threads remain owned by <see cref="Service"/> and are not
/// exposed to application code.
/// </summary>
public sealed class TemplateMatchingRuntime
{
    internal TemplateMatchingRuntime(
        ITemplateMatchingService service,
        ITemplateModelResourceManager resources)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public ITemplateMatchingService Service { get; }

    public ITemplateModelResourceManager Resources { get; }
}

/// <summary>
/// Creates the complete template-matching runtime without exposing HALCON-native types.
/// </summary>
public static class HalconTemplateMatchingFactory
{
    public static TemplateMatchingRuntime Create(
        ITemplateModelStore store,
        HalconRuntimeConfiguration configuration,
        ITemplateMatchingDiagnosticSink diagnostics)
    {
        return Create(
            store,
            configuration,
            diagnostics,
            NullHalconFindScaledShapeObserver.Instance);
    }

    internal static TemplateMatchingRuntime Create(
        ITemplateModelStore store,
        HalconRuntimeConfiguration configuration,
        ITemplateMatchingDiagnosticSink diagnostics,
        IHalconFindScaledShapeObserver findObserver)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(findObserver);

        var scheduler = new HalconOperationScheduler();
        HalconTemplateModelCache? cache = null;
        try
        {
            var operators = new HalconDotNetOperatorBackend();
            var runtimeProbe = new HalconRuntimeProbe(
                configuration,
                new HalconRuntimeLocator(),
                new HalconNativeLibraryBootstrapper(),
                new HalconRuntimeNativeApi(operators),
                scheduler);
            var learner = new HalconTemplateLearner(
                runtimeProbe,
                new HalconTemplateFeatureExtractor(),
                store,
                scheduler,
                operators);
            var loader = new HalconModelLoader(scheduler, operators);
            cache = new HalconTemplateModelCache(loader);
            var backend = new HalconTemplateMatchingBackend(
                learner,
                store,
                runtimeProbe,
                cache,
                scheduler,
                new HalconScaledShapeCandidateSource(operators, findObserver),
                new TemplateCandidateEvidenceBuilder(),
                new TemplateCandidateValidator(),
                diagnostics);
            var service = new TemplateMatchingService(
            [
                backend,
                new OpenCvTemplateMatchingBackend(),
                new ManagedNccTemplateMatchingBackend()
            ]);
            var resources = new TemplateModelResourceManager(store, cache, diagnostics);
            return new TemplateMatchingRuntime(service, resources);
        }
        catch (Exception primaryFailure)
        {
            Exception? cleanupFailure = TryDisposeFailedComposition(cache, scheduler);
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "HALCON composition construction failed and cleanup also failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new InvalidOperationException("Unreachable HALCON composition failure path.");
        }
    }

    private static Exception? TryDisposeFailedComposition(
        HalconTemplateModelCache? cache,
        IHalconOperationScheduler scheduler)
    {
        var failures = new List<Exception>();
        if (cache is not null)
        {
            try
            {
                cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "HALCON cache and scheduler both failed while rolling back composition.",
                failures)
        };
    }
}
