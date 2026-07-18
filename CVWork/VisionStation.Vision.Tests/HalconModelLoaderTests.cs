using System.Security.Cryptography;
using System.Text.Json.Nodes;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconModelLoaderTests
{
    private static readonly TemplateModelOwner Owner = new("recipe", "flow", "tool");
    private const string ModelChecksum =
        "1111111111111111111111111111111111111111111111111111111111111111";

    public static IEnumerable<object[]> InvalidValidationBoundaries()
    {
        yield return ["metadata", "schemaVersion", "2", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "engine", "OpenCv", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "modelFormat", "other", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "owner.recipeId", "other", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "owner.flowId", "other", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "owner.toolId", "other", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "geometry.templateWidth", "41", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid];
        yield return ["metadata", "modelVersion", "halcon-scaled-shape-v0", TemplateMatchingDiagnosticCodes.ModelVersionMismatch];
        yield return ["metadata", "managedPackageVersion", "0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch];
        yield return ["metadata", "managedAssemblyVersion", "0.0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch];
        yield return ["metadata", "nativeRuntimeVersion", "0.0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch];
        yield return ["reference-fingerprint", "", "", TemplateMatchingDiagnosticCodes.ModelRelearnRequired];
        yield return ["current-fingerprint", "", "", TemplateMatchingDiagnosticCodes.ModelRelearnRequired];
    }

    [Theory]
    [MemberData(nameof(InvalidValidationBoundaries))]
    public async Task ValidationHarnessNeverProbesRuntimeOrLoadsNativeForInvalidBoundary(
        string kind,
        string propertyPath,
        string replacement,
        string expectedCode)
    {
        Fixture fixture = ValidFixture();
        ResolvedTemplateModel resolved = fixture.Resolved;
        HalconTemplateModelState state = fixture.State;
        HalconTemplateMatchingParameters parameters = fixture.Parameters;
        if (kind == "metadata")
        {
            ReadOnlyMemory<byte> metadata = Mutate(
                fixture.Resolved.MetadataJson,
                propertyPath,
                replacement);
            resolved = fixture.Resolved with { MetadataJson = metadata };
            state = state with
            {
                Reference = state.Reference with
                {
                    MetadataChecksum = Convert.ToHexString(SHA256.HashData(metadata.Span))
                        .ToLowerInvariant()
                }
            };
        }
        else if (kind == "reference-fingerprint")
        {
            state = state with
            {
                Reference = state.Reference with
                {
                    GenerationParameterFingerprint = new string('b', 64)
                }
            };
        }
        else
        {
            parameters = parameters with { NumLevels = 4 };
        }

        var runtime = new CountingRuntimeProbe();
        var scheduler = new RecordingScheduler();
        var operators = new RecordingOperatorBackend();
        var harness = new ValidationLoadHarness(
            runtime,
            new HalconModelLoader(scheduler, operators));

        TemplateMatchingConfigurationException exception =
            await Assert.ThrowsAsync<TemplateMatchingConfigurationException>(
                () => harness.ValidateProbeAndLoadAsync(
                    resolved,
                    Owner,
                    state,
                    parameters));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, runtime.CallCount);
        Assert.Equal(0, scheduler.RunCount);
        Assert.Equal(0, operators.LoadCount);
    }

    [Theory]
    [InlineData("schemaVersion", "2", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid)]
    [InlineData("owner.recipeId", "other", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid)]
    [InlineData("geometry.templateWidth", "41", TemplateMatchingDiagnosticCodes.ModelMetadataInvalid)]
    [InlineData("modelVersion", "halcon-scaled-shape-v0", TemplateMatchingDiagnosticCodes.ModelVersionMismatch)]
    [InlineData("managedPackageVersion", "0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch)]
    [InlineData("managedAssemblyVersion", "0.0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch)]
    [InlineData("nativeRuntimeVersion", "0.0.0.0", TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch)]
    public void InvalidMetadataCannotReachRuntimeOrNativeLoader(
        string propertyPath,
        string replacement,
        string expectedCode)
    {
        Fixture fixture = ValidFixture();
        var operators = new RecordingOperatorBackend();
        var scheduler = new RecordingScheduler();
        var loader = new HalconModelLoader(scheduler, operators);
        ResolvedTemplateModel tampered = fixture.Resolved with
        {
            MetadataJson = Mutate(fixture.Resolved.MetadataJson, propertyPath, replacement)
        };

        TemplateMatchingConfigurationException exception = Assert.Throws<TemplateMatchingConfigurationException>(
            () => HalconModelMetadataValidator.Validate(
                tampered,
                Owner,
                fixture.State,
                fixture.Parameters));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, operators.LoadCount);
        Assert.Equal(0, scheduler.RunCount);
        GC.KeepAlive(loader);
    }

    [Fact]
    public void GenerationParameterChangeRequiresRelearnBeforeNativeLoad()
    {
        Fixture fixture = ValidFixture();
        var changed = fixture.Parameters with { NumLevels = 4 };
        var operators = new RecordingOperatorBackend();
        var loader = new HalconModelLoader(new RecordingScheduler(), operators);

        TemplateMatchingConfigurationException exception = Assert.Throws<TemplateMatchingConfigurationException>(
            () => HalconModelMetadataValidator.Validate(
                fixture.Resolved,
                Owner,
                fixture.State,
                changed));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, exception.Code);
        Assert.Equal(0, operators.LoadCount);
        GC.KeepAlive(loader);
    }

    [Fact]
    public void OutOfCropModelDomainCentroidIsRejectedBeforeNativeLoad()
    {
        Fixture fixture = ValidFixture();
        ReadOnlyMemory<byte> tamperedJson = Mutate(
            fixture.Resolved.MetadataJson,
            "modelDomainCentroidColumn",
            "999");
        string metadataChecksum = Convert.ToHexString(SHA256.HashData(tamperedJson.Span))
            .ToLowerInvariant();
        var state = fixture.State with
        {
            Reference = fixture.State.Reference with { MetadataChecksum = metadataChecksum }
        };
        var operators = new RecordingOperatorBackend();

        TemplateMatchingConfigurationException exception = Assert.Throws<TemplateMatchingConfigurationException>(
            () => HalconModelMetadataValidator.Validate(
                fixture.Resolved with { MetadataJson = tamperedJson },
                Owner,
                state,
                fixture.Parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, exception.Code);
        Assert.Equal(0, operators.LoadCount);
    }

    [Fact]
    public async Task ValidatedDescriptorLoadsAndChecksModelInsideScheduler()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        var scheduler = new RecordingScheduler();
        var loader = new HalconModelLoader(scheduler, operators);

        using IHalconModelHandle handle = await loader.LoadAsync(descriptor, default);

        Assert.Equal(1, scheduler.RunCount);
        Assert.Equal(1, operators.LoadCount);
        Assert.Equal(descriptor.ModelPath, operators.LastLoadedPath);
    }

    [Fact]
    public async Task LoadedHandleInvokesOperatorAgainstSameRawModelWithoutReloadingFile()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        var scheduler = new RecordingScheduler();
        var loader = new HalconModelLoader(scheduler, operators);
        using IHalconModelHandle handle = await loader.LoadAsync(descriptor, default);

        int value = await handle.InvokeAsync(
            rawHandle =>
            {
                Assert.Same(operators.LastHandle, rawHandle);
                return 37;
            },
            default);

        Assert.Equal(37, value);
        Assert.Equal(1, operators.LoadCount);
        Assert.Equal(2, scheduler.RunCount);
    }

    [Fact]
    public async Task CacheOperationLeaseInvokesSameLoadedRawModelWithoutPathReload()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        var scheduler = new RecordingScheduler();
        await using var cache = new HalconTemplateModelCache(
            new HalconModelLoader(scheduler, operators));
        await using HalconTemplateModelLease lease = await cache.AcquireAsync(
            Owner,
            descriptor,
            default);
        await using HalconTemplateModelOperationLease operation =
            await lease.EnterOperationAsync(default);

        bool sameRawHandle = await operation.InvokeAsync(
            rawHandle => ReferenceEquals(rawHandle, operators.LastHandle),
            default);

        Assert.True(sameRawHandle);
        Assert.Equal(1, operators.LoadCount);
        Assert.Equal(2, scheduler.RunCount);
    }

    [Fact]
    public async Task OperationLeaseDisposalWaitsForUnawaitedInvocationBeforeRawHandleCleanup()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        await using var scheduler = new HalconOperationScheduler(workerCount: 2, capacity: 8);
        var cache = new HalconTemplateModelCache(
            new HalconModelLoader(scheduler, operators));
        HalconTemplateModelLease lease = await cache.AcquireAsync(
            Owner,
            descriptor,
            default);
        HalconTemplateModelOperationLease operation = await lease.EnterOperationAsync(default);
        using var invocationStarted = new ManualResetEventSlim();
        using var releaseInvocation = new ManualResetEventSlim();

        Task<int> invocation = operation.InvokeAsync(
            _ =>
            {
                invocationStarted.Set();
                releaseInvocation.Wait();
                return 41;
            },
            default);
        Assert.True(invocationStarted.Wait(TimeSpan.FromSeconds(5)));

        Task operationDispose = operation.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => operation.InvokeAsync(_ => 99, default));
        await cache.RetireAsync(Owner, default);
        await lease.DisposeAsync();
        Task cacheDispose = cache.DisposeAsync().AsTask();
        try
        {
            Assert.False(operationDispose.IsCompleted);
            Assert.False(cacheDispose.IsCompleted);
            Assert.Equal(0, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
        }
        finally
        {
            releaseInvocation.Set();
        }

        Assert.Equal(41, await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
        await operationDispose.WaitAsync(TimeSpan.FromSeconds(5));
        await cacheDispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
    }

    [Fact]
    public void OperationCallbackContractCannotDisposeBorrowedRawHandle()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IHalconModelBorrow)));
        Assert.Null(typeof(IHalconModelBorrow).GetMethod(nameof(IDisposable.Dispose)));
        Assert.Null(typeof(HalconTemplateModelOperationLease).GetProperty("Handle"));

        System.Reflection.MethodInfo invoke = Assert.Single(
            typeof(IHalconModelHandle).GetMethods(),
            method => method.Name == nameof(IHalconModelHandle.InvokeAsync));
        Type callback = invoke.GetParameters()[0].ParameterType;
        Assert.Equal(typeof(IHalconModelBorrow), callback.GetGenericArguments()[0]);
    }

    [Fact]
    public async Task SchedulerRejectionTransfersExactRawHandleToCleanupOwner()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        var primary = new RejectDisposalScheduler();
        var cleanupOwner = new HoldingRejectedHandleOwner();
        var loader = new HalconModelLoader(primary, operators, cleanupOwner);
        IHalconModelHandle handle = await loader.LoadAsync(descriptor, default);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(2, primary.RunCount);
        Assert.Equal(1, cleanupOwner.TakeOwnershipCount);
        Assert.Same(operators.LastHandle, cleanupOwner.OwnedHandle);
        Assert.Equal(0, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);

        cleanupOwner.Release();

        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
    }

    [Fact]
    public async Task CleanupOwnerFailureFallsBackToSynchronousExactCleanup()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend();
        var primary = new RejectDisposalScheduler();
        var cleanupOwner = new ThrowingRejectedHandleOwner();
        var loader = new HalconModelLoader(primary, operators, cleanupOwner);
        IHalconModelHandle handle = await loader.LoadAsync(descriptor, default);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
        Assert.Equal(1, cleanupOwner.TakeOwnershipCount);
    }

    [Fact]
    public async Task CancellationTransfersExactRawHandleWhenPrimaryRejectsCleanup()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        using var cancellation = new CancellationTokenSource();
        var operators = new RecordingOperatorBackend();
        var primary = new CancelLoadThenRejectScheduler(cancellation);
        var cleanupOwner = new HoldingRejectedHandleOwner();
        var loader = new HalconModelLoader(primary, operators, cleanupOwner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(descriptor, cancellation.Token));

        Assert.Equal(2, primary.RunCount);
        Assert.Equal(1, cleanupOwner.TakeOwnershipCount);
        Assert.Same(operators.LastHandle, cleanupOwner.OwnedHandle);
        Assert.Equal(0, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);

        cleanupOwner.Release();

        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
    }

    [Fact]
    public async Task CancellationCleanupOwnerFailureStillDisposesRawHandleSynchronously()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        using var cancellation = new CancellationTokenSource();
        var operators = new RecordingOperatorBackend();
        var primary = new CancelLoadThenRejectScheduler(cancellation);
        var cleanupOwner = new ThrowingRejectedHandleOwner();
        var loader = new HalconModelLoader(primary, operators, cleanupOwner);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(descriptor, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, cleanupOwner.TakeOwnershipCount);
        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
    }

    [Fact]
    public void ModelHealthFailsClosedForEmptyOrAnyNonFiniteContour()
    {
        Assert.Throws<InvalidDataException>(
            () => HalconModelHealthValidator.Validate(Array.Empty<HalconContourSamples>()));
        Assert.Throws<InvalidDataException>(
            () => HalconModelHealthValidator.Validate(
                [new HalconContourSamples([], [])]));
        Assert.Throws<InvalidDataException>(
            () => HalconModelHealthValidator.Validate(
                [
                    new HalconContourSamples([1, 2], [3, 4]),
                    new HalconContourSamples([double.NaN], [5])
                ]));
        Assert.Throws<InvalidDataException>(
            () => HalconModelHealthValidator.Validate(
                [new HalconContourSamples([1], [double.PositiveInfinity])]));
        Assert.Throws<InvalidDataException>(
            () => HalconModelHealthValidator.Validate(
                [new HalconContourSamples([1, 2], [3])]));

        HalconModelHealthValidator.Validate(
            [
                new HalconContourSamples([1, 2], [3, 4]),
                new HalconContourSamples([5], [6])
            ]);
    }

    [Fact]
    public async Task CorruptNativeModelMapsToSanitizedStableFailure()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        var operators = new RecordingOperatorBackend
        {
            LoadFailure = new InvalidDataException("raw native .shm detail")
        };
        var loader = new HalconModelLoader(new RecordingScheduler(), operators);

        TemplateMatchingConfigurationException exception =
            await Assert.ThrowsAsync<TemplateMatchingConfigurationException>(
                () => loader.LoadAsync(descriptor, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelLoadFailed, exception.Code);
        Assert.DoesNotContain("raw native", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InvalidDataException", exception.TechnicalDetails);
    }

    [Fact]
    public async Task CancellationAfterNativeAdmissionDisposesLoadedHandleInsideScheduler()
    {
        Fixture fixture = ValidFixture();
        ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
            fixture.Resolved,
            Owner,
            fixture.State,
            fixture.Parameters);
        using var cancellation = new CancellationTokenSource();
        var scheduler = new CancelAfterOperationScheduler(cancellation);
        var operators = new RecordingOperatorBackend();
        var loader = new HalconModelLoader(scheduler, operators);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(descriptor, cancellation.Token));

        Assert.Equal(2, scheduler.RunCount);
        Assert.Equal(1, Assert.IsType<Handle>(operators.LastHandle).DisposeCount);
    }

    private static Fixture ValidFixture()
    {
        HalconTemplateMatchingParameters parameters = TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
        TemplateModelGenerationParameters generation = TemplateModelGenerationParameters.From(parameters);
        string fingerprint = TemplateModelGenerationFingerprint.Compute(generation);
        var geometry = new TemplateLearnedGeometry(new Pose2D(120, 80, 0), 40, 30);
        var metadata = new HalconTemplateModelMetadata(
            Owner,
            "generation-1",
            "model-generation-1.shm",
            ModelChecksum,
            geometry,
            15,
            20,
            14.5,
            19.5,
            true,
            Enumerable.Range(0, 100)
                .Select(index =>
                {
                    double angle = 2 * Math.PI * index / 100;
                    return new Point2D(10 * Math.Cos(angle), 8 * Math.Sin(angle));
                })
                .ToArray(),
            [
                [new Point2D(-5, -3)],
                [new Point2D(5, -3)],
                [new Point2D(0, 5)]
            ],
            3,
            new HalconFilledSupportRegion(-20, -15, [new HalconSupportRun(10, 10, 20)]),
            generation,
            fingerprint,
            HalconTemplateValidationDefaults.From(parameters));
        byte[] json = HalconTemplateModelMetadataJson.Serialize(metadata);
        string metadataChecksum = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
        var reference = new TemplateModelReference(
            "recipe/flow/tool/model-generation-1.shm",
            "recipe/flow/tool/model-generation-1.json",
            TemplateModelParameterCodec.HalconScaledShapeModelFormat,
            ModelChecksum,
            metadataChecksum,
            "generation-1",
            HalconTemplateModelMetadata.CurrentModelVersion,
            HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
            fingerprint);
        string absolutePath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "VisionStation",
            "model-generation-1.shm"));
        return new Fixture(
            new ResolvedTemplateModel(absolutePath, json),
            new HalconTemplateModelState(reference, geometry),
            parameters);
    }

    private static ReadOnlyMemory<byte> Mutate(
        ReadOnlyMemory<byte> json,
        string propertyPath,
        string replacement)
    {
        JsonObject root = JsonNode.Parse(json.Span)!.AsObject();
        string[] segments = propertyPath.Split('.');
        JsonObject owner = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            owner = owner[segments[index]]!.AsObject();
        }

        string leaf = segments[^1];
        owner[leaf] = int.TryParse(replacement, out int integer)
            ? JsonValue.Create(integer)
            : JsonValue.Create(replacement);
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private sealed record Fixture(
        ResolvedTemplateModel Resolved,
        HalconTemplateModelState State,
        HalconTemplateMatchingParameters Parameters);

    private sealed class RecordingScheduler : IHalconOperationScheduler
    {
        public int RunCount { get; private set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingRuntimeProbe : IHalconRuntimeProbe
    {
        public int CallCount { get; private set; }

        public Task<HalconRuntimeProbeResult> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(HalconRuntimeProbeResult.Ready(
                new HalconRuntimeDescriptor(
                    "runtime",
                    HalconRuntimeSource.DeviceConfiguration,
                    "x64",
                    HalconTemplateModelMetadata.CurrentManagedPackageVersion,
                    HalconTemplateModelMetadata.CurrentManagedAssemblyVersion,
                    HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                    HalconTemplateModelMetadata.CurrentNativeRuntimeVersion)));
        }
    }

    private sealed class ValidationLoadHarness(
        IHalconRuntimeProbe runtime,
        IHalconModelLoader loader)
    {
        public async Task<IHalconModelHandle> ValidateProbeAndLoadAsync(
            ResolvedTemplateModel resolved,
            TemplateModelOwner owner,
            HalconTemplateModelState state,
            HalconTemplateMatchingParameters parameters)
        {
            ValidatedHalconModelDescriptor descriptor = HalconModelMetadataValidator.Validate(
                resolved,
                owner,
                state,
                parameters);
            HalconRuntimeProbeResult probe = await runtime.EnsureReadyAsync(default);
            if (!probe.IsReady)
            {
                throw new TemplateMatchingConfigurationException(probe.Diagnostic!);
            }

            return await loader.LoadAsync(descriptor, default);
        }
    }

    private sealed class RecordingOperatorBackend : IHalconOperatorBackend
    {
        public Exception? LoadFailure { get; init; }

        public int LoadCount { get; private set; }

        public string? LastLoadedPath { get; private set; }

        public IHalconRawModelHandle? LastHandle { get; private set; }

        public void CreateAndWriteShapeModel(
            HalconShapeModelCreationRequest request,
            string stagingModelPath) => throw new NotSupportedException();

        public IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath)
        {
            LoadCount++;
            LastLoadedPath = modelPath;
            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }

            LastHandle = new Handle();
            return LastHandle;
        }

        public void VerifyMatchingLicense()
        {
        }
    }

    private sealed class Handle : IHalconRawModelHandle
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class CancelAfterOperationScheduler(CancellationTokenSource cancellation) :
        IHalconOperationScheduler
    {
        public int RunCount { get; private set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            RunCount++;
            T result = operation();
            if (cancellationToken.CanBeCanceled)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectDisposalScheduler : IHalconOperationScheduler
    {
        public int RunCount { get; private set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            RunCount++;
            return RunCount == 1
                ? Task.FromResult(operation())
                : Task.FromException<T>(new ObjectDisposedException("primary-scheduler"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HoldingRejectedHandleOwner : IHalconRejectedHandleOwner
    {
        public int TakeOwnershipCount { get; private set; }

        public IHalconRawModelHandle? OwnedHandle { get; private set; }

        public void TakeOwnership(IHalconRawModelHandle handle)
        {
            TakeOwnershipCount++;
            OwnedHandle = handle;
        }

        public void Release()
        {
            IHalconRawModelHandle handle = OwnedHandle
                ?? throw new InvalidOperationException("No rejected HALCON handle is owned.");
            OwnedHandle = null;
            handle.Dispose();
        }
    }

    private sealed class ThrowingRejectedHandleOwner : IHalconRejectedHandleOwner
    {
        public int TakeOwnershipCount { get; private set; }

        public void TakeOwnership(IHalconRawModelHandle handle)
        {
            TakeOwnershipCount++;
            throw new InvalidOperationException("cleanup-owner-failure");
        }
    }

    private sealed class CancelLoadThenRejectScheduler(CancellationTokenSource cancellation) :
        IHalconOperationScheduler
    {
        public int RunCount { get; private set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            RunCount++;
            if (RunCount != 1)
            {
                return Task.FromException<T>(new ObjectDisposedException("primary-scheduler"));
            }

            T result = operation();
            cancellation.Cancel();
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
