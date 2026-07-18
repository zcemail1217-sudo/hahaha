namespace VisionStation.Vision;

/// <summary>
/// Identifies one immutable HALCON model generation.
/// </summary>
internal sealed record HalconTemplateModelCacheKey
{
    public HalconTemplateModelCacheKey(
        string absoluteModelPath,
        string modelSha256,
        string metadataSha256)
    {
        AbsoluteModelPath = NormalizeAbsolutePath(absoluteModelPath, nameof(absoluteModelPath));
        ModelSha256 = NormalizeSha256(modelSha256, nameof(modelSha256));
        MetadataSha256 = NormalizeSha256(metadataSha256, nameof(metadataSha256));
    }

    public string AbsoluteModelPath { get; }

    public string ModelSha256 { get; }

    public string MetadataSha256 { get; }

    internal static string NormalizeAbsolutePath(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The HALCON model path must be fully qualified.", parameterName);
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The HALCON model path is invalid.", parameterName, exception);
        }
    }

    private static string NormalizeSha256(string? value, string parameterName)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 checksum must contain exactly 64 hexadecimal characters.", parameterName);
        }

        return normalized.ToUpperInvariant();
    }
}

internal interface IHalconOperationGate : IDisposable
{
    Task WaitAsync(CancellationToken cancellationToken);

    void Release();
}

internal sealed class SemaphoreHalconOperationGate : IHalconOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return _semaphore.WaitAsync(cancellationToken);
    }

    public void Release()
    {
        _semaphore.Release();
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}

/// <summary>
/// Shares immutable model generations, serializes operations per model and owns exact disposal.
/// </summary>
internal sealed class HalconTemplateModelCache : ITemplateModelRetirementSink, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly IHalconModelLoader _loader;
    private readonly Func<IHalconOperationGate> _operationGateFactory;
    private readonly Dictionary<HalconTemplateModelCacheKey, Entry> _entries =
        new(HalconTemplateModelCacheKeyComparer.Instance);
    private readonly HashSet<Entry> _liveEntries = [];
    private readonly Dictionary<TemplateModelOwner, Entry> _activeEntries = [];
    private readonly Dictionary<TemplateModelOwner, OwnerState> _ownerStates = [];
    private readonly List<Exception> _disposalFailures = [];
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposeStarted;
    private int _liveEntryCount;

    internal HalconTemplateModelCache(IHalconModelLoader loader)
        : this(loader, static () => new SemaphoreHalconOperationGate())
    {
    }

    internal HalconTemplateModelCache(
        IHalconModelLoader loader,
        Func<IHalconOperationGate> operationGateFactory)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _operationGateFactory = operationGateFactory
            ?? throw new ArgumentNullException(nameof(operationGateFactory));
    }

    /// <summary>
    /// Acquires one owner-scoped lease. A shared load is never canceled by one caller's token.
    /// </summary>
    public Task<HalconTemplateModelLease> AcquireAsync(
        TemplateModelOwner owner,
        ValidatedHalconModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TemplateModelOwner ownerSnapshot = SnapshotOwner(owner);
        ValidateDescriptorOwner(ownerSnapshot, descriptor);
        HalconTemplateModelCacheKey key = descriptor.CacheKey;
        Entry entry;
        var startLoad = false;
        long ownerRetirementEpoch;
        long ownerAcquireSequence;

        lock (_syncRoot)
        {
            ThrowIfDisposing();
            OwnerState ownerState = GetOrCreateOwnerStateLocked(ownerSnapshot);
            if (IsGenerationRetiring(ownerState, key))
            {
                throw CreateRetiredGenerationException(ownerSnapshot, key);
            }

            ownerRetirementEpoch = ownerState.RetirementEpoch;
            ownerAcquireSequence = unchecked(
                ownerState.NextAcquireSequence + 1);
            ownerState.NextAcquireSequence = ownerAcquireSequence;
            if (!_entries.TryGetValue(key, out entry!))
            {
                IHalconOperationGate operationGate = _operationGateFactory()
                    ?? throw new InvalidOperationException("The HALCON operation gate factory returned null.");
                entry = new Entry(key, descriptor, operationGate);
                _entries.Add(entry.Key, entry);
                _liveEntries.Add(entry);
                _liveEntryCount++;
                startLoad = true;
            }

            entry.AssociatedOwners.Add(ownerSnapshot);
            entry.WaiterCount++;
        }

        if (startLoad)
        {
            _ = LoadEntryAsync(entry);
        }

        return AwaitEntryAsync(
            entry,
            ownerSnapshot,
            ownerRetirementEpoch,
            ownerAcquireSequence,
            cancellationToken);
    }

    /// <summary>
    /// Retires every live entry associated with one owner while preserving other active owners.
    /// The fence waits for loads already reading model files, but not for active leases or native
    /// operations. Resource-disposal failures are reported by the lease that triggers cleanup and
    /// again by cache shutdown; they do not make retirement success depend on lease timing.
    /// </summary>
    public ValueTask RetireAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TemplateModelOwner ownerSnapshot = SnapshotOwner(owner);
        List<Entry>? disposals = null;
        List<Task>? loadsToDrain = null;

        lock (_syncRoot)
        {
            OwnerState ownerState = GetOrCreateOwnerStateLocked(ownerSnapshot);
            ownerState.RetirementEpoch = unchecked(ownerState.RetirementEpoch + 1);
            if (_activeEntries.Remove(ownerSnapshot, out Entry? activeEntry))
            {
                activeEntry.ActiveOwners.Remove(ownerSnapshot);
            }

            foreach (Entry entry in _liveEntries.ToArray())
            {
                if (!entry.AssociatedOwners.Contains(ownerSnapshot))
                {
                    continue;
                }

                if (entry.RetiredOwners.Add(ownerSnapshot))
                {
                    ownerState.RetiringEntries.Add(entry);
                }

                entry.ActiveOwners.Remove(ownerSnapshot);
                if (!entry.LoadCompleted)
                {
                    loadsToDrain ??= [];
                    loadsToDrain.Add(entry.LoadCompletion.Task);
                }

                AddDisposal(ref disposals, TryBeginEntryDisposalLocked(entry));
            }

            TryRemoveOwnerStateLocked(ownerSnapshot);
        }

        _ = RunEntryDisposals(disposals);
        return loadsToDrain is null
            ? ValueTask.CompletedTask
            : new ValueTask(WaitForRetiredLoadsAsync(loadsToDrain));
    }

    public ValueTask DisposeAsync()
    {
        List<Entry>? disposals = null;
        Task completion;
        lock (_syncRoot)
        {
            if (!_disposeStarted)
            {
                _disposeStarted = true;
                _activeEntries.Clear();
                foreach (Entry entry in _liveEntries)
                {
                    entry.ActiveOwners.Clear();
                }

                foreach (Entry entry in _liveEntries.ToArray())
                {
                    AddDisposal(ref disposals, TryBeginEntryDisposalLocked(entry));
                }

                TryCompleteDisposeLocked();
            }

            completion = _disposeCompletion.Task;
        }

        _ = RunEntryDisposals(disposals);
        return new ValueTask(completion);
    }

    internal Task<HalconTemplateModelOperationLease> EnterOperationAsync(
        HalconTemplateModelLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (lease.IsReleased)
            {
                throw new ObjectDisposedException(nameof(HalconTemplateModelLease));
            }

            ThrowIfDisposing();
            lease.Entry.OperationReferenceCount++;
        }

        return WaitForOperationGateAsync(lease.Entry, cancellationToken);
    }

    internal ValueTask ReleaseLeaseAsync(HalconTemplateModelLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Entry? disposal;
        lock (_syncRoot)
        {
            if (lease.IsReleased)
            {
                return ValueTask.CompletedTask;
            }

            lease.IsReleased = true;
            lease.Entry.LeaseCount--;
            disposal = TryBeginEntryDisposalLocked(lease.Entry);
        }

        Exception? disposalFailure = RunEntryDisposal(disposal);
        return disposalFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(disposalFailure);
    }

    internal ValueTask ReleaseOperationAsync(HalconTemplateModelOperationLease operationLease)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        lock (_syncRoot)
        {
            if (operationLease.IsReleased)
            {
                return ValueTask.CompletedTask;
            }

            operationLease.IsReleased = true;
        }

        Exception? releaseFailure = null;
        Exception? disposalFailure = null;
        try
        {
            operationLease.Entry.OperationGate.Release();
        }
        catch (Exception exception)
        {
            releaseFailure = exception;
        }
        finally
        {
            Entry? disposal;
            lock (_syncRoot)
            {
                operationLease.Entry.OperationReferenceCount--;
                disposal = TryBeginEntryDisposalLocked(operationLease.Entry);
            }

            disposalFailure = RunEntryDisposal(disposal);
        }

        Exception? operationFailure = CombineFailures(releaseFailure, disposalFailure);
        return operationFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(operationFailure);
    }

    private async Task LoadEntryAsync(Entry entry)
    {
        IHalconModelHandle? handle = null;
        Exception? loadFailure = null;
        try
        {
            handle = await _loader
                .LoadAsync(entry.Descriptor, CancellationToken.None)
                .ConfigureAwait(false);
            if (handle is null)
            {
                throw new InvalidOperationException("The HALCON model loader returned a null handle.");
            }
        }
        catch (Exception exception)
        {
            loadFailure = exception;
        }

        Entry? disposal;
        lock (_syncRoot)
        {
            entry.LoadCompleted = true;
            entry.Handle = handle;
            if (loadFailure is not null &&
                _entries.TryGetValue(entry.Key, out Entry? current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.Key);
            }

            disposal = TryBeginEntryDisposalLocked(entry);
        }

        if (loadFailure is null)
        {
            entry.LoadCompletion.TrySetResult(handle!);
        }
        else
        {
            entry.LoadCompletion.TrySetException(loadFailure);
        }

        _ = RunEntryDisposal(disposal);
    }

    private async Task<HalconTemplateModelLease> AwaitEntryAsync(
        Entry entry,
        TemplateModelOwner owner,
        long ownerRetirementEpoch,
        long ownerAcquireSequence,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                await entry.LoadCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await entry.LoadCompletion.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            EndWaiter(entry);
            throw;
        }

        List<Entry>? disposals = null;
        HalconTemplateModelLease? lease = null;
        var cacheIsDisposing = false;
        lock (_syncRoot)
        {
            entry.WaiterCount--;
            if (_disposeStarted)
            {
                cacheIsDisposing = true;
                AddDisposal(ref disposals, TryBeginEntryDisposalLocked(entry));
            }
            else
            {
                OwnerState ownerState = GetOrCreateOwnerStateLocked(owner);
                bool ownerCanActivate =
                    ownerState.RetirementEpoch == ownerRetirementEpoch &&
                    !entry.RetiredOwners.Contains(owner) &&
                    ownerAcquireSequence > ownerState.LatestActivatedSequence;
                if (ownerCanActivate &&
                    _activeEntries.TryGetValue(owner, out Entry? previousEntry) &&
                    !ReferenceEquals(previousEntry, entry))
                {
                    previousEntry.ActiveOwners.Remove(owner);
                    AddDisposal(ref disposals, TryBeginEntryDisposalLocked(previousEntry));
                }

                if (ownerCanActivate)
                {
                    ownerState.LatestActivatedSequence = ownerAcquireSequence;
                    entry.ActiveOwners.Add(owner);
                    _activeEntries[owner] = entry;
                }

                entry.LeaseCount++;
                lease = new HalconTemplateModelLease(this, entry, owner);
            }
        }

        _ = RunEntryDisposals(disposals);
        if (cacheIsDisposing)
        {
            throw new ObjectDisposedException(nameof(HalconTemplateModelCache));
        }

        return lease!;
    }

    private async Task<HalconTemplateModelOperationLease> WaitForOperationGateAsync(
        Entry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            await entry.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HalconTemplateModelOperationLease(this, entry, entry.Handle!);
        }
        catch
        {
            Entry? disposal;
            lock (_syncRoot)
            {
                entry.OperationReferenceCount--;
                disposal = TryBeginEntryDisposalLocked(entry);
            }

            _ = RunEntryDisposal(disposal);
            throw;
        }
    }

    private void EndWaiter(Entry entry)
    {
        Entry? disposal;
        lock (_syncRoot)
        {
            entry.WaiterCount--;
            disposal = TryBeginEntryDisposalLocked(entry);
        }

        _ = RunEntryDisposal(disposal);
    }

    private Entry? TryBeginEntryDisposalLocked(Entry entry)
    {
        if (entry.DisposalStarted ||
            !entry.LoadCompleted ||
            entry.WaiterCount != 0 ||
            entry.LeaseCount != 0 ||
            entry.OperationReferenceCount != 0 ||
            entry.ActiveOwners.Count != 0)
        {
            return null;
        }

        entry.DisposalStarted = true;
        if (_entries.TryGetValue(entry.Key, out Entry? current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(entry.Key);
        }

        _liveEntries.Remove(entry);
        foreach (TemplateModelOwner owner in entry.AssociatedOwners)
        {
            if (_activeEntries.TryGetValue(owner, out Entry? activeEntry) &&
                ReferenceEquals(activeEntry, entry))
            {
                _activeEntries.Remove(owner);
            }
        }

        return entry;
    }

    private Exception? RunEntryDisposals(IReadOnlyList<Entry>? entries)
    {
        if (entries is null)
        {
            return null;
        }

        List<Exception>? failures = null;
        foreach (Entry entry in entries)
        {
            Exception? failure = RunEntryDisposal(entry);
            if (failure is not null)
            {
                failures ??= [];
                failures.Add(failure);
            }
        }

        return CreateFailure(failures);
    }

    private Exception? RunEntryDisposal(Entry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var failures = new List<Exception>();
        try
        {
            entry.Handle?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            entry.OperationGate.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        Exception? disposalFailure = CreateFailure(failures);
        lock (_syncRoot)
        {
            _liveEntryCount--;
            if (disposalFailure is not null)
            {
                _disposalFailures.Add(disposalFailure);
            }

            foreach (TemplateModelOwner owner in entry.RetiredOwners)
            {
                if (_ownerStates.TryGetValue(owner, out OwnerState? ownerState))
                {
                    ownerState.RetiringEntries.Remove(entry);
                }
            }

            foreach (TemplateModelOwner owner in entry.AssociatedOwners)
            {
                TryRemoveOwnerStateLocked(owner);
            }

            TryCompleteDisposeLocked();
        }

        return disposalFailure;
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null
            ? first
            : new AggregateException(first, second);
    }

    private static Exception? CreateFailure(IReadOnlyList<Exception>? failures)
    {
        return failures?.Count switch
        {
            null or 0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }

    private void TryCompleteDisposeLocked()
    {
        if (!_disposeStarted || _liveEntryCount != 0 || _disposeCompletion.Task.IsCompleted)
        {
            return;
        }

        if (_disposalFailures.Count == 0)
        {
            _disposeCompletion.TrySetResult();
        }
        else
        {
            _disposeCompletion.TrySetException(
                new AggregateException("One or more HALCON model cache entries failed to dispose.", _disposalFailures));
        }
    }

    private static async Task WaitForRetiredLoadsAsync(IReadOnlyList<Task> loads)
    {
        try
        {
            await Task.WhenAll(loads).ConfigureAwait(false);
        }
        catch
        {
            // Retirement needs every pre-fence file read to settle, not a successful model load.
            // The original waiter observes its exact load failure.
        }
    }

    private void ThrowIfDisposing()
    {
        if (_disposeStarted)
        {
            throw new ObjectDisposedException(nameof(HalconTemplateModelCache));
        }
    }

    private OwnerState GetOrCreateOwnerStateLocked(TemplateModelOwner owner)
    {
        if (!_ownerStates.TryGetValue(owner, out OwnerState? state))
        {
            state = new OwnerState();
            _ownerStates.Add(owner, state);
        }

        return state;
    }

    private static bool IsGenerationRetiring(
        OwnerState ownerState,
        HalconTemplateModelCacheKey key)
    {
        return ownerState.RetiringEntries.Any(
            entry => HalconTemplateModelCacheKeyComparer.Instance.Equals(entry.Key, key));
    }

    private void TryRemoveOwnerStateLocked(TemplateModelOwner owner)
    {
        if (!_ownerStates.TryGetValue(owner, out OwnerState? state) ||
            state.RetiringEntries.Count != 0 ||
            _activeEntries.ContainsKey(owner) ||
            _liveEntries.Any(entry => entry.AssociatedOwners.Contains(owner)))
        {
            return;
        }

        _ownerStates.Remove(owner);
    }

    private static ObjectDisposedException CreateRetiredGenerationException(
        TemplateModelOwner owner,
        HalconTemplateModelCacheKey key)
    {
        return new ObjectDisposedException(
            nameof(HalconTemplateModelCacheKey),
            $"The HALCON model generation '{key.AbsoluteModelPath}' was retired for " +
            $"'{owner.RecipeId}/{owner.FlowId}/{owner.ToolId}' and cannot be acquired again.");
    }

    private static TemplateModelOwner SnapshotOwner(TemplateModelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.RecipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.FlowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.ToolId);
        return new TemplateModelOwner(owner.RecipeId, owner.FlowId, owner.ToolId);
    }

    private static void ValidateDescriptorOwner(
        TemplateModelOwner owner,
        ValidatedHalconModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(owner.RecipeId, descriptor.Owner.RecipeId, StringComparison.Ordinal) ||
            !string.Equals(owner.FlowId, descriptor.Owner.FlowId, StringComparison.Ordinal) ||
            !string.Equals(owner.ToolId, descriptor.Owner.ToolId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The validated HALCON descriptor owner does not match the requested cache owner.",
                nameof(descriptor));
        }
    }

    private static void AddDisposal(ref List<Entry>? entries, Entry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entries ??= [];
        entries.Add(entry);
    }

    internal sealed class Entry
    {
        internal Entry(
            HalconTemplateModelCacheKey key,
            ValidatedHalconModelDescriptor descriptor,
            IHalconOperationGate operationGate)
        {
            Key = key;
            Descriptor = descriptor;
            OperationGate = operationGate;
        }

        internal HalconTemplateModelCacheKey Key { get; }

        internal ValidatedHalconModelDescriptor Descriptor { get; }

        internal IHalconOperationGate OperationGate { get; }

        internal TaskCompletionSource<IHalconModelHandle> LoadCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HashSet<TemplateModelOwner> ActiveOwners { get; } = [];

        internal HashSet<TemplateModelOwner> AssociatedOwners { get; } = [];

        internal HashSet<TemplateModelOwner> RetiredOwners { get; } = [];

        internal IHalconModelHandle? Handle { get; set; }

        internal int WaiterCount { get; set; }

        internal int LeaseCount { get; set; }

        internal int OperationReferenceCount { get; set; }

        internal bool LoadCompleted { get; set; }

        internal bool DisposalStarted { get; set; }
    }

    private sealed class OwnerState
    {
        internal HashSet<Entry> RetiringEntries { get; } = [];

        internal long RetirementEpoch { get; set; }

        internal long NextAcquireSequence { get; set; }

        internal long LatestActivatedSequence { get; set; }
    }

    private sealed class HalconTemplateModelCacheKeyComparer :
        IEqualityComparer<HalconTemplateModelCacheKey>
    {
        internal static HalconTemplateModelCacheKeyComparer Instance { get; } = new();

        public bool Equals(HalconTemplateModelCacheKey? left, HalconTemplateModelCacheKey? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return left is not null &&
                   right is not null &&
                   string.Equals(left.AbsoluteModelPath, right.AbsoluteModelPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ModelSha256, right.ModelSha256, StringComparison.Ordinal) &&
                   string.Equals(left.MetadataSha256, right.MetadataSha256, StringComparison.Ordinal);
        }

        public int GetHashCode(HalconTemplateModelCacheKey key)
        {
            ArgumentNullException.ThrowIfNull(key);
            var hash = new HashCode();
            hash.Add(key.AbsoluteModelPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.ModelSha256, StringComparer.Ordinal);
            hash.Add(key.MetadataSha256, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
