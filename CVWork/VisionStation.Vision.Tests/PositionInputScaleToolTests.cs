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
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("1e309")]
    public async Task FindLineRejectsInvalidReferenceScaleBeforeAlgorithmAndClearsOutputs(
        string scale)
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
        Assert.Equal(
            $"Position input mapping failed: roiReferencePoseScale must be finite and greater than zero; actual value is '{scale}'.",
            result.Message);
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

    public static IEnumerable<object[]> InvalidScaleToolCases()
    {
        var kinds = new[]
        {
            VisionToolKind.FindLine,
            VisionToolKind.FindCircle,
            VisionToolKind.DefectDetect,
            VisionToolKind.MultiTargetMatch
        };
        var invalidScales = new[] { "NaN", "0", "-1" };
        foreach (var kind in kinds)
        {
            foreach (var invalidScale in invalidScales)
            {
                yield return [kind, invalidScale, true];
                yield return [kind, invalidScale, false];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidScaleToolCases))]
    public async Task PositionInputToolsRejectInvalidLearnedStandardScaleBeforeAlgorithmAndClearAllOutputs(
        VisionToolKind kind,
        string invalidScale,
        bool hasBoundRoi)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12",
                ["standardScale"] = invalidScale
            }
        };
        var definition = CreatePositionInputDefinition(
            kind,
            imageSource,
            positionSource,
            "1");
        RemoveTaughtReferencePose(definition);
        AddRealTemplateParametersIfRequired(definition);
        var outputConsumer = CreateAllOutputConsumer(definition, kind);
        var roi = hasBoundRoi ? CreateBoundRoi(kind) : null;
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        SeedAllBusinessOutputs(context, definition, kind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateTool(kind).ExecuteAsync(
            definition,
            context,
            cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        AssertAllBusinessOutputsAreInaccessible(context, outputConsumer, kind);
    }

    [Theory]
    [MemberData(nameof(InvalidScaleToolCases))]
    public async Task PositionInputToolsRejectInvalidCurrentScaleBeforeAlgorithmAndClearAllOutputs(
        VisionToolKind kind,
        string invalidScale,
        bool hasBoundRoi)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        AddRealTemplateParametersIfRequired(definition);
        var outputConsumer = CreateAllOutputConsumer(definition, kind);
        var roi = hasBoundRoi ? CreateBoundRoi(kind) : null;
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            outputConsumer,
            roi);
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(16, 16, 0) { Scale = ParseScale(invalidScale) });
        SeedAllBusinessOutputs(context, definition, kind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateTool(kind).ExecuteAsync(
            definition,
            context,
            cancellation.Token);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
        AssertAllBusinessOutputsAreInaccessible(context, outputConsumer, kind);
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    [InlineData(VisionToolKind.DefectDetect)]
    [InlineData(VisionToolKind.MultiTargetMatch)]
    public async Task PositionInputToolsKeepLegacyBehaviorWhenSourceHasNoReferencePose(VisionToolKind kind)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition { Id = "position-source" };
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        RemoveTaughtReferencePose(definition);
        AddRealTemplateParametersIfRequired(definition);
        var frame = CreateFrame();
        using var context = new VisionToolContext(
            new Recipe { Tools = [imageSource, positionSource, definition] },
            frame);
        context.SetImageOutput(imageSource, frame);
        context.SetPortOutput(positionSource, "PositionOutput", new Pose2D(16, 16, 0));

        var result = await CreateTool(kind).ExecuteAsync(definition, context);

        Assert.NotEqual("CONFIG_INVALID_PARAMETER", result.Data.GetValueOrDefault("code"));
    }

    [Theory]
    [InlineData(null, 8d)]
    [InlineData("0.5", 16d)]
    public async Task DefectDetectMapsLearnedReferenceUsingExplicitOrDefaultStandardScale(
        string? standardScale,
        double expectedWidth)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            }
        };
        if (standardScale is not null)
        {
            positionSource.Parameters["standardScale"] = standardScale;
        }

        var definition = CreatePositionInputDefinition(
            VisionToolKind.DefectDetect,
            imageSource,
            positionSource,
            "1");
        RemoveTaughtReferencePose(definition);
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 10,
            Y = 10,
            Width = 8,
            Height = 8
        };
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            new VisionToolDefinition { Id = "output-consumer" },
            roi);

        var result = await new DefectDetectTool().ExecuteAsync(definition, context);

        Assert.NotEqual("CONFIG_INVALID_PARAMETER", result.Data.GetValueOrDefault("code"));
        Assert.Equal(16, double.Parse(result.Data["searchRoiX"]), 6);
        Assert.Equal(16, double.Parse(result.Data["searchRoiY"]), 6);
        Assert.Equal(expectedWidth, double.Parse(result.Data["searchRoiWidth"]), 6);
        Assert.Equal(expectedWidth, double.Parse(result.Data["searchRoiHeight"]), 6);
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
    public void TemplateAndMultiTargetResultScaleFormatterRoundTripsSmallPositiveScale()
    {
        const double scale = 0.0004;

        var serialized = scale.ToRoundTripScaleInvariant();

        Assert.NotEqual("0", serialized);
        Assert.Equal(
            scale,
            double.Parse(serialized, System.Globalization.CultureInfo.InvariantCulture));
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

    [Theory]
    [InlineData(VisionToolKind.FindLine, 18d, 18d, 10d, 5d, 0d)]
    [InlineData(VisionToolKind.FindCircle, 18d, 18d, 0d, 0d, 4d)]
    [InlineData(VisionToolKind.DefectDetect, 18d, 18d, 12d, 12d, 0d)]
    [InlineData(VisionToolKind.MultiTargetMatch, 18d, 18d, 12d, 12d, 0d)]
    public async Task PositionInputToolsUseOnlyHalconReferenceNamespace(
        VisionToolKind kind,
        double expectedX,
        double expectedY,
        double expectedWidth,
        double expectedHeight,
        double expectedRadius)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = CreateHalconPositionSource("1");
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        RemoveTaughtReferencePose(definition);
        AddRealTemplateParametersIfRequired(definition);
        var roi = CreateBoundRoi(kind);
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            new VisionToolDefinition { Id = "output-consumer" },
            roi);
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(18, 18, 0) { Scale = 0.5 });

        var result = await CreateTool(kind).ExecuteAsync(definition, context);

        Assert.NotEqual("CONFIG_INVALID_PARAMETER", result.Data.GetValueOrDefault("code"));
        Assert.Equal(expectedX, double.Parse(result.Data["searchRoiX"]), 6);
        Assert.Equal(expectedY, double.Parse(result.Data["searchRoiY"]), 6);
        if (kind == VisionToolKind.FindCircle)
        {
            Assert.Equal(expectedRadius, double.Parse(result.Data["searchRoiRadius"]), 6);
        }
        else
        {
            Assert.Equal(expectedWidth, double.Parse(result.Data["searchRoiWidth"]), 6);
            Assert.Equal(expectedHeight, double.Parse(result.Data["searchRoiHeight"]), 6);
        }
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    [InlineData(VisionToolKind.DefectDetect)]
    [InlineData(VisionToolKind.MultiTargetMatch)]
    public async Task PositionInputToolsTreatMissingHalconScaleAsIncompleteWithoutLegacyFallback(
        VisionToolKind kind)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = CreateHalconPositionSource(null);
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        RemoveTaughtReferencePose(definition);
        var roi = CreateBoundRoi(kind);
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            new VisionToolDefinition { Id = "output-consumer" },
            roi);
        context.SetPortOutput(
            positionSource,
            "PositionOutput",
            new Pose2D(18, 18, 0) { Scale = 0.5 });

        var result = await CreateTool(kind).ExecuteAsync(definition, context);

        Assert.NotEqual("CONFIG_INVALID_PARAMETER", result.Data.GetValueOrDefault("code"));
        Assert.Equal(roi.X, double.Parse(result.Data["searchRoiX"]), 6);
        Assert.Equal(roi.Y, double.Parse(result.Data["searchRoiY"]), 6);
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    [InlineData(VisionToolKind.DefectDetect)]
    [InlineData(VisionToolKind.MultiTargetMatch)]
    public async Task PositionInputToolsRejectInvalidHalconReferenceScaleBeforeAlgorithm(
        VisionToolKind kind)
    {
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        var positionSource = CreateHalconPositionSource("NaN");
        var definition = CreatePositionInputDefinition(kind, imageSource, positionSource, "1");
        RemoveTaughtReferencePose(definition);
        using var context = CreatePositionInputContext(
            imageSource,
            positionSource,
            definition,
            new VisionToolDefinition { Id = "output-consumer" },
            CreateBoundRoi(kind));

        var result = await CreateTool(kind).ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("CONFIG_INVALID_PARAMETER", result.Data["code"]);
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

    private static VisionToolDefinition CreateHalconPositionSource(string? scale)
    {
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["engine"] = "Halcon",
                ["halcon.standardX"] = "16",
                ["halcon.standardY"] = "16",
                ["halcon.standardAngle"] = "0",
                ["halcon.templateWidth"] = "12",
                ["halcon.templateHeight"] = "12",
                ["standardX"] = "999",
                ["standardY"] = "999",
                ["standardAngle"] = "90",
                ["standardScale"] = "4",
                ["templateWidth"] = "100",
                ["templateHeight"] = "100"
            }
        };
        if (scale is not null)
        {
            source.Parameters["halcon.standardScale"] = scale;
        }

        return source;
    }

    private static VisionToolContext CreatePositionInputContext(
        VisionToolDefinition imageSource,
        VisionToolDefinition positionSource,
        VisionToolDefinition definition,
        VisionToolDefinition outputConsumer,
        RoiDefinition? roi)
    {
        var frame = CreateFrame();
        var context = new VisionToolContext(
            new Recipe
            {
                Tools = [imageSource, positionSource, definition, outputConsumer],
                Rois = roi is null ? [] : [roi]
            },
            frame);
        context.SetImageOutput(imageSource, frame);
        context.SetPortOutput(positionSource, "PositionOutput", new Pose2D(16, 16, 0));
        return context;
    }

    private static void RemoveTaughtReferencePose(VisionToolDefinition definition)
    {
        definition.Parameters.Remove("roiReferencePoseX");
        definition.Parameters.Remove("roiReferencePoseY");
        definition.Parameters.Remove("roiReferencePoseAngle");
        definition.Parameters.Remove("roiReferencePoseScale");
        definition.Parameters.Remove("roiReferencePoseToolId");
    }

    private static void AddRealTemplateParametersIfRequired(VisionToolDefinition definition)
    {
        if (definition.Kind != VisionToolKind.MultiTargetMatch)
        {
            return;
        }

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

    private static RoiDefinition CreateBoundRoi(VisionToolKind kind)
    {
        return kind switch
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
    }

    private static IVisionTool CreateTool(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.FindLine => new FindLineTool(),
            VisionToolKind.FindCircle => new FindCircleTool(),
            VisionToolKind.DefectDetect => new DefectDetectTool(),
            VisionToolKind.MultiTargetMatch => new MultiTargetMatchTool(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static VisionToolDefinition CreateAllOutputConsumer(
        VisionToolDefinition definition,
        VisionToolKind kind)
    {
        var consumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var portKey in GetBusinessOutputPortKeys(kind))
        {
            var inputKey = $"{portKey}Input";
            consumer.Parameters[$"input:{inputKey}:toolId"] = definition.Id;
            consumer.Parameters[$"input:{inputKey}:portKey"] = portKey;
        }

        return consumer;
    }

    private static IReadOnlyList<string> GetBusinessOutputPortKeys(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.FindLine => ["LineOutput", "MidPointOutput"],
            VisionToolKind.FindCircle => ["CircleOutput", "CenterOutput", "RadiusOutput"],
            VisionToolKind.DefectDetect =>
            [
                "CountOutput",
                "AllCentersOutput",
                "BestCenterOutput",
                "BestAreaOutput",
                "BestCircularityOutput",
                "BestWidthOutput",
                "BestHeightOutput",
                "BestAspectRatioOutput",
                "BestPerimeterOutput",
                "BestCircleOutput",
                "BestContourOutput",
                "BestRectOutput"
            ],
            VisionToolKind.MultiTargetMatch =>
            [
                "PositionOutput",
                "OriginOutput",
                "BestPositionOutput",
                "ScoreOutput",
                "XOutput",
                "YOutput",
                "AngleOutput",
                "CountOutput",
                "AllPositionsOutput",
                "ScoresOutput"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static void SeedAllBusinessOutputs(
        VisionToolContext context,
        VisionToolDefinition definition,
        VisionToolKind kind)
    {
        var point = new Point2D(16, 16);
        var pose = new Pose2D(16, 16, 0);
        var staleOutputs = kind switch
        {
            VisionToolKind.FindLine => new Dictionary<string, object>
            {
                ["LineOutput"] = new Line2D(new Point2D(4, 4), new Point2D(28, 28)),
                ["MidPointOutput"] = point
            },
            VisionToolKind.FindCircle => new Dictionary<string, object>
            {
                ["CircleOutput"] = new Circle2D(point, 8),
                ["CenterOutput"] = point,
                ["RadiusOutput"] = 8d
            },
            VisionToolKind.DefectDetect => new Dictionary<string, object>
            {
                ["CountOutput"] = 1,
                ["AllCentersOutput"] = new[] { point },
                ["BestCenterOutput"] = point,
                ["BestAreaOutput"] = 10d,
                ["BestCircularityOutput"] = 0.9d,
                ["BestWidthOutput"] = 4d,
                ["BestHeightOutput"] = 5d,
                ["BestAspectRatioOutput"] = 0.8d,
                ["BestPerimeterOutput"] = 12d,
                ["BestCircleOutput"] = new Circle2D(point, 2),
                ["BestContourOutput"] = new[] { point, new Point2D(17, 16), new Point2D(16, 17) },
                ["BestRectOutput"] = new RoiDefinition
                {
                    Id = "stale-rect",
                    Shape = RoiShapeKind.Rectangle,
                    X = 14,
                    Y = 14,
                    Width = 4,
                    Height = 5
                }
            },
            VisionToolKind.MultiTargetMatch => new Dictionary<string, object>
            {
                ["PositionOutput"] = pose,
                ["OriginOutput"] = pose,
                ["BestPositionOutput"] = pose,
                ["ScoreOutput"] = 0.9d,
                ["XOutput"] = 16d,
                ["YOutput"] = 16d,
                ["AngleOutput"] = 0d,
                ["CountOutput"] = 1,
                ["AllPositionsOutput"] = new[] { pose },
                ["ScoresOutput"] = new[] { 0.9d }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        foreach (var output in staleOutputs)
        {
            context.SetPortOutput(definition, output.Key, output.Value);
        }

        if (kind == VisionToolKind.MultiTargetMatch)
        {
            context.Properties["pose"] = pose;
        }
    }

    private static void AssertAllBusinessOutputsAreInaccessible(
        VisionToolContext context,
        VisionToolDefinition outputConsumer,
        VisionToolKind kind)
    {
        foreach (var portKey in GetBusinessOutputPortKeys(kind))
        {
            Assert.False(
                context.TryGetPortInputValue(outputConsumer, $"{portKey}Input", out _),
                $"Stale output {portKey} remained accessible.");
        }

        if (kind == VisionToolKind.MultiTargetMatch)
        {
            Assert.False(context.Properties.ContainsKey("pose"));
        }
    }

    private static double ParseScale(string value)
    {
        return value switch
        {
            "NaN" => double.NaN,
            "0" => 0,
            "-1" => -1,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
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
