using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application;

public sealed class ApplicationShutdownService
{
    private readonly object _syncRoot = new();
    private readonly Func<Task>? _stopProduction;
    private readonly IAsyncDisposable _templateMatchingService;
    private readonly IDisposable _communicationRuntime;
    private Task? _shutdownTask;

    public ApplicationShutdownService(
        ProductionCoordinator productionCoordinator,
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime communicationRuntime)
        : this(
            GetStopProduction(productionCoordinator),
            templateMatchingService,
            communicationRuntime)
    {
    }

    internal ApplicationShutdownService(
        Func<Task>? stopProduction,
        IAsyncDisposable templateMatchingService,
        IDisposable communicationRuntime)
    {
        _stopProduction = stopProduction;
        _templateMatchingService = templateMatchingService ??
                                   throw new ArgumentNullException(nameof(templateMatchingService));
        _communicationRuntime = communicationRuntime ??
                                throw new ArgumentNullException(nameof(communicationRuntime));
    }

    public static ApplicationShutdownService WithoutProduction(
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime communicationRuntime)
    {
        return new ApplicationShutdownService(
            (Func<Task>?)null,
            templateMatchingService,
            communicationRuntime);
    }

    public Task ShutdownAsync()
    {
        lock (_syncRoot)
        {
            _shutdownTask ??= ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var failures = new List<Exception>();
        if (_stopProduction is not null)
        {
            await TryRunAsync(_stopProduction, failures).ConfigureAwait(false);
        }

        await TryRunAsync(
                () => _templateMatchingService.DisposeAsync().AsTask(),
                failures)
            .ConfigureAwait(false);

        try
        {
            _communicationRuntime.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Application shutdown completed with failures.", failures);
        }
    }

    private static async Task TryRunAsync(
        Func<Task> action,
        ICollection<Exception> failures)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static Func<Task> GetStopProduction(ProductionCoordinator productionCoordinator)
    {
        ArgumentNullException.ThrowIfNull(productionCoordinator);
        return productionCoordinator.StopAsync;
    }
}
