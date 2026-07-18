using System.Security.Cryptography;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconTemplateMatchingBackendTests
{
    [Fact]
    public async Task SingleAndExactCountUseTheSameCandidateValidationCore()
    {
        await using BackendFixture single = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 3);
        await using BackendFixture exact = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 3,
            candidateCount: 3);

        TemplateMatchBatchResult singleResult = await single.Backend.MatchAsync(single.Request, default);
        TemplateMatchBatchResult exactResult = await exact.Backend.MatchAsync(exact.Request, default);

        Assert.True(singleResult.HasMatch);
        Assert.Equal(InspectionOutcome.Ok, singleResult.Outcome);
        TemplateMatchBatchCandidate singleCandidate = Assert.Single(singleResult.Matches);
        Assert.Equal((40, 30), (singleCandidate.TemplateWidth, singleCandidate.TemplateHeight));
        Assert.Equal((0.99, 0.99, 1d, 0.99),
            (singleCandidate.OuterCoverage,
                singleCandidate.InnerCoverage,
                singleCandidate.EdgeDistanceP95Px,
                singleCandidate.PolarityAgreement));
        Assert.True(exactResult.HasMatch);
        Assert.Equal(InspectionOutcome.Ok, exactResult.Outcome);
        Assert.Equal(3, exactResult.Matches.Count);
        Assert.Equal(1, single.Evidence.BuildCount);
        Assert.Equal(1, exact.Evidence.BuildCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ExactCountMismatchIsNgButKeepsAcceptedCandidatesForDiagnostics(int actualCount)
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 3,
            candidateCount: actualCount);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(actualCount, result.Matches.Count);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.MatchCountMismatch,
            result.Diagnostic?.Code);
        Assert.Contains(actualCount.ToString(), result.Message, StringComparison.Ordinal);
        Assert.Contains("3", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactCountAtCandidateLimitFailsClosedWhenExhaustionIsUnproven()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 3,
            candidateCount: 3,
            limitReached: true);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.MatchCandidateLimitReached,
            result.Diagnostic?.Code);
    }

    [Fact]
    public async Task ProvenOverCountTakesPriorityOverCandidateLimitDiagnostic()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 3,
            candidateCount: 4,
            limitReached: true);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.False(result.HasMatch);
        Assert.Equal(4, result.Matches.Count);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.MatchCountMismatch,
            result.Diagnostic?.Code);
        Assert.Contains("4", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllHardGateRejectionsReturnZeroAcceptedAndFirstStableDiagnostic()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 2,
            candidateCount: 2);
        fixture.Evidence.RejectAllByPolarity = true;

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.False(result.HasMatch);
        Assert.Empty(result.Matches);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch,
            result.Diagnostic?.Code);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("parameter")]
    [InlineData("missing-model")]
    public async Task InvalidConfigurationNeverTouchesStoreProbeCacheOrCandidateSource(string scenario)
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        Dictionary<string, string> parameters = new(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase);
        if (scenario == "mode")
        {
            parameters[TemplateMatchingParameterCatalog.MatchMode] = "Orb";
        }
        else if (scenario == "parameter")
        {
            parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = "0";
        }
        else
        {
            TemplateModelParameterCodec.RemoveHalcon(parameters);
        }

        var request = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            fixture.Request.SearchRoi,
            parameters,
            fixture.Request.Cardinality,
            fixture.Request.ExpectedCount);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(request, default);

        Assert.False(result.HasMatch);
        Assert.Equal(0, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task MissingModelStatePrecedesManagedImagePreflight()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        var parameters = new Dictionary<string, string>(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase);
        TemplateModelParameterCodec.RemoveHalcon(parameters);
        var request = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame with { Stride = 1 },
            fixture.Request.SearchRoi,
            parameters,
            fixture.Request.Cardinality,
            fixture.Request.ExpectedCount);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, result.Diagnostic?.Code);
        Assert.Equal(0, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task RequestAndPersistedExpectedCountMismatchFailsBeforeStoreOrProbe()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.ExactCount,
            expectedCount: 3,
            candidateCount: 3);
        var request = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            fixture.Request.SearchRoi,
            fixture.Request.Parameters,
            TemplateMatchCardinality.ExactCount,
            ExpectedCount: 2);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal(0, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task StoreFailurePrecedesRuntimeAndNativeLoad()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        fixture.Store.ResolveFailure = new TemplateModelStoreException(
            TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
            "controlled-store-checksum-failure");

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch, result.Diagnostic?.Code);
        Assert.Equal(1, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task MetadataFailurePrecedesRuntimeAndNativeLoad()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        Dictionary<string, string> parameters = new(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase);
        HalconTemplateModelState state = Assert.IsType<HalconTemplateModelState>(
            TemplateModelParameterCodec.ReadHalcon(parameters));
        TemplateModelParameterCodec.WriteHalcon(
            parameters,
            state with
            {
                Geometry = state.Geometry with
                {
                    TemplateWidth = state.Geometry.TemplateWidth + 1
                }
            });
        var request = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            fixture.Request.SearchRoi,
            parameters,
            fixture.Request.Cardinality,
            fixture.Request.ExpectedCount);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, result.Diagnostic?.Code);
        Assert.Equal(1, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task GenerationParameterChangeRequiresRelearnBeforeRuntimeProbe()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        var parameters = new Dictionary<string, string>(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.AngleStartDeg] = "-170"
        };
        var request = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            fixture.Request.SearchRoi,
            parameters,
            fixture.Request.Cardinality,
            fixture.Request.ExpectedCount);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, result.Diagnostic?.Code);
        Assert.Equal(1, fixture.Store.ResolveCount);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task RuntimeFailurePreventsCacheLoadAndCandidateSearch()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        fixture.Runtime.Result = HalconRuntimeProbeResult.Failed(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.LicenseUnavailable,
                "sanitized-license-probe"));

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.LicenseUnavailable, result.Diagnostic?.Code);
        Assert.Equal(1, fixture.Store.ResolveCount);
        Assert.Equal(1, fixture.Runtime.CallCount);
        Assert.Equal(0, fixture.Loader.LoadCount);
        Assert.Equal(0, fixture.Candidates.FindCount);
    }

    [Fact]
    public async Task RuntimeTimeoutChangesAreHotWithoutReloadingImmutableModel()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        Dictionary<string, string> secondParameters = new(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.OperatorTimeoutMs] = "7000"
        };
        var secondRequest = new TemplateMatchingRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            fixture.Request.SearchRoi,
            secondParameters,
            fixture.Request.Cardinality,
            fixture.Request.ExpectedCount);

        TemplateMatchBatchResult first = await fixture.Backend.MatchAsync(fixture.Request, default);
        TemplateMatchBatchResult second = await fixture.Backend.MatchAsync(secondRequest, default);

        Assert.Equal(InspectionOutcome.Ok, first.Outcome);
        Assert.Equal(InspectionOutcome.Ok, second.Outcome);
        Assert.Equal([5000, 7000], fixture.Candidates.Timeouts);
        Assert.Equal(1, fixture.Loader.LoadCount);
        Assert.Equal(2, fixture.Store.ResolveCount);
    }

    [Fact]
    public async Task ManagedEvidenceStartsOnlyAfterPerModelOperationLeaseIsReleased()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        fixture.Evidence.OnBuild = () =>
        {
            HalconTemplateModelOperationLease operation =
                Assert.IsType<HalconTemplateModelOperationLease>(
                    fixture.Candidates.LastOperation);
            Assert.True(operation.IsReleased);
        };

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(
            fixture.Request,
            default);

        Assert.True(result.HasMatch);
        Assert.Equal(1, fixture.Evidence.BuildCount);
    }

    [Fact]
    public async Task NativeTimeoutReturnsStableNgAndUserCancellationStillPropagates()
    {
        await using BackendFixture timeout = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        timeout.Candidates.Failure = new HalconOperatorFailure(
            TemplateMatchingDiagnosticCodes.MatchTimeout,
            "Operation=FindScaledShapeModel; ErrorCode=9400");

        TemplateMatchBatchResult timedOut = await timeout.Backend.MatchAsync(timeout.Request, default);

        Assert.False(timedOut.HasMatch);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchTimeout, timedOut.Diagnostic?.Code);

        await using BackendFixture cancelled = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancelled.Candidates.OnFind = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelled.Backend.MatchAsync(cancelled.Request, cancellation.Token));
    }

    [Fact]
    public async Task UnknownCandidateFailureLogsFullExceptionButReturnsOnlyStableDetails()
    {
        const string sensitive = "candidate-sensitive-stack-marker";
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        fixture.Candidates.Failure = new InvalidOperationException(sensitive);

        TemplateMatchBatchResult result = await fixture.Backend.MatchAsync(fixture.Request, default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchOperatorFailed, result.Diagnostic?.Code);
        Assert.Contains(nameof(InvalidOperationException), result.Diagnostic?.TechnicalDetails);
        Assert.DoesNotContain(sensitive, result.Diagnostic?.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
        string log = Assert.Single(fixture.DiagnosticSink.Errors);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.Contains(sensitive, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownDisposesCacheBeforeSchedulerAndAggregatesBothFailures()
    {
        BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        TemplateMatchBatchResult match = await fixture.Backend.MatchAsync(fixture.Request, default);
        Assert.True(match.HasMatch);
        fixture.Loader.DisposeFailure = new IOException("cache-dispose-marker");
        fixture.Scheduler.DisposeFailure = new InvalidOperationException("scheduler-dispose-marker");

        Task firstShutdown = fixture.Backend.DisposeAsync().AsTask();
        Task concurrentShutdown = fixture.Backend.DisposeAsync().AsTask();
        Assert.Same(firstShutdown, concurrentShutdown);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => firstShutdown);

        Assert.Equal(["cache", "scheduler"], fixture.DisposalEvents);
        AggregateException flattened = failure.Flatten();
        Assert.Contains(
            flattened.InnerExceptions,
            exception => exception.Message.Contains("cache-dispose-marker", StringComparison.Ordinal));
        Assert.Contains(
            flattened.InnerExceptions,
            exception => exception.Message.Contains("scheduler-dispose-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LowercaseHalconAndShapeRemainValidForLearnerIntegration()
    {
        await using BackendFixture fixture = BackendFixture.Create(
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            candidateCount: 1);
        Dictionary<string, string> parameters = new(
            fixture.Request.Parameters,
            StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.Engine] = "halcon",
            [TemplateMatchingParameterCatalog.MatchMode] = "shape"
        };
        var learning = new TemplateLearningRequest(
            fixture.Request.Owner,
            fixture.Request.Frame,
            new RoiDefinition
            {
                Shape = RoiShapeKind.Rectangle,
                X = 20,
                Y = 20,
                Width = 80,
                Height = 80
            },
            null,
            parameters);

        TemplateLearningResult result = await fixture.Backend.LearnAsync(learning, default);

        Assert.True(result.Success);
        Assert.Equal(1, fixture.Learner.CallCount);
    }

    private sealed class BackendFixture : IAsyncDisposable
    {
        private BackendFixture(
            HalconTemplateMatchingBackend backend,
            TemplateMatchingRequest request,
            RecordingTemplateLearner learner,
            RecordingTemplateModelStore store,
            RecordingRuntimeProbe runtime,
            RecordingModelLoader loader,
            RecordingScheduler scheduler,
            RecordingCandidateSource candidates,
            RecordingEvidenceBuilder evidence,
            RecordingDiagnosticSink diagnosticSink,
            List<string> disposalEvents)
        {
            Backend = backend;
            Request = request;
            Learner = learner;
            Store = store;
            Runtime = runtime;
            Loader = loader;
            Scheduler = scheduler;
            Candidates = candidates;
            Evidence = evidence;
            DiagnosticSink = diagnosticSink;
            DisposalEvents = disposalEvents;
        }

        public HalconTemplateMatchingBackend Backend { get; }

        public TemplateMatchingRequest Request { get; }

        public RecordingTemplateLearner Learner { get; }

        public RecordingTemplateModelStore Store { get; }

        public RecordingRuntimeProbe Runtime { get; }

        public RecordingModelLoader Loader { get; }

        public RecordingScheduler Scheduler { get; }

        public RecordingCandidateSource Candidates { get; }

        public RecordingEvidenceBuilder Evidence { get; }

        public RecordingDiagnosticSink DiagnosticSink { get; }

        public List<string> DisposalEvents { get; }

        public static BackendFixture Create(
            TemplateMatchCardinality cardinality,
            int expectedCount,
            int candidateCount,
            bool limitReached = false)
        {
            TemplateModelOwner owner = new("recipe", "flow", "tool");
            Dictionary<string, string> parameters = cardinality == TemplateMatchCardinality.Single
                ? TemplateMatchingParameterCatalog.CreateStrictDefaults(cardinality)
                : TemplateMatchingParameterCatalog.CreateStrictDefaults(cardinality);
            if (cardinality == TemplateMatchCardinality.ExactCount)
            {
                parameters[TemplateMatchingParameterCatalog.ExpectedCount] =
                    expectedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            HalconTemplateMatchingParameters parsed =
                TemplateMatchingParameterCatalog.ParseHalcon(parameters, cardinality);
            FixtureModel model = CreateModel(owner, parsed);
            TemplateModelParameterCodec.WriteHalcon(parameters, model.State);
            var request = new TemplateMatchingRequest(
                owner,
                Frame(),
                null,
                parameters,
                cardinality,
                expectedCount);
            var learner = new RecordingTemplateLearner();
            var store = new RecordingTemplateModelStore(model.Resolved);
            var runtime = new RecordingRuntimeProbe();
            var disposalEvents = new List<string>();
            var loader = new RecordingModelLoader(disposalEvents);
            var scheduler = new RecordingScheduler(disposalEvents);
            var cache = new HalconTemplateModelCache(loader);
            var candidates = new RecordingCandidateSource(candidateCount, limitReached);
            var evidence = new RecordingEvidenceBuilder();
            var diagnosticSink = new RecordingDiagnosticSink();
            var backend = new HalconTemplateMatchingBackend(
                learner,
                store,
                runtime,
                cache,
                scheduler,
                candidates,
                evidence,
                new TemplateCandidateValidator(),
                diagnosticSink);
            return new BackendFixture(
                backend,
                request,
                learner,
                store,
                runtime,
                loader,
                scheduler,
                candidates,
                evidence,
                diagnosticSink,
                disposalEvents);
        }

        public ValueTask DisposeAsync() => Backend.DisposeAsync();

        private static FixtureModel CreateModel(
            TemplateModelOwner owner,
            HalconTemplateMatchingParameters parameters)
        {
            const string generation = "generation-1";
            string modelChecksum = new('a', 64);
            TemplateModelGenerationParameters generationParameters =
                TemplateModelGenerationParameters.From(parameters);
            string fingerprint =
                TemplateModelGenerationFingerprint.Compute(generationParameters);
            var geometry = new TemplateLearnedGeometry(
                new Pose2D(100, 100, 0) { Scale = 1 },
                40,
                30);
            Point2D[] outer = Enumerable.Range(0, 100)
                .Select(index =>
                {
                    double angle = 2d * Math.PI * index / 100d;
                    return new Point2D(18 * Math.Cos(angle), 13 * Math.Sin(angle));
                })
                .ToArray();
            var metadata = new HalconTemplateModelMetadata(
                owner,
                generation,
                $"model-{generation}.shm",
                modelChecksum,
                geometry,
                15,
                20,
                14.5,
                19.5,
                true,
                outer,
                [
                    [new Point2D(-5, -3)],
                    [new Point2D(5, -3)],
                    [new Point2D(0, 5)]
                ],
                3,
                new HalconFilledSupportRegion(
                    -20,
                    -15,
                    [new HalconSupportRun(10, 10, 20)]),
                generationParameters,
                fingerprint,
                HalconTemplateValidationDefaults.From(parameters));
            byte[] metadataJson = HalconTemplateModelMetadataJson.Serialize(metadata);
            string metadataChecksum = Convert.ToHexString(SHA256.HashData(metadataJson))
                .ToLowerInvariant();
            var reference = new TemplateModelReference(
                $"recipe/flow/tool/model-{generation}.shm",
                $"recipe/flow/tool/model-{generation}.json",
                TemplateModelParameterCodec.HalconScaledShapeModelFormat,
                modelChecksum,
                metadataChecksum,
                generation,
                HalconTemplateModelMetadata.CurrentModelVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                fingerprint);
            string absolutePath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "VisionStation",
                $"model-{generation}.shm"));
            return new FixtureModel(
                new ResolvedTemplateModel(absolutePath, metadataJson),
                new HalconTemplateModelState(reference, geometry));
        }

        private static ImageFrame Frame()
        {
            const int width = 200;
            const int height = 200;
            return new ImageFrame(
                "backend",
                width,
                height,
                width,
                PixelFormatKind.Gray8,
                Enumerable.Repeat((byte)230, width * height).ToArray(),
                DateTimeOffset.UnixEpoch,
                "test");
        }
    }

    private sealed record FixtureModel(
        ResolvedTemplateModel Resolved,
        HalconTemplateModelState State);

    private sealed class RecordingTemplateLearner : IHalconTemplateLearner
    {
        public int CallCount { get; private set; }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                request.Parameters,
                "learned",
                null));
        }
    }

    private sealed class RecordingTemplateModelStore(ResolvedTemplateModel resolved) :
        ITemplateModelStore
    {
        public Exception? ResolveFailure { get; set; }

        public int ResolveCount { get; private set; }

        public Task<ResolvedTemplateModel> ResolveAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            return ResolveFailure is null
                ? Task.FromResult(resolved)
                : Task.FromException<ResolvedTemplateModel>(ResolveFailure);
        }

        public Task<TemplateModelWriteSession> BeginWriteAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateModelReference> CommitAsync(
            TemplateModelWriteSession session,
            ReadOnlyMemory<byte> metadataJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateModelReference> CopyGenerationAsync(
            TemplateModelOwner sourceOwner,
            TemplateModelReference sourceReference,
            TemplateModelOwner targetOwner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteGenerationAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteOwnerResourcesAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRuntimeProbe : IHalconRuntimeProbe
    {
        public HalconRuntimeProbeResult Result { get; set; } = HalconRuntimeProbeResult.Ready(
            new HalconRuntimeDescriptor(
                "runtime",
                HalconRuntimeSource.DeviceConfiguration,
                "x64",
                HalconTemplateModelMetadata.CurrentManagedPackageVersion,
                HalconTemplateModelMetadata.CurrentManagedAssemblyVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion));

        public int CallCount { get; private set; }

        public Task<HalconRuntimeProbeResult> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingModelLoader(List<string> disposalEvents) : IHalconModelLoader
    {
        public Exception? DisposeFailure { get; set; }

        public int LoadCount { get; private set; }

        public Task<IHalconModelHandle> LoadAsync(
            ValidatedHalconModelDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult<IHalconModelHandle>(new ModelHandle(this, disposalEvents));
        }

        private sealed class ModelHandle(
            RecordingModelLoader owner,
            List<string> disposalEvents) : IHalconModelHandle
        {
            private readonly IHalconModelBorrow _borrow = new Borrow();

            public Task<T> InvokeAsync<T>(
                Func<IHalconModelBorrow, T> invocation,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(invocation(_borrow));
            }

            public void Dispose()
            {
                disposalEvents.Add("cache");
                if (owner.DisposeFailure is not null)
                {
                    throw owner.DisposeFailure;
                }
            }

            private sealed class Borrow : IHalconModelBorrow
            {
            }
        }
    }

    private sealed class RecordingCandidateSource : IHalconCandidateSource
    {
        private readonly int _candidateCount;
        private readonly bool _limitReached;

        public RecordingCandidateSource(int candidateCount, bool limitReached)
        {
            _candidateCount = candidateCount;
            _limitReached = limitReached;
        }

        public Exception? Failure { get; set; }

        public Action? OnFind { get; set; }

        public IHalconModelOperation? LastOperation { get; private set; }

        public int FindCount { get; private set; }

        public List<int> Timeouts { get; } = [];

        public Task<HalconCandidateBatch> FindAsync(
            IHalconModelOperation modelOperation,
            ImageFrame frame,
            RoiDefinition? searchRoi,
            HalconTemplateMatchingParameters parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOperation = modelOperation;
            FindCount++;
            Timeouts.Add(parameters.OperatorTimeoutMs);
            OnFind?.Invoke();
            if (Failure is not null)
            {
                return Task.FromException<HalconCandidateBatch>(Failure);
            }

            TemplateCandidate[] candidates = Enumerable.Range(0, _candidateCount)
                .Select(index => new TemplateCandidate(
                    index,
                    new Pose2D(50 + index * 20, 60, 0) { Scale = 1 },
                    0.99 - index * 0.01))
                .ToArray();
            return Task.FromResult(new HalconCandidateBatch(
                candidates,
                _limitReached,
                new TemplateSearchRegion(0, 0, frame.Width, frame.Height)));
        }
    }

    private sealed class RecordingEvidenceBuilder : ITemplateCandidateEvidenceBuilder
    {
        public bool RejectAllByPolarity { get; set; }

        public Action? OnBuild { get; set; }

        public int BuildCount { get; private set; }

        public IReadOnlyList<TemplateCandidateEvidence> BuildBatch(
            ImageFrame frame,
            RoiDefinition? searchRoi,
            HalconTemplateModelMetadata metadata,
            IReadOnlyList<TemplateCandidate> candidates,
            HalconTemplateMatchingParameters parameters,
            CancellationToken cancellationToken = default)
        {
            OnBuild?.Invoke();
            BuildCount++;
            return candidates.Select(candidate => new TemplateCandidateEvidence(
                candidate,
                geometryUsable: true,
                originInsideSearchDomain: true,
                completeAtBoundary: true,
                [
                    new Point2D(candidate.Pose.X - 5, candidate.Pose.Y - 5),
                    new Point2D(candidate.Pose.X + 5, candidate.Pose.Y - 5),
                    new Point2D(candidate.Pose.X + 5, candidate.Pose.Y + 5),
                    new Point2D(candidate.Pose.X - 5, candidate.Pose.Y + 5)
                ],
                [new Point2D(candidate.Pose.X, candidate.Pose.Y)],
                [
                    [new Point2D(candidate.Pose.X - 2, candidate.Pose.Y)],
                    [new Point2D(candidate.Pose.X + 2, candidate.Pose.Y)],
                    [new Point2D(candidate.Pose.X, candidate.Pose.Y + 2)]
                ],
                outerCoverage: 0.99,
                innerCoverage: 0.99,
                edgeDistanceP95Px: 1,
                polarityAgreement: RejectAllByPolarity ? 0 : 0.99,
                validInnerGroupCount: 3,
                new FilledSupportMask(
                    candidate.SourceIndex * 10,
                    0,
                    3,
                    3,
                    Enumerable.Repeat((byte)1, 9).ToArray())))
                .ToArray();
        }
    }

    private sealed class RecordingScheduler(List<string> disposalEvents) : IHalconOperationScheduler
    {
        public Exception? DisposeFailure { get; set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            disposalEvents.Add("scheduler");
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class RecordingDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        public List<string> Warnings { get; } = [];

        public List<string> Errors { get; } = [];

        public void Warning(string source, string message)
        {
            Warnings.Add($"{source}: {message}");
        }

        public void Error(string source, string message)
        {
            Errors.Add($"{source}: {message}");
        }
    }
}
