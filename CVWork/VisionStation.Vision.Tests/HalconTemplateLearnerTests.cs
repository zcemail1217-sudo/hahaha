using System.Security.Cryptography;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconTemplateLearnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "VisionStation-HalconLearnerTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PartialUiClockwiseIntervalConvertsToHalconCounterClockwiseInterval()
    {
        HalconNativeAngleInterval native = HalconAngleConvention.ToNativeInterval(
            new TemplateModelGenerationParameters(-10, 30, 0.9, 1.1, 0));

        Assert.Equal(-20 * Math.PI / 180.0, native.StartRadians, 12);
        Assert.Equal(30 * Math.PI / 180.0, native.ExtentRadians, 12);
        Assert.Equal(20, HalconAngleConvention.ToUiDegrees(native.StartRadians), 12);
    }

    [Fact]
    public async Task SuccessfulLearningUsesSingleModelDomainContractAndPublishesCommittedReference()
    {
        Directory.CreateDirectory(_root);
        HalconTemplateFeatureSet features = Features();
        var operators = new RecordingHalconOperatorBackend();
        var store = new RecordingTemplateModelStore(_root);
        var learner = CreateLearner(features, operators, store);

        TemplateLearningResult result = await learner.LearnAsync(ValidRequest(), default);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        Assert.Null(result.Diagnostic);
        Assert.Equal(1, operators.CreateCount);
        Assert.Equal(1, operators.LoadCount);
        Assert.Equal(1, Assert.IsType<FakeHandle>(operators.LastHandle).DisposeCount);
        Assert.Equal(store.LastStagingModelPath, operators.LastWritePath);
        HalconShapeModelCreationRequest command = Assert.IsType<HalconShapeModelCreationRequest>(
            operators.LastCreationRequest);
        Assert.Equal(8, command.TemplateImage.Width);
        Assert.Equal(6, command.TemplateImage.Height);
        Assert.Equal(features.ModelDomain.Runs, command.ModelDomain.Runs);
        Assert.NotEqual(features.FilledSupport.Runs, command.ModelDomain.Runs);
        Assert.Equal("use_polarity", command.Metric);
        Assert.False(command.AllowBorderShapeModels);
        Assert.Equal(features.ReferenceRow - features.ModelDomain.CentroidRow, command.OriginRow);
        Assert.Equal(features.ReferenceColumn - features.ModelDomain.CentroidColumn, command.OriginColumn);
        Assert.Equal(-180, command.GenerationParameters.AngleStartDeg);
        Assert.Equal(360, command.GenerationParameters.AngleExtentDeg);
        Assert.Equal(0.9, command.GenerationParameters.ScaleMin);
        Assert.Equal(1.1, command.GenerationParameters.ScaleMax);
        Assert.Equal(0, command.GenerationParameters.NumLevels);

        Assert.Equal(1, store.CommitCount);
        HalconTemplateModelMetadata metadata = HalconTemplateModelMetadataJson.Deserialize(
            Assert.IsType<byte[]>(store.CommittedMetadata));
        Assert.Equal($"model-{store.Generation}.shm", metadata.ModelFileName);
        Assert.Equal(command.ModelDomain.CentroidRow, metadata.ModelDomainCentroidRow);
        Assert.Equal(command.ModelDomain.CentroidColumn, metadata.ModelDomainCentroidColumn);
        Assert.Equal(Sha256(store.LastStagingModelBytes), metadata.ModelChecksum);

        HalconTemplateModelState state = Assert.IsType<HalconTemplateModelState>(
            TemplateModelParameterCodec.ReadHalcon(result.Parameters));
        Assert.Equal(store.Generation, state.Reference.Generation);
        Assert.Equal(6, state.Geometry.StandardPose.X);
        Assert.Equal(6, state.Geometry.StandardPose.Y);
        Assert.Equal(0, state.Geometry.StandardPose.Angle);
        Assert.Equal(1, state.Geometry.StandardPose.Scale);
        Assert.Equal(8, state.Geometry.TemplateWidth);
        Assert.Equal(6, state.Geometry.TemplateHeight);
        Assert.Equal("Halcon", result.Parameters[TemplateMatchingParameterCatalog.Engine]);

        TemplateLearningPreview preview = Assert.IsType<TemplateLearningPreview>(result.Preview);
        Assert.Equal(new Point2D(6, 6), preview.Origin);
        Assert.Equal(features.OuterContour, preview.OuterContour);
        Assert.Equal(features.InnerFeatureGroups, preview.InnerFeatureGroups);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("readback")]
    public async Task CreateWriteOrReadbackFailureNeverReturnsNewModelParameters(string phase)
    {
        Directory.CreateDirectory(_root);
        var operators = new RecordingHalconOperatorBackend
        {
            CreateFailure = phase == "create" ? new HalconOperatorFailure(
                TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                "native create detail must stay technical") : null,
            WriteFailure = phase == "write" ? new HalconOperatorFailure(
                TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                "native write detail must stay technical") : null,
            LoadFailure = phase == "readback" ? new HalconOperatorFailure(
                TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                "corrupt model native detail") : null
        };
        var store = new RecordingTemplateModelStore(_root);
        var learner = CreateLearner(Features(), operators, store);

        TemplateLearningResult result = await learner.LearnAsync(ValidRequest(), default);

        Assert.False(result.Success);
        Assert.Empty(result.Parameters);
        Assert.Null(result.Preview);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelLoadFailed, result.Diagnostic?.Code);
        Assert.Equal(0, store.CommitCount);
        Assert.True(store.SessionDisposed);
        await AssertOldGenerationUnchangedAsync(store);
        Assert.False(File.Exists(store.LastStagingModelPath));
        Assert.DoesNotContain("native detail", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterSessionCreationRollsBackWithoutPublishingParameters()
    {
        Directory.CreateDirectory(_root);
        using var cancellation = new CancellationTokenSource();
        var operators = new RecordingHalconOperatorBackend
        {
            AfterCreate = cancellation.Cancel
        };
        var store = new RecordingTemplateModelStore(_root);
        var learner = CreateLearner(Features(), operators, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => learner.LearnAsync(ValidRequest(), cancellation.Token));

        Assert.Equal(0, store.CommitCount);
        Assert.True(store.SessionDisposed);
        await AssertOldGenerationUnchangedAsync(store);
        Assert.False(File.Exists(store.LastStagingModelPath));
    }

    [Fact]
    public async Task CommitFailureRollsBackStagingAndKeepsOldGenerationResolvable()
    {
        Directory.CreateDirectory(_root);
        var operators = new RecordingHalconOperatorBackend();
        var store = new RecordingTemplateModelStore(_root)
        {
            CommitFailure = new IOException("injected commit failure")
        };
        var learner = CreateLearner(Features(), operators, store);

        TemplateLearningResult result = await learner.LearnAsync(ValidRequest(), default);

        Assert.False(result.Success);
        Assert.Empty(result.Parameters);
        Assert.Equal(1, store.CommitCount);
        Assert.True(store.SessionDisposed);
        Assert.False(File.Exists(store.LastStagingModelPath));
        await AssertOldGenerationUnchangedAsync(store);
    }

    [Fact]
    public async Task DurableCommitRemainsSuccessfulWhenSessionCleanupThrows()
    {
        Directory.CreateDirectory(_root);
        var store = new RecordingTemplateModelStore(_root)
        {
            DisposeFailure = new IOException("injected post-commit cleanup failure")
        };
        var learner = CreateLearner(Features(), new RecordingHalconOperatorBackend(), store);

        TemplateLearningResult result = await learner.LearnAsync(ValidRequest(), default);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        Assert.NotNull(TemplateModelParameterCodec.ReadHalcon(result.Parameters));
        Assert.Equal(1, store.CommitCount);
        Assert.True(store.SessionDisposed);
    }

    [Fact]
    public async Task MalformedCommittedReferenceThrowsOnlyAfterSessionCleanup()
    {
        Directory.CreateDirectory(_root);
        var store = new RecordingTemplateModelStore(_root)
        {
            ReturnMalformedCommittedReference = true
        };
        var learner = CreateLearner(Features(), new RecordingHalconOperatorBackend(), store);

        await Assert.ThrowsAsync<TemplateMatchingConfigurationException>(
            () => learner.LearnAsync(ValidRequest(), default));

        Assert.Equal(1, store.CommitCount);
        Assert.True(store.SessionDisposed);
        Assert.Equal(1, store.SessionDisposeCount);
    }

    [Fact]
    public async Task CleanupFailureDoesNotMaskPrimaryLearningFailure()
    {
        Directory.CreateDirectory(_root);
        var operators = new RecordingHalconOperatorBackend
        {
            CreateFailure = new HalconOperatorFailure(
                TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                "primary create failure")
        };
        var store = new RecordingTemplateModelStore(_root)
        {
            DisposeFailure = new IOException("secondary cleanup failure")
        };
        var learner = CreateLearner(Features(), operators, store);

        TemplateLearningResult result = await learner.LearnAsync(ValidRequest(), default);

        Assert.False(result.Success);
        Assert.Empty(result.Parameters);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelLoadFailed, result.Diagnostic?.Code);
        Assert.Contains("IOException", result.Diagnostic?.TechnicalDetails);
        Assert.True(store.SessionDisposed);
    }

    [Fact]
    public async Task CleanupFailureDoesNotMaskPrimaryCancellation()
    {
        Directory.CreateDirectory(_root);
        using var cancellation = new CancellationTokenSource();
        var operators = new RecordingHalconOperatorBackend { AfterCreate = cancellation.Cancel };
        var store = new RecordingTemplateModelStore(_root)
        {
            DisposeFailure = new IOException("secondary cleanup failure")
        };
        var learner = CreateLearner(Features(), operators, store);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => learner.LearnAsync(ValidRequest(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("IOException", exception.Data["HalconSessionCleanupExceptionType"]);
        Assert.True(store.SessionDisposed);
    }

    [Fact]
    public async Task InvalidParametersAreRejectedBeforeRuntimeStoreOrOperatorCalls()
    {
        Directory.CreateDirectory(_root);
        var runtime = ReadyRuntimeProbe();
        var operators = new RecordingHalconOperatorBackend();
        var store = new RecordingTemplateModelStore(_root);
        var learner = new HalconTemplateLearner(
            runtime,
            new StubFeatureExtractor(Features()),
            store,
            new ImmediateHalconScheduler(),
            operators);
        TemplateLearningRequest valid = ValidRequest();
        var invalid = new TemplateLearningRequest(
            valid.Owner,
            valid.Frame,
            valid.TemplateRoi,
            valid.SearchRoi,
            new Dictionary<string, string>(valid.Parameters, StringComparer.OrdinalIgnoreCase)
            {
                [TemplateMatchingParameterCatalog.NumLevels] = "0"
            });

        TemplateLearningResult result = await learner.LearnAsync(invalid, default);

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal(0, runtime.CallCount);
        Assert.Equal(0, store.BeginCount);
        Assert.Equal(0, operators.CreateCount);
    }

    [Theory]
    [InlineData("stride")]
    [InlineData("format")]
    [InlineData("empty-roi")]
    [InlineData("outside-roi")]
    public async Task InvalidManagedInputIsRejectedBeforeRuntimeOrNativeWork(string invalidPart)
    {
        Directory.CreateDirectory(_root);
        TemplateLearningRequest valid = ValidRequest();
        ImageFrame frame = invalidPart switch
        {
            "stride" => valid.Frame with { Stride = 1 },
            "format" => valid.Frame with { Format = (PixelFormatKind)999 },
            _ => valid.Frame
        };
        RoiDefinition roi = invalidPart switch
        {
            "empty-roi" => valid.TemplateRoi with { Width = 0 },
            "outside-roi" => valid.TemplateRoi with { X = -1 },
            _ => valid.TemplateRoi
        };
        var request = new TemplateLearningRequest(
            valid.Owner,
            frame,
            roi,
            valid.SearchRoi,
            valid.Parameters);
        var runtime = ReadyRuntimeProbe();
        var extractor = new PreflightRecordingExtractor(Features());
        var store = new RecordingTemplateModelStore(_root);
        var scheduler = new ImmediateHalconScheduler();
        var operators = new RecordingHalconOperatorBackend();
        var learner = new HalconTemplateLearner(
            runtime,
            extractor,
            store,
            scheduler,
            operators);

        TemplateLearningResult result = await learner.LearnAsync(request, default);

        Assert.False(result.Success);
        Assert.Empty(result.Parameters);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal(1, extractor.PreflightCount);
        Assert.Equal(0, extractor.ExtractCount);
        Assert.Equal(0, runtime.CallCount);
        Assert.Equal(0, store.BeginCount);
        Assert.Equal(0, scheduler.RunCount);
        Assert.Equal(0, operators.CreateCount);
        Assert.Equal(0, operators.LoadCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HalconTemplateLearner CreateLearner(
        HalconTemplateFeatureSet features,
        RecordingHalconOperatorBackend operators,
        RecordingTemplateModelStore store)
    {
        return new HalconTemplateLearner(
            ReadyRuntimeProbe(),
            new StubFeatureExtractor(features),
            store,
            new ImmediateHalconScheduler(),
            operators);
    }

    private static RecordingRuntimeProbe ReadyRuntimeProbe()
    {
        return new RecordingRuntimeProbe(HalconRuntimeProbeResult.Ready(
            new HalconRuntimeDescriptor(
                "runtime",
                HalconRuntimeSource.DeviceConfiguration,
                "x64",
                HalconTemplateModelMetadata.CurrentManagedPackageVersion,
                HalconTemplateModelMetadata.CurrentManagedAssemblyVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion)));
    }

    private static TemplateLearningRequest ValidRequest()
    {
        const int width = 12;
        const int height = 10;
        const int stride = 15;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                pixels[row * stride + column] = (byte)(row * 12 + column);
            }

            for (var column = width; column < stride; column++)
            {
                pixels[row * stride + column] = 251;
            }
        }

        return new TemplateLearningRequest(
            new TemplateModelOwner("recipe", "flow", "tool"),
            new ImageFrame(
                "frame",
                width,
                height,
                stride,
                PixelFormatKind.Gray8,
                pixels,
                DateTimeOffset.UnixEpoch,
                "test"),
            new RoiDefinition
            {
                Id = "template",
                Shape = RoiShapeKind.Rectangle,
                X = 2,
                Y = 3,
                Width = 8,
                Height = 6
            },
            null,
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single));
    }

    private static HalconTemplateFeatureSet Features()
    {
        IReadOnlyList<Point2D> outer = Enumerable.Range(0, 100)
            .Select(index =>
            {
                double angle = 2 * Math.PI * index / 100;
                return new Point2D(2.5 * Math.Cos(angle), 2 * Math.Sin(angle));
            })
            .ToArray();
        IReadOnlyList<IReadOnlyList<Point2D>> inner =
        [
            [new Point2D(-1, -1)],
            [new Point2D(1, -1)],
            [new Point2D(0, 1)]
        ];
        var filled = new HalconFilledSupportRegion(
            -4,
            -3,
            [
                new HalconSupportRun(2, 2, 4),
                new HalconSupportRun(3, 2, 4)
            ]);
        var domain = new HalconModelDomain(
            8,
            6,
            [
                new HalconSupportRun(1, 1, 6),
                new HalconSupportRun(2, 1, 6),
                new HalconSupportRun(3, 1, 6),
                new HalconSupportRun(4, 1, 6)
            ]);
        return new HalconTemplateFeatureSet(
            8,
            6,
            2,
            3,
            3,
            4,
            true,
            outer,
            inner,
            3,
            filled,
            domain);
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task AssertOldGenerationUnchangedAsync(
        RecordingTemplateModelStore store)
    {
        ResolvedTemplateModel resolved = await store.ResolveAsync(
            store.OldOwner,
            store.OldReference,
            default);
        Assert.Equal(store.OldModelBytes, File.ReadAllBytes(resolved.ModelPath));
        Assert.Equal(store.OldMetadataBytes, resolved.MetadataJson.ToArray());
        Assert.Equal(Sha256(store.OldModelBytes), store.OldReference.ModelChecksum);
        Assert.Equal(Sha256(store.OldMetadataBytes), store.OldReference.MetadataChecksum);
    }

    private sealed class StubFeatureExtractor(HalconTemplateFeatureSet features) :
        IHalconTemplateFeatureExtractor
    {
        public HalconTemplateInputValidationResult ValidateInput(
            ImageFrame frame,
            RoiDefinition roi,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return HalconTemplateInputValidationResult.Valid;
        }

        public HalconTemplateFeatureExtractionResult Extract(
            ImageFrame frame,
            RoiDefinition roi,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return HalconTemplateFeatureExtractionResult.FromFeatures(features);
        }
    }

    private sealed class RecordingRuntimeProbe(HalconRuntimeProbeResult result) : IHalconRuntimeProbe
    {
        public int CallCount { get; private set; }

        public Task<HalconRuntimeProbeResult> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ImmediateHalconScheduler : IHalconOperationScheduler
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

    private sealed class PreflightRecordingExtractor(HalconTemplateFeatureSet features) :
        IHalconTemplateFeatureExtractor
    {
        private readonly HalconTemplateFeatureExtractor _preflight = new();

        public int PreflightCount { get; private set; }

        public int ExtractCount { get; private set; }

        public HalconTemplateInputValidationResult ValidateInput(
            ImageFrame frame,
            RoiDefinition roi,
            CancellationToken cancellationToken = default)
        {
            PreflightCount++;
            return _preflight.ValidateInput(frame, roi, cancellationToken);
        }

        public HalconTemplateFeatureExtractionResult Extract(
            ImageFrame frame,
            RoiDefinition roi,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractCount++;
            return HalconTemplateFeatureExtractionResult.FromFeatures(features);
        }
    }

    private sealed class RecordingHalconOperatorBackend : IHalconOperatorBackend
    {
        public HalconOperatorFailure? CreateFailure { get; init; }

        public HalconOperatorFailure? LoadFailure { get; init; }

        public HalconOperatorFailure? WriteFailure { get; init; }

        public Action? AfterCreate { get; init; }

        public int CreateCount { get; private set; }

        public int LoadCount { get; private set; }

        public HalconShapeModelCreationRequest? LastCreationRequest { get; private set; }

        public string? LastWritePath { get; private set; }

        public IHalconRawModelHandle? LastHandle { get; private set; }

        public void CreateAndWriteShapeModel(
            HalconShapeModelCreationRequest request,
            string stagingModelPath)
        {
            CreateCount++;
            LastCreationRequest = request;
            LastWritePath = stagingModelPath;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            File.WriteAllBytes(stagingModelPath, [1, 3, 5, 7, 9]);
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            AfterCreate?.Invoke();
        }

        public IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath)
        {
            LoadCount++;
            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }

            LastHandle = new FakeHandle();
            return LastHandle;
        }

        public void VerifyMatchingLicense()
        {
        }
    }

    private sealed class FakeHandle : IHalconRawModelHandle
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class RecordingTemplateModelStore : ITemplateModelStore
    {
        private readonly string _root;

        public RecordingTemplateModelStore(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            OldOwner = new TemplateModelOwner("recipe", "flow", "tool");
            OldModelBytes = [42, 43, 44, 45];
            OldMetadataBytes = [7, 8, 9, 10];
            OldModelPath = Path.Combine(root, "model-old-generation.shm");
            string oldMetadataPath = Path.Combine(root, "model-old-generation.json");
            File.WriteAllBytes(OldModelPath, OldModelBytes);
            File.WriteAllBytes(oldMetadataPath, OldMetadataBytes);
            OldReference = new TemplateModelReference(
                OldModelPath,
                oldMetadataPath,
                TemplateModelParameterCodec.HalconScaledShapeModelFormat,
                Sha256(OldModelBytes),
                Sha256(OldMetadataBytes),
                "old-generation",
                HalconTemplateModelMetadata.CurrentModelVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                new string('a', 64));
        }

        public string Generation { get; } = "generation-1";

        public TemplateModelOwner OldOwner { get; }

        public TemplateModelReference OldReference { get; }

        public string OldModelPath { get; }

        public byte[] OldModelBytes { get; }

        public byte[] OldMetadataBytes { get; }

        public Exception? CommitFailure { get; init; }

        public Exception? DisposeFailure { get; init; }

        public bool ReturnMalformedCommittedReference { get; init; }

        public int BeginCount { get; private set; }

        public int CommitCount { get; private set; }

        public bool SessionDisposed { get; private set; }

        public int SessionDisposeCount { get; private set; }

        public string? LastStagingModelPath { get; private set; }

        public byte[] LastStagingModelBytes { get; private set; } = [];

        public byte[]? CommittedMetadata { get; private set; }

        public Task<TemplateModelWriteSession> BeginWriteAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCount++;
            LastStagingModelPath = Path.Combine(_root, ".staging.shm");
            return Task.FromResult<TemplateModelWriteSession>(new Session(
                LastStagingModelPath,
                Generation,
                () =>
                {
                    SessionDisposed = true;
                    SessionDisposeCount++;
                },
                DisposeFailure));
        }

        public Task<TemplateModelReference> CommitAsync(
            TemplateModelWriteSession session,
            ReadOnlyMemory<byte> metadataJson,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            LastStagingModelBytes = File.ReadAllBytes(session.StagingModelPath);
            CommittedMetadata = metadataJson.ToArray();
            if (CommitFailure is not null)
            {
                throw CommitFailure;
            }

            HalconTemplateModelMetadata metadata = HalconTemplateModelMetadataJson.Deserialize(
                metadataJson.Span);
            string metadataChecksum = Convert.ToHexString(SHA256.HashData(metadataJson.Span))
                .ToLowerInvariant();
            var reference = new TemplateModelReference(
                $"recipe/flow/tool/model-{Generation}.shm",
                $"recipe/flow/tool/model-{Generation}.json",
                metadata.ModelFormat,
                metadata.ModelChecksum,
                metadataChecksum,
                Generation,
                metadata.ModelVersion,
                metadata.NativeRuntimeVersion,
                metadata.GenerationParameterFingerprint);
            if (ReturnMalformedCommittedReference)
            {
                reference = reference with { ModelChecksum = "malformed" };
            }

            return Task.FromResult(reference);
        }

        public Task<ResolvedTemplateModel> ResolveAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner != OldOwner || reference != OldReference)
            {
                throw new InvalidOperationException("Only the pre-existing generation is resolvable in this fake store.");
            }

            byte[] model = File.ReadAllBytes(OldReference.ModelPath);
            byte[] metadata = File.ReadAllBytes(OldReference.MetadataPath);
            if (!string.Equals(Sha256(model), OldReference.ModelChecksum, StringComparison.Ordinal) ||
                !string.Equals(Sha256(metadata), OldReference.MetadataChecksum, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The pre-existing fake generation changed.");
            }

            return Task.FromResult(new ResolvedTemplateModel(OldReference.ModelPath, metadata));
        }

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

        private sealed class Session(
            string stagingModelPath,
            string generation,
            Action onDispose,
            Exception? disposeFailure) : TemplateModelWriteSession
        {
            public override string StagingModelPath { get; } = stagingModelPath;

            public override string Generation { get; } = generation;

            public override ValueTask DisposeAsync()
            {
                onDispose();
                if (File.Exists(StagingModelPath))
                {
                    File.Delete(StagingModelPath);
                }

                if (disposeFailure is not null)
                {
                    throw disposeFailure;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
