using System.Diagnostics;
using System.Globalization;
using System.IO;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class PositionInputScalePreviewTests
{
    private const string SourceToolId = "position-source";
    private const string ConsumerToolId = "position-consumer";

    public static IEnumerable<object[]> PositionInputKinds()
    {
        yield return [VisionToolKind.MultiTargetMatch];
        yield return [VisionToolKind.FindLine];
        yield return [VisionToolKind.FindCircle];
        yield return [VisionToolKind.DefectDetect];
    }

    public static IEnumerable<object[]> InvalidCurrentScaleCases()
    {
        var invalidScales = new[] { "not-a-number", "NaN", "Infinity", "0", "-1" };
        foreach (var kind in PositionInputKinds().Select(item => (VisionToolKind)item[0]))
        {
            foreach (var invalidScale in invalidScales)
            {
                yield return [kind, invalidScale];
            }
        }
    }

    public static IEnumerable<object[]> InvalidReferenceScaleCases()
    {
        var invalidScales = new[] { "NaN", "0", "-1" };
        foreach (var kind in PositionInputKinds().Select(item => (VisionToolKind)item[0]))
        {
            foreach (var invalidScale in invalidScales)
            {
                yield return [kind, invalidScale];
            }
        }
    }

    public static IEnumerable<object[]> RuntimeRoiKinds()
    {
        yield return [VisionToolKind.FindLine];
        yield return [VisionToolKind.FindCircle];
        yield return [VisionToolKind.DefectDetect];
    }

    [Theory]
    [MemberData(nameof(PositionInputKinds))]
    public async Task TeachingPersistsValidScaleAndTreatsMissingScaleAsOne(VisionToolKind kind)
    {
        var explicitScale = CreateHarness(kind, currentScale: "1.1");
        var legacyScale = CreateHarness(kind, currentScale: null);

        Assert.True(await explicitScale.ApplyAsync());
        Assert.True(await legacyScale.ApplyAsync());

        Assert.Equal("1.1", explicitScale.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
        Assert.Equal("1", legacyScale.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Theory]
    [MemberData(nameof(InvalidCurrentScaleCases))]
    public async Task ExplicitInvalidCurrentScaleIsConfigurationErrorAndIsNeverPersisted(
        VisionToolKind kind,
        string invalidScale)
    {
        var harness = CreateHarness(kind, currentScale: invalidScale);

        var applied = await harness.ApplyAsync();

        Assert.False(applied);
        Assert.Contains("PositionInput.Scale", harness.StatusText, StringComparison.Ordinal);
        Assert.Contains("finite and greater than zero", harness.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Theory]
    [MemberData(nameof(PositionInputKinds))]
    public async Task PartialCurrentPoseIsConfigurationErrorAndCannotBeTaught(VisionToolKind kind)
    {
        var harness = CreateHarness(
            kind,
            currentScale: null,
            sourceData: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = "200"
            });

        var applied = await harness.ApplyAsync();

        Assert.False(applied);
        Assert.Contains("PositionInput.Y", harness.StatusText, StringComparison.Ordinal);
        Assert.Contains("finite", harness.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Theory]
    [MemberData(nameof(InvalidReferenceScaleCases))]
    public async Task OnlyScaleCurrentPoseIsExplicitConfigurationError(
        VisionToolKind kind,
        string invalidScale)
    {
        var harness = CreateHarness(
            kind,
            currentScale: null,
            sourceData: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scale"] = invalidScale
            });

        var applied = await harness.ApplyAsync();

        Assert.False(applied);
        Assert.Equal(1, harness.Pipeline.ExecutionCount);
        Assert.Contains("PositionInput.Scale", harness.StatusText, StringComparison.Ordinal);
        Assert.False(harness.Tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Theory]
    [MemberData(nameof(InvalidReferenceScaleCases))]
    public async Task ActiveInvalidReferenceScaleCannotBeSilentlyRetaughtWithoutRoiEdit(
        VisionToolKind kind,
        string invalidScale)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, SourceToolId, invalidScale);
        var harness = CreateHarness(kind, currentScale: "1.1", consumerParameters: parameters);

        var applied = await harness.ApplyAsync();

        Assert.False(applied);
        Assert.Equal(0, harness.Pipeline.ExecutionCount);
        Assert.Contains("roiReferencePoseScale", harness.StatusText, StringComparison.Ordinal);
        Assert.Equal(invalidScale, harness.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Theory]
    [MemberData(nameof(InvalidReferenceScaleCases))]
    public async Task OnlyScaleActiveReferenceIsExplicitConfigurationError(
        VisionToolKind kind,
        string invalidScale)
    {
        var parameters = CreateConsumerParameters();
        parameters["roiReferencePoseScale"] = invalidScale;
        parameters["roiReferencePoseToolId"] = SourceToolId;
        var harness = CreateHarness(kind, currentScale: "1.1", consumerParameters: parameters);

        var applied = await harness.ApplyAsync();

        Assert.False(applied);
        Assert.Equal(0, harness.Pipeline.ExecutionCount);
        Assert.Contains("roiReferencePoseScale", harness.StatusText, StringComparison.Ordinal);
        Assert.Equal(invalidScale, harness.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Theory]
    [MemberData(nameof(PositionInputKinds))]
    public async Task InvalidReferenceFromPreviousSourceIsInactiveAndCanBeRecaptured(VisionToolKind kind)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, "previous-source", "NaN");
        var harness = CreateHarness(kind, currentScale: "1.1", consumerParameters: parameters);

        var applied = await harness.ApplyAsync();

        Assert.True(applied);
        var saved = harness.Tool.ToDefinition().Parameters;
        Assert.Equal("1.1", saved["roiReferencePoseScale"]);
        Assert.Equal(SourceToolId, saved["roiReferencePoseToolId"]);
    }

    [Theory]
    [MemberData(nameof(PositionInputKinds))]
    public async Task MissingRuntimeRoiUsesSharedCurrentToReferenceSimilarityTransform(VisionToolKind kind)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, SourceToolId, "0.5");
        var harness = CreateHarness(
            kind,
            currentScale: "1.1",
            consumerParameters: parameters,
            consumerData: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        await harness.RunAsync();

        Assert.Equal(1, harness.Pipeline.ExecutionCount);
        var reference = new Pose2D(100, 100, 0) { Scale = 0.5 };
        var current = new Pose2D(200, 150, 0) { Scale = 1.1 };
        var expected = PoseSimilarityTransform.MapRoi(harness.Roi, reference, current);
        AssertFallbackGeometry(kind, harness, expected);
    }

    [Theory]
    [MemberData(nameof(RuntimeRoiKinds))]
    public async Task CompleteProductionRuntimeRoiWinsOverLocalFallback(VisionToolKind kind)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, SourceToolId, "0.5");
        var runtimeData = CreateAuthoritativeRuntimeRoiData(kind);
        var harness = CreateHarness(
            kind,
            currentScale: "NaN",
            consumerParameters: parameters,
            consumerData: runtimeData);

        await harness.RunAsync();

        Assert.Equal(1, harness.Pipeline.ExecutionCount);
        Assert.DoesNotContain("PositionInput.Scale", harness.StatusText, StringComparison.Ordinal);
        AssertAuthoritativeRuntimeGeometry(kind, harness);
    }

    [Theory]
    [MemberData(nameof(RuntimeRoiKinds))]
    public async Task PartialRuntimeRoiFailsClosedInsteadOfMixingLocalFallback(VisionToolKind kind)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, SourceToolId, "0.5");
        var harness = CreateHarness(
            kind,
            currentScale: "1.1",
            consumerParameters: parameters,
            consumerData: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["searchRoiX"] = "310"
            });

        await harness.RunAsync();

        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, harness.StatusText, StringComparison.Ordinal);
        Assert.Contains("runtime search ROI", harness.StatusText, StringComparison.OrdinalIgnoreCase);
        AssertNoSearchRoiGeometry(kind, harness.Overlays);
    }

    [Theory]
    [MemberData(nameof(RuntimeRoiKinds))]
    public async Task MalformedRuntimeRoiFailsClosedInsteadOfMixingLocalFallback(VisionToolKind kind)
    {
        var parameters = CreateConsumerParameters();
        AddTaughtReference(parameters, SourceToolId, "0.5");
        var runtimeData = CreateAuthoritativeRuntimeRoiData(kind);
        runtimeData["searchRoiY"] = "NaN";
        var harness = CreateHarness(
            kind,
            currentScale: "1.1",
            consumerParameters: parameters,
            consumerData: runtimeData);

        await harness.RunAsync();

        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, harness.StatusText, StringComparison.Ordinal);
        Assert.Contains("runtime search ROI", harness.StatusText, StringComparison.OrdinalIgnoreCase);
        AssertNoSearchRoiGeometry(kind, harness.Overlays);
    }

    [Fact]
    public async Task HalconSourceReferenceIgnoresConflictingInactiveLegacyNamespace()
    {
        var source = CreateHalconSource(standardScale: "1");
        source.Parameters["standardX"] = "999";
        source.Parameters["standardY"] = "999";
        source.Parameters["standardAngle"] = "90";
        source.Parameters["standardScale"] = "0";
        source.Parameters["templateWidth"] = "100";
        source.Parameters["templateHeight"] = "100";
        var harness = CreateHarness(
            VisionToolKind.MultiTargetMatch,
            currentScale: "1.1",
            source: source);

        await harness.RunAsync();

        Assert.True(harness.Pipeline.ExecutionCount > 0);
        Assert.True(harness.MatchingService.MatchCount > 0);
        Assert.DoesNotContain("standardScale", harness.StatusText, StringComparison.Ordinal);
        Assert.True(await harness.ApplyAsync());
        Assert.Equal("1.1", harness.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task HalconSourceReferenceRejectsInvalidActiveScaleEvenWhenLegacyScaleIsValid()
    {
        var source = CreateHalconSource(standardScale: "0");
        source.Parameters["standardX"] = "100";
        source.Parameters["standardY"] = "100";
        source.Parameters["standardAngle"] = "0";
        source.Parameters["standardScale"] = "1";
        source.Parameters["templateWidth"] = "20";
        source.Parameters["templateHeight"] = "10";
        var harness = CreateHarness(
            VisionToolKind.MultiTargetMatch,
            currentScale: "1.1",
            source: source);

        await harness.RunAsync();

        Assert.Equal(0, harness.Pipeline.ExecutionCount);
        Assert.Equal(0, harness.MatchingService.MatchCount);
        Assert.Contains("halcon.standardScale", harness.StatusText, StringComparison.Ordinal);
    }

    private static DialogHarness CreateHarness(
        VisionToolKind kind,
        string? currentScale,
        Dictionary<string, string>? consumerParameters = null,
        IReadOnlyDictionary<string, string>? consumerData = null,
        VisionToolDefinition? source = null,
        IReadOnlyDictionary<string, string>? sourceData = null)
    {
        var frame = CreateFrame();
        var roi = CreateRoi(kind);
        var parameters = consumerParameters ?? CreateConsumerParameters();
        parameters["input:PositionInput:toolId"] = SourceToolId;
        parameters["input:PositionInput:portKey"] = "PositionOutput";
        var tool = new VisionToolItem
        {
            Id = ConsumerToolId,
            Name = kind.ToString(),
            Kind = kind,
            Enabled = true,
            RoiId = roi.Id,
            ParametersText = FormatParameters(parameters)
        };
        source ??= new VisionToolDefinition
        {
            Id = SourceToolId,
            Name = "Position source",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true
        };
        var recipe = new Recipe
        {
            Id = "position-preview-recipe",
            Name = "Position preview",
            Tools = [source, tool.ToDefinition()],
            Rois = [roi]
        };
        var pipeline = new PreviewPipeline(
            frame,
            source.Id,
            tool.Id,
            currentScale,
            consumerData ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            sourceData);
        var matchingService = new NoMatchTemplateMatchingService();

        return kind switch
        {
            VisionToolKind.MultiTargetMatch => CreateTemplateHarness(
                tool,
                roi,
                frame,
                recipe,
                pipeline,
                matchingService),
            VisionToolKind.FindLine => CreateFindLineHarness(tool, roi, frame, recipe, pipeline, matchingService),
            VisionToolKind.FindCircle => CreateFindCircleHarness(tool, roi, frame, recipe, pipeline, matchingService),
            VisionToolKind.DefectDetect => CreateBlobHarness(tool, roi, frame, recipe, pipeline, matchingService),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static DialogHarness CreateTemplateHarness(
        VisionToolItem tool,
        RoiDefinition roi,
        ImageFrame frame,
        Recipe recipe,
        PreviewPipeline pipeline,
        NoMatchTemplateMatchingService matchingService)
    {
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [roi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            recipe,
            pipeline,
            matchingService,
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

        return new DialogHarness(
            VisionToolKind.MultiTargetMatch,
            tool,
            roi,
            frame,
            pipeline,
            matchingService,
            async () =>
            {
                if (!await viewModel.PrepareToCloseAsync())
                {
                    return false;
                }

                return viewModel.ApplyTo(tool);
            },
            () => viewModel.RunToolCommand.Execute(CancellationToken.None),
            () => viewModel.StatusText,
            () => viewModel.PreviewOverlays.ToArray());
    }

    private static DialogHarness CreateFindLineHarness(
        VisionToolItem tool,
        RoiDefinition roi,
        ImageFrame frame,
        Recipe recipe,
        PreviewPipeline pipeline,
        NoMatchTemplateMatchingService matchingService)
    {
        var viewModel = new FindLineToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            recipe,
            pipeline,
            new NullAppLogService());
        return new DialogHarness(
            VisionToolKind.FindLine,
            tool,
            roi,
            frame,
            pipeline,
            matchingService,
            () => viewModel.ApplyToAsync(tool),
            () => RunPrismCommandAsync(viewModel.RunToolCommand.Execute, () => viewModel.IsBusy, pipeline),
            () => viewModel.StatusText,
            () => viewModel.PreviewOverlays.ToArray());
    }

    private static DialogHarness CreateFindCircleHarness(
        VisionToolItem tool,
        RoiDefinition roi,
        ImageFrame frame,
        Recipe recipe,
        PreviewPipeline pipeline,
        NoMatchTemplateMatchingService matchingService)
    {
        var viewModel = new FindCircleToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            recipe,
            pipeline,
            new NullAppLogService());
        return new DialogHarness(
            VisionToolKind.FindCircle,
            tool,
            roi,
            frame,
            pipeline,
            matchingService,
            () => viewModel.ApplyToAsync(tool),
            () => RunPrismCommandAsync(viewModel.RunToolCommand.Execute, () => viewModel.IsBusy, pipeline),
            () => viewModel.StatusText,
            () => viewModel.PreviewOverlays.ToArray());
    }

    private static DialogHarness CreateBlobHarness(
        VisionToolItem tool,
        RoiDefinition roi,
        ImageFrame frame,
        Recipe recipe,
        PreviewPipeline pipeline,
        NoMatchTemplateMatchingService matchingService)
    {
        var viewModel = new BlobAnalysisToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            recipe,
            pipeline,
            new NullAppLogService());
        return new DialogHarness(
            VisionToolKind.DefectDetect,
            tool,
            roi,
            frame,
            pipeline,
            matchingService,
            () => viewModel.ApplyToAsync(tool),
            () => RunPrismCommandAsync(viewModel.RunToolCommand.Execute, () => viewModel.IsBusy, pipeline),
            () => viewModel.StatusText,
            () => viewModel.PreviewOverlays.ToArray());
    }

    private static async Task RunPrismCommandAsync(
        Action execute,
        Func<bool> isBusy,
        PreviewPipeline pipeline)
    {
        var previousExecutionCount = pipeline.ExecutionCount;
        execute();
        await WaitUntilAsync(
            () => pipeline.ExecutionCount > previousExecutionCount && !isBusy(),
            "Position-input preview did not finish.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), failureMessage);
    }

    private static void AssertFallbackGeometry(
        VisionToolKind kind,
        DialogHarness harness,
        RoiDefinition expected)
    {
        var overlays = harness.Overlays;
        switch (kind)
        {
            case VisionToolKind.MultiTargetMatch:
                {
                    var search = TemplateMatcher.GetSearchRegion(harness.Frame, expected);
                    var overlay = Assert.Single(overlays, item =>
                        item.Kind == VisionOverlayKind.Rectangle && item.State == VisionOverlayState.Warning);
                    Assert.Equal(search.X, overlay.X, 6);
                    Assert.Equal(search.Y, overlay.Y, 6);
                    Assert.Equal(search.Width, overlay.Width, 6);
                    Assert.Equal(search.Height, overlay.Height, 6);
                    break;
                }
            case VisionToolKind.FindLine:
                {
                    var calipers = overlays
                        .Where(item => item.Kind == VisionOverlayKind.RotatedRectangle &&
                                       item.State == VisionOverlayState.Neutral)
                        .ToArray();
                    Assert.NotEmpty(calipers);
                    Assert.All(calipers, item => Assert.Equal(expected.Width, item.Width, 6));
                    Assert.Equal(expected.Angle, calipers[0].Angle, 6);
                    Assert.Equal(expected.Height * (calipers.Length - 1d) / calipers.Length,
                        calipers.Max(item => item.Y) - calipers.Min(item => item.Y),
                        6);
                    break;
                }
            case VisionToolKind.FindCircle:
                {
                    var annulus = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.CircleAnnulus);
                    Assert.Equal(expected.X, annulus.X, 6);
                    Assert.Equal(expected.Y, annulus.Y, 6);
                    Assert.Equal(expected.Radius, (annulus.Width + annulus.Radius) / 2d, 6);
                    break;
                }
            case VisionToolKind.DefectDetect:
                {
                    var overlay = Assert.Single(overlays, item => item.State == VisionOverlayState.Neutral);
                    Assert.Equal(VisionOverlayKind.RotatedRectangle, overlay.Kind);
                    Assert.Equal(expected.X, overlay.X, 6);
                    Assert.Equal(expected.Y, overlay.Y, 6);
                    Assert.Equal(expected.Width, overlay.Width, 6);
                    Assert.Equal(expected.Height, overlay.Height, 6);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void AssertAuthoritativeRuntimeGeometry(VisionToolKind kind, DialogHarness harness)
    {
        var overlays = harness.Overlays;
        switch (kind)
        {
            case VisionToolKind.FindLine:
                {
                    var runtimeRois = overlays.Where(item =>
                        item.Kind == VisionOverlayKind.RotatedRectangle &&
                        item.State == VisionOverlayState.Warning).ToArray();
                    Assert.NotEmpty(runtimeRois);
                    Assert.All(runtimeRois, roi =>
                    {
                        Assert.Equal(310, roi.X, 6);
                        Assert.Equal(220, roi.Y, 6);
                        Assert.Equal(77, roi.Width, 6);
                        Assert.Equal(33, roi.Height, 6);
                        Assert.Equal(12, roi.Angle, 6);
                    });
                    break;
                }
            case VisionToolKind.FindCircle:
                {
                    var annulus = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.CircleAnnulus);
                    Assert.Equal(310, annulus.X, 6);
                    Assert.Equal(220, annulus.Y, 6);
                    Assert.Equal(41, (annulus.Width + annulus.Radius) / 2d, 6);
                    break;
                }
            case VisionToolKind.DefectDetect:
                {
                    var roi = Assert.Single(overlays, item =>
                        item.Kind == VisionOverlayKind.Circle && item.State == VisionOverlayState.Neutral);
                    Assert.Equal(310, roi.X, 6);
                    Assert.Equal(220, roi.Y, 6);
                    Assert.Equal(41, roi.Radius, 6);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void AssertNoSearchRoiGeometry(
        VisionToolKind kind,
        IReadOnlyList<VisionOverlayItem> overlays)
    {
        switch (kind)
        {
            case VisionToolKind.FindLine:
                Assert.DoesNotContain(overlays, item =>
                    item.Kind == VisionOverlayKind.RotatedRectangle &&
                    item.State == VisionOverlayState.Neutral);
                Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.Cross);
                break;
            case VisionToolKind.FindCircle:
                Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.CircleAnnulus);
                Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.Cross);
                break;
            case VisionToolKind.DefectDetect:
                Assert.DoesNotContain(overlays, item => item.State == VisionOverlayState.Neutral);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static Dictionary<string, string> CreateAuthoritativeRuntimeRoiData(VisionToolKind kind)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["searchRoiX"] = "310",
            ["searchRoiY"] = "220"
        };
        switch (kind)
        {
            case VisionToolKind.FindLine:
                data["searchRoiShape"] = RoiShapeKind.RotatedRectangle.ToString();
                data["searchRoiWidth"] = "77";
                data["searchRoiHeight"] = "33";
                data["searchRoiAngle"] = "12";
                break;
            case VisionToolKind.FindCircle:
            case VisionToolKind.DefectDetect:
                data["searchRoiShape"] = RoiShapeKind.Circle.ToString();
                data["searchRoiRadius"] = "41";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return data;
    }

    private static Dictionary<string, string> CreateConsumerParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["multiMatchMode"] = "Shape",
            ["matchCount"] = "2",
            ["showSearchRegion"] = "true"
        };
    }

    private static void AddTaughtReference(
        IDictionary<string, string> parameters,
        string referenceToolId,
        string scale)
    {
        parameters["roiReferencePoseX"] = "100";
        parameters["roiReferencePoseY"] = "100";
        parameters["roiReferencePoseAngle"] = "0";
        parameters["roiReferencePoseScale"] = scale;
        parameters["roiReferencePoseToolId"] = referenceToolId;
    }

    private static VisionToolDefinition CreateHalconSource(string standardScale)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "Halcon"
        };
        TemplateModelParameterCodec.WriteHalcon(
            parameters,
            new HalconTemplateModelState(
                new TemplateModelReference(
                    "recipes/recipe/flows/flow/source/model.shm",
                    "recipes/recipe/flows/flow/source/model.json",
                    TemplateModelParameterCodec.HalconScaledShapeModelFormat,
                    new string('a', 64),
                    new string('b', 64),
                    "generation-1",
                    "1",
                    "26.05",
                    new string('c', 64)),
                new TemplateLearnedGeometry(
                    new Pose2D(100, 100, 0),
                    20,
                    10)));
        parameters["halcon.standardScale"] = standardScale;
        return new VisionToolDefinition
        {
            Id = SourceToolId,
            Name = "HALCON source",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true,
            Parameters = parameters
        };
    }

    private static RoiDefinition CreateRoi(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.FindLine => new RoiDefinition
            {
                Id = "preview-roi",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 110,
                Y = 105,
                Width = 20,
                Height = 10,
                Angle = 0
            },
            VisionToolKind.FindCircle => new RoiDefinition
            {
                Id = "preview-roi",
                Shape = RoiShapeKind.Circle,
                X = 110,
                Y = 105,
                Radius = 10
            },
            _ => new RoiDefinition
            {
                Id = "preview-roi",
                Shape = RoiShapeKind.Rectangle,
                X = 105,
                Y = 105,
                Width = 20,
                Height = 10
            }
        };
    }

    private static ImageFrame CreateFrame()
    {
        return new ImageFrame(
            "position-preview-frame",
            640,
            480,
            640,
            PixelFormatKind.Gray8,
            new byte[640 * 480],
            DateTimeOffset.UtcNow,
            "test");
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"));
    }

    private sealed class PreviewPipeline(
        ImageFrame frame,
        string sourceToolId,
        string consumerToolId,
        string? scale,
        IReadOnlyDictionary<string, string> consumerData,
        IReadOnlyDictionary<string, string>? sourceData) : IVisionPipeline
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame inputFrame,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            var poseData = sourceData is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x"] = "200",
                    ["y"] = "150",
                    ["angle"] = "0"
                }
                : new Dictionary<string, string>(sourceData, StringComparer.OrdinalIgnoreCase);
            if (sourceData is null && scale is not null)
            {
                poseData["scale"] = scale;
            }

            return Task.FromResult(new VisionPipelineResult(
                frame,
                [
                    new ToolResult
                    {
                        ToolId = sourceToolId,
                        ToolName = "Position source",
                        Kind = VisionToolKind.TemplateLocate,
                        Outcome = InspectionOutcome.Ok,
                        Data = poseData
                    },
                    new ToolResult
                    {
                        ToolId = consumerToolId,
                        ToolName = "Position consumer",
                        Kind = VisionToolKind.DefectDetect,
                        Outcome = InspectionOutcome.Ng,
                        Message = "Synthetic preview result",
                        Data = new Dictionary<string, string>(consumerData, StringComparer.OrdinalIgnoreCase)
                    }
                ],
                InspectionOutcome.Ng,
                "Synthetic preview",
                string.Empty));
        }
    }

    private sealed class NoMatchTemplateMatchingService : ITemplateMatchingService
    {
        private int _matchCount;

        public int MatchCount => Volatile.Read(ref _matchCount);

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Learning is not expected in position-input preview tests.");
        }

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _matchCount);
            return Task.FromResult(new TemplateMatchBatchResult(
                TemplateMatchingEngine.OpenCv,
                InspectionOutcome.Ng,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                new TemplateSearchRegion(0, 0, request.Frame.Width, request.Frame.Height),
                "No match",
                false));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed record DialogHarness(
        VisionToolKind Kind,
        VisionToolItem Tool,
        RoiDefinition Roi,
        ImageFrame Frame,
        PreviewPipeline Pipeline,
        NoMatchTemplateMatchingService MatchingService,
        Func<Task<bool>> Apply,
        Func<Task> Run,
        Func<string> ReadStatus,
        Func<IReadOnlyList<VisionOverlayItem>> ReadOverlays)
    {
        public string StatusText => ReadStatus();

        public IReadOnlyList<VisionOverlayItem> Overlays => ReadOverlays();

        public Task<bool> ApplyAsync() => Apply();

        public Task RunAsync() => Run();
    }

    private sealed class NullAppLogService : IAppLogService
    {
        public event EventHandler<AppLogEntry>? LogWritten
        {
            add { }
            remove { }
        }

        public void Info(string source, string message)
        {
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }

        public void Critical(string source, string message)
        {
        }

        public IReadOnlyList<AppLogEntry> Recent(int count)
        {
            return Array.Empty<AppLogEntry>();
        }
    }
}
