using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Prism.Commands;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateLocateToolDialogViewModelTests
{
    private static readonly string[] HalconEditorPropertyNames =
    [
        "HalconAngleStartDeg",
        "HalconAngleExtentDeg",
        "HalconScaleMin",
        "HalconScaleMax",
        "HalconCandidateMinScore",
        "HalconOuterCoverageMin",
        "HalconInnerCoverageMin",
        "HalconEdgeTolerancePx",
        "HalconPolarityAgreementMin",
        "HalconCandidateMaxOverlap",
        "HalconMaxOverlap",
        "HalconGreediness",
        "HalconSubPixel",
        "HalconNumLevels",
        "HalconOperatorTimeoutMs",
        "HalconCandidateLimit",
        "HalconExpectedCount"
    ];

    [Theory]
    [InlineData(VisionToolKind.TemplateLocate, "ScaleOutput")]
    [InlineData(VisionToolKind.MultiTargetMatch, "ScalesOutput")]
    public void ScaleOutputOptionIsConfigurableAndSurvivesApply(
        VisionToolKind kind,
        string outputKey)
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "scale-output-tool",
            Name = "Scale output",
            Kind = kind,
            Enabled = true,
            ParametersText = $"engine=OpenCv; matchMode=Shape; enabledOutputs={outputKey}"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);

        var option = Assert.Single(viewModel.OutputOptions, item => item.Key == outputKey);
        Assert.True(option.IsEnabled);

        viewModel.ApplyTo(tool);

        Assert.Contains(
            outputKey,
            tool.ToDefinition().Parameters["enabledOutputs"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData(null, "3")]
    [InlineData("4.5", "4.5")]
    public void ApplyTo_LegacyTemplateLocateShape_MigratesToShapeV2(
        string? existingCoverageDistance,
        string expectedCoverageDistance)
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "legacy-template-tool",
            Name = "Legacy Template",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true,
            ParametersText = existingCoverageDistance is null
                ? "matchMode=Shape"
                : $"matchMode=Shape; shapeCoverageDistance={existingCoverageDistance}"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);

        viewModel.ApplyTo(tool);

        var parameters = tool.ToDefinition().Parameters;
        Assert.Equal("2", parameters["shapeScoreVersion"]);
        Assert.Equal(expectedCoverageDistance, parameters["shapeCoverageDistance"]);
    }

    [Theory]
    [InlineData(VisionToolKind.TemplateLocate, "GrayNcc")]
    [InlineData(VisionToolKind.MultiTargetMatch, "Shape")]
    public void ApplyTo_NonSingleTargetShape_DoesNotAddShapeV2Parameters(
        VisionToolKind toolKind,
        string matchMode)
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "legacy-template-tool",
            Name = "Legacy Template",
            Kind = toolKind,
            Enabled = true,
            ParametersText = $"matchMode={matchMode}"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);

        viewModel.ApplyTo(tool);

        var parameters = tool.ToDefinition().Parameters;
        Assert.False(parameters.ContainsKey("shapeScoreVersion"));
        Assert.False(parameters.ContainsKey("shapeCoverageDistance"));
    }

    [Fact]
    public void MultiTargetLearnThenApplyDoesNotPersistSingleTargetShapeV2Parameters()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Name = "Multi target",
            Kind = VisionToolKind.MultiTargetMatch,
            Enabled = true,
            ParametersText =
                "engine=OpenCv; matchMode=Shape; minScore=0.80; " +
                "angleStart=0; angleExtent=0; angleStep=1; matchCount=1; " +
                "templateRoiX=60; templateRoiY=40; " +
                "templateRoiWidth=100; templateRoiHeight=300"
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.LearnTemplateCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(
                () => !viewModel.IsBusy && viewModel.ScoreText != "-",
                TimeSpan.FromSeconds(10)),
            $"Learn/match did not finish: IsBusy={viewModel.IsBusy}; " +
            $"Score={viewModel.ScoreText}; Status={viewModel.StatusText}");
        Assert.False(
            string.IsNullOrWhiteSpace(viewModel.TemplatePreviewImagePng),
            viewModel.StatusText);
        var bestResult = Assert.Single(viewModel.MultiTargetResultPoints);
        Assert.Same(bestResult, viewModel.SelectedMultiTargetResultPoint);
        viewModel.SelectedMultiTargetResultPoint = null;
        Assert.Null(viewModel.SelectedMultiTargetResultPoint);
        viewModel.SelectedMultiTargetResultPoint = bestResult;
        Assert.Same(bestResult, viewModel.SelectedMultiTargetResultPoint);
        viewModel.SetStandardCommand.Execute();

        viewModel.ApplyTo(tool);

        var parameters = tool.ToDefinition().Parameters;
        Assert.True(parameters.ContainsKey("templateImagePng"));
        Assert.Equal("1", parameters["standardScale"]);
        Assert.False(parameters.ContainsKey("shapeScoreVersion"));
        Assert.False(parameters.ContainsKey("shapeCoverageDistance"));
    }

    [Fact]
    public void PlacingSearchRoiAfterTemplateKeepsSearchRegionBelowTemplateRoi()
    {
        using var tempDirectory = new TempDirectory();
        var viewModel = new TemplateLocateToolDialogViewModel(
            new VisionToolItem
            {
                Id = "template-tool",
                Name = "Template",
                Kind = VisionToolKind.TemplateLocate,
                Enabled = true
            },
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            CreateFrame(320, 240),
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.CreateTemplateRoiCommand.Execute();
        viewModel.PlaceRoiCommand.Execute(new Point2D(120, 100));
        viewModel.CreateRoiCommand.Execute();
        viewModel.PlaceRoiCommand.Execute(new Point2D(100, 80));

        Assert.NotEqual("template-roi", viewModel.EditableRois[0].Id);
        Assert.Equal("template-roi", viewModel.EditableRois[1].Id);
    }

    [Fact]
    public void RunToolUsesSeparateScoredEdgeAndTemplateRoiOverlays()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var parameters = CreateLearnedPolygonTemplateParameters(frame);
        var viewModel = new TemplateLocateToolDialogViewModel(
            new VisionToolItem
            {
                Id = "template-tool",
                Name = "Template",
                Kind = VisionToolKind.TemplateLocate,
                Enabled = true,
                ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
            },
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Template matching did not finish: {viewModel.StatusText}");
        Assert.NotEqual("-", viewModel.ScoreText);
        Assert.Contains(viewModel.PreviewOverlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        Assert.Contains(viewModel.PreviewOverlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);
    }

    [Fact]
    public void FirstSuccessfulMatchCreatesStandardScale()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var parameters = CreateLearnedPolygonTemplateParameters(frame);
        parameters.Remove("standardScale");
        var tool = new VisionToolItem
        {
            Id = "template-tool",
            Name = "Template",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.RunToolCommand.Execute();

        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Template matching did not finish: {viewModel.StatusText}");
        viewModel.ApplyTo(tool);
        Assert.Equal("1", tool.ToDefinition().Parameters["standardScale"]);
    }

    [Fact]
    public void SetStandardCommandReplacesStoredStandardScaleWithMatchScale()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var parameters = CreateLearnedPolygonTemplateParameters(frame);
        parameters["standardX"] = "0";
        parameters["standardY"] = "0";
        parameters["standardAngle"] = "0";
        parameters["standardScale"] = "2";
        var tool = new VisionToolItem
        {
            Id = "template-tool",
            Name = "Template",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.RunToolCommand.Execute();
        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Template matching did not finish: {viewModel.StatusText}");
        viewModel.SetStandardCommand.Execute();
        viewModel.ApplyTo(tool);

        Assert.Equal("1", tool.ToDefinition().Parameters["standardScale"]);
    }

    [Fact]
    public void ResetTemplateCommandRemovesCompleteStandardPoseAndKeepsUnrelatedParameters()
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "template-tool",
            Kind = VisionToolKind.TemplateLocate,
            ParametersText =
                "standardX=10; standardY=20; standardAngle=30; standardScale=0.0004; " +
                "minScore=0.75; keepMe=unchanged"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);

        viewModel.ResetTemplateCommand.Execute();
        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.False(saved.ContainsKey("standardX"));
        Assert.False(saved.ContainsKey("standardY"));
        Assert.False(saved.ContainsKey("standardAngle"));
        Assert.False(saved.ContainsKey("standardScale"));
        Assert.Equal("0.75", saved["minScore"]);
        Assert.Equal("unchanged", saved["keepMe"]);
    }

    [Fact]
    public void SetStandardWithoutMultiTargetMatchReportsThatMatchingIsRequired()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var parameters = CreateLearnedPolygonTemplateParameters(frame);
        parameters["standardX"] = "10";
        parameters["standardY"] = "20";
        parameters["standardAngle"] = "30";
        parameters["standardScale"] = "2";
        var searchRoi = new RoiDefinition
        {
            Id = "blank-search",
            Shape = RoiShapeKind.Rectangle,
            X = 0,
            Y = 0,
            Width = 40,
            Height = 30
        };
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            RoiId = searchRoi.Id,
            ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
        };
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [searchRoi],
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());

        viewModel.RunToolCommand.Execute();
        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Multi-target matching did not finish: {viewModel.StatusText}");
        Assert.Empty(viewModel.MultiTargetResultPoints);
        Assert.Equal("0.000", viewModel.ScoreText);

        viewModel.SetStandardCommand.Execute();
        viewModel.ApplyTo(tool);

        Assert.Equal("请先匹配模板，再设置标准", viewModel.StatusText);
        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("10", saved["standardX"]);
        Assert.Equal("20", saved["standardY"]);
        Assert.Equal("30", saved["standardAngle"]);
        Assert.Equal("2", saved["standardScale"]);
    }

    [Fact]
    public void MultiTargetResultCollectionIsReadOnly()
    {
        using var tempDirectory = new TempDirectory();
        var viewModel = CreateViewModel(
            new VisionToolItem
            {
                Id = "multi-target",
                Kind = VisionToolKind.MultiTargetMatch
            },
            tempDirectory);

        var results = Assert.IsType<ReadOnlyObservableCollection<MultiTargetMatchPointItem>>(
            viewModel.MultiTargetResultPoints);
        var list = Assert.IsAssignableFrom<IList<MultiTargetMatchPointItem>>(results);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(new MultiTargetMatchPointItem(
            1,
            "1",
            "2",
            "3",
            "0.900",
            "4",
            "5",
            "Rectangle",
            "-")));
    }

    [Fact]
    public void FakeMultiTargetSelectionCannotChangeStoredStandard()
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            ParametersText = "standardX=10; standardY=20; standardAngle=30; standardScale=2"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);
        var fake = MultiTargetMatchPointItem.FromCandidate(
            1,
            new MultiTargetMatchCandidate(100, 200, 300, 0.99, 12, 14) { Scale = 9 });

        viewModel.SelectedMultiTargetResultPoint = fake;
        Assert.Null(viewModel.SelectedMultiTargetResultPoint);
        viewModel.SetStandardCommand.Execute();
        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("10", saved["standardX"]);
        Assert.Equal("20", saved["standardY"]);
        Assert.Equal("30", saved["standardAngle"]);
        Assert.Equal("2", saved["standardScale"]);
    }

    [Fact]
    public void MultiTargetPointFromCandidatePreservesRawPoseAndScore()
    {
        var candidate = new MultiTargetMatchCandidate(
            10.123456789,
            20.987654321,
            30.111222333,
            0.9123456789,
            12,
            14)
        {
            Scale = 1.23456789
        };

        var item = MultiTargetMatchPointItem.FromCandidate(1, candidate);

        Assert.Equal(candidate.Pose, item.Pose);
        Assert.Equal(candidate.Score, item.NumericScore);
    }

    [Fact]
    public void StartingNewRunClearsPreviousSingleMatchOverlaysBeforeAwaitingResult()
    {
        using var tempDirectory = new TempDirectory();
        var frame = CreateShapeFrame();
        var parameters = CreateLearnedPolygonTemplateParameters(frame);
        var viewModel = new TemplateLocateToolDialogViewModel(
            new VisionToolItem
            {
                Id = "template-tool",
                Name = "Template",
                Kind = VisionToolKind.TemplateLocate,
                Enabled = true,
                ParametersText = string.Join("; ", parameters.Select(item => $"{item.Key}={item.Value}"))
            },
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());
        viewModel.RunToolCommand.Execute();
        Assert.True(
            SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)),
            $"Initial template matching did not finish: {viewModel.StatusText}");
        Assert.Contains(viewModel.PreviewOverlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        Assert.Contains(viewModel.PreviewOverlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);

        viewModel.AngleStart = -180;
        viewModel.AngleExtent = 360;
        var previousContext = SynchronizationContext.Current;
        using var queuedContext = new QueuedSynchronizationContext();
        var startedBusy = false;
        var oldResultWasCleared = false;
        var finished = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            viewModel.RunToolCommand.Execute();
            startedBusy = viewModel.IsBusy;
            oldResultWasCleared = !viewModel.PreviewOverlays.Any(item => item.Kind == VisionOverlayKind.Polyline);
        }
        finally
        {
            finished = CompleteQueuedRun(
                queuedContext,
                previousContext,
                () => !viewModel.IsBusy,
                TimeSpan.FromSeconds(10));
        }

        Assert.Same(previousContext, SynchronizationContext.Current);
        Assert.True(startedBusy, "Second template matching completed before its queued continuation could be observed.");
        Assert.True(oldResultWasCleared, "The previous result overlays remained visible after the new run started.");
        Assert.True(finished, $"Second template matching did not finish: {viewModel.StatusText}");
    }

    [Fact]
    public void CompleteQueuedRunRestoresPreviousContextWhenCallbackThrows()
    {
        var initialContext = SynchronizationContext.Current;
        var expectedPrevious = new SynchronizationContext();
        using var queuedContext = new QueuedSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(expectedPrevious);
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            queuedContext.Post(_ => throw new InvalidOperationException("queued failure"), null);

            Assert.Throws<InvalidOperationException>(() => CompleteQueuedRun(
                queuedContext,
                previousContext,
                () => false,
                TimeSpan.FromSeconds(1)));
            Assert.Same(expectedPrevious, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(initialContext);
        }
    }

    [Fact]
    public void LegacyMissingEngineDefaultsToOpenCvWithoutConstructorMutationAndAppliesExplicitEngine()
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "legacy-open-cv",
            Name = "Legacy",
            Kind = VisionToolKind.TemplateLocate,
            ParametersText = "matchMode=Shape; angleStart=-10; angleExtent=20; keepMe=unchanged"
        };
        var originalText = tool.ParametersText;

        var viewModel = CreateViewModel(tool, tempDirectory);

        Assert.Equal(TemplateMatchingEngine.OpenCv, GetProperty<TemplateMatchingEngine>(viewModel, "SelectedEngine"));
        Assert.Equal(originalText, tool.ParametersText);

        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("OpenCv", saved[TemplateMatchingParameterCatalog.Engine]);
        Assert.Equal("-45", saved["angleStart"]);
        Assert.Equal("90", saved["angleExtent"]);
        Assert.Equal("unchanged", saved["keepMe"]);
    }

    [Fact]
    public void LegacyOpenCvAngleMigrationDoesNotRewriteHalconTools()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters["angleStart"] = "-10";
        parameters["angleExtent"] = "20";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);

        var viewModel = CreateViewModel(tool, tempDirectory);
        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("-10", saved["angleStart"]);
        Assert.Equal("20", saved["angleExtent"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OpenCv")]
    public void SwitchingLegacyOpenCvMultiToHalconUsesStrictExpectedCountAndPreservesMaxMatches(
        string? engine)
    {
        using var tempDirectory = new TempDirectory();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.LegacyMatchCount] = "128",
            ["minCount"] = "1",
            [TemplateMatchingParameterCatalog.MatchMode] = "Shape",
            ["multiMatchMode"] = "Shape"
        };
        if (engine is not null)
        {
            parameters[TemplateMatchingParameterCatalog.Engine] = engine;
        }

        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);
        var viewModel = CreateViewModel(tool, tempDirectory);

        Assert.Equal("1", viewModel.HalconExpectedCount);

        viewModel.SelectedEngine = TemplateMatchingEngine.Halcon;

        Assert.True(viewModel.ApplyTo(tool));
        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("1", saved[TemplateMatchingParameterCatalog.ExpectedCount]);
        Assert.Equal("128", saved[TemplateMatchingParameterCatalog.LegacyMatchCount]);
    }

    [Fact]
    public void HalconMultiDraftWithoutExpectedCountUsesLegacyMatchCountFallback()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        parameters.Remove(TemplateMatchingParameterCatalog.ExpectedCount);
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "3";
        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);

        var viewModel = CreateViewModel(tool, tempDirectory);

        Assert.Equal("3", viewModel.HalconExpectedCount);
    }

    [Fact]
    public void HalconEditorExposesStrongEnginePresetAndRawParameterContracts()
    {
        var type = typeof(TemplateLocateToolDialogViewModel);
        Assert.Equal(typeof(TemplateMatchingEngine), RequireProperty(type, "SelectedEngine").PropertyType);
        Assert.Equal(typeof(TemplateMatchingPreset?), RequireProperty(type, "SelectedPreset").PropertyType);
        Assert.Equal(typeof(bool), RequireProperty(type, "IsHalconEngine").PropertyType);
        Assert.Equal(typeof(bool), RequireProperty(type, "IsAdvancedParametersExpanded").PropertyType);
        Assert.Equal(typeof(bool), RequireProperty(type, "RequiresRelearn").PropertyType);

        foreach (var propertyName in HalconEditorPropertyNames)
        {
            Assert.Equal(typeof(string), RequireProperty(type, propertyName).PropertyType);
        }
    }

    [Fact]
    public void BalancedPresetCopiesCatalogAndManualHotEditBecomesCustomWithoutRelearn()
    {
        using var tempDirectory = new TempDirectory();
        var tool = CreateTool(
            VisionToolKind.TemplateLocate,
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single));
        var viewModel = CreateViewModel(tool, tempDirectory);

        SetProperty(viewModel, "SelectedPreset", TemplateMatchingPreset.Balanced);

        var balanced = TemplateMatchingParameterCatalog.CreateBalancedDefaults(TemplateMatchCardinality.Single);
        Assert.Equal(balanced[TemplateMatchingParameterCatalog.CandidateMinScore], GetProperty<string>(viewModel, "HalconCandidateMinScore"));
        Assert.Equal(balanced[TemplateMatchingParameterCatalog.OuterCoverageMin], GetProperty<string>(viewModel, "HalconOuterCoverageMin"));
        Assert.Equal(balanced[TemplateMatchingParameterCatalog.OperatorTimeoutMs], GetProperty<string>(viewModel, "HalconOperatorTimeoutMs"));
        Assert.Equal(TemplateMatchingPreset.Balanced, GetProperty<TemplateMatchingPreset?>(viewModel, "SelectedPreset"));
        Assert.False(GetProperty<bool>(viewModel, "RequiresRelearn"));

        SetProperty(viewModel, "HalconOuterCoverageMin", "0.81");

        Assert.Null(GetProperty<TemplateMatchingPreset?>(viewModel, "SelectedPreset"));
        Assert.False(GetProperty<bool>(viewModel, "RequiresRelearn"));
    }

    [Theory]
    [InlineData("HalconAngleStartDeg", "-170")]
    [InlineData("HalconAngleExtentDeg", "350")]
    [InlineData("HalconScaleMin", "0.91")]
    [InlineData("HalconScaleMax", "1.09")]
    [InlineData("HalconNumLevels", "4")]
    public void EditingModelGenerationParameterRequiresRelearn(string propertyName, string value)
    {
        using var tempDirectory = new TempDirectory();
        var tool = CreateTool(
            VisionToolKind.TemplateLocate,
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single));
        var viewModel = CreateViewModel(tool, tempDirectory);

        SetProperty(viewModel, propertyName, value);

        Assert.True(GetProperty<bool>(viewModel, "RequiresRelearn"));
        Assert.Null(GetProperty<TemplateMatchingPreset?>(viewModel, "SelectedPreset"));
    }

    [Fact]
    public void OpenCvApplyIgnoresAndPreservesBrokenInactiveHalconParameters()
    {
        using var tempDirectory = new TempDirectory();
        var tool = new VisionToolItem
        {
            Id = "open-cv-with-inactive-halcon",
            Name = "OpenCV",
            Kind = VisionToolKind.TemplateLocate,
            ParametersText =
                "engine=OpenCv; matchMode=Shape; halcon.operatorTimeoutMs=garbage; " +
                "halcon.scaleMin=also-bad; keepMe=unchanged"
        };
        var viewModel = CreateViewModel(tool, tempDirectory);

        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("garbage", saved[TemplateMatchingParameterCatalog.OperatorTimeoutMs]);
        Assert.Equal("also-bad", saved[TemplateMatchingParameterCatalog.ScaleMin]);
        Assert.Equal("unchanged", saved["keepMe"]);
    }

    [Fact]
    public void HalconApplyRejectsBrokenActiveTimeoutWithoutMutatingTool()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = "0";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        tool.Name = "Original";
        var originalText = tool.ParametersText;
        var viewModel = CreateViewModel(tool, tempDirectory);
        viewModel.Name = "Changed";

        viewModel.ApplyTo(tool);

        Assert.Equal("Original", tool.Name);
        Assert.Equal(originalText, tool.ParametersText);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
    }

    [Fact]
    public void HalconApplyIgnoresAndPreservesBrokenInactiveOpenCvParameters()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters["minScore"] = "garbage";
        parameters["angleStart"] = "not-a-number";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var viewModel = CreateViewModel(tool, tempDirectory);

        viewModel.ApplyTo(tool);

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("garbage", saved["minScore"]);
        Assert.Equal("not-a-number", saved["angleStart"]);
    }

    [Fact]
    public async Task HalconLearnAndTrialRunUseInjectedAsyncServiceAndStableOwner()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var recipe = CreatePreviewRecipe("recipe-1", "flow-2", tool);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (request, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                new Dictionary<string, string> { ["halcon.learnedMarker"] = "learned" },
                "learned",
                null)),
            MatchHandler = (request, _) => Task.FromResult(CreateNoMatchBatch(request, TemplateMatchingEngine.Halcon))
        };
        var viewModel = CreateInjectedViewModel(tool, tempDirectory, service, recipe, CreateFrame(80, 60));

        await GetProperty<IAsyncCommand>(viewModel, "LearnTemplateCommand")
            .ExecuteAsync(null, CancellationToken.None);

        var owner = new TemplateModelOwner("recipe-1", "flow-2", tool.Id);
        Assert.Equal(owner, Assert.Single(service.LearningRequests).Owner);
        Assert.Equal(owner, Assert.Single(service.MatchingRequests).Owner);
        viewModel.ApplyTo(tool);
        Assert.Equal("learned", tool.ToDefinition().Parameters["halcon.learnedMarker"]);
    }

    [Fact]
    public async Task HalconSingleLearnRequestOmitsLegacyMatchCountButApplyPreservesIt()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "128";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = RecordingTemplateMatchingService.Successful();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await GetProperty<IAsyncCommand>(viewModel, "LearnTemplateCommand")
            .ExecuteAsync(null, CancellationToken.None);

        Assert.False(Assert.Single(service.LearningRequests).Parameters.ContainsKey(
            TemplateMatchingParameterCatalog.LegacyMatchCount));
        viewModel.ApplyTo(tool);
        Assert.Equal("128", tool.ToDefinition().Parameters[TemplateMatchingParameterCatalog.LegacyMatchCount]);
    }

    [Fact]
    public async Task AsyncLearnCommandPropagatesCallerCancellationToken()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var enteredService = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = async (_, token) =>
            {
                enteredService.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        using var cancellation = new CancellationTokenSource();

        var execution =
            GetProperty<IAsyncCommand>(viewModel, "LearnTemplateCommand")
                .ExecuteAsync(null, cancellation.Token);
        await enteredService.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(Assert.Single(service.LearningTokens).IsCancellationRequested);
    }

    [Theory]
    [InlineData("HalconCandidateMinScore", "0.61")]
    [InlineData("HalconInnerCoverageMin", "0.79")]
    [InlineData("HalconEdgeTolerancePx", "3.5")]
    [InlineData("HalconPolarityAgreementMin", "0.88")]
    [InlineData("HalconCandidateMaxOverlap", "0.71")]
    [InlineData("HalconMaxOverlap", "0.24")]
    [InlineData("HalconGreediness", "0.77")]
    [InlineData("HalconSubPixel", "custom-subpixel")]
    [InlineData("HalconOperatorTimeoutMs", "6000")]
    [InlineData("HalconCandidateLimit", "40")]
    public void EditingHotHalconParameterDoesNotRequireRelearn(string propertyName, string value)
    {
        using var tempDirectory = new TempDirectory();
        var tool = CreateTool(
            VisionToolKind.TemplateLocate,
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single));
        var viewModel = CreateViewModel(tool, tempDirectory);

        SetProperty(viewModel, propertyName, value);

        Assert.False(viewModel.RequiresRelearn);
        Assert.Null(viewModel.SelectedPreset);
    }

    [Fact]
    public async Task HalconMultiRequestUsesExpectedCountAndOmitsLegacyMaxMatches()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = "3";
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "128";
        parameters["multiMatchMode"] = "GrayNcc";
        parameters["minScore"] = "broken-inactive-open-cv";
        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);
        var service = RecordingTemplateMatchingService.Successful();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        var request = Assert.Single(service.MatchingRequests);
        Assert.Equal(TemplateMatchCardinality.ExactCount, request.Cardinality);
        Assert.Equal(3, request.ExpectedCount);
        Assert.Equal("3", request.Parameters[TemplateMatchingParameterCatalog.ExpectedCount]);
        Assert.False(request.Parameters.ContainsKey(TemplateMatchingParameterCatalog.LegacyMatchCount));
        Assert.Equal("Shape", request.Parameters["matchMode"]);
        Assert.Equal("Shape", request.Parameters["multiMatchMode"]);
        Assert.Equal("broken-inactive-open-cv", request.Parameters["minScore"]);

        Assert.True(viewModel.ApplyTo(tool));
        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("128", saved[TemplateMatchingParameterCatalog.LegacyMatchCount]);
        Assert.Equal("Shape", saved["multiMatchMode"]);
        Assert.Equal("GrayNcc", saved["opencv.multiMatchMode"]);
    }

    [Fact]
    public async Task OpenCvTrialRunIgnoresAndPreservesBrokenInactiveHalconParameters()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["templateImagePng"] = "learned-preview",
            [TemplateMatchingParameterCatalog.OperatorTimeoutMs] = "garbage",
            [TemplateMatchingParameterCatalog.ScaleMin] = "also-garbage"
        };
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = RecordingTemplateMatchingService.Successful();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        var request = Assert.Single(service.MatchingRequests);
        Assert.Equal("garbage", request.Parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs]);
        Assert.Equal("also-garbage", request.Parameters[TemplateMatchingParameterCatalog.ScaleMin]);
        Assert.True(viewModel.ApplyTo(tool));
        Assert.Equal(
            "garbage",
            tool.ToDefinition().Parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs]);
    }

    [Fact]
    public async Task InvalidActiveHalconTimeoutBlocksTrialRunBeforeService()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = "not-a-timeout";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = RecordingTemplateMatchingService.Successful();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        Assert.Empty(service.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
    }

    [Fact]
    public async Task ExplicitHalconNonShapeModeIsRejectedBeforeService()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.MatchMode] = "CircularBlob";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = RecordingTemplateMatchingService.Successful();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        Assert.Empty(service.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, viewModel.StatusText);
    }

    [Fact]
    public void HalconMultiCandidateLimitMustExceedExpectedCount()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = "4";
        parameters[TemplateMatchingParameterCatalog.CandidateLimit] = "4";
        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);
        var originalText = tool.ParametersText;
        var viewModel = CreateViewModel(tool, tempDirectory);

        Assert.False(viewModel.ApplyTo(tool));

        Assert.Equal(originalText, tool.ParametersText);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
    }

    [Fact]
    public async Task OpenCvToHalconLearnAndBackPreservesCompleteOpenCvState()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["engine"] = "OpenCv";
        parameters["matchMode"] = "GrayNcc";
        parameters["minScore"] = "0.77";
        parameters["angleStart"] = "-22";
        parameters["angleExtent"] = "44";
        parameters["standardX"] = "101.25";
        parameters["standardY"] = "202.5";
        parameters["standardAngle"] = "13";
        parameters["standardScale"] = "1.02";
        parameters["templateWidth"] = "30";
        parameters["templateHeight"] = "20";
        parameters["templateImagePng"] = "open-cv-preview";
        parameters["templatePixels"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        parameters["modelPath"] = "legacy/model.bin";
        parameters["modelVersion"] = "1.0";
        var openCvSnapshot = parameters
            .Where(parameter => !parameter.Key.StartsWith("halcon.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var learnedHalcon = CreateCompleteHalconLearnedState();
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                learnedHalcon,
                "halcon learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        viewModel.SelectedEngine = TemplateMatchingEngine.Halcon;

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        Assert.True(viewModel.ApplyTo(tool));
        var halconSaved = tool.ToDefinition().Parameters;
        Assert.Equal("Shape", halconSaved[TemplateMatchingParameterCatalog.MatchMode]);
        Assert.Equal("GrayNcc", halconSaved["opencv.matchMode"]);

        var reopened = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        reopened.SelectedEngine = TemplateMatchingEngine.OpenCv;
        Assert.True(reopened.ApplyTo(tool));

        var saved = tool.ToDefinition().Parameters;
        foreach (var parameter in openCvSnapshot)
        {
            Assert.Equal(parameter.Value, saved[parameter.Key]);
        }

        foreach (var parameter in learnedHalcon)
        {
            Assert.Equal(parameter.Value, saved[parameter.Key]);
        }
    }

    [Fact]
    public async Task HalconToOpenCvLearnAndBackPreservesCompleteHalconState()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var halconSnapshot = CreateCompleteHalconLearnedState();
        foreach (var parameter in halconSnapshot)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters["templateImagePng"] = "existing-open-cv-preview";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.OpenCv,
                true,
                new Dictionary<string, string>
                {
                    ["templateImagePng"] = "new-open-cv-preview",
                    ["templateWidth"] = "30",
                    ["templateHeight"] = "20",
                    ["templatePixels"] = Convert.ToBase64String(new byte[] { 5, 6, 7, 8 })
                },
                "opencv learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        viewModel.SelectedEngine = TemplateMatchingEngine.OpenCv;

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        viewModel.SelectedEngine = TemplateMatchingEngine.Halcon;
        Assert.True(viewModel.ApplyTo(tool));

        var saved = tool.ToDefinition().Parameters;
        foreach (var parameter in halconSnapshot)
        {
            Assert.Equal(parameter.Value, saved[parameter.Key]);
        }

        Assert.NotNull(TemplateModelParameterCodec.ReadHalcon(saved));
    }

    [Fact]
    public async Task OpenCvLearningDoesNotClearPendingHalconRelearnRequirement()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["engine"] = "OpenCv";
        parameters["templateImagePng"] = "existing-preview";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.OpenCv,
                true,
                new Dictionary<string, string>
                {
                    ["templateImagePng"] = "new-preview",
                    ["templateWidth"] = "30",
                    ["templateHeight"] = "20"
                },
                "opencv learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        viewModel.HalconScaleMin = "0.91";
        Assert.True(viewModel.RequiresRelearn);

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.True(viewModel.RequiresRelearn);
    }

    [Fact]
    public void PresetMarksRelearnOnlyWhenGenerationValuesActuallyChange()
    {
        using var tempDirectory = new TempDirectory();
        var custom = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        custom[TemplateMatchingParameterCatalog.AngleStartDeg] = "-170";
        var customViewModel = CreateViewModel(
            CreateTool(VisionToolKind.TemplateLocate, custom),
            tempDirectory);

        customViewModel.SelectedPreset = TemplateMatchingPreset.Strict;

        Assert.True(customViewModel.RequiresRelearn);

        var strictViewModel = CreateViewModel(
            CreateTool(
                VisionToolKind.TemplateLocate,
                TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single)),
            tempDirectory);
        strictViewModel.SelectedPreset = TemplateMatchingPreset.Balanced;
        Assert.False(strictViewModel.RequiresRelearn);
    }

    [Fact]
    public async Task HalconSetStandardDoesNotPolluteInactiveOpenCvStandardPose()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters["standardX"] = "10";
        parameters["standardY"] = "20";
        parameters["standardAngle"] = "30";
        parameters["standardScale"] = "1.2";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            MatchHandler = (request, _) => Task.FromResult(CreateSuccessfulBatch(request))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.RunToolCommand.Execute(CancellationToken.None);
        viewModel.SetStandardCommand.Execute();
        Assert.True(viewModel.ApplyTo(tool));

        var saved = tool.ToDefinition().Parameters;
        Assert.Equal("10", saved["standardX"]);
        Assert.Equal("20", saved["standardY"]);
        Assert.Equal("30", saved["standardAngle"]);
        Assert.Equal("1.2", saved["standardScale"]);
        Assert.Contains("重新学习", viewModel.StatusText);
    }

    private static bool CompleteQueuedRun(
        QueuedSynchronizationContext queuedContext,
        SynchronizationContext? previousContext,
        Func<bool> predicate,
        TimeSpan timeout)
    {
        try
        {
            var finished = queuedContext.RunUntil(predicate, timeout);
            queuedContext.Drain();
            return finished;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static TemplateLocateToolDialogViewModel CreateInjectedViewModel(
        VisionToolItem tool,
        TempDirectory tempDirectory,
        ITemplateMatchingService service,
        Recipe recipe,
        ImageFrame frame)
    {
        var constructor = typeof(TemplateLocateToolDialogViewModel)
            .GetConstructors()
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 10 &&
                       parameters[^1].ParameterType == typeof(ITemplateMatchingService);
            });
        Assert.NotNull(constructor);
        return Assert.IsType<TemplateLocateToolDialogViewModel>(constructor.Invoke(
        [
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Display only flow name",
            frame,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            recipe,
            null,
            service
        ]));
    }

    private static VisionToolItem CreateTool(
        VisionToolKind kind,
        IReadOnlyDictionary<string, string> parameters)
    {
        return new VisionToolItem
        {
            Id = $"tool-{Guid.NewGuid():N}",
            Name = kind.ToString(),
            Kind = kind,
            Enabled = true,
            ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"))
        };
    }

    private static Dictionary<string, string> CreateHalconParametersWithTemplateRoi(
        TemplateMatchCardinality cardinality)
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(cardinality);
        var roi = new RoiDefinition
        {
            Id = "template-roi",
            Name = "Template ROI",
            Shape = RoiShapeKind.Rectangle,
            X = 10,
            Y = 10,
            Width = 30,
            Height = 20
        };
        parameters["templateRoiJson"] = JsonSerializer.Serialize(roi);
        parameters["templateRoiShape"] = RoiShapeKind.Rectangle.ToString();
        parameters["templateRoiX"] = "10";
        parameters["templateRoiY"] = "10";
        parameters["templateRoiWidth"] = "30";
        parameters["templateRoiHeight"] = "20";
        return parameters;
    }

    private static Dictionary<string, string> CreateCompleteHalconLearnedState()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["halcon.modelPath"] = "recipes/recipe/flows/flow/tools/tool/model-7.shm",
            ["halcon.modelMetadataPath"] = "recipes/recipe/flows/flow/tools/tool/model-7.json",
            ["halcon.modelFormat"] = TemplateModelParameterCodec.HalconScaledShapeModelFormat,
            ["halcon.modelVersion"] = "26.05",
            ["halcon.modelRuntimeVersion"] = "26.05.0.0",
            ["halcon.modelGeneration"] = "7",
            ["halcon.modelChecksum"] = new string('a', 64),
            ["halcon.metadataChecksum"] = new string('b', 64),
            ["halcon.generationParameterFingerprint"] = new string('c', 64),
            ["halcon.standardX"] = "25",
            ["halcon.standardY"] = "20",
            ["halcon.standardAngle"] = "0",
            ["halcon.standardScale"] = "1",
            ["halcon.templateWidth"] = "30",
            ["halcon.templateHeight"] = "20"
        };
    }

    private static Recipe CreatePreviewRecipe(
        string recipeId,
        string flowId,
        VisionToolItem tool)
    {
        return new Recipe
        {
            Id = recipeId,
            CurrentFlowId = flowId,
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = flowId,
                    Name = "Stable Flow",
                    Tools = [tool.ToDefinition()]
                }
            ]
        };
    }

    private static TemplateMatchBatchResult CreateNoMatchBatch(
        TemplateMatchingRequest request,
        TemplateMatchingEngine engine)
    {
        return new TemplateMatchBatchResult(
            engine,
            InspectionOutcome.Ng,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            new TemplateSearchRegion(0, 0, request.Frame.Width, request.Frame.Height),
            "No match",
            false);
    }

    private static TemplateMatchBatchResult CreateSuccessfulBatch(TemplateMatchingRequest request)
    {
        var pose = new Pose2D(40, 30, 15) { Scale = 1.05 };
        var candidate = new TemplateMatchBatchCandidate(
            pose,
            0.95,
            30,
            20,
            Array.Empty<IReadOnlyList<Point2D>>(),
            new IReadOnlyList<Point2D>[]
            {
                new[]
                {
                    new Point2D(25, 20),
                    new Point2D(55, 20),
                    new Point2D(55, 40),
                    new Point2D(25, 40)
                }
            });
        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ok,
            true,
            [candidate],
            new TemplateSearchRegion(0, 0, request.Frame.Width, request.Frame.Height),
            "matched",
            false);
    }

    private static PropertyInfo RequireProperty(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        return (T)RequireProperty(target.GetType(), propertyName).GetValue(target)!;
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var property = RequireProperty(target.GetType(), propertyName);
        Assert.True(property.CanWrite, $"Property {propertyName} must be writable.");
        property.SetValue(target, value);
    }

    private static Dictionary<string, string> CreateLearnedPolygonTemplateParameters(ImageFrame frame)
    {
        var templateRoi = new RoiDefinition
        {
            Id = "polygon-template-roi",
            Name = "Polygon Template ROI",
            Shape = RoiShapeKind.Polygon,
            Points =
            [
                new Point2D(60, 40),
                new Point2D(160, 40),
                new Point2D(160, 340),
                new Point2D(60, 340)
            ]
        };
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["autoContrast"] = "false",
            ["contrast"] = "30",
            ["minScore"] = "0.80",
            ["angleStart"] = "0",
            ["angleExtent"] = "0",
            ["angleStep"] = "1",
            ["shapeCoarseScale"] = "1",
            ["templateRoiX"] = "60",
            ["templateRoiY"] = "40",
            ["templateRoiWidth"] = "100",
            ["templateRoiHeight"] = "300",
            ["templateRoiJson"] = JsonSerializer.Serialize(templateRoi),
            ["templateRoiShape"] = RoiShapeKind.Polygon.ToString()
        };

        foreach (var learned in TemplateMatcher.Learn(frame, null, parameters))
        {
            parameters[learned.Key] = learned.Value;
        }

        return parameters;
    }

    private static ImageFrame CreateShapeFrame()
    {
        const int width = 220;
        const int height = 380;
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        FillRectangle(pixels, width, x: 94, y: 60, rectangleWidth: 32, rectangleHeight: 260);
        FillRectangle(pixels, width, x: 94, y: 75, rectangleWidth: 55, rectangleHeight: 33);
        FillRectangle(pixels, width, x: 68, y: 260, rectangleWidth: 58, rectangleHeight: 40);

        return new ImageFrame(
            "synthetic-template",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }

    private static void FillRectangle(
        byte[] pixels,
        int stride,
        int x,
        int y,
        int rectangleWidth,
        int rectangleHeight)
    {
        for (var row = y; row < y + rectangleHeight; row++)
        {
            Array.Fill(pixels, (byte)0, row * stride + x, rectangleWidth);
        }
    }

    private static ImageFrame CreateFrame(int width, int height)
    {
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            new byte[width * height],
            DateTimeOffset.UtcNow,
            "test");
    }

    private static TemplateLocateToolDialogViewModel CreateViewModel(
        VisionToolItem tool,
        TempDirectory tempDirectory)
    {
        return new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Flow",
            currentFrame: null,
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            TemplateMatchingService.CreateLegacyOnly());
    }

    private sealed class RecordingTemplateMatchingService : ITemplateMatchingService
    {
        public Func<TemplateLearningRequest, CancellationToken, Task<TemplateLearningResult>>? LearnHandler { get; init; }

        public Func<TemplateMatchingRequest, CancellationToken, Task<TemplateMatchBatchResult>>? MatchHandler { get; init; }

        public List<TemplateLearningRequest> LearningRequests { get; } = new();

        public List<TemplateMatchingRequest> MatchingRequests { get; } = new();

        public List<CancellationToken> LearningTokens { get; } = new();

        public List<CancellationToken> MatchingTokens { get; } = new();

        public static RecordingTemplateMatchingService Successful()
        {
            return new RecordingTemplateMatchingService
            {
                LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                    TemplateMatchingEngine.Halcon,
                    true,
                    new Dictionary<string, string> { ["halcon.learnedMarker"] = "learned" },
                    "learned",
                    null))
            };
        }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken)
        {
            LearningRequests.Add(request);
            LearningTokens.Add(cancellationToken);
            return LearnHandler?.Invoke(request, cancellationToken)
                   ?? Task.FromResult(new TemplateLearningResult(
                       TemplateMatchingEngine.Halcon,
                       true,
                       new Dictionary<string, string>(),
                       "learned",
                       null));
        }

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken)
        {
            MatchingRequests.Add(request);
            MatchingTokens.Add(cancellationToken);
            return MatchHandler?.Invoke(request, cancellationToken)
                   ?? Task.FromResult(CreateNoMatchBatch(
                       request,
                       request.Parameters.TryGetValue(TemplateMatchingParameterCatalog.Engine, out var engine) &&
                       Enum.TryParse<TemplateMatchingEngine>(engine, true, out var parsed)
                           ? parsed
                           : TemplateMatchingEngine.OpenCv));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
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

    private sealed class QueuedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly AutoResetEvent _callbackAvailable = new(false);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _callbackAvailable.Set();
        }

        public bool RunUntil(Func<bool> predicate, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!predicate())
            {
                if (_callbacks.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                    continue;
                }

                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                _callbackAvailable.WaitOne(remaining < TimeSpan.FromMilliseconds(50)
                    ? remaining
                    : TimeSpan.FromMilliseconds(50));
            }

            return true;
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }

        public void Dispose()
        {
            _callbackAvailable.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vision-ui-tests-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
    }
}
