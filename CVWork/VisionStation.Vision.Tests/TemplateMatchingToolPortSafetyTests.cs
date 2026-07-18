using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateMatchingToolPortSafetyTests
{
    private static readonly string[] SingleOperationalPorts =
    [
        "PositionOutput",
        "OriginOutput",
        "ScoreOutput",
        "XOutput",
        "YOutput",
        "AngleOutput",
        "ScaleOutput"
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

    [Fact]
    public async Task NgCandidateClearsLegacyPoseAndPublishesNoOperationalPorts()
    {
        var candidate = Candidate(new Pose2D(50, 60, 10) { Scale = 1.1 });
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.OpenCv, InspectionOutcome.Ng, true, [candidate])));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, new Dictionary<string, string>
        {
            ["engine"] = "OpenCv"
        });
        using var context = fixture.Context;
        fixture.SeedSingleOutputs();

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.False(context.Properties.ContainsKey("pose"));
        AssertNoPorts(fixture, SingleOperationalPorts);
        Assert.Equal("1.1", result.Data["rejectedCandidate.scale"]);
        Assert.Equal("OpenCv", result.Data["engine"]);
        var request = Assert.Single(service.MatchRequests);
        Assert.Equal(new TemplateModelOwner("recipe-id", "flow-id", "template-tool"), request.Owner);
        Assert.Equal((TemplateMatchCardinality.Single, 1), (request.Cardinality, request.ExpectedCount));
    }

    [Fact]
    public async Task SingleOkPublishesScaleFromProjectedPose()
    {
        var candidate = Candidate(new Pose2D(12, 34, 56) { Scale = 1.1 });
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ok, true, [candidate])));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, new Dictionary<string, string>
        {
            ["engine"] = "halcon"
        });
        using var context = fixture.Context;

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.True(context.TryGetPortInput<Pose2D>(fixture.Consumer, InputOf("PositionOutput"), out var pose));
        Assert.True(context.TryGetPortInput<double>(fixture.Consumer, InputOf("ScaleOutput"), out var scale));
        Assert.Equal(1.1, pose.Scale, 12);
        Assert.Equal(pose.Scale, scale, 12);
        Assert.Equal("1.1", result.Data["scale"]);
        Assert.Equal("Halcon", result.Data["engine"]);
        Assert.Equal(0, service.DisposeCount);
    }

    [Fact]
    public async Task RealOpenCvLowScoreCandidateIsDiagnosticOnly()
    {
        var frame = TemplateMatcherTestData.CreateSearchFrame(fragmentOnly: true);
        var parameters = TemplateMatcherTestData.LearnRuntimeParameters();
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var fixture = ToolFixture.Create(
            VisionToolKind.TemplateLocate,
            parameters,
            frame: frame);
        using var context = fixture.Context;
        fixture.SeedSingleOutputs();

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("True", result.Data["hasMatch"]);
        Assert.True(result.Data.ContainsKey("rejectedCandidate.score"));
        Assert.True(result.Data.ContainsKey("rejectedCandidate.scale"));
        Assert.False(result.Data.ContainsKey("x"));
        Assert.False(result.Data.ContainsKey("scale"));
        AssertNoPorts(fixture, SingleOperationalPorts);
        Assert.False(context.Properties.ContainsKey("pose"));
    }

    [Fact]
    public async Task HalconSingleOkSerializesValidationMetricsInvariantly()
    {
        var candidate = Candidate(new Pose2D(12, 34, 56) { Scale = 0.95 }) with
        {
            OuterCoverage = 0.93,
            InnerCoverage = 0.86,
            EdgeDistanceP95Px = 2.2,
            PolarityAgreement = 0.94
        };
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ok, true, [candidate])));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, new Dictionary<string, string>
        {
            ["engine"] = "halcon"
        });
        using var context = fixture.Context;

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal("0.93", result.Data["outerCoverage"]);
        Assert.Equal("0.86", result.Data["innerCoverage"]);
        Assert.Equal("2.2", result.Data["edgeDistanceP95Px"]);
        Assert.Equal("0.94", result.Data["polarityAgreement"]);
    }

    [Fact]
    public async Task HalconSingleWithoutCandidatePublishesOnlyFailureDiagnostics()
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
            "no-candidate");
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ng, false, [], diagnostic)));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, new Dictionary<string, string>
        {
            ["engine"] = "Halcon"
        });
        using var context = fixture.Context;

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(diagnostic.Code, result.Data["failureCode"]);
        Assert.Equal(diagnostic.FailureStage, result.Data["failureStage"]);
        Assert.DoesNotContain("x", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("y", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("angle", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("scale", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Data.Keys, key => key.StartsWith("rejectedCandidate.", StringComparison.OrdinalIgnoreCase));
        AssertNoPorts(fixture, SingleOperationalPorts);
    }

    [Theory]
    [InlineData(null, TemplateMatchingEngine.OpenCv, "OpenCv")]
    [InlineData("halcon", TemplateMatchingEngine.Halcon, "Halcon")]
    [InlineData("future-engine", TemplateMatchingEngine.Unknown, "Unknown")]
    public async Task EngineDataComesOnlyFromNormalizedBatchEngine(
        string? configuredEngine,
        TemplateMatchingEngine batchEngine,
        string expected)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (configuredEngine is not null)
        {
            parameters["engine"] = configuredEngine;
        }

        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(batchEngine, InspectionOutcome.Ng, false, [])));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, parameters);
        using var context = fixture.Context;

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(expected, result.Data["engine"]);
    }

    [Theory]
    [InlineData(null, TemplateMatchingEngine.OpenCv, "OpenCv")]
    [InlineData("halcon", TemplateMatchingEngine.Halcon, "Halcon")]
    [InlineData("future-engine", TemplateMatchingEngine.Unknown, "Unknown")]
    public async Task MultiEngineDataAlsoComesOnlyFromNormalizedBatchEngine(
        string? configuredEngine,
        TemplateMatchingEngine batchEngine,
        string expected)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["expectedCount"] = "1"
        };
        if (configuredEngine is not null)
        {
            parameters["engine"] = configuredEngine;
        }

        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(batchEngine, InspectionOutcome.Ng, false, [])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, parameters);
        using var context = fixture.Context;

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(expected, result.Data["engine"]);
    }

    [Fact]
    public async Task MissingImageClearsOldSinglePoseAndDoesNotCallService()
    {
        var service = new RecordingMatchingService(_ => throw new InvalidOperationException("Must not run."));
        var fixture = ToolFixture.Create(VisionToolKind.TemplateLocate, connectImage: false);
        using var context = fixture.Context;
        fixture.SeedSingleOutputs();

        var result = await new TemplateLocateTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.False(context.Properties.ContainsKey("pose"));
        AssertNoPorts(fixture, SingleOperationalPorts);
        Assert.Empty(service.MatchRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MultiCountMismatchKeepsCountAndDiagnosticsButPublishesNoOperationalPorts(int actualCount)
    {
        var candidates = Enumerable.Range(0, actualCount)
            .Select(index => Candidate(new Pose2D(20 + index * 10, 30, index) { Scale = 1 + index * 0.01 }))
            .ToArray();
        var diagnostic = TemplateMatchingDiagnostics.Create(
            "MATCH_COUNT_MISMATCH",
            "count-mismatch");
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ng, false, candidates, diagnostic)));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["expectedCount"] = "2"
        });
        using var context = fixture.Context;
        fixture.SeedMultiOutputs();

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.False(context.Properties.ContainsKey("pose"));
        AssertNoPorts(fixture, MultiOperationalPorts);
        Assert.True(context.TryGetPortInput<int>(fixture.Consumer, InputOf("CountOutput"), out var count));
        Assert.Equal(actualCount, count);
        Assert.Equal(actualCount.ToString(), result.Data["count"]);
        Assert.Equal(diagnostic.Code, result.Data["failureCode"]);
        Assert.False(result.Data.ContainsKey("technicalDetails"));
        Assert.False(string.IsNullOrWhiteSpace(result.Data["matches"]));
        var request = Assert.Single(service.MatchRequests);
        Assert.Equal((TemplateMatchCardinality.ExactCount, 2), (request.Cardinality, request.ExpectedCount));
    }

    [Fact]
    public async Task MultiExactCountOkPublishesPoseAndScorePortsWithProjectedScale()
    {
        var first = Candidate(new Pose2D(20, 30, 5) { Scale = 0.9 }) with
        {
            OuterCoverage = 0.93,
            InnerCoverage = 0.86,
            EdgeDistanceP95Px = 2.2,
            PolarityAgreement = 0.94
        };
        var second = Candidate(new Pose2D(60, 70, -15) { Scale = 1.1 });
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ok, true, [first, second])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["expectedCount"] = "2"
        });
        using var context = fixture.Context;

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.True(context.TryGetPortInput<Pose2D[]>(fixture.Consumer, InputOf("AllPositionsOutput"), out var poses));
        Assert.True(context.TryGetPortInput<double[]>(fixture.Consumer, InputOf("ScalesOutput"), out var scales));
        Assert.Equal([0.9, 1.1], poses.Select(pose => pose.Scale));
        Assert.Equal(poses.Select(pose => pose.Scale), scales);
        Assert.Equal("0.9", result.Data["scale"]);
        Assert.Equal("0.9,1.1", result.Data["scales"]);
        Assert.Equal("0.91,0.91", result.Data["scores"]);
        Assert.Equal("2", result.Data["matchSchemaVersion"]);
        Assert.Equal("2", result.Data["overlaySchemaVersion"]);
        Assert.Equal(
            "20,30,5,0.91,40,20,Rectangle,0;60,70,-15,0.91,40,20,Rectangle,0",
            result.Data["matches"]);
        using var matchesV2 = JsonDocument.Parse(result.Data["matchesV2"]);
        Assert.Collection(
            matchesV2.RootElement.EnumerateArray(),
            first =>
            {
                Assert.Equal(0.9, first.GetProperty("scale").GetDouble(), 12);
                Assert.Equal(0.93, first.GetProperty("outerCoverage").GetDouble(), 12);
                Assert.Equal(0.86, first.GetProperty("innerCoverage").GetDouble(), 12);
                Assert.Equal(2.2, first.GetProperty("edgeDistanceP95Px").GetDouble(), 12);
                Assert.Equal(0.94, first.GetProperty("polarityAgreement").GetDouble(), 12);
            },
            second => Assert.Equal(1.1, second.GetProperty("scale").GetDouble(), 12));
        Assert.True(context.Properties.ContainsKey("pose"));
    }

    [Fact]
    public async Task InvalidCandidateShapeGeometryClearsSeededMultiOutputsAtomically()
    {
        var candidate = Candidate(new Pose2D(20, 30, 5) { Scale = 1 }) with
        {
            Shape = "Circle",
            Radius = double.NaN
        };
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ok, true, [candidate])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["expectedCount"] = "1"
        });
        using var context = fixture.Context;
        fixture.SeedMultiOutputs();

        var exception = await Record.ExceptionAsync(() =>
            new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context));

        Assert.NotNull(exception);
        AssertNoPorts(fixture, MultiOperationalPorts.Append("CountOutput"));
        Assert.False(context.Properties.ContainsKey("pose"));
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("shape geometry", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContradictoryMultiOkCountFailsClosedAsNg()
    {
        var candidate = Candidate(new Pose2D(20, 30, 5) { Scale = 0.9 });
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ok, true, [candidate])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["expectedCount"] = "2"
        });
        using var context = fixture.Context;

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchOperatorFailed, result.Data["failureCode"]);
        AssertNoPorts(fixture, MultiOperationalPorts);
        Assert.True(context.TryGetPortInput<int>(fixture.Consumer, InputOf("CountOutput"), out var count));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task MissingMultiImageStillPublishesZeroCountAndClearsOperationalPorts()
    {
        var service = new RecordingMatchingService(_ => throw new InvalidOperationException("Must not run."));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, connectImage: false);
        using var context = fixture.Context;
        fixture.SeedMultiOutputs();

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        AssertNoPorts(fixture, MultiOperationalPorts);
        Assert.True(context.TryGetPortInput<int>(fixture.Consumer, InputOf("CountOutput"), out var count));
        Assert.Equal(0, count);
        Assert.Equal("0", result.Data["count"]);
        Assert.Empty(service.MatchRequests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OpenCv")]
    public async Task MissingAndOpenCvEnginesUseMinCountAndIgnoreInactiveHalconCounts(
        string? engine)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matchCount"] = "128",
            ["minCount"] = "2",
            ["expectedCount"] = "garbage"
        };
        if (engine is not null)
        {
            parameters["engine"] = engine;
        }

        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.OpenCv, InspectionOutcome.Ng, false, [])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, parameters);
        using var context = fixture.Context;

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal("OpenCv", result.Data["engine"]);
        var request = Assert.Single(service.MatchRequests);
        Assert.Equal(2, request.ExpectedCount);
        Assert.Equal("128", request.Parameters["matchCount"]);
        Assert.Equal("garbage", request.Parameters["expectedCount"]);
    }

    [Fact]
    public async Task UnknownEngineDiagnosticIsNotMaskedByInactiveExpectedCount()
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.ConfigUnknownEngine,
            "future-engine");
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Unknown, InspectionOutcome.Ng, false, [], diagnostic)));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "future-engine",
            ["expectedCount"] = "garbage",
            ["minCount"] = "4"
        });
        using var context = fixture.Context;

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, result.Data["failureCode"]);
        Assert.Equal(1, Assert.Single(service.MatchRequests).ExpectedCount);
    }

    [Fact]
    public async Task HalconDoesNotUseInactiveOpenCvMinCountAsExpectedCount()
    {
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ng, false, [])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["minCount"] = "7"
        });
        using var context = fixture.Context;

        await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        var request = Assert.Single(service.MatchRequests);
        Assert.Equal(1, request.ExpectedCount);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("101")]
    public async Task InvalidActiveHalconExpectedCountFailsBeforeServiceAndPublishesOnlyZeroCount(
        string expectedCount)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["expectedCount"] = expectedCount,
            ["minCount"] = "1"
        };
        parameters["engine"] = "Halcon";

        var service = new RecordingMatchingService(_ => throw new InvalidOperationException("Must not run."));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, parameters);
        using var context = fixture.Context;
        fixture.SeedMultiOutputs();

        var result = await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Data["failureCode"]);
        Assert.Equal(TemplateMatchingFailureStages.Configuration, result.Data["failureStage"]);
        Assert.Equal("0", result.Data["count"]);
        Assert.Empty(service.MatchRequests);
        AssertNoPorts(fixture, MultiOperationalPorts);
        Assert.True(context.TryGetPortInput<int>(fixture.Consumer, InputOf("CountOutput"), out var count));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task TrimmedHalconEngineUsesLegacyMatchCountOnlyAsEarlyDraftExpectedCount()
    {
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.Halcon, InspectionOutcome.Ng, false, [])));
        var fixture = ToolFixture.Create(VisionToolKind.MultiTargetMatch, new Dictionary<string, string>
        {
            ["engine"] = " Halcon ",
            ["matchCount"] = "3"
        });
        using var context = fixture.Context;

        await new MultiTargetMatchTool(service).ExecuteAsync(fixture.Definition, context);

        var request = Assert.Single(service.MatchRequests);
        Assert.Equal(3, request.ExpectedCount);
    }

    [Fact]
    public async Task SingleAndMultiResolveBoundRoiFromTheSameActiveFlow()
    {
        var activeRoi = new RoiDefinition
        {
            Id = "search-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 10,
            Y = 12,
            Width = 40,
            Height = 30
        };
        var staleRootRoi = activeRoi with { X = 70, Y = 72 };
        var service = new RecordingMatchingService(_ => Task.FromResult(
            Batch(TemplateMatchingEngine.OpenCv, InspectionOutcome.Ng, false, [])));
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["roiId"] = activeRoi.Id,
            ["expectedCount"] = "1"
        };
        var single = ToolFixture.Create(
            VisionToolKind.TemplateLocate,
            new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
            activeRois: [activeRoi],
            rootRois: [staleRootRoi]);
        var multi = ToolFixture.Create(
            VisionToolKind.MultiTargetMatch,
            new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
            activeRois: [activeRoi],
            rootRois: [staleRootRoi]);
        using var singleContext = single.Context;
        using var multiContext = multi.Context;

        await new TemplateLocateTool(service).ExecuteAsync(single.Definition, singleContext);
        await new MultiTargetMatchTool(service).ExecuteAsync(multi.Definition, multiContext);

        Assert.Collection(
            service.MatchRequests,
            request => AssertSearchRoi(activeRoi, request.SearchRoi),
            request => AssertSearchRoi(activeRoi, request.SearchRoi));
    }

    private static TemplateMatchBatchCandidate Candidate(Pose2D pose)
    {
        return new TemplateMatchBatchCandidate(
            pose,
            0.91,
            40,
            20,
            [],
            [[
                new Point2D(pose.X - 20, pose.Y - 10),
                new Point2D(pose.X + 20, pose.Y - 10),
                new Point2D(pose.X + 20, pose.Y + 10),
                new Point2D(pose.X - 20, pose.Y + 10)
            ]]);
    }

    private static TemplateMatchBatchResult Batch(
        TemplateMatchingEngine engine,
        InspectionOutcome outcome,
        bool hasMatch,
        IReadOnlyList<TemplateMatchBatchCandidate> candidates,
        TemplateMatchingDiagnostic? diagnostic = null)
    {
        return new TemplateMatchBatchResult(
            engine,
            outcome,
            hasMatch,
            candidates,
            new TemplateSearchRegion(0, 0, 96, 96),
            diagnostic?.UserMessage ?? "match-result",
            false,
            diagnostic);
    }

    private static string InputOf(string outputPort) => $"{outputPort}Input";

    private static void AssertSearchRoi(RoiDefinition expected, RoiDefinition? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Shape, actual.Shape);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Angle, actual.Angle);
    }

    private static void AssertNoPorts(ToolFixture fixture, IEnumerable<string> outputPorts)
    {
        foreach (var outputPort in outputPorts)
        {
            Assert.False(
                fixture.Context.TryGetPortInputValue(fixture.Consumer, InputOf(outputPort), out _),
                $"Unexpected operational port: {outputPort}");
        }
    }

    private sealed class RecordingMatchingService(
        Func<TemplateMatchingRequest, Task<TemplateMatchBatchResult>> handler) : ITemplateMatchingService
    {
        public List<TemplateMatchingRequest> MatchRequests { get; } = [];

        public int DisposeCount { get; private set; }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken)
        {
            MatchRequests.Add(request);
            return handler(request);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ToolFixture
    {
        private ToolFixture(
            VisionToolDefinition definition,
            VisionToolDefinition consumer,
            VisionToolContext context)
        {
            Definition = definition;
            Consumer = consumer;
            Context = context;
        }

        public VisionToolDefinition Definition { get; }

        public VisionToolDefinition Consumer { get; }

        public VisionToolContext Context { get; }

        public static ToolFixture Create(
            VisionToolKind kind,
            Dictionary<string, string>? parameters = null,
            bool connectImage = true,
            ImageFrame? frame = null,
            IReadOnlyList<RoiDefinition>? activeRois = null,
            IReadOnlyList<RoiDefinition>? rootRois = null)
        {
            frame ??= new ImageFrame(
                "frame-id",
                96,
                96,
                96,
                PixelFormatKind.Gray8,
                new byte[96 * 96],
                DateTimeOffset.UtcNow,
                "test");
            var source = new VisionToolDefinition
            {
                Id = "image-source",
                Kind = VisionToolKind.AcquireImage
            };
            parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (connectImage)
            {
                parameters["input:ImageInput:toolId"] = source.Id;
                parameters["input:ImageInput:portKey"] = "ImageOutput";
            }

            var definition = new VisionToolDefinition
            {
                Id = "template-tool",
                Name = "Display Name Must Not Be Owner",
                Kind = kind,
                Parameters = parameters
            };
            var consumerParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in SingleOperationalPorts
                         .Concat(MultiOperationalPorts)
                         .Append("CountOutput")
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                consumerParameters[$"input:{InputOf(output)}:toolId"] = definition.Id;
                consumerParameters[$"input:{InputOf(output)}:portKey"] = output;
            }

            var consumer = new VisionToolDefinition
            {
                Id = "consumer",
                Parameters = consumerParameters
            };
            var flow = new VisionFlowDefinition
            {
                Id = "flow-id",
                Name = "Flow Display Name Must Not Be Owner",
                Rois = activeRois ?? [],
                Tools = [source, definition, consumer]
            };
            var recipe = new Recipe
            {
                Id = "recipe-id",
                CurrentFlowId = flow.Id,
                Rois = rootRois ?? [],
                Flows = [flow]
            };
            var context = new VisionToolContext(recipe, frame);
            if (connectImage)
            {
                context.SetImageOutput(source, frame);
            }

            return new ToolFixture(definition, consumer, context);
        }

        public void SeedSingleOutputs()
        {
            var pose = new Pose2D(1, 2, 3) { Scale = 4 };
            Context.Properties["pose"] = pose;
            Context.SetPortOutput(Definition, "PositionOutput", pose);
            Context.SetPortOutput(Definition, "OriginOutput", pose);
            Context.SetPortOutput(Definition, "ScoreOutput", 0.99);
            Context.SetPortOutput(Definition, "XOutput", pose.X);
            Context.SetPortOutput(Definition, "YOutput", pose.Y);
            Context.SetPortOutput(Definition, "AngleOutput", pose.Angle);
            Context.SetPortOutput(Definition, "ScaleOutput", pose.Scale);
        }

        public void SeedMultiOutputs()
        {
            SeedSingleOutputs();
            var pose = new Pose2D(1, 2, 3) { Scale = 4 };
            Context.SetPortOutput(Definition, "BestPositionOutput", pose);
            Context.SetPortOutput(Definition, "CountOutput", 1);
            Context.SetPortOutput(Definition, "AllPositionsOutput", new[] { pose });
            Context.SetPortOutput(Definition, "ScoresOutput", new[] { 0.99 });
            Context.SetPortOutput(Definition, "ScalesOutput", new[] { pose.Scale });
        }
    }
}
