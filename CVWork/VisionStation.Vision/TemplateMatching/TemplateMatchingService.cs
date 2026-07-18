namespace VisionStation.Vision;

public sealed class TemplateMatchingService : ITemplateMatchingService
{
    private readonly object _lifecycleGate = new();
    private readonly IReadOnlyDictionary<TemplateMatchingEngine, ITemplateMatchingBackend> _backends;
    private readonly IReadOnlyList<ITemplateMatchingBackend> _backendsInDisposalOrder;
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _disposeStarted;

    internal TemplateMatchingService(IEnumerable<ITemplateMatchingBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        var registry = new Dictionary<TemplateMatchingEngine, ITemplateMatchingBackend>();
        var registeredBackends = new List<ITemplateMatchingBackend>();
        foreach (var backend in backends)
        {
            if (backend is null)
            {
                throw new ArgumentException("Template matching backend registry cannot contain null.", nameof(backends));
            }

            if (backend.Engine is not TemplateMatchingEngine.ManagedNcc and
                not TemplateMatchingEngine.OpenCv and
                not TemplateMatchingEngine.Halcon)
            {
                throw new ArgumentException("Unknown cannot be registered as a template matching backend.", nameof(backends));
            }

            if (!registry.TryAdd(backend.Engine, backend))
            {
                throw new ArgumentException(
                    $"Template matching backend '{backend.Engine}' is registered more than once.",
                    nameof(backends));
            }

            registeredBackends.Add(backend);
        }

        _backends = registry;
        // The HALCON backend owns native handles, its cache and dedicated workers. Stable
        // OrderBy moves it first while preserving registration order for all other backends.
        _backendsInDisposalOrder = registeredBackends
            .OrderBy(backend => backend.Engine == TemplateMatchingEngine.Halcon ? 0 : 1)
            .ToArray();
    }

    public static TemplateMatchingService CreateLegacyOnly()
    {
        return new TemplateMatchingService(
        [
            new OpenCvTemplateMatchingBackend(),
            new ManagedNccTemplateMatchingBackend()
        ]);
    }

    internal static TemplateMatchingService ForTests(params ITemplateMatchingBackend[] backends)
    {
        return new TemplateMatchingService(backends);
    }

    public async Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = EnterOperation();
        cancellationToken.ThrowIfCancellationRequested();
        var engine = TemplateMatchingEngine.Unknown;
        try
        {
            engine = TemplateMatchingEngineResolver.Resolve(request.Parameters);
            if (!_backends.TryGetValue(engine, out var backend))
            {
                return CreateLearningFailure(engine, CreateServiceRequired(engine));
            }

            var result = await backend.LearnAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                return new TemplateLearningResult(
                    engine,
                    false,
                    new Dictionary<string, string>(),
                    result.Message,
                    result.Diagnostic);
            }

            return result with { Engine = engine };
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return CreateLearningFailure(engine, ToDiagnostic(exception));
        }
    }

    public async Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = EnterOperation();
        cancellationToken.ThrowIfCancellationRequested();
        var engine = TemplateMatchingEngine.Unknown;
        try
        {
            engine = TemplateMatchingEngineResolver.Resolve(request.Parameters);
            if (!_backends.TryGetValue(engine, out var backend))
            {
                return CreateMatchFailure(request, engine, CreateServiceRequired(engine));
            }

            var result = await backend.MatchAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { Engine = engine };
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return CreateMatchFailure(request, engine, ToDiagnostic(exception));
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool disposeOwner;
        lock (_lifecycleGate)
        {
            disposeOwner = !_disposeStarted;
            if (disposeOwner)
            {
                _disposeStarted = true;
                if (_activeOperations == 0)
                {
                    _operationsDrained.TrySetResult();
                }
            }
        }

        if (!disposeOwner)
        {
            await _disposeCompleted.Task.ConfigureAwait(false);
            return;
        }

        await _operationsDrained.Task.ConfigureAwait(false);
        var failures = new List<Exception>();
        foreach (var backend in _backendsInDisposalOrder)
        {
            try
            {
                await backend.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 0)
        {
            _disposeCompleted.TrySetResult();
        }
        else
        {
            var failure = failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "Multiple template matching backends failed during shutdown.",
                    failures);
            _disposeCompleted.TrySetException(failure);
        }

        await _disposeCompleted.Task.ConfigureAwait(false);
    }

    private OperationLease EnterOperation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        lock (_lifecycleGate)
        {
            _activeOperations--;
            if (_disposeStarted && _activeOperations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private static TemplateLearningResult CreateLearningFailure(
        TemplateMatchingEngine engine,
        TemplateMatchingDiagnostic diagnostic)
    {
        return new TemplateLearningResult(
            engine,
            false,
            new Dictionary<string, string>(),
            diagnostic.UserMessage,
            diagnostic);
    }

    private static TemplateMatchBatchResult CreateMatchFailure(
        TemplateMatchingRequest request,
        TemplateMatchingEngine engine,
        TemplateMatchingDiagnostic diagnostic)
    {
        return new TemplateMatchBatchResult(
            engine,
            Domain.InspectionOutcome.Ng,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            TemplateMatcher.GetSearchRegion(request.Frame, request.SearchRoi),
            diagnostic.UserMessage,
            false,
            diagnostic);
    }

    private static TemplateMatchingDiagnostic CreateServiceRequired(TemplateMatchingEngine engine)
    {
        return TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.ConfigServiceRequired,
            $"No template matching backend is registered for '{engine}'.");
    }

    private static TemplateMatchingDiagnostic ToDiagnostic(TemplateMatchingConfigurationException exception)
    {
        return new TemplateMatchingDiagnostic(
            exception.Code,
            exception.Message,
            exception.FailureStage,
            exception.TechnicalDetails);
    }

    private sealed class OperationLease : IDisposable
    {
        private TemplateMatchingService? _owner;

        public OperationLease(TemplateMatchingService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitOperation();
        }
    }
}
