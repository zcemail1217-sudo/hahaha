using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconTemplateModelCacheTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void CacheKeyRequiresAbsolutePathAndSha256Checksums()
    {
        string checksum = Sha256("valid");

        Assert.Throws<ArgumentException>(
            () => new HalconTemplateModelCacheKey("relative\\model.shm", checksum, checksum));
        Assert.Throws<ArgumentException>(
            () => new HalconTemplateModelCacheKey(ModelPath("invalid-model"), "not-a-sha256", checksum));
        Assert.Throws<ArgumentException>(
            () => new HalconTemplateModelCacheKey(ModelPath("invalid-metadata"), checksum, "not-a-sha256"));

        var key = new HalconTemplateModelCacheKey(
            ModelPath("normalized"),
            checksum.ToLowerInvariant(),
            checksum.ToLowerInvariant());
        Assert.Equal(checksum, key.ModelSha256);
        Assert.Equal(checksum, key.MetadataSha256);
        Assert.True(Path.IsPathFullyQualified(key.AbsoluteModelPath));
    }

    [Fact]
    public async Task AcquireRejectsResolvedPathAndMetadataChecksumMismatchBeforeLoading()
    {
        var loader = new RecordingModelLoader(
            (_, _, _) => Task.FromResult<IHalconModelHandle>(new SentinelModelHandle()));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        ModelInput input = Input("validation");
        var owner = Owner("validation");
        var wrongPath = input.ResolvedModel with { ModelPath = ModelPath("other") };
        var wrongMetadata = input.ResolvedModel with
        {
            MetadataJson = Encoding.UTF8.GetBytes("{\"different\":true}")
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.AcquireAsync(owner, input.DescriptorFor(owner, wrongPath), default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.AcquireAsync(owner, input.DescriptorFor(owner, wrongMetadata), default));

        Assert.Equal(0, loader.CallCount);
    }

    [Fact]
    public async Task AcquireRejectsDescriptorOwnedByDifferentToolBeforeLoading()
    {
        var loader = new RecordingModelLoader(
            (_, _, _) => Task.FromResult<IHalconModelHandle>(new SentinelModelHandle()));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        ModelInput input = Input("wrong-owner");
        TemplateModelOwner requestedOwner = Owner("requested");
        ValidatedHalconModelDescriptor descriptor = input.DescriptorFor(Owner("descriptor"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => cacheScope.Cache.AcquireAsync(requestedOwner, descriptor, default));

        Assert.Equal(0, loader.CallCount);
    }

    [Fact]
    public async Task ConcurrentSameDescriptorLoadsOnceWithoutHotPathResnapshot()
    {
        ModelInput input = Input("shared");
        byte[] expectedMetadata = input.MetadataBytes.ToArray();
        var handle = new SentinelModelHandle();
        var releaseLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader((_, _, _) => releaseLoad.Task);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        var owner = Owner("shared");
        ValidatedHalconModelDescriptor descriptor = input.DescriptorFor(owner);

        Task<HalconTemplateModelLease> firstTask = cache.AcquireAsync(
            owner,
            descriptor,
            default);
        Task<HalconTemplateModelLease> secondTask = cache.AcquireAsync(
            owner,
            descriptor,
            default);
        await loader.WaitForCallsAsync(1);
        input.MetadataBytes[0] ^= 0x1;

        try
        {
            Assert.Equal(1, loader.CallCount);
            Assert.Same(descriptor, loader.Requests.Single().Descriptor);
        }
        finally
        {
            releaseLoad.TrySetResult(handle);
        }
        await using HalconTemplateModelLease first = await firstTask.WaitAsync(TestTimeout);
        await using HalconTemplateModelLease second = await secondTask.WaitAsync(TestTimeout);

        Assert.NotSame(owner, first.Owner);
        Assert.Same(descriptor.CacheKey, first.Key);
        Assert.Same(descriptor, first.Descriptor);
        Assert.Same(descriptor, second.Descriptor);
        Assert.Equal(expectedMetadata, descriptor.MetadataJson.ToArray());
        Assert.Null(
            typeof(HalconTemplateModelLease).GetProperty(
                "ResolvedModel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(
            typeof(HalconTemplateModelLease).GetProperty(
                "Handle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public async Task CanceledWaiterDoesNotCancelSharedLoadOrPoisonOtherCaller()
    {
        ModelInput input = Input("load-cancellation");
        var handle = new SentinelModelHandle();
        var releaseLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader((_, _, _) => releaseLoad.Task);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        using var cancellation = new CancellationTokenSource();

        Task<HalconTemplateModelLease> canceled = cache.AcquireAsync(
            Owner("canceled"),
            input.DescriptorFor(Owner("canceled")),
            cancellation.Token);
        Task<HalconTemplateModelLease> survivor = cache.AcquireAsync(
            Owner("survivor"),
            input.DescriptorFor(Owner("survivor")),
            default);
        await loader.WaitForCallsAsync(1);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TestTimeout));
            Assert.False(loader.Requests.Single().CancellationToken.CanBeCanceled);
        }
        finally
        {
            releaseLoad.TrySetResult(handle);
        }

        await using HalconTemplateModelLease lease = await survivor.WaitAsync(TestTimeout);
        Assert.Equal(1, loader.CallCount);
    }

    [Fact]
    public async Task NewKeyLoadsBeforeReplacingActiveEntryAndThenRetiresOldEntry()
    {
        ModelInput oldInput = Input("old-generation");
        ModelInput newInput = Input("new-generation");
        var oldHandle = new SentinelModelHandle();
        var newHandle = new SentinelModelHandle();
        var releaseNewLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader(
            (key, _, _) =>
            {
                if (key == oldInput.Key)
                {
                    return Task.FromResult<IHalconModelHandle>(oldHandle);
                }

                newLoadStarted.TrySetResult();
                return releaseNewLoad.Task;
            });
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("replace");
        HalconTemplateModelLease oldLease = await cache.AcquireAsync(
            owner,
            oldInput.DescriptorFor(owner),
            default);

        Task<HalconTemplateModelLease> newLeaseTask = cache.AcquireAsync(
            owner,
            newInput.DescriptorFor(owner),
            default);
        await newLoadStarted.Task.WaitAsync(TestTimeout);
        try
        {
            await oldLease.DisposeAsync();
            Assert.Equal(0, oldHandle.DisposeCount);
        }
        finally
        {
            releaseNewLoad.TrySetResult(newHandle);
        }
        await using HalconTemplateModelLease newLease = await newLeaseTask.WaitAsync(TestTimeout);
        Assert.Equal(1, oldHandle.DisposeCount);
        Assert.Equal(newInput.Key, newLease.Key);
    }

    [Fact]
    public async Task OlderAcquireCompletingAfterNewerKeyDoesNotRollBackActiveEntry()
    {
        ModelInput oldInput = Input("out-of-order-old");
        ModelInput newInput = Input("out-of-order-new");
        var oldHandle = new SentinelModelHandle();
        var newHandle = new SentinelModelHandle();
        var releaseOldLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader(
            (key, _, _) => key == oldInput.Key
                ? releaseOldLoad.Task
                : Task.FromResult<IHalconModelHandle>(newHandle));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("out-of-order");

        Task<HalconTemplateModelLease> oldLeaseTask = cache.AcquireAsync(
            owner,
            oldInput.DescriptorFor(owner),
            default);
        await loader.WaitForCallsAsync(1);
        HalconTemplateModelLease newLeaseValue;
        try
        {
            newLeaseValue = await cache.AcquireAsync(
                owner,
                newInput.DescriptorFor(owner),
                default);
        }
        finally
        {
            releaseOldLoad.TrySetResult(oldHandle);
        }

        await using HalconTemplateModelLease newLease = newLeaseValue;
        await using HalconTemplateModelLease oldLease = await oldLeaseTask.WaitAsync(TestTimeout);
        await oldLease.DisposeAsync();

        Assert.Equal(1, oldHandle.DisposeCount);
        Assert.Equal(0, newHandle.DisposeCount);

        await newLease.DisposeAsync();
        Assert.Equal(0, newHandle.DisposeCount);
        await using HalconTemplateModelLease reacquired = await cache.AcquireAsync(
            owner,
            newInput.DescriptorFor(owner),
            default);
        Assert.Equal(2, loader.CallCount);
    }

    [Fact]
    public async Task LaterFailedAcquireDoesNotPreventEarlierSuccessfulKeyFromBecomingActive()
    {
        ModelInput activeInput = Input("activation-failure-active");
        ModelInput earlierInput = Input("activation-failure-earlier");
        ModelInput laterInput = Input("activation-failure-later");
        var activeHandle = new SentinelModelHandle();
        var earlierHandle = new SentinelModelHandle();
        var releaseEarlierLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader(
            (key, _, _) =>
            {
                if (key == activeInput.Key)
                {
                    return Task.FromResult<IHalconModelHandle>(activeHandle);
                }

                return key == earlierInput.Key
                    ? releaseEarlierLoad.Task
                    : Task.FromException<IHalconModelHandle>(
                        new IOException("injected later load failure"));
            });
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("activation-failure");
        HalconTemplateModelLease activeLease = await cache.AcquireAsync(
            owner,
            activeInput.DescriptorFor(owner),
            default);
        await activeLease.DisposeAsync();
        Task<HalconTemplateModelLease> earlierLeaseTask = cache.AcquireAsync(
            owner,
            earlierInput.DescriptorFor(owner),
            default);
        await loader.WaitForCallsAsync(2);

        Exception? laterFailure;
        try
        {
            laterFailure = await Record.ExceptionAsync(
                async () =>
                {
                    await using HalconTemplateModelLease unexpected = await cache.AcquireAsync(
                        owner,
                        laterInput.DescriptorFor(owner),
                        default);
                });
        }
        finally
        {
            releaseEarlierLoad.TrySetResult(earlierHandle);
        }

        Assert.IsType<IOException>(laterFailure);
        HalconTemplateModelLease earlierLease = await earlierLeaseTask.WaitAsync(TestTimeout);
        await earlierLease.DisposeAsync();

        Assert.Equal(1, activeHandle.DisposeCount);
        Assert.Equal(0, earlierHandle.DisposeCount);
    }

    [Fact]
    public async Task FaultedLoadIsRemovedSoNextAcquireCanRetry()
    {
        ModelInput input = Input("retry");
        var handle = new SentinelModelHandle();
        var call = 0;
        var loader = new RecordingModelLoader(
            (_, _, _) => Interlocked.Increment(ref call) == 1
                ? Task.FromException<IHalconModelHandle>(new IOException("injected load failure"))
                : Task.FromResult<IHalconModelHandle>(handle));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;

        await Assert.ThrowsAsync<IOException>(
            () => cache.AcquireAsync(
                Owner("retry"),
                input.DescriptorFor(Owner("retry")),
                default));
        await using HalconTemplateModelLease lease = await cache.AcquireAsync(
            Owner("retry"),
            input.DescriptorFor(Owner("retry")),
            default);

        Assert.Equal(2, loader.CallCount);
    }

    [Fact]
    public async Task SameModelOperationGateSerializesCallersAndOperationLeaseBorrowsLoadedHandle()
    {
        ModelInput input = Input("same-gate");
        var handle = new SentinelModelHandle();
        var loader = LoaderReturning(_ => handle);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        await using HalconTemplateModelLease first = await cache.AcquireAsync(
            Owner("gate-first"),
            input.DescriptorFor(Owner("gate-first")),
            default);
        await using HalconTemplateModelLease second = await cache.AcquireAsync(
            Owner("gate-second"),
            input.DescriptorFor(Owner("gate-second")),
            default);

        HalconTemplateModelOperationLease firstOperation = await first.EnterOperationAsync(default);
        Task<HalconTemplateModelOperationLease> secondOperationTask = second.EnterOperationAsync(default);

        Assert.True(await firstOperation.InvokeAsync(
            borrow => ReferenceEquals(handle, borrow),
            default));
        Assert.False(secondOperationTask.IsCompleted);
        await firstOperation.DisposeAsync();
        await using HalconTemplateModelOperationLease secondOperation =
            await secondOperationTask.WaitAsync(TestTimeout);
        Assert.True(await secondOperation.InvokeAsync(
            borrow => ReferenceEquals(handle, borrow),
            default));
    }

    [Fact]
    public async Task DifferentModelOperationGatesAllowParallelEntry()
    {
        ModelInput firstInput = Input("parallel-first");
        ModelInput secondInput = Input("parallel-second");
        var firstHandle = new SentinelModelHandle();
        var secondHandle = new SentinelModelHandle();
        var handles = new Dictionary<HalconTemplateModelCacheKey, SentinelModelHandle>
        {
            [firstInput.Key] = firstHandle,
            [secondInput.Key] = secondHandle
        };
        var loader = LoaderReturning(key => handles[key]);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        await using HalconTemplateModelLease first = await cache.AcquireAsync(
            Owner("parallel-first"),
            firstInput.DescriptorFor(Owner("parallel-first")),
            default);
        await using HalconTemplateModelLease second = await cache.AcquireAsync(
            Owner("parallel-second"),
            secondInput.DescriptorFor(Owner("parallel-second")),
            default);

        await using HalconTemplateModelOperationLease firstOperation = await first.EnterOperationAsync(default);
        Task<HalconTemplateModelOperationLease> secondOperationTask = second.EnterOperationAsync(default);

        Assert.True(secondOperationTask.IsCompletedSuccessfully);
        await using HalconTemplateModelOperationLease secondOperation =
            await secondOperationTask.WaitAsync(TestTimeout);
        Assert.True(await firstOperation.InvokeAsync(
            borrow => ReferenceEquals(firstHandle, borrow),
            default));
        Assert.True(await secondOperation.InvokeAsync(
            borrow => ReferenceEquals(secondHandle, borrow),
            default));
    }

    [Fact]
    public async Task WaitingForOperationGateCanBeCanceledWithoutLeakingReservation()
    {
        ModelInput input = Input("gate-cancellation");
        var handle = new SentinelModelHandle();
        var loader = LoaderReturning(_ => handle);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        await using HalconTemplateModelLease first = await cache.AcquireAsync(
            Owner("cancel-first"),
            input.DescriptorFor(Owner("cancel-first")),
            default);
        await using HalconTemplateModelLease second = await cache.AcquireAsync(
            Owner("cancel-second"),
            input.DescriptorFor(Owner("cancel-second")),
            default);
        HalconTemplateModelOperationLease held = await first.EnterOperationAsync(default);
        using var cancellation = new CancellationTokenSource();
        Task<HalconTemplateModelOperationLease> canceled = second.EnterOperationAsync(cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.WaitAsync(TestTimeout));
        await held.DisposeAsync();
        await using HalconTemplateModelOperationLease retry = await second.EnterOperationAsync(default);
        Assert.True(await retry.InvokeAsync(
            borrow => ReferenceEquals(handle, borrow),
            default));
    }

    [Fact]
    public async Task RetiredEntryWaitsForLastLeaseBeforeDisposal()
    {
        ModelInput input = Input("retired");
        var handle = new SentinelModelHandle();
        var gates = new CountingGateFactory();
        await using TimeoutCacheScope cacheScope =
            CreateCacheScope(LoaderReturning(_ => handle), gates.Create);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("retired");
        HalconTemplateModelLease lease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);

        ITemplateModelRetirementSink retirementSink = cache;
        await retirementSink.RetireAsync(owner, default);
        Assert.Equal(0, handle.DisposeCount);
        Assert.Equal(0, gates.Single.DisposeCount);

        await lease.DisposeAsync();
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, gates.Single.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RetireRecordsImmediateDisposalFailuresForDeterministicCacheShutdown()
    {
        ModelInput input = Input("retire-disposal-failures");
        var handleFailure = new IOException("injected handle disposal failure");
        var gateFailure = new InvalidOperationException("injected gate disposal failure");
        var handle = new SentinelModelHandle(handleFailure);
        var gates = new CountingGateFactory(gateFailure);
        var cache = new HalconTemplateModelCache(LoaderReturning(_ => handle), gates.Create);
        TemplateModelOwner owner = Owner("retire-disposal-failures");
        HalconTemplateModelLease lease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);
        await lease.DisposeAsync();

        await cache.RetireAsync(owner, default);

        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, gates.Single.DisposeCount);
        AggregateException shutdownFailure = await Assert.ThrowsAsync<AggregateException>(
            () => cache.DisposeAsync().AsTask().WaitAsync(TestTimeout));
        AggregateException entryFailure = Assert.IsType<AggregateException>(
            Assert.Single(shutdownFailure.InnerExceptions));
        Assert.Equal([handleFailure, gateFailure], entryFailure.InnerExceptions);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, gates.Single.DisposeCount);
    }

    [Fact]
    public async Task FinalLeaseDisposePropagatesDeferredHandleDisposalFailure()
    {
        ModelInput input = Input("lease-disposal-failure");
        var handleFailure = new IOException("injected deferred handle disposal failure");
        var handle = new SentinelModelHandle(handleFailure);
        var cache = new HalconTemplateModelCache(LoaderReturning(_ => handle));
        TemplateModelOwner owner = Owner("lease-disposal-failure");
        HalconTemplateModelLease lease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);
        await cache.RetireAsync(owner, default);

        IOException leaseFailure = await Assert.ThrowsAsync<IOException>(
            () => lease.DisposeAsync().AsTask());

        Assert.Same(handleFailure, leaseFailure);
        Assert.Equal(1, handle.DisposeCount);
        await Assert.ThrowsAsync<AggregateException>(
            () => cache.DisposeAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task RetiringOneOwnerKeepsSharedEntryActiveForOtherOwner()
    {
        ModelInput input = Input("shared-owners");
        var handle = new SentinelModelHandle();
        var loader = LoaderReturning(_ => handle);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner firstOwner = Owner("shared-first");
        TemplateModelOwner secondOwner = Owner("shared-second");
        HalconTemplateModelLease first = await cache.AcquireAsync(
            firstOwner,
            input.DescriptorFor(firstOwner),
            default);
        HalconTemplateModelLease second = await cache.AcquireAsync(
            secondOwner,
            input.DescriptorFor(secondOwner),
            default);

        ITemplateModelRetirementSink retirementSink = cache;
        await retirementSink.RetireAsync(firstOwner, default);
        await first.DisposeAsync();
        Assert.Equal(0, handle.DisposeCount);

        await using HalconTemplateModelOperationLease operation = await second.EnterOperationAsync(default);
        HalconTemplateModelLease secondAgain = await cache.AcquireAsync(
            secondOwner,
            input.DescriptorFor(secondOwner),
            default);
        Assert.Equal(1, loader.CallCount);

        await operation.DisposeAsync();
        await second.DisposeAsync();
        await secondAgain.DisposeAsync();
        await retirementSink.RetireAsync(secondOwner, default);
        Assert.Equal(1, handle.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RetireOwnerMarksOldAndActiveGenerations()
    {
        ModelInput oldInput = Input("owner-old");
        ModelInput activeInput = Input("owner-active");
        var oldHandle = new SentinelModelHandle();
        var activeHandle = new SentinelModelHandle();
        var handles = new Dictionary<HalconTemplateModelCacheKey, SentinelModelHandle>
        {
            [oldInput.Key] = oldHandle,
            [activeInput.Key] = activeHandle
        };
        await using TimeoutCacheScope cacheScope =
            CreateCacheScope(LoaderReturning(key => handles[key]));
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("all-generations");
        HalconTemplateModelLease oldLease = await cache.AcquireAsync(
            owner,
            oldInput.DescriptorFor(owner),
            default);
        HalconTemplateModelLease activeLease = await cache.AcquireAsync(
            owner,
            activeInput.DescriptorFor(owner),
            default);

        await cache.RetireAsync(owner, default);
        Assert.Equal(0, oldHandle.DisposeCount);
        Assert.Equal(0, activeHandle.DisposeCount);

        await oldLease.DisposeAsync();
        await activeLease.DisposeAsync();
        Assert.Equal(1, oldHandle.DisposeCount);
        Assert.Equal(1, activeHandle.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RetireDuringLoadPreventsOlderWaiterFromReactivatingOwner()
    {
        ModelInput staleInput = Input("retire-during-load-stale");
        ModelInput freshInput = Input("retire-during-load-fresh");
        var staleHandle = new SentinelModelHandle();
        var freshHandle = new SentinelModelHandle();
        var releaseFirstLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        var loader = new RecordingModelLoader(
            (_, _, _) => Interlocked.Increment(ref call) == 1
                ? releaseFirstLoad.Task
                : Task.FromResult<IHalconModelHandle>(freshHandle));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("retire-during-load");
        Task<HalconTemplateModelLease> staleAcquire = cache.AcquireAsync(
            owner,
            staleInput.DescriptorFor(owner),
            default);
        await loader.WaitForCallsAsync(1);

        ITemplateModelRetirementSink retirementSink = cache;
        Task retirement = retirementSink.RetireAsync(owner, default).AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            releaseFirstLoad.TrySetResult(staleHandle);
        }

        await retirement.WaitAsync(TestTimeout);
        HalconTemplateModelLease staleLease = await staleAcquire.WaitAsync(TestTimeout);
        await staleLease.DisposeAsync();

        Assert.Equal(1, staleHandle.DisposeCount);
        HalconTemplateModelLease freshLease = await cache.AcquireAsync(
            owner,
            freshInput.DescriptorFor(owner),
            default);
        Assert.Equal(2, loader.CallCount);

        await retirementSink.RetireAsync(owner, default);
        await freshLease.DisposeAsync();
        Assert.Equal(1, freshHandle.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RetireFenceRejectsSameOwnerAndGenerationWhileOldLeaseIsOutstanding()
    {
        ModelInput input = Input("retire-fence");
        var handle = new SentinelModelHandle();
        var loader = LoaderReturning(_ => handle);
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("retire-fence");
        HalconTemplateModelLease oldLease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);

        await cache.RetireAsync(owner, default);
        Exception? reacquireFailure;
        try
        {
            reacquireFailure = await Record.ExceptionAsync(
                async () =>
                {
                    await using HalconTemplateModelLease unexpected = await cache.AcquireAsync(
                        owner,
                        input.DescriptorFor(owner),
                        default);
                });
        }
        finally
        {
            await oldLease.DisposeAsync();
        }

        Assert.IsType<ObjectDisposedException>(reacquireFailure);
        Assert.Equal(1, loader.CallCount);
        Assert.Equal(1, handle.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RetireFenceAllowsSameGenerationFreshReloadAfterOldEntryDisposes()
    {
        ModelInput input = Input("retire-fence-reload");
        var firstHandle = new SentinelModelHandle();
        var secondHandle = new SentinelModelHandle();
        var call = 0;
        var loader = new RecordingModelLoader(
            (_, _, _) => Task.FromResult<IHalconModelHandle>(
                Interlocked.Increment(ref call) == 1 ? firstHandle : secondHandle));
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader);
        HalconTemplateModelCache cache = cacheScope.Cache;
        TemplateModelOwner owner = Owner("retire-fence-reload");
        HalconTemplateModelLease firstLease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);

        await cache.RetireAsync(owner, default);
        await firstLease.DisposeAsync();
        Assert.Equal(1, firstHandle.DisposeCount);

        HalconTemplateModelLease secondLease = await cache.AcquireAsync(
            owner,
            input.DescriptorFor(owner),
            default);
        Assert.Equal(2, loader.CallCount);
        await cache.RetireAsync(owner, default);
        await secondLease.DisposeAsync();
        Assert.Equal(1, secondHandle.DisposeCount);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DisposeRejectsNewAcquireAndWaitsForLeaseAndOperationBeforeExactCleanup()
    {
        ModelInput input = Input("shutdown");
        var handle = new SentinelModelHandle();
        var gates = new CountingGateFactory();
        await using TimeoutCacheScope cacheScope =
            CreateCacheScope(LoaderReturning(_ => handle), gates.Create);
        HalconTemplateModelCache cache = cacheScope.Cache;
        HalconTemplateModelLease lease = await cache.AcquireAsync(
            Owner("shutdown"),
            input.DescriptorFor(Owner("shutdown")),
            default);
        HalconTemplateModelOperationLease operation = await lease.EnterOperationAsync(default);

        Task disposeTask = cache.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => cache.AcquireAsync(
                Owner("late"),
                input.DescriptorFor(Owner("late")),
                default));
        await lease.DisposeAsync();
        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(0, handle.DisposeCount);

        await operation.DisposeAsync();
        await disposeTask.WaitAsync(TestTimeout);
        await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, gates.Single.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentDisposeWaitsCanceledSharedLoadAndDisposesLateHandleOnce()
    {
        ModelInput input = Input("shutdown-loading");
        var handle = new SentinelModelHandle();
        var releaseLoad = new TaskCompletionSource<IHalconModelHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new RecordingModelLoader((_, _, _) => releaseLoad.Task);
        var gates = new CountingGateFactory();
        await using TimeoutCacheScope cacheScope = CreateCacheScope(loader, gates.Create);
        HalconTemplateModelCache cache = cacheScope.Cache;
        using var cancellation = new CancellationTokenSource();
        Task<HalconTemplateModelLease> canceledAcquire = cache.AcquireAsync(
            Owner("shutdown-loading"),
            input.DescriptorFor(Owner("shutdown-loading")),
            cancellation.Token);
        await loader.WaitForCallsAsync(1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledAcquire.WaitAsync(TestTimeout));

        Task firstDispose = cache.DisposeAsync().AsTask();
        Task secondDispose = cache.DisposeAsync().AsTask();
        try
        {
            Assert.Same(firstDispose, secondDispose);
            Assert.False(firstDispose.IsCompleted);
        }
        finally
        {
            releaseLoad.TrySetResult(handle);
        }

        await firstDispose.WaitAsync(TestTimeout);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, gates.Single.DisposeCount);
    }

    private static TimeoutCacheScope CreateCacheScope(IHalconModelLoader loader)
    {
        return new TimeoutCacheScope(new HalconTemplateModelCache(loader));
    }

    private static TimeoutCacheScope CreateCacheScope(
        IHalconModelLoader loader,
        Func<IHalconOperationGate> operationGateFactory)
    {
        return new TimeoutCacheScope(
            new HalconTemplateModelCache(loader, operationGateFactory));
    }

    private static RecordingModelLoader LoaderReturning(
        Func<HalconTemplateModelCacheKey, SentinelModelHandle> handle)
    {
        return new RecordingModelLoader(
            (key, _, _) => Task.FromResult<IHalconModelHandle>(handle(key)));
    }

    private static ModelInput Input(string name)
    {
        byte[] metadata = Encoding.UTF8.GetBytes($"{{\"generation\":\"{name}\"}}");
        var key = new HalconTemplateModelCacheKey(
            ModelPath(name),
            Sha256($"model-{name}"),
            Convert.ToHexString(SHA256.HashData(metadata)));
        return new ModelInput(
            key,
            new ResolvedTemplateModel(key.AbsoluteModelPath, metadata),
            metadata,
            TestMetadata(name, key.ModelSha256));
    }

    private static HalconTemplateModelMetadata TestMetadata(string generation, string modelChecksum)
    {
        HalconTemplateMatchingParameters parameters = TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
        TemplateModelGenerationParameters generationParameters =
            TemplateModelGenerationParameters.From(parameters);
        return new HalconTemplateModelMetadata(
            Owner(generation),
            generation,
            $"model-{generation}.shm",
            modelChecksum,
            new TemplateLearnedGeometry(new Pose2D(5, 5, 0), 10, 10),
            5,
            5,
            4.5,
            4.5,
            true,
            Enumerable.Range(0, 100)
                .Select(index =>
                {
                    double angle = index * 2 * Math.PI / 100;
                    return new Point2D(4 * Math.Cos(angle), 4 * Math.Sin(angle));
                })
                .ToArray(),
            [
                [new Point2D(-1, -1)],
                [new Point2D(1, -1)],
                [new Point2D(0, 1)]
            ],
            3,
            new HalconFilledSupportRegion(-5, -5, [new HalconSupportRun(4, 3, 4)]),
            generationParameters,
            TemplateModelGenerationFingerprint.Compute(generationParameters),
            HalconTemplateValidationDefaults.From(parameters));
    }

    private static TemplateModelOwner Owner(string suffix)
    {
        return new TemplateModelOwner($"recipe-{suffix}", "flow-main", "tool-match");
    }

    private static string ModelPath(string name)
    {
        return Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "VisionStationTests", "HalconCache", $"{name}.shm"));
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record ModelInput(
        HalconTemplateModelCacheKey Key,
        ResolvedTemplateModel ResolvedModel,
        byte[] MetadataBytes,
        HalconTemplateModelMetadata Metadata)
    {
        public ValidatedHalconModelDescriptor DescriptorFor(
            TemplateModelOwner owner,
            ResolvedTemplateModel? resolvedModel = null)
        {
            return new ValidatedHalconModelDescriptor(
                owner,
                resolvedModel ?? ResolvedModel,
                Metadata,
                Key);
        }
    }

    private sealed record LoadRequest(
        ValidatedHalconModelDescriptor Descriptor,
        CancellationToken CancellationToken);

    private sealed class TimeoutCacheScope(HalconTemplateModelCache cache) : IAsyncDisposable
    {
        public HalconTemplateModelCache Cache { get; } = cache;

        public ValueTask DisposeAsync()
        {
            return new ValueTask(
                Cache.DisposeAsync()
                    .AsTask()
                    .WaitAsync(TestTimeout));
        }
    }

    private sealed class RecordingModelLoader(
        Func<HalconTemplateModelCacheKey, ResolvedTemplateModel, CancellationToken, Task<IHalconModelHandle>> load) :
        IHalconModelLoader
    {
        private readonly SemaphoreSlim _callSignal = new(0);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ConcurrentQueue<LoadRequest> Requests { get; } = new();

        public Task<IHalconModelHandle> LoadAsync(
            ValidatedHalconModelDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var resolvedModel = new ResolvedTemplateModel(
                descriptor.ModelPath,
                descriptor.MetadataJson);
            Requests.Enqueue(new LoadRequest(descriptor, cancellationToken));
            _callSignal.Release();
            return load(descriptor.CacheKey, resolvedModel, cancellationToken);
        }

        public async Task WaitForCallsAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (CallCount < expected)
            {
                await _callSignal.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class SentinelModelHandle(Exception? disposeFailure = null) :
        IHalconModelHandle,
        IHalconRawModelHandle
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<T> InvokeAsync<T>(
            Func<IHalconModelBorrow, T> invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(invocation(this));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }

    private sealed class CountingGateFactory(Exception? disposeFailure = null)
    {
        private readonly List<CountingGate> _gates = [];

        public CountingGate Single => Assert.Single(_gates);

        public IHalconOperationGate Create()
        {
            var gate = new CountingGate(disposeFailure);
            _gates.Add(gate);
            return gate;
        }
    }

    private sealed class CountingGate(Exception? disposeFailure) : IHalconOperationGate
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

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
            Interlocked.Increment(ref _disposeCount);
            _semaphore.Dispose();
            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }
}
