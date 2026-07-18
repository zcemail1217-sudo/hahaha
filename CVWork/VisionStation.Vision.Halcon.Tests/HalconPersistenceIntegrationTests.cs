using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class HalconPersistenceIntegrationTests
{
    private const string ModelVersionParameter = "halcon.modelVersion";
    private const string RuntimeVersionParameter = "halcon.modelRuntimeVersion";
    private const string ModelChecksumParameter = "halcon.modelChecksum";
    private const string MetadataChecksumParameter = "halcon.metadataChecksum";

    private static readonly TemplateModelOwner Owner =
        new("persistence-recipe", "main-flow", "locate-product");

    private static readonly Pose2D[] ThreeTargetPoses =
    [
        new Pose2D(70, 112, 0) { Scale = 0.90 },
        new Pose2D(285, 140, 35) { Scale = 1.00 },
        new Pose2D(205, 340, 90) { Scale = 1.10 }
    ];

    private static readonly string[] MultiOperationalPorts =
    [
        "PositionOutput",
        "OriginOutput",
        "BestPositionOutput",
        "ScoreOutput",
        "XOutput",
        "YOutput",
        "AngleOutput",
        "AllPositionsOutput",
        "ScoresOutput",
        "ScalesOutput"
    ];

    [HalconIntegrationFact]
    public async Task MultiTarget_RequiresExactCountAndPreservesEveryDistinctPose()
    {
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            RuntimePaths paths = new(workingDirectory);
            LearnedModel learned = await LearnAndUnloadAsync(paths);
            TemplateMatchingRuntime runtime = CreateRuntime(paths);
            try
            {
                ImageFrame threeTargets =
                    SyntheticHalconProductFactory.CreateMultiTargetFrame(ThreeTargetPoses);
                TemplateMatchBatchResult exact = await MatchExactCountAsync(
                    runtime.Service,
                    learned.Parameters,
                    threeTargets,
                    expectedCount: ThreeTargetPoses.Length);

                Assert.True(exact.HasMatch, DescribeMatchFailure(exact));
                Assert.Equal(InspectionOutcome.Ok, exact.Outcome);
                Assert.Null(exact.Diagnostic);
                Assert.Equal(ThreeTargetPoses.Length, exact.Matches.Count);
                AssertMatchesExpectedPoses(exact.Matches, ThreeTargetPoses);

                TemplateMatchBatchResult tooMany = await MatchExactCountAsync(
                    runtime.Service,
                    learned.Parameters,
                    threeTargets,
                    expectedCount: ThreeTargetPoses.Length - 1);
                AssertCountMismatch(tooMany, expectedAcceptedCount: ThreeTargetPoses.Length);

                TemplateMatchBatchResult tooFew = await MatchExactCountAsync(
                    runtime.Service,
                    learned.Parameters,
                    threeTargets,
                    expectedCount: ThreeTargetPoses.Length + 1);
                AssertCountMismatch(tooFew, expectedAcceptedCount: ThreeTargetPoses.Length);

                await AssertRealCountMismatchPublishesNoOperationalPortsAsync(
                    runtime.Service,
                    learned.Parameters,
                    threeTargets);

                Pose2D[] adjacentPoses =
                [
                    new Pose2D(105, 210, 90) { Scale = 0.90 },
                    new Pose2D(315, 210, 90) { Scale = 0.90 }
                ];
                TemplateMatchBatchResult adjacent = await MatchExactCountAsync(
                    runtime.Service,
                    learned.Parameters,
                    SyntheticHalconProductFactory.CreateMultiTargetFrame(adjacentPoses),
                    expectedCount: adjacentPoses.Length);

                Assert.True(adjacent.HasMatch, DescribeMatchFailure(adjacent));
                Assert.Equal(adjacentPoses.Length, adjacent.Matches.Count);
                AssertMatchesExpectedPoses(adjacent.Matches, adjacentPoses);
            }
            finally
            {
                await runtime.Service.DisposeAsync();
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    [HalconIntegrationFact]
    public async Task OverlappingNativeHypotheses_AreRemovedBySupportIouBeforeExactCount()
    {
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            RuntimePaths paths = new(workingDirectory);
            LearnedModel learned = await LearnAndUnloadAsync(paths);
            TemplateMatchingRuntime runtime = CreateRuntime(paths);
            try
            {
                var parameters = new Dictionary<string, string>(
                    learned.Parameters,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [TemplateMatchingParameterCatalog.ExpectedCount] = "2",
                    [TemplateMatchingParameterCatalog.CandidateMinScore] = "0.55",
                    [TemplateMatchingParameterCatalog.OuterCoverageMin] = "0",
                    [TemplateMatchingParameterCatalog.InnerCoverageMin] = "0",
                    [TemplateMatchingParameterCatalog.EdgeTolerancePx] = "10",
                    [TemplateMatchingParameterCatalog.PolarityAgreementMin] = "0",
                    [TemplateMatchingParameterCatalog.CandidateMaxOverlap] = "0.99",
                    [TemplateMatchingParameterCatalog.MaxOverlap] = "0.01",
                    [TemplateMatchingParameterCatalog.Greediness] = "0.50",
                    [TemplateMatchingParameterCatalog.CandidateLimit] = "64"
                };

                // The two real native detections have a small filled-support intersection. HALCON
                // is deliberately allowed to return both; the stricter managed support-IoU gate
                // must reject the lower-priority hypothesis before ExactCount is evaluated.
                TemplateMatchBatchResult result = await runtime.Service.MatchAsync(
                    new TemplateMatchingRequest(
                        Owner,
                        SyntheticHalconProductFactory.CreateMultiTargetFrame(
                            new Pose2D(210, 125, 0) { Scale = 1 },
                            new Pose2D(210, 285, 0) { Scale = 1 }),
                        SearchRoi: null,
                        parameters,
                        TemplateMatchCardinality.ExactCount,
                        ExpectedCount: 2),
                    CancellationToken.None);

                Assert.False(result.HasMatch, DescribeMatchFailure(result));
                Assert.Equal(InspectionOutcome.Ng, result.Outcome);
                Assert.True(
                    string.Equals(
                        TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap,
                        result.Diagnostic?.Code,
                        StringComparison.Ordinal),
                    DescribeMatchFailure(result));
                Assert.Single(result.Matches);
                Assert.Contains(
                    "measured=iou=",
                    result.Diagnostic!.TechnicalDetails,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "conflictingAcceptedSourceIndex=",
                    result.Diagnostic.TechnicalDetails,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "threshold=MaxOverlap=0.01",
                    result.Diagnostic.TechnicalDetails,
                    StringComparison.Ordinal);
            }
            finally
            {
                await runtime.Service.DisposeAsync();
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    [HalconIntegrationFact]
    public async Task Persistence_DisposeAndFreshRuntimeReloadProduceEquivalentMatch()
    {
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            RuntimePaths paths = new(workingDirectory);
            ImageFrame frame = SyntheticHalconProductFactory.CreateMatchFrame(35, 1.10);
            TemplateMatchingRuntime learningRuntime = CreateRuntime(paths);
            TemplateMatchBatchResult beforeReload;
            LearnedModel learned;
            try
            {
                learned = await LearnAsync(learningRuntime.Service, paths);
                beforeReload = await MatchSingleAsync(
                    learningRuntime.Service,
                    learned.Parameters,
                    frame);
                AssertSuccessfulSingle(beforeReload);
            }
            finally
            {
                // This is the public cache-clear boundary: it retires native handles and the
                // scheduler before the fresh composition below reads the persisted generation.
                await learningRuntime.Service.DisposeAsync();
            }

            Assert.True(File.Exists(ToFullModelPath(paths, learned.State.Reference.ModelPath)));
            Assert.True(File.Exists(ToFullModelPath(paths, learned.State.Reference.MetadataPath)));

            TemplateMatchingRuntime reloadedRuntime = CreateRuntime(paths);
            try
            {
                TemplateMatchBatchResult afterReload = await MatchSingleAsync(
                    reloadedRuntime.Service,
                    learned.Parameters,
                    frame);
                AssertSuccessfulSingle(afterReload);
                AssertEquivalentMatches(beforeReload, afterReload);
            }
            finally
            {
                await reloadedRuntime.Service.DisposeAsync();
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    [HalconIntegrationFact]
    public async Task Persistence_AllIntegrityAndCompatibilityTamperingFailsClosedWithStableCode()
    {
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            RuntimePaths pristinePaths = new(workingDirectory);
            LearnedModel learned = await LearnAndUnloadAsync(pristinePaths);
            CorruptionExpectation[] expectations =
            [
                new(
                    CorruptionKind.ModelPayload,
                    TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.MetadataPayload,
                    TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.ModelReferenceChecksum,
                    TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.MetadataReferenceChecksum,
                    TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.MetadataOwner,
                    TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.ModelVersion,
                    TemplateMatchingDiagnosticCodes.ModelVersionMismatch,
                    TemplateMatchingFailureStages.Model),
                new(
                    CorruptionKind.RuntimeVersion,
                    TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                    TemplateMatchingFailureStages.Runtime)
            ];

            foreach (CorruptionExpectation expectation in expectations)
            {
                string scenarioDirectory = Path.Combine(
                    workingDirectory,
                    $"corruption-{expectation.Kind}");
                RuntimePaths scenarioPaths = new(scenarioDirectory);
                CopyDirectory(
                    pristinePaths.TemplateResourceDirectory,
                    scenarioPaths.TemplateResourceDirectory);
                var parameters = new Dictionary<string, string>(
                    learned.Parameters,
                    StringComparer.OrdinalIgnoreCase);
                await ApplyCorruptionAsync(expectation.Kind, scenarioPaths, parameters);

                TemplateMatchingRuntime runtime = CreateRuntime(scenarioPaths);
                try
                {
                    TemplateMatchBatchResult result = await MatchSingleAsync(
                        runtime.Service,
                        parameters,
                        SyntheticHalconProductFactory.CreateMatchFrame(0, 1));
                    AssertFailedWithDiagnostic(
                        result,
                        expectation.Code,
                        expectation.Stage,
                        expectation.Kind);
                }
                finally
                {
                    await runtime.Service.DisposeAsync();
                }
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static async Task<LearnedModel> LearnAndUnloadAsync(RuntimePaths paths)
    {
        TemplateMatchingRuntime runtime = CreateRuntime(paths);
        try
        {
            return await LearnAsync(runtime.Service, paths);
        }
        finally
        {
            await runtime.Service.DisposeAsync();
        }
    }

    private static async Task<LearnedModel> LearnAsync(
        ITemplateMatchingService service,
        RuntimePaths paths)
    {
        Dictionary<string, string> parameters =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        TemplateLearningResult result = await service.LearnAsync(
            new TemplateLearningRequest(
                Owner,
                SyntheticHalconProductFactory.CreateLearningFrame(),
                SyntheticHalconProductFactory.CreateTemplateRoi(),
                SearchRoi: null,
                parameters),
            CancellationToken.None);

        Assert.True(result.Success, DescribeLearningFailure(result));
        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Null(result.Diagnostic);
        HalconTemplateModelState? state = TemplateModelParameterCodec.ReadHalcon(result.Parameters);
        Assert.NotNull(state);
        Assert.True(
            File.Exists(ToFullModelPath(paths, state!.Reference.ModelPath)),
            $"Expected persisted HALCON model '{state.Reference.ModelPath}'.");
        Assert.True(
            File.Exists(ToFullModelPath(paths, state.Reference.MetadataPath)),
            $"Expected persisted HALCON metadata '{state.Reference.MetadataPath}'.");
        return new LearnedModel(
            new Dictionary<string, string>(result.Parameters, StringComparer.OrdinalIgnoreCase),
            state);
    }

    private static TemplateMatchingRuntime CreateRuntime(RuntimePaths paths)
    {
        return HalconTemplateMatchingFactory.Create(
            new FileTemplateModelStore(paths),
            SyntheticHalconProductFactory.CreateRuntimeConfiguration(),
            IntegrationDiagnosticSink.Instance);
    }

    private static Task<TemplateMatchBatchResult> MatchSingleAsync(
        ITemplateMatchingService service,
        IReadOnlyDictionary<string, string> parameters,
        ImageFrame frame)
    {
        return service.MatchAsync(
            new TemplateMatchingRequest(
                Owner,
                frame,
                SearchRoi: null,
                parameters,
                TemplateMatchCardinality.Single,
                ExpectedCount: 1),
            CancellationToken.None);
    }

    private static Task<TemplateMatchBatchResult> MatchExactCountAsync(
        ITemplateMatchingService service,
        IReadOnlyDictionary<string, string> learnedParameters,
        ImageFrame frame,
        int expectedCount)
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(
            TemplateMatchCardinality.ExactCount);
        HalconTemplateModelState? state = TemplateModelParameterCodec.ReadHalcon(learnedParameters);
        Assert.NotNull(state);
        TemplateModelParameterCodec.WriteHalcon(parameters, state!);
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = expectedCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        return service.MatchAsync(
            new TemplateMatchingRequest(
                Owner,
                frame,
                SearchRoi: null,
                parameters,
                TemplateMatchCardinality.ExactCount,
                expectedCount),
            CancellationToken.None);
    }

    private static async Task AssertRealCountMismatchPublishesNoOperationalPortsAsync(
        ITemplateMatchingService service,
        IReadOnlyDictionary<string, string> learnedParameters,
        ImageFrame frame)
    {
        var source = new VisionToolDefinition
        {
            Id = "image-source",
            Kind = VisionToolKind.AcquireImage
        };
        var parameters = new Dictionary<string, string>(
            learnedParameters,
            StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.ExpectedCount] = "2",
            ["input:ImageInput:toolId"] = source.Id,
            ["input:ImageInput:portKey"] = "ImageOutput"
        };
        var matchingTool = new VisionToolDefinition
        {
            Id = Owner.ToolId,
            Kind = VisionToolKind.MultiTargetMatch,
            Parameters = parameters
        };
        var consumerParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string port in MultiOperationalPorts.Append("CountOutput"))
        {
            consumerParameters[$"input:{port}Input:toolId"] = matchingTool.Id;
            consumerParameters[$"input:{port}Input:portKey"] = port;
        }

        var consumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = consumerParameters
        };
        var flow = new VisionFlowDefinition
        {
            Id = Owner.FlowId,
            Tools = [source, matchingTool, consumer]
        };
        var recipe = new Recipe
        {
            Id = Owner.RecipeId,
            CurrentFlowId = flow.Id,
            Flows = [flow]
        };
        using var context = new VisionToolContext(recipe, frame);
        context.SetImageOutput(source, frame);
        SeedStaleOperationalPorts(context, matchingTool);

        ToolResult result = await new MultiTargetMatchTool(service).ExecuteAsync(
            matchingTool,
            context,
            CancellationToken.None);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchCountMismatch, result.Data["failureCode"]);
        Assert.False(context.Properties.ContainsKey("pose"));
        foreach (string port in MultiOperationalPorts)
        {
            Assert.False(
                context.TryGetPortInputValue(consumer, $"{port}Input", out _),
                $"Real HALCON count mismatch published stale operational port '{port}'.");
        }

        Assert.True(context.TryGetPortInput<int>(consumer, "CountOutputInput", out int count));
        Assert.Equal(ThreeTargetPoses.Length, count);
    }

    private static void SeedStaleOperationalPorts(
        VisionToolContext context,
        VisionToolDefinition definition)
    {
        var pose = new Pose2D(1, 2, 3) { Scale = 4 };
        context.Properties["pose"] = pose;
        context.SetPortOutput(definition, "PositionOutput", pose);
        context.SetPortOutput(definition, "OriginOutput", pose);
        context.SetPortOutput(definition, "BestPositionOutput", pose);
        context.SetPortOutput(definition, "ScoreOutput", 0.99d);
        context.SetPortOutput(definition, "XOutput", pose.X);
        context.SetPortOutput(definition, "YOutput", pose.Y);
        context.SetPortOutput(definition, "AngleOutput", pose.Angle);
        context.SetPortOutput(definition, "AllPositionsOutput", new[] { pose });
        context.SetPortOutput(definition, "ScoresOutput", new[] { 0.99d });
        context.SetPortOutput(definition, "ScalesOutput", new[] { pose.Scale });
    }

    private static async Task ApplyCorruptionAsync(
        CorruptionKind kind,
        RuntimePaths paths,
        Dictionary<string, string> parameters)
    {
        HalconTemplateModelState state = TemplateModelParameterCodec.ReadHalcon(parameters)
                                         ?? throw new InvalidOperationException(
                                             "A learned HALCON state is required for corruption tests.");
        switch (kind)
        {
            case CorruptionKind.ModelPayload:
                await using (FileStream stream = new(
                                 ToFullModelPath(paths, state.Reference.ModelPath),
                                 FileMode.Append,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 1,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(new byte[] { 0xA5 });
                    await stream.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }

                return;

            case CorruptionKind.MetadataPayload:
                await File.AppendAllTextAsync(
                    ToFullModelPath(paths, state.Reference.MetadataPath),
                    " ");
                return;

            case CorruptionKind.ModelReferenceChecksum:
                WriteReference(
                    parameters,
                    state,
                    state.Reference with { ModelChecksum = DifferentChecksum(state.Reference.ModelChecksum) });
                return;

            case CorruptionKind.MetadataReferenceChecksum:
                WriteReference(
                    parameters,
                    state,
                    state.Reference with
                    {
                        MetadataChecksum = DifferentChecksum(state.Reference.MetadataChecksum)
                    });
                return;

            case CorruptionKind.MetadataOwner:
                string metadataPath = ToFullModelPath(paths, state.Reference.MetadataPath);
                JsonNode root = JsonNode.Parse(await File.ReadAllBytesAsync(metadataPath))
                                ?? throw new InvalidOperationException(
                                    "Persisted HALCON metadata must be a JSON value.");
                root["owner"] = new JsonObject
                {
                    ["recipeId"] = "another-recipe",
                    ["flowId"] = Owner.FlowId,
                    ["toolId"] = Owner.ToolId
                };
                byte[] changedMetadata = JsonSerializer.SerializeToUtf8Bytes(root);
                await File.WriteAllBytesAsync(metadataPath, changedMetadata);
                WriteReference(
                    parameters,
                    state,
                    state.Reference with { MetadataChecksum = Hash(changedMetadata) });
                return;

            case CorruptionKind.ModelVersion:
                WriteReference(
                    parameters,
                    state,
                    state.Reference with
                    {
                        ModelVersion = state.Reference.ModelVersion + "-corrupt"
                    });
                Assert.EndsWith(
                    "-corrupt",
                    parameters[ModelVersionParameter],
                    StringComparison.Ordinal);
                return;

            case CorruptionKind.RuntimeVersion:
                string changedRuntimeVersion = state.Reference.RuntimeVersion + "-corrupt";
                string runtimeMetadataPath = ToFullModelPath(
                    paths,
                    state.Reference.MetadataPath);
                JsonNode runtimeMetadata = JsonNode.Parse(
                                                   await File.ReadAllBytesAsync(runtimeMetadataPath))
                                               ?? throw new InvalidOperationException(
                                                   "Persisted HALCON metadata must be a JSON value.");
                runtimeMetadata["nativeRuntimeVersion"] = changedRuntimeVersion;
                byte[] changedRuntimeMetadata = JsonSerializer.SerializeToUtf8Bytes(runtimeMetadata);
                await File.WriteAllBytesAsync(runtimeMetadataPath, changedRuntimeMetadata);
                WriteReference(
                    parameters,
                    state,
                    state.Reference with
                    {
                        RuntimeVersion = changedRuntimeVersion,
                        MetadataChecksum = Hash(changedRuntimeMetadata)
                    });
                Assert.EndsWith(
                    "-corrupt",
                    parameters[RuntimeVersionParameter],
                    StringComparison.Ordinal);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void WriteReference(
        IDictionary<string, string> parameters,
        HalconTemplateModelState state,
        TemplateModelReference reference)
    {
        TemplateModelParameterCodec.WriteHalcon(parameters, state with { Reference = reference });
        Assert.Equal(reference.ModelChecksum, parameters[ModelChecksumParameter]);
        Assert.Equal(reference.MetadataChecksum, parameters[MetadataChecksumParameter]);
    }

    private static void AssertCountMismatch(
        TemplateMatchBatchResult result,
        int expectedAcceptedCount)
    {
        Assert.False(result.HasMatch, DescribeMatchFailure(result));
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(expectedAcceptedCount, result.Matches.Count);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchCountMismatch, result.Diagnostic!.Code);
        Assert.Equal(TemplateMatchingFailureStages.Match, result.Diagnostic.FailureStage);
    }

    private static void AssertMatchesExpectedPoses(
        IReadOnlyList<TemplateMatchBatchCandidate> actual,
        IReadOnlyList<Pose2D> expected)
    {
        var remaining = actual.ToList();
        foreach (Pose2D expectedPose in expected)
        {
            TemplateMatchBatchCandidate closest = remaining
                .OrderBy(candidate => SquaredDistance(candidate.Pose, expectedPose))
                .First();
            remaining.Remove(closest);

            double centerError = Math.Sqrt(SquaredDistance(closest.Pose, expectedPose));
            Assert.True(
                centerError <= 2,
                $"Center error {centerError:F4}px exceeded 2px for expected pose {expectedPose}.");
            double angleError = Math.Abs(NormalizeAngle(closest.Pose.Angle - expectedPose.Angle));
            Assert.True(
                angleError <= 1,
                $"Angle error {angleError:F4}° exceeded 1° for expected pose {expectedPose}.");
            double scaleError = Math.Abs(closest.Pose.Scale - expectedPose.Scale);
            Assert.True(
                scaleError <= 0.02,
                $"Scale error {scaleError:F6} exceeded 0.02 for expected pose {expectedPose}.");
        }

        Assert.Empty(remaining);
    }

    private static void AssertSuccessfulSingle(TemplateMatchBatchResult result)
    {
        Assert.True(result.HasMatch, DescribeMatchFailure(result));
        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Null(result.Diagnostic);
        Assert.Single(result.Matches);
    }

    private static void AssertEquivalentMatches(
        TemplateMatchBatchResult beforeReload,
        TemplateMatchBatchResult afterReload)
    {
        TemplateMatchBatchCandidate before = Assert.Single(beforeReload.Matches);
        TemplateMatchBatchCandidate after = Assert.Single(afterReload.Matches);
        Assert.Equal(before.Pose.X, after.Pose.X, precision: 6);
        Assert.Equal(before.Pose.Y, after.Pose.Y, precision: 6);
        Assert.Equal(before.Pose.Angle, after.Pose.Angle, precision: 6);
        Assert.Equal(before.Pose.Scale, after.Pose.Scale, precision: 6);
        Assert.Equal(before.Score, after.Score, precision: 6);
        Assert.Equal(before.TemplateWidth, after.TemplateWidth);
        Assert.Equal(before.TemplateHeight, after.TemplateHeight);
    }

    private static void AssertFailedWithDiagnostic(
        TemplateMatchBatchResult result,
        string expectedCode,
        string expectedStage,
        CorruptionKind kind)
    {
        string details = $"Corruption={kind}; {DescribeMatchFailure(result)}";
        Assert.False(result.HasMatch, details);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(expectedCode, result.Diagnostic!.Code);
        Assert.Equal(expectedStage, result.Diagnostic.FailureStage);
    }

    private static string ToFullModelPath(RuntimePaths paths, string relativePath)
    {
        return Path.Combine(
            paths.TemplateResourceDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (string directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (string file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string destination = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static string DifferentChecksum(string checksum)
    {
        char replacement = checksum[0] == 'a' ? 'b' : 'a';
        return replacement + checksum[1..];
    }

    private static string Hash(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static double SquaredDistance(Pose2D actual, Pose2D expected)
    {
        return Math.Pow(actual.X - expected.X, 2) + Math.Pow(actual.Y - expected.Y, 2);
    }

    private static double NormalizeAngle(double angleDeg)
    {
        double normalized = (angleDeg + 180) % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized - 180;
    }

    private static string DescribeLearningFailure(TemplateLearningResult result)
    {
        return $"Learning failed. Engine={result.Engine}; Message={result.Message}; " +
               $"Code={result.Diagnostic?.Code ?? "<none>"}; " +
               $"Stage={result.Diagnostic?.FailureStage ?? "<none>"}; " +
               $"Technical={result.Diagnostic?.TechnicalDetails ?? "<none>"}.";
    }

    private static string DescribeMatchFailure(TemplateMatchBatchResult result)
    {
        return $"Matching failed. Engine={result.Engine}; Outcome={result.Outcome}; " +
               $"Message={result.Message}; Code={result.Diagnostic?.Code ?? "<none>"}; " +
               $"Stage={result.Diagnostic?.FailureStage ?? "<none>"}; " +
               $"Technical={result.Diagnostic?.TechnicalDetails ?? "<none>"}.";
    }

    private sealed record LearnedModel(
        IReadOnlyDictionary<string, string> Parameters,
        HalconTemplateModelState State);

    private sealed record CorruptionExpectation(
        CorruptionKind Kind,
        string Code,
        string Stage);

    private enum CorruptionKind
    {
        ModelPayload,
        MetadataPayload,
        ModelReferenceChecksum,
        MetadataReferenceChecksum,
        MetadataOwner,
        ModelVersion,
        RuntimeVersion
    }

    private sealed class IntegrationDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        public static IntegrationDiagnosticSink Instance { get; } = new();

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }
    }
}
