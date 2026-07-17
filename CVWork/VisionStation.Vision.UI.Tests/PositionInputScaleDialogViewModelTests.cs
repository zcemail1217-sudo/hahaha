using System.IO;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class PositionInputScaleDialogViewModelTests
{
    [Fact]
    public async Task FindLineApplyCapturesPositionInputScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
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
        var tool = new VisionToolItem
        {
            Id = "find-line",
            Name = "Find line",
            Kind = VisionToolKind.FindLine,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "input:PositionInput:portKey=PositionOutput"
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(100, 200, 30) { Scale = 0.0004 });
        var viewModel = new FindLineToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            pipeline,
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("0.0004", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task FindCircleApplyCapturesPositionInputScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
        };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        var tool = new VisionToolItem
        {
            Id = "find-circle",
            Name = "Find circle",
            Kind = VisionToolKind.FindCircle,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "input:PositionInput:portKey=PositionOutput"
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(100, 200, 30) { Scale = 0.0004 });
        var viewModel = new FindCircleToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            pipeline,
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("0.0004", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task BlobAnalysisApplyCapturesPositionInputScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
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
        var tool = new VisionToolItem
        {
            Id = "blob-analysis",
            Name = "Blob analysis",
            Kind = VisionToolKind.DefectDetect,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "input:PositionInput:portKey=PositionOutput"
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(100, 200, 30) { Scale = 0.0004 });
        var viewModel = new BlobAnalysisToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            pipeline,
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("0.0004", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task MultiTargetPrepareToCloseCapturesPositionInputScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
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
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Name = "Multi target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "input:PositionInput:portKey=PositionOutput"
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(100, 200, 30) { Scale = 0.0004 });
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [roi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            pipeline);

        var prepared = await viewModel.PrepareToCloseAsync();
        viewModel.ApplyTo(tool);

        Assert.True(prepared);
        Assert.Equal("0.0004", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task FindLineApplyWithoutPositionInputRemovesReferenceScale()
    {
        var frame = CreateFrame();
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.RotatedRectangle,
            X = 16,
            Y = 16,
            Width = 20,
            Height = 10
        };
        var tool = new VisionToolItem
        {
            Id = "find-line",
            Kind = VisionToolKind.FindLine,
            RoiId = roi.Id,
            ParametersText = "roiReferencePoseScale=1.1"
        };
        var viewModel = new FindLineToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe(),
            new PoseResultPipeline(frame, "unused", new Pose2D(0, 0, 0)),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.False(tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Fact]
    public async Task FindLineApplyRecapturesExplicitInvalidReferenceScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.RotatedRectangle,
            X = 16,
            Y = 16,
            Width = 20,
            Height = 10
        };
        var tool = new VisionToolItem
        {
            Id = "find-line",
            Kind = VisionToolKind.FindLine,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=0; " +
                "roiReferencePoseToolId=position-source"
        };
        var viewModel = new FindLineToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            new PoseResultPipeline(frame, source.Id, new Pose2D(100, 200, 30) { Scale = 1.1 }),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("1.1", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task FindCircleApplyWithoutPositionInputRemovesReferenceScale()
    {
        var frame = CreateFrame();
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        var tool = new VisionToolItem
        {
            Id = "find-circle",
            Kind = VisionToolKind.FindCircle,
            RoiId = roi.Id,
            ParametersText = "roiReferencePoseScale=1.1"
        };
        var viewModel = new FindCircleToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe(),
            new PoseResultPipeline(frame, "unused", new Pose2D(0, 0, 0)),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.False(tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Fact]
    public async Task FindCircleApplyRecapturesExplicitInvalidReferenceScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        var tool = new VisionToolItem
        {
            Id = "find-circle",
            Kind = VisionToolKind.FindCircle,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=NaN; " +
                "roiReferencePoseToolId=position-source"
        };
        var viewModel = new FindCircleToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            new PoseResultPipeline(frame, source.Id, new Pose2D(100, 200, 30) { Scale = 0.9 }),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("0.9", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    public async Task FindGeometryApplyWithoutPositionInputRemovesCompleteReference(VisionToolKind toolKind)
    {
        var scenario = await ApplyFindGeometryDialogAsync(
            toolKind,
            sourceToolId: string.Empty,
            referenceToolId: "old-source",
            referenceScale: "1.1",
            currentPose: new Pose2D(20, 16, 30) { Scale = 1.25 });

        Assert.True(scenario.Applied);
        Assert.Equal(0, scenario.Pipeline.ExecutionCount);
        Assert.DoesNotContain(
            scenario.Tool.ToDefinition().Parameters.Keys,
            key => key.StartsWith("roiReferencePose", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    public async Task FindGeometryApplyRecapturesReferenceAfterPositionInputSourceChanges(VisionToolKind toolKind)
    {
        var scenario = await ApplyFindGeometryDialogAsync(
            toolKind,
            sourceToolId: "new-source",
            referenceToolId: "old-source",
            referenceScale: "0.5",
            currentPose: new Pose2D(20, 16, 30) { Scale = 1.25 });

        Assert.True(scenario.Applied);
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        var saved = scenario.Tool.ToDefinition().Parameters;
        Assert.Equal("20", saved["roiReferencePoseX"]);
        Assert.Equal("16", saved["roiReferencePoseY"]);
        Assert.Equal("30", saved["roiReferencePoseAngle"]);
        Assert.Equal("1.25", saved["roiReferencePoseScale"]);
        Assert.Equal("new-source", saved["roiReferencePoseToolId"]);
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine, "not-a-number")]
    [InlineData(VisionToolKind.FindLine, "NaN")]
    [InlineData(VisionToolKind.FindCircle, "not-a-number")]
    [InlineData(VisionToolKind.FindCircle, "NaN")]
    public async Task FindGeometryApplyIgnoresStaleInvalidReferenceScaleFromPreviousSource(
        VisionToolKind toolKind,
        string staleScale)
    {
        var scenario = await ApplyFindGeometryDialogAsync(
            toolKind,
            sourceToolId: "new-source",
            referenceToolId: "old-source",
            referenceScale: staleScale,
            currentPose: new Pose2D(20, 16, 30) { Scale = 1.2 });

        Assert.True(scenario.Applied);
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        var saved = scenario.Tool.ToDefinition().Parameters;
        Assert.Equal("20", saved["roiReferencePoseX"]);
        Assert.Equal("16", saved["roiReferencePoseY"]);
        Assert.Equal("30", saved["roiReferencePoseAngle"]);
        Assert.Equal("1.2", saved["roiReferencePoseScale"]);
        Assert.Equal("new-source", saved["roiReferencePoseToolId"]);
    }

    [Theory]
    [InlineData(VisionToolKind.FindLine)]
    [InlineData(VisionToolKind.FindCircle)]
    public async Task FindGeometryLegacyReferenceWithoutToolIdRemainsCompatible(VisionToolKind toolKind)
    {
        var scenario = await ApplyFindGeometryDialogAsync(
            toolKind,
            sourceToolId: "position-source",
            referenceToolId: null,
            referenceScale: "1.1",
            currentPose: new Pose2D(20, 16, 30) { Scale = 1.25 });

        Assert.True(scenario.Applied);
        Assert.Equal(0, scenario.Pipeline.ExecutionCount);
        var saved = scenario.Tool.ToDefinition().Parameters;
        Assert.Equal("10", saved["roiReferencePoseX"]);
        Assert.Equal("8", saved["roiReferencePoseY"]);
        Assert.Equal("5", saved["roiReferencePoseAngle"]);
        Assert.Equal("1.1", saved["roiReferencePoseScale"]);
        Assert.False(saved.ContainsKey("roiReferencePoseToolId"));
    }

    [Fact]
    public async Task BlobAnalysisApplyWithoutPositionInputRemovesReferenceScale()
    {
        var frame = CreateFrame();
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        var tool = new VisionToolItem
        {
            Id = "blob-analysis",
            Kind = VisionToolKind.DefectDetect,
            RoiId = roi.Id,
            ParametersText = "roiReferencePoseScale=1.1"
        };
        var viewModel = new BlobAnalysisToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe(),
            new PoseResultPipeline(frame, "unused", new Pose2D(0, 0, 0)),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.False(tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Fact]
    public async Task BlobAnalysisApplyRecapturesExplicitInvalidReferenceScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        var tool = new VisionToolItem
        {
            Id = "blob-analysis",
            Kind = VisionToolKind.DefectDetect,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=-1; " +
                "roiReferencePoseToolId=position-source"
        };
        var viewModel = new BlobAnalysisToolDialogViewModel(
            tool,
            [roi],
            "Flow",
            frame,
            new Recipe { Tools = [source] },
            new PoseResultPipeline(frame, source.Id, new Pose2D(100, 200, 30) { Scale = 1.25 }),
            new NullAppLogService());

        var applied = await viewModel.ApplyToAsync(tool);

        Assert.True(applied);
        Assert.Equal("1.25", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public async Task MultiTargetPrepareWithoutPositionInputRemovesCompleteReference()
    {
        var frame = CreateFrame();
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            ParametersText =
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=1.1; " +
                "roiReferencePoseToolId=old-source"
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe(),
            new PoseResultPipeline(frame, "unused", new Pose2D(0, 0, 0)));

        var prepared = await viewModel.PrepareToCloseAsync();
        viewModel.ApplyTo(tool);

        Assert.True(prepared);
        Assert.DoesNotContain(
            tool.ToDefinition().Parameters.Keys,
            key => key.StartsWith("roiReferencePose", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultiTargetPrepareRecapturesExplicitInvalidReferenceScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = roi.Id,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=0; " +
                "roiReferencePoseToolId=position-source"
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [roi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            new PoseResultPipeline(frame, source.Id, new Pose2D(100, 200, 30) { Scale = 1.2 }));

        var prepared = await viewModel.PrepareToCloseAsync();
        viewModel.ApplyTo(tool);

        Assert.True(prepared);
        Assert.Equal("1.2", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Fact]
    public void MultiTargetRunRecapturesReferenceAfterPositionInputSourceChanges()
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: "0.5",
            sourceStandardScale: null,
            currentScale: 1.25,
            sourceToolId: "new-source",
            referenceToolId: "old-source");

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !scenario.ViewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        var searchOverlay = Assert.Single(
            scenario.ViewModel.PreviewOverlays,
            overlay => overlay.Kind == VisionOverlayKind.Rectangle &&
                       overlay.State == VisionOverlayState.Warning);
        Assert.Equal(4, searchOverlay.X, 6);
        Assert.Equal(4, searchOverlay.Y, 6);
        Assert.Equal(12, searchOverlay.Width, 6);
        Assert.Equal(10, searchOverlay.Height, 6);

        scenario.ViewModel.ApplyTo(scenario.Tool);
        var saved = scenario.Tool.ToDefinition().Parameters;
        Assert.Equal("20", saved["roiReferencePoseX"]);
        Assert.Equal("16", saved["roiReferencePoseY"]);
        Assert.Equal("1.25", saved["roiReferencePoseScale"]);
        Assert.Equal("new-source", saved["roiReferencePoseToolId"]);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    public void MultiTargetRunIgnoresStaleInvalidReferenceScaleFromPreviousSource(string staleScale)
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: staleScale,
            sourceStandardScale: null,
            currentScale: 1.2,
            sourceToolId: "new-source",
            referenceToolId: "old-source");

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !scenario.ViewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        Assert.DoesNotContain("roiReferencePoseScale must", scenario.ViewModel.StatusText);
        scenario.ViewModel.ApplyTo(scenario.Tool);
        var saved = scenario.Tool.ToDefinition().Parameters;
        Assert.Equal("1.2", saved["roiReferencePoseScale"]);
        Assert.Equal("new-source", saved["roiReferencePoseToolId"]);
    }

    [Fact]
    public async Task MultiTargetLegacyReferenceWithoutToolIdRemainsCompatible()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            ParametersText =
                "input:PositionInput:toolId=position-source; " +
                "roiReferencePoseX=100; roiReferencePoseY=200; " +
                "roiReferencePoseAngle=30; roiReferencePoseScale=1.1"
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            new UnexpectedPipeline());

        Assert.True(await viewModel.PrepareToCloseAsync());
        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("100", saved["roiReferencePoseX"]);
        Assert.Equal("200", saved["roiReferencePoseY"]);
        Assert.Equal("1.1", saved["roiReferencePoseScale"]);
        Assert.False(saved.ContainsKey("roiReferencePoseToolId"));
    }

    [Fact]
    public async Task LegacyReferencesWithoutScaleRemainValidAcrossPositionInputDialogs()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var recipe = new Recipe { Tools = [source] };
        const string legacyReference =
            "input:PositionInput:toolId=position-source; " +
            "roiReferencePoseX=100; roiReferencePoseY=200; " +
            "roiReferencePoseAngle=30; roiReferencePoseToolId=position-source";
        var lineRoi = new RoiDefinition
        {
            Id = "line-roi",
            Shape = RoiShapeKind.RotatedRectangle,
            X = 16,
            Y = 16,
            Width = 20,
            Height = 10
        };
        var circleRoi = new RoiDefinition
        {
            Id = "circle-roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        var areaRoi = new RoiDefinition
        {
            Id = "area-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        var lineTool = new VisionToolItem
        {
            Id = "find-line",
            Kind = VisionToolKind.FindLine,
            RoiId = lineRoi.Id,
            ParametersText = legacyReference
        };
        var circleTool = new VisionToolItem
        {
            Id = "find-circle",
            Kind = VisionToolKind.FindCircle,
            RoiId = circleRoi.Id,
            ParametersText = legacyReference
        };
        var blobTool = new VisionToolItem
        {
            Id = "blob",
            Kind = VisionToolKind.DefectDetect,
            RoiId = areaRoi.Id,
            ParametersText = legacyReference
        };
        var multiTargetTool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = areaRoi.Id,
            ParametersText = legacyReference
        };
        var pipeline = new UnexpectedPipeline();
        var lineViewModel = new FindLineToolDialogViewModel(
            lineTool, [lineRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var circleViewModel = new FindCircleToolDialogViewModel(
            circleTool, [circleRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var blobViewModel = new BlobAnalysisToolDialogViewModel(
            blobTool, [areaRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var multiTargetViewModel = new TemplateLocateToolDialogViewModel(
            multiTargetTool,
            Array.Empty<RoiChoiceItem>(),
            [areaRoi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            recipe,
            pipeline);

        Assert.True(await lineViewModel.ApplyToAsync(lineTool));
        Assert.True(await circleViewModel.ApplyToAsync(circleTool));
        Assert.True(await blobViewModel.ApplyToAsync(blobTool));
        Assert.True(await multiTargetViewModel.PrepareToCloseAsync());
        multiTargetViewModel.ApplyTo(multiTargetTool);

        Assert.All(
            new[] { lineTool, circleTool, blobTool, multiTargetTool },
            tool => Assert.False(tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale")));
    }

    [Fact]
    public async Task PositionInputDialogsRejectExplicitInvalidCurrentScale()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var recipe = new Recipe { Tools = [source] };
        var lineRoi = new RoiDefinition
        {
            Id = "line-roi",
            Shape = RoiShapeKind.RotatedRectangle,
            X = 16,
            Y = 16,
            Width = 20,
            Height = 10
        };
        var circleRoi = new RoiDefinition
        {
            Id = "circle-roi",
            Shape = RoiShapeKind.Circle,
            X = 16,
            Y = 16,
            Radius = 8
        };
        var areaRoi = new RoiDefinition
        {
            Id = "area-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 24,
            Height = 24
        };
        var lineTool = CreatePositionInputTool("find-line", VisionToolKind.FindLine, lineRoi.Id);
        var circleTool = CreatePositionInputTool("find-circle", VisionToolKind.FindCircle, circleRoi.Id);
        var blobTool = CreatePositionInputTool("blob", VisionToolKind.DefectDetect, areaRoi.Id);
        var multiTargetTool = CreatePositionInputTool("multi-target", VisionToolKind.MultiTargetMatch, areaRoi.Id);
        var pipeline = new PoseResultPipeline(frame, source.Id, new Pose2D(100, 200, 30) { Scale = 0 });
        var lineViewModel = new FindLineToolDialogViewModel(
            lineTool, [lineRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var circleViewModel = new FindCircleToolDialogViewModel(
            circleTool, [circleRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var blobViewModel = new BlobAnalysisToolDialogViewModel(
            blobTool, [areaRoi], "Flow", frame, recipe, pipeline, new NullAppLogService());
        var multiTargetViewModel = new TemplateLocateToolDialogViewModel(
            multiTargetTool,
            Array.Empty<RoiChoiceItem>(),
            [areaRoi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            recipe,
            pipeline);

        Assert.False(await lineViewModel.ApplyToAsync(lineTool));
        Assert.False(await circleViewModel.ApplyToAsync(circleTool));
        Assert.False(await blobViewModel.ApplyToAsync(blobTool));
        Assert.False(await multiTargetViewModel.PrepareToCloseAsync());
    }

    [Fact]
    public void MultiTargetRunPreviewsScaleAwareMappedSearchRoi()
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "area-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 12,
            // RoiEditor normalizes rectangle height to a minimum of 10; use that effective value explicitly.
            Height = 10
        };
        var parameters = new Dictionary<string, string>(TemplateMatcher.Learn(
            frame,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            }), StringComparer.OrdinalIgnoreCase)
        {
            ["input:PositionInput:toolId"] = source.Id,
            ["input:PositionInput:portKey"] = "PositionOutput",
            ["roiReferencePoseX"] = "10",
            ["roiReferencePoseY"] = "8",
            ["roiReferencePoseAngle"] = "0",
            ["roiReferencePoseScale"] = "0.5",
            ["roiReferencePoseToolId"] = source.Id
        };
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = roi.Id,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [roi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            new PoseResultPipeline(frame, source.Id, new Pose2D(20, 16, 0)));

        viewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {viewModel.StatusText}");
        var searchOverlay = Assert.Single(viewModel.PreviewOverlays, overlay =>
            overlay.Kind == VisionOverlayKind.Rectangle && overlay.State == VisionOverlayState.Warning);
        Assert.Equal(8, searchOverlay.X, 6);
        Assert.Equal(8, searchOverlay.Y, 6);
        Assert.Equal(24, searchOverlay.Width, 6);
        Assert.Equal(20, searchOverlay.Height, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void MultiTargetRunRejectsInvalidCurrentScaleInsteadOfMatchingOriginalRoi(double invalidScale)
    {
        var frame = CreateFrame();
        var source = new VisionToolDefinition { Id = "position-source", Kind = VisionToolKind.TemplateLocate };
        var roi = new RoiDefinition
        {
            Id = "area-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 12,
            Height = 10
        };
        var parameters = new Dictionary<string, string>(TemplateMatcher.Learn(
            frame,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            }), StringComparer.OrdinalIgnoreCase)
        {
            ["input:PositionInput:toolId"] = source.Id,
            ["input:PositionInput:portKey"] = "PositionOutput",
            ["roiReferencePoseX"] = "10",
            ["roiReferencePoseY"] = "8",
            ["roiReferencePoseAngle"] = "0",
            ["roiReferencePoseScale"] = "0.5",
            ["roiReferencePoseToolId"] = source.Id
        };
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = roi.Id,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(20, 16, 0) { Scale = invalidScale });
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [roi],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            pipeline);

        viewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => pipeline.ExecutionCount == 1 && !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {viewModel.StatusText}");
        Assert.Equal("-", viewModel.ScoreText);
        Assert.Equal(
            "Position input mapping failed: PositionInput.Scale must be finite and greater than zero.",
            viewModel.StatusText);
        var searchOverlay = Assert.Single(viewModel.PreviewOverlays);
        Assert.Equal(VisionOverlayState.Warning, searchOverlay.State);
        Assert.Equal(4, searchOverlay.X, 6);
        Assert.Equal(4, searchOverlay.Y, 6);
        Assert.Equal(12, searchOverlay.Width, 6);
        Assert.Equal(10, searchOverlay.Height, 6);
        viewModel.ApplyTo(tool);
        Assert.Equal("0.5", tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    public static IEnumerable<object[]> InvalidConfiguredScaleCases()
    {
        foreach (var invalidScale in new[] { "NaN", "0", "-1" })
        {
            yield return ["roiReferencePoseScale", invalidScale];
            yield return ["standardScale", invalidScale];
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void MultiTargetRunDoesNotCaptureOrSaveInvalidCurrentScale(double invalidScale)
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: null,
            sourceStandardScale: null,
            currentScale: invalidScale);

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(
                () => scenario.Pipeline.ExecutionCount == 1 && !scenario.ViewModel.IsBusy,
                TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal("-", scenario.ViewModel.ScoreText);
        Assert.Equal(
            "Position input mapping failed: PositionInput.Scale must be finite and greater than zero.",
            scenario.ViewModel.StatusText);
        Assert.Single(scenario.ViewModel.PreviewOverlays);

        scenario.ViewModel.ApplyTo(scenario.Tool);
        Assert.False(scenario.Tool.ToDefinition().Parameters.ContainsKey("roiReferencePoseScale"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void MultiTargetRunWithoutRoiStillRejectsInvalidCurrentScaleBeforeMatcher(double invalidScale)
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: "0.5",
            sourceStandardScale: null,
            currentScale: invalidScale,
            hasBoundRoi: false);

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !scenario.ViewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        Assert.Equal("-", scenario.ViewModel.ScoreText);
        Assert.Empty(scenario.ViewModel.MultiTargetResultPoints);
        Assert.Equal(
            "Position input mapping failed: PositionInput.Scale must be finite and greater than zero.",
            scenario.ViewModel.StatusText);
        Assert.DoesNotContain(
            scenario.ViewModel.PreviewOverlays,
            overlay => overlay.State is VisionOverlayState.Ok or VisionOverlayState.Ng);
    }

    [Fact]
    public void MultiTargetRunWithoutRoiReusesCapturedCurrentPose()
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: null,
            sourceStandardScale: null,
            currentScale: 1.2,
            hasBoundRoi: false);

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !scenario.ViewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal(1, scenario.Pipeline.ExecutionCount);
        scenario.ViewModel.ApplyTo(scenario.Tool);
        Assert.Equal("1.2", scenario.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    [Theory]
    [MemberData(nameof(InvalidConfiguredScaleCases))]
    public async Task MultiTargetRunRejectsInvalidExplicitReferenceScaleBeforePipeline(
        string parameter,
        string invalidScale)
    {
        var scenario = CreateMultiTargetRunScenario(
            roiReferenceScale: parameter == "roiReferencePoseScale" ? invalidScale : null,
            sourceStandardScale: parameter == "standardScale" ? invalidScale : null,
            currentScale: 1);

        scenario.ViewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !scenario.ViewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target preview did not finish: {scenario.ViewModel.StatusText}");
        Assert.Equal(0, scenario.Pipeline.ExecutionCount);
        Assert.Equal("-", scenario.ViewModel.ScoreText);
        Assert.Equal(
            $"Position input mapping failed: {parameter} must be finite and greater than zero.",
            scenario.ViewModel.StatusText);
        var searchOverlay = Assert.Single(scenario.ViewModel.PreviewOverlays);
        Assert.Equal(VisionOverlayState.Warning, searchOverlay.State);
        Assert.Equal(4, searchOverlay.X, 6);
        Assert.Equal(4, searchOverlay.Y, 6);
        Assert.Equal(12, searchOverlay.Width, 6);
        Assert.Equal(10, searchOverlay.Height, 6);

        Assert.True(await scenario.ViewModel.PrepareToCloseAsync());
        scenario.ViewModel.ApplyTo(scenario.Tool);
        Assert.Equal("1", scenario.Tool.ToDefinition().Parameters["roiReferencePoseScale"]);
    }

    private static async Task<FindGeometryApplyScenario> ApplyFindGeometryDialogAsync(
        VisionToolKind toolKind,
        string sourceToolId,
        string? referenceToolId,
        string referenceScale,
        Pose2D currentPose)
    {
        if (toolKind != VisionToolKind.FindLine && toolKind != VisionToolKind.FindCircle)
        {
            throw new ArgumentOutOfRangeException(nameof(toolKind));
        }

        var frame = CreateFrame();
        var source = new VisionToolDefinition
        {
            Id = string.IsNullOrWhiteSpace(sourceToolId) ? "unused" : sourceToolId,
            Kind = VisionToolKind.TemplateLocate
        };
        var roi = toolKind == VisionToolKind.FindLine
            ? new RoiDefinition
            {
                Id = "roi",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 20,
                Height = 10
            }
            : new RoiDefinition
            {
                Id = "roi",
                Shape = RoiShapeKind.Circle,
                X = 16,
                Y = 16,
                Radius = 8
            };
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["roiReferencePoseX"] = "10",
            ["roiReferencePoseY"] = "8",
            ["roiReferencePoseAngle"] = "5",
            ["roiReferencePoseScale"] = referenceScale
        };
        if (!string.IsNullOrWhiteSpace(sourceToolId))
        {
            parameters["input:PositionInput:toolId"] = sourceToolId;
            parameters["input:PositionInput:portKey"] = "PositionOutput";
        }

        if (referenceToolId is not null)
        {
            parameters["roiReferencePoseToolId"] = referenceToolId;
        }

        var tool = new VisionToolItem
        {
            Id = toolKind == VisionToolKind.FindLine ? "find-line" : "find-circle",
            Kind = toolKind,
            RoiId = roi.Id,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var recipe = string.IsNullOrWhiteSpace(sourceToolId)
            ? new Recipe()
            : new Recipe { Tools = [source] };
        var pipeline = new PoseResultPipeline(frame, source.Id, currentPose);
        bool applied;
        if (toolKind == VisionToolKind.FindLine)
        {
            var viewModel = new FindLineToolDialogViewModel(
                tool, [roi], "Flow", frame, recipe, pipeline, new NullAppLogService());
            applied = await viewModel.ApplyToAsync(tool);
        }
        else
        {
            var viewModel = new FindCircleToolDialogViewModel(
                tool, [roi], "Flow", frame, recipe, pipeline, new NullAppLogService());
            applied = await viewModel.ApplyToAsync(tool);
        }

        return new FindGeometryApplyScenario(applied, tool, pipeline);
    }

    private static MultiTargetRunScenario CreateMultiTargetRunScenario(
        string? roiReferenceScale,
        string? sourceStandardScale,
        double currentScale,
        bool hasBoundRoi = true,
        string sourceToolId = "position-source",
        string? referenceToolId = null)
    {
        var frame = CreateFrame();
        var sourceParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sourceStandardScale is not null)
        {
            sourceParameters["templateX"] = "8";
            sourceParameters["templateY"] = "8";
            sourceParameters["templateWidth"] = "12";
            sourceParameters["templateHeight"] = "12";
            sourceParameters["standardScale"] = sourceStandardScale;
        }

        var source = new VisionToolDefinition
        {
            Id = sourceToolId,
            Kind = VisionToolKind.TemplateLocate,
            Parameters = sourceParameters
        };
        var roi = new RoiDefinition
        {
            Id = "area-roi",
            Shape = RoiShapeKind.Rectangle,
            X = 4,
            Y = 4,
            Width = 12,
            Height = 10
        };
        var parameters = new Dictionary<string, string>(TemplateMatcher.Learn(
            frame,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateX"] = "8",
                ["templateY"] = "8",
                ["templateWidth"] = "12",
                ["templateHeight"] = "12"
            }), StringComparer.OrdinalIgnoreCase)
        {
            ["input:PositionInput:toolId"] = source.Id,
            ["input:PositionInput:portKey"] = "PositionOutput"
        };
        if (roiReferenceScale is not null)
        {
            parameters["roiReferencePoseX"] = "10";
            parameters["roiReferencePoseY"] = "8";
            parameters["roiReferencePoseAngle"] = "0";
            parameters["roiReferencePoseScale"] = roiReferenceScale;
            parameters["roiReferencePoseToolId"] = referenceToolId ?? source.Id;
        }

        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = hasBoundRoi ? roi.Id : string.Empty,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var pipeline = new PoseResultPipeline(
            frame,
            source.Id,
            new Pose2D(20, 16, 0) { Scale = currentScale });
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            hasBoundRoi ? [roi] : [],
            "Flow",
            frame,
            new RuntimePaths(Path.GetTempPath()),
            new NullAppLogService(),
            new Recipe { Tools = [source] },
            pipeline);
        return new MultiTargetRunScenario(viewModel, pipeline, tool);
    }

    private static VisionToolItem CreatePositionInputTool(string id, VisionToolKind kind, string roiId)
    {
        return new VisionToolItem
        {
            Id = id,
            Kind = kind,
            RoiId = roiId,
            ParametersText = "input:PositionInput:toolId=position-source; input:PositionInput:portKey=PositionOutput"
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

    private sealed class PoseResultPipeline(
        ImageFrame frame,
        string toolId,
        Pose2D pose) : IVisionPipeline
    {
        public int ExecutionCount { get; private set; }

        public Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame inputFrame,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(new VisionPipelineResult(
                frame,
                [
                    new ToolResult
                    {
                        ToolId = toolId,
                        Kind = VisionToolKind.TemplateLocate,
                        Outcome = InspectionOutcome.Ok,
                        Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["x"] = pose.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["y"] = pose.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["angle"] = pose.Angle.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["scale"] = pose.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }
                    }
                ],
                InspectionOutcome.Ok,
                string.Empty,
                string.Empty));
        }
    }

    private sealed record MultiTargetRunScenario(
        TemplateLocateToolDialogViewModel ViewModel,
        PoseResultPipeline Pipeline,
        VisionToolItem Tool);

    private sealed record FindGeometryApplyScenario(
        bool Applied,
        VisionToolItem Tool,
        PoseResultPipeline Pipeline);

    private sealed class UnexpectedPipeline : IVisionPipeline
    {
        public Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame inputFrame,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Legacy reference should not trigger position recapture.");
        }
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
