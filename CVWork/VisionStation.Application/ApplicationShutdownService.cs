using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application;

public sealed class ApplicationShutdownService
{
    private readonly object _syncRoot = new();
    private readonly IInspectionRunLifetime _inspectionRunLifetime;
    private readonly Func<Task>? _stopProduction;
    private readonly IAsyncDisposable _templateMatchingService;
    private readonly IDisposable _communicationRuntime;
    private Task? _shutdownTask;

    public ApplicationShutdownService(
        ProductionCoordinator productionCoordinator,
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime communicationRuntime)
        : this(
            GetRunLifetime(productionCoordinator),
            GetStopProduction(productionCoordinator),
            templateMatchingService,
            communicationRuntime)
    {
    }

    public ApplicationShutdownService(
        ProductionCoordinator productionCoordinator,
        IInspectionRunLifetime inspectionRunLifetime,
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime communicationRuntime)
        : this(
            GetSharedRunLifetime(productionCoordinator, inspectionRunLifetime),
            GetStopProduction(productionCoordinator),
            templateMatchingService,
            communicationRuntime)
    {
    }

    internal ApplicationShutdownService(
        IInspectionRunLifetime inspectionRunLifetime,
        Func<Task>? stopProduction,
        IAsyncDisposable templateMatchingService,
        IDisposable communicationRuntime)
    {
        _inspectionRunLifetime = inspectionRunLifetime ??
                                 throw new ArgumentNullException(nameof(inspectionRunLifetime));
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
            new InspectionRunLifetime(),
            (Func<Task>?)null,
            templateMatchingService,
            communicationRuntime);
    }

    public static ApplicationShutdownService WithoutProduction(
        IInspectionRunLifetime inspectionRunLifetime,
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime communicationRuntime)
    {
        return new ApplicationShutdownService(
            inspectionRunLifetime,
            (Func<Task>?)null,
            templateMatchingService,
            communicationRuntime);
    }

    public Task ShutdownAsync()
    {
        lock (_syncRoot)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            _ = CompleteShutdownAsync(completion);
            return _shutdownTask;
        }
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            await ShutdownCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var failures = new List<Exception>();
        try
        {
            _inspectionRunLifetime.BeginShutdown();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (_stopProduction is not null)
        {
            await TryRunAsync(_stopProduction, failures).ConfigureAwait(false);
        }

        try
        {
            await _inspectionRunLifetime.DrainAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            throw new AggregateException("Application shutdown stopped because inspection runs did not drain.", failures);
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
        return productionCoordinator.StopForShutdownAsync;
    }

    private static IInspectionRunLifetime GetRunLifetime(ProductionCoordinator productionCoordinator)
    {
        ArgumentNullException.ThrowIfNull(productionCoordinator);
        return productionCoordinator.RunLifetime;
    }

    private static IInspectionRunLifetime GetSharedRunLifetime(
        ProductionCoordinator productionCoordinator,
        IInspectionRunLifetime inspectionRunLifetime)
    {
        ArgumentNullException.ThrowIfNull(inspectionRunLifetime);
        var coordinatorLifetime = GetRunLifetime(productionCoordinator);
        if (!ReferenceEquals(coordinatorLifetime, inspectionRunLifetime))
        {
            throw new ArgumentException(
                "Application shutdown and production must share the same inspection run lifetime.",
                nameof(inspectionRunLifetime));
        }

        return inspectionRunLifetime;
    }
}
