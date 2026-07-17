using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class ConfiguredVisionPipelineTests
{
    [Fact]
    public void DefaultFactoryRequiresExplicitMatchingService()
    {
        var repository = new DeviceConfigurationRepositoryStub();

        Assert.Throws<ArgumentNullException>(() =>
            VisionPipelineFactory.CreateDefault(repository, null!));
        Assert.DoesNotContain(
            typeof(TemplateLocateTool).GetConstructors(),
            constructor => constructor.GetParameters().Length == 0);
        Assert.DoesNotContain(
            typeof(MultiTargetMatchTool).GetConstructors(),
            constructor => constructor.GetParameters().Length == 0);
    }

    [Fact]
    public async Task DefaultFactoryPassesSameMatchingServiceToSingleAndMultiTools()
    {
        var service = new RecordingMatchingService();
        var pipeline = VisionPipelineFactory.CreateDefault(
            new DeviceConfigurationRepositoryStub(),
            service);
        var source = new VisionToolDefinition
        {
            Id = "source",
            Kind = VisionToolKind.AcquireImage
        };
        var single = MatchDefinition("single", VisionToolKind.TemplateLocate, source.Id);
        var multi = MatchDefinition("multi", VisionToolKind.MultiTargetMatch, source.Id);
        multi.Parameters["expectedCount"] = "1";
        var flow = new VisionFlowDefinition
        {
            Id = "flow",
            Tools = [source, single, multi]
        };
        var recipe = new Recipe
        {
            Id = "recipe",
            CurrentFlowId = flow.Id,
            Flows = [flow]
        };

        var result = await pipeline.ExecuteAsync(recipe, Frame());

        Assert.Equal(3, result.ToolResults.Count);
        Assert.Collection(
            service.Requests,
            request => Assert.Equal(
                (new TemplateModelOwner("recipe", "flow", "single"), TemplateMatchCardinality.Single),
                (request.Owner, request.Cardinality)),
            request => Assert.Equal(
                (new TemplateModelOwner("recipe", "flow", "multi"), TemplateMatchCardinality.ExactCount),
                (request.Owner, request.Cardinality)));
    }

    [Fact]
    public async Task MatchingCancellationStopsPipelineWithoutRunningNextTool()
    {
        var service = new ThrowingMatchingService();
        var source = new ImageSourceTool();
        var locate = new CancellationSnapshotTool(new TemplateLocateTool(service));
        var next = new RecordingTool();
        var pipeline = new ConfiguredVisionPipeline([source, locate, next]);
        var sourceDefinition = new VisionToolDefinition
        {
            Id = "source",
            Kind = VisionToolKind.AcquireImage
        };
        var locateDefinition = new VisionToolDefinition
        {
            Id = "locate",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:ImageInput:toolId"] = sourceDefinition.Id,
                ["input:ImageInput:portKey"] = "ImageOutput"
            }
        };
        var nextDefinition = new VisionToolDefinition
        {
            Id = "next",
            Kind = VisionToolKind.Result
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Tools = [sourceDefinition, locateDefinition, nextDefinition]
        };
        var recipe = new Recipe
        {
            Id = "recipe",
            CurrentFlowId = flow.Id,
            Flows = [flow]
        };
        var frame = Frame();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pipeline.ExecuteAsync(recipe, frame));

        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(0, next.ExecutionCount);
        Assert.Equal(1, service.ExecutionCount);
        var onlyResult = Assert.Single(locate.ResultsAtCancellation);
        Assert.Equal(sourceDefinition.Id, onlyResult.ToolId);
        Assert.DoesNotContain(locate.ResultsAtCancellation, result => result.ToolId == locateDefinition.Id);
    }

    private static ImageFrame Frame()
    {
        return new ImageFrame(
            "frame",
            32,
            32,
            32,
            PixelFormatKind.Gray8,
            new byte[32 * 32],
            DateTimeOffset.UtcNow,
            "test");
    }

    private static VisionToolDefinition MatchDefinition(
        string id,
        VisionToolKind kind,
        string sourceId)
    {
        return new VisionToolDefinition
        {
            Id = id,
            Kind = kind,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:ImageInput:toolId"] = sourceId,
                ["input:ImageInput:portKey"] = "ImageOutput"
            }
        };
    }

    private sealed class RecordingMatchingService : ITemplateMatchingService
    {
        public List<TemplateMatchingRequest> Requests { get; } = [];

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var candidate = new TemplateMatchBatchCandidate(
                new Pose2D(16, 16, 0) { Scale = 1 },
                0.95,
                12,
                12,
                [],
                [[
                    new Point2D(10, 10),
                    new Point2D(22, 10),
                    new Point2D(22, 22),
                    new Point2D(10, 22)
                ]]);
            return Task.FromResult(new TemplateMatchBatchResult(
                TemplateMatchingEngine.OpenCv,
                InspectionOutcome.Ok,
                true,
                [candidate],
                new TemplateSearchRegion(0, 0, 32, 32),
                "ok",
                false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeviceConfigurationRepositoryStub : IDeviceConfigurationRepository
    {
        public event EventHandler<DeviceConfiguration>? ConfigurationSaved
        {
            add { }
            remove { }
        }

        public Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceConfiguration());

        public Task SaveAsync(
            DeviceConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingMatchingService : ITemplateMatchingService
    {
        public int ExecutionCount { get; private set; }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImageSourceTool : IVisionTool
    {
        public VisionToolKind Kind => VisionToolKind.AcquireImage;

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            VisionToolDefinition definition,
            VisionToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            context.SetImageOutput(definition, context.OriginalFrame);
            return Task.FromResult(Result(definition, Kind));
        }
    }

    private sealed class CancellationSnapshotTool(IVisionTool inner) : IVisionTool
    {
        public VisionToolKind Kind => inner.Kind;

        public IReadOnlyList<ToolResult> ResultsAtCancellation { get; private set; } = [];

        public async Task<ToolResult> ExecuteAsync(
            VisionToolDefinition definition,
            VisionToolContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ExecuteAsync(definition, context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ResultsAtCancellation = context.ToolResults.ToArray();
                throw;
            }
        }
    }

    private sealed class RecordingTool : IVisionTool
    {
        public VisionToolKind Kind => VisionToolKind.Result;

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            VisionToolDefinition definition,
            VisionToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(Result(definition, Kind));
        }
    }

    private static ToolResult Result(VisionToolDefinition definition, VisionToolKind kind)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = kind,
            Outcome = InspectionOutcome.Ok,
            Message = "ok"
        };
    }
}
