using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class PositionInputScaleToolTests
{
    [Fact]
    public async Task TemplatePointScalesAndRotatesOffsetWithCurrentPose()
    {
        var positionSource = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
        };
        var definition = new VisionToolDefinition
        {
            Id = "template-point",
            Kind = VisionToolKind.TemplatePoint,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = positionSource.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["offsetX"] = "10",
                ["offsetY"] = "0"
            }
        };
        using var context = new VisionToolContext(
            new Recipe { Tools = [positionSource, definition] },
            CreateFrame());
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(100, 200, 90) { Scale = 2 });

        var result = await new TemplatePointTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal(100, double.Parse(result.Data["x"]), 6);
        Assert.Equal(220, double.Parse(result.Data["y"]), 6);
    }

    [Fact]
    public async Task TemplatePointUsesTaughtReferenceScale()
    {
        var positionSource = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
        };
        var definition = new VisionToolDefinition
        {
            Id = "template-point",
            Kind = VisionToolKind.TemplatePoint,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = positionSource.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["pointX"] = "110",
                ["pointY"] = "100",
                ["referenceX"] = "100",
                ["referenceY"] = "100",
                ["referenceAngle"] = "0",
                ["referenceScale"] = "0.5"
            }
        };
        using var context = new VisionToolContext(
            new Recipe { Tools = [positionSource, definition] },
            CreateFrame());
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(200, 150, 90));

        var result = await new TemplatePointTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal(200, double.Parse(result.Data["x"]), 6);
        Assert.Equal(170, double.Parse(result.Data["y"]), 6);
    }

    [Fact]
    public async Task CoordinateTransformPreservesPoseScale()
    {
        var positionSource = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
        };
        var definition = new VisionToolDefinition
        {
            Id = "coordinate-transform",
            Kind = VisionToolKind.CoordinateTransform,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = positionSource.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["matrix"] = CalibrationProfileText.FormatMatrix([1, 0, 0, 0, 1, 0]),
                ["model"] = "Affine"
            }
        };
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = definition.Id,
                ["input:PositionInput:portKey"] = "PositionOutput"
            }
        };
        using var context = new VisionToolContext(
            new Recipe { Tools = [positionSource, definition, outputConsumer] },
            CreateFrame());
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(100, 200, 30) { Scale = 1.1 });

        var result = await new CoordinateTransformTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.True(context.TryGetPortInput<Pose2D>(outputConsumer, "PositionInput", out var worldPose));
        Assert.Equal(1.1, worldPose.Scale, 6);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task FindLineRejectsInvalidReferenceScaleBeforeAlgorithmAndClearsOutputs(string scale)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(VisionToolKind.FindLine, imageSource, positionSource, scale);
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:LineInput:toolId"] = definition.Id,
                ["input:LineInput:portKey"] = "LineOutput",
                ["input:PointInput:toolId"] = definition.Id,
                ["input:PointInput:portKey"] = "MidPointOutput"
            }
        };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.RotatedRectangle,
            X = 16,
            Y = 16,
            Width = 20,
            Height = 10
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        context.SetPortOutput(definition, "LineOutput", new Line2D(new Point2D(0, 0), new Point2D(1, 1)));
        context.SetPortOutput(definition, "MidPointOutput", new Point2D(0.5, 0.5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new FindLineTool().ExecuteAsync(definition, context, cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        Assert.False(context.TryGetPortInput<Line2D>(outputConsumer, "LineInput", out _));
        Assert.False(context.TryGetPortInput<Point2D>(outputConsumer, "PointInput", out _));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task FindCircleRejectsInvalidReferenceScaleBeforeAlgorithmAndClearsOutputs(string scale)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(VisionToolKind.FindCircle, imageSource, positionSource, scale);
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:CircleInput:toolId"] = definition.Id,
                ["input:CircleInput:portKey"] = "CircleOutput",
                ["input:PointInput:toolId"] = definition.Id,
                ["input:PointInput:portKey"] = "CenterOutput",
                ["input:RadiusInput:toolId"] = definition.Id,
                ["input:RadiusInput:portKey"] = "RadiusOutput"
            }
        };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        context.SetPortOutput(definition, "CircleOutput", new Circle2D(new Point2D(16, 16), 8));
        context.SetPortOutput(definition, "CenterOutput", new Point2D(16, 16));
        context.SetPortOutput(definition, "RadiusOutput", 8d);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new FindCircleTool().ExecuteAsync(definition, context, cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        Assert.False(context.TryGetPortInput<Circle2D>(outputConsumer, "CircleInput", out _));
        Assert.False(context.TryGetPortInput<Point2D>(outputConsumer, "PointInput", out _));
        Assert.False(context.TryGetPortInput<double>(outputConsumer, "RadiusInput", out _));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task DefectDetectRejectsInvalidReferenceScaleBeforeAlgorithmAndClearsOutputs(string scale)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(VisionToolKind.DefectDetect, imageSource, positionSource, scale);
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:CountInput:toolId"] = definition.Id,
                ["input:CountInput:portKey"] = "CountOutput",
                ["input:PointInput:toolId"] = definition.Id,
                ["input:PointInput:portKey"] = "BestCenterOutput"
            }
        };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        context.SetPortOutput(definition, "CountOutput", 1);
        context.SetPortOutput(definition, "BestCenterOutput", new Point2D(16, 16));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new DefectDetectTool().ExecuteAsync(definition, context, cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        Assert.False(context.TryGetPortInput<int>(outputConsumer, "CountInput", out _));
        Assert.False(context.TryGetPortInput<Point2D>(outputConsumer, "PointInput", out _));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task MultiTargetMatchRejectsInvalidReferenceScaleBeforeMatcherAndClearsOutputs(string scale)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(VisionToolKind.MultiTargetMatch, imageSource, positionSource, scale);
        var learned = TemplateMatcher.Learn(
            CreateFrame(),
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            });
        foreach (var parameter in learned)
        {
            definition.Parameters[parameter.Key] = parameter.Value;
        }

        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = definition.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["input:CountInput:toolId"] = definition.Id,
                ["input:CountInput:portKey"] = "CountOutput"
            }
        };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        var stalePose = new Pose2D(16, 16, 0);
        context.Properties["pose"] = stalePose;
        context.SetPortOutput(definition, "PositionOutput", stalePose);
        context.SetPortOutput(definition, "CountOutput", 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new MultiTargetMatchTool().ExecuteAsync(definition, context, cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        Assert.False(context.Properties.ContainsKey("pose"));
        Assert.False(context.TryGetPortInput<Pose2D>(outputConsumer, "PositionInput", out _));
        Assert.False(context.TryGetPortInput<int>(outputConsumer, "CountInput", out _));
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    [InlineData(VisionToolKind.DefectDetect)]
    [InlineData(VisionToolKind.MultiTargetMatch)]
    public async Task PositionInputToolsTreatMissingReferenceScaleAsOne(VisionToolKind kind)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        definition.Parameters.Remove("roiReferencePoseScale");
        if (kind == VisionToolKind.MultiTargetMatch)
        {
            foreach (var parameter in TemplateMatcher.Learn(
                         CreateFrame(),
                         null,
                         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                         {
                             ["templateX"] = "8",
                             ["templateY"] = "8",
                             ["templateWidth"] = "12",
                             ["templateHeight"] = "12"
                         }))
            {
                definition.Parameters[parameter.Key] = parameter.Value;
            }
        }

        var roi = kind switch
        {
            VisionToolKind.FindLine => new RoiDefinition
            {
                Id = "roi",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 20,
                Height = 10
            },
            VisionToolKind.FindCircle => new RoiDefinition
            {
                Id = "roi",
                Shape = RoiShapeKind.Circle,
                X = 16,
                Y = 16,
                Radius = 8
            },
            _ => new RoiDefinition
            {
                Id = "roi",
                Shape = RoiShapeKind.Rectangle,
                X = 4,
                Y = 4,
                Width = 24,
                Height = 24
            }
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            new VisionToolDefinition { Id = "output-consumer" },
            roi);
        IVisionTool tool = kind switch
        {
            VisionToolKind.FindLine => new FindLineTool(),
            VisionToolKind.FindCircle => new FindCircleTool(),
            VisionToolKind.DefectDetect => new DefectDetectTool(),
            VisionToolKind.MultiTargetMatch => new MultiTargetMatchTool(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var result = await tool.ExecuteAsync(definition, context);

        Assert.NotEqual("CONFIG_INVALID_PARAMETER", result.Data.GetValueOrDefault("code"));
        Assert.True(result.Data.ContainsKey("searchRoiShape"));
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    [InlineData(VisionToolKind.DefectDetect)]
    [InlineData(VisionToolKind.MultiTargetMatch)]
    public async Task PositionInputToolsRejectInvalidReferenceScaleEvenWithoutBoundRoi(VisionToolKind kind)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "0");
        if (kind == VisionToolKind.MultiTargetMatch)
        {
            foreach (var parameter in TemplateMatcher.Learn(
                         CreateFrame(),
                         null,
                         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                         {
                             ["templateX"] = "8",
                             ["templateY"] = "8",
                             ["templateWidth"] = "12",
                             ["templateHeight"] = "12"
                         }))
            {
                definition.Parameters[parameter.Key] = parameter.Value;
            }
        }

        var frame = CreateFrame();
        using var context = new VisionToolContext(
            new Recipe { Tools = [imageSource, positionSource, definition] },
            frame);
        context.SetImageOutput(imageSource, frame);
        context.SetPortOutput(positionSource, "PositionOutput", new Pose2D(16, 16, 0));
        IVisionTool tool = kind switch
        {
            VisionToolKind.FindLine => new FindLineTool(),
            VisionToolKind.FindCircle => new FindCircleTool(),
            VisionToolKind.DefectDetect => new DefectDetectTool(),
            VisionToolKind.MultiTargetMatch => new MultiTargetMatchTool(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await tool.ExecuteAsync(definition, context, cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
    }

    [Fact]
    public void ManagedTemplateLearningPublishesStandardScaleOne()
    {
        var learned = TemplateMatcher.Learn(
            CreateFrame(),
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["engine"] = "ManagedNcc",
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            });

        Assert.Equal("1", learned["standardScale"]);
    }

    [Fact]
    public void OpenCvTemplateLearningPublishesStandardScaleOne()
    {
        var learned = TemplateMatcher.Learn(
            CreateFrame(),
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["engine"] = "OpenCv",
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            });

        Assert.Equal("1", learned["standardScale"]);
    }

    [Fact]
    public void MultiTargetCandidateScaleDefaultsToOneWithoutChangingDeconstruction()
    {
        var candidate = new MultiTargetMatchCandidate(10, 20, 30, 0.9, 40, 50);
        var (x, y, angle, score, width, height, shape, radius) = candidate;

        Assert.Equal((10d, 20d, 30d, 0.9d, 40, 50, "Rectangle", 0d),
            (x, y, angle, score, width, height, shape, radius));
        Assert.Equal(1, candidate.Scale);
    }

    [Fact]
    public void MultiTargetCandidateExplicitScaleFlowsToPose()
    {
        var candidate = new MultiTargetMatchCandidate(16, 16, 10, 0.95, 12, 12)
        {
            Scale = 1.1
        };

        Assert.Equal(1.1, candidate.Pose.Scale, 6);
    }

    [Fact]
    public async Task MultiTargetToolRealMatcherPublishesDefaultScaleToDataAndPoseOutputs()
    {
        var frame = TemplateMatcherTestData.CreateSearchFrame();
        var parameters = TemplateMatcherTestData.LearnRuntimeParameters();
        parameters["matchCount"] = "1";
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        parameters["input:ImageInput:toolId"] = imageSource.Id;
        parameters["input:ImageInput:portKey"] = "ImageOutput";
        var definition = new VisionToolDefinition
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            Parameters = parameters
        };
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = definition.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["input:AllPositionsInput:toolId"] = definition.Id,
                ["input:AllPositionsInput:portKey"] = "AllPositionsOutput"
            }
        };
        using var context = new VisionToolContext(
            new Recipe { Tools = [imageSource, definition, outputConsumer] },
            frame);
        context.SetImageOutput(imageSource, frame);

        var result = await new MultiTargetMatchTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal("1", result.Data["scale"]);
        Assert.True(context.TryGetPortInput<Pose2D>(outputConsumer, "PositionInput", out var bestPose));
        Assert.Equal(1, bestPose.Scale);
        Assert.True(context.TryGetPortInput<Pose2D[]>(outputConsumer, "AllPositionsInput", out var allPoses));
        Assert.All(allPoses, pose => Assert.Equal(1, pose.Scale));
    }

    private static VisionToolDefinition CreatePositionInputDefinition(
        VisionToolKind kind,
        VisionToolDefinition imageSource,
        VisionToolDefinition positionSource,
        string referenceScale)
    {
        return new VisionToolDefinition
        {
            Id = kind.ToString(),
            Kind = kind,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:ImageInput:toolId"] = imageSource.Id,
                ["input:ImageInput:portKey"] = "ImageOutput",
                ["input:PositionInput:toolId"] = positionSource.Id,
                ["input:PositionInput:portKey"] = "PositionOutput",
                ["roiId"] = "roi",
                ["roiReferencePoseX"] = "16",
                ["roiReferencePoseY"] = "16",
                ["roiReferencePoseAngle"] = "0",
                ["roiReferencePoseScale"] = referenceScale,
                ["roiReferencePoseToolId"] = positionSource.Id
            }
        };
    }

    private static VisionToolContext CreatePositionInputContext(
        VisionToolDefinition imageSource,
        VisionToolDefinition positionSource,
        VisionToolDefinition definition,
        VisionToolDefinition outputConsumer,
        RoiDefinition roi)
    {
        var frame = CreateFrame();
        var context = new VisionToolContext(
            new Recipe
            {
                Tools = [imageSource, positionSource, definition, outputConsumer],
                Rois = [roi]
            },
            frame);
        context.SetImageOutput(imageSource, frame);
        context.SetPortOutput(positionSource, "PositionOutput", new Pose2D(16, 16, 0));
        return context;
    }

    private static ImageFrame CreateFrame()
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
}
