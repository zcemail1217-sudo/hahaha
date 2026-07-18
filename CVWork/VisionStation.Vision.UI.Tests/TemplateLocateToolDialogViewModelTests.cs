using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    [InlineData(typeof(TemplateLocateToolDialogViewModel))]
    [InlineData(typeof(VisionStation.Vision.UI.Services.WpfToolParameterDialogService))]
    public void TemplateDialogCompositionUsesMatchingStoreAndResourcePorts(Type compositionType)
    {
        var parameterTypes = Assert.Single(compositionType.GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(ITemplateMatchingService), parameterTypes);
        Assert.Contains(typeof(ITemplateModelStore), parameterTypes);
        Assert.Contains(typeof(ITemplateModelResourceManager), parameterTypes);
    }

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);

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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);
        viewModel.RunToolCommand.Execute();
        Assert.True(
            SpinWait.SpinUntil(
                () => !viewModel.IsBusy && viewModel.ScoreText != "-",
                TimeSpan.FromSeconds(10)),
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
        var learnedParameters = CreateCompleteHalconLearnedState();
        learnedParameters["halcon.learnedMarker"] = "learned";
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (request, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                learnedParameters,
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

    [Fact]
    public async Task HalconInitializeUsesCodecAndStoreResolutionForStableOwner()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = (_, _, _) => Task.FromResult(
                new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe-1", "flow-2", tool),
            CreateFrame(80, 60),
            store);

        await InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);

        Assert.True(GetProperty<bool>(viewModel, "HasLearnedTemplateModel"));
        var resolution = Assert.Single(store.Resolutions);
        Assert.Equal(new TemplateModelOwner("recipe-1", "flow-2", tool.Id), resolution.Owner);
        Assert.Equal(
            TemplateModelParameterCodec.ReadHalcon(parameters)!.Reference,
            resolution.Reference);
    }

    [Fact]
    public async Task HalconInitializeRejectsLegacyOpenCvModelWithoutCallingStore()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["modelPath"] = Path.Combine(tempDirectory.Path, "template.bin");
        parameters["modelVersion"] = "1.0";
        parameters["templateWidth"] = "30";
        parameters["templateHeight"] = "20";
        parameters["templatePixels"] = Convert.ToBase64String([1, 2, 3, 4]);
        Directory.CreateDirectory(tempDirectory.Path);
        File.WriteAllBytes(parameters["modelPath"], [1, 2, 3, 4]);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);

        await InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);

        Assert.False(GetProperty<bool>(viewModel, "HasLearnedTemplateModel"));
        Assert.Empty(store.Resolutions);
    }

    [Fact]
    public async Task HalconInitializeWithMissingOwnerFailsBeforeStoreResolution()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore();
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Display flow",
            CreateFrame(80, 60),
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            RecordingTemplateMatchingService.Successful(),
            store,
            NoOpTemplateModelResourceManager.Instance);

        await InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);

        Assert.False(GetProperty<bool>(viewModel, "HasLearnedTemplateModel"));
        Assert.Empty(store.Resolutions);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task HalconLearnWithAnyMissingOwnerIdFailsBeforeService(
        bool missingRecipeId,
        bool missingFlowId,
        bool missingToolId)
    {
        using var tempDirectory = new TempDirectory();
        var tool = CreateTool(
            VisionToolKind.TemplateLocate,
            CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single));
        if (missingToolId)
        {
            tool.Id = string.Empty;
        }

        var recipeId = missingRecipeId ? string.Empty : "recipe";
        var flowId = missingFlowId ? string.Empty : "flow";
        var service = new RecordingTemplateMatchingService();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe(recipeId, flowId, tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Empty(service.LearningRequests);
        Assert.Equal(before, viewModel.PendingParameters);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
    }

    [Fact]
    public async Task SwitchingFromOpenCvToHalconResolvesStoreBeforeTrialRun()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters[TemplateMatchingParameterCatalog.Engine] = TemplateMatchingEngine.OpenCv.ToString();
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = (_, _, _) => Task.FromResult(
                new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()))
        };
        var matchingService = new RecordingTemplateMatchingService();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            matchingService,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);
        await InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);
        Assert.Empty(store.Resolutions);
        viewModel.SelectedEngine = TemplateMatchingEngine.Halcon;

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        Assert.Single(store.Resolutions);
        Assert.Empty(matchingService.LearningRequests);
        Assert.Single(matchingService.MatchingRequests);
    }

    [Fact]
    public async Task HalconResolveFailureBlocksTrialRunWithoutAutoLearning()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = (_, _, _) => Task.FromException<ResolvedTemplateModel>(
                new TemplateModelStoreException(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch))
        };
        var matchingService = new RecordingTemplateMatchingService();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            matchingService,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        Assert.Single(store.Resolutions);
        Assert.Empty(matchingService.LearningRequests);
        Assert.Empty(matchingService.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch, viewModel.StatusText);
    }

    [Fact]
    public async Task RejectedHalconTrialShowsExplicitFirstRejectionReason()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var learned = CreateCompleteHalconLearnedState();
        foreach (var parameter in learned)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = (_, _, _) => Task.FromResult(
                new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()))
        };
        var service = new RecordingTemplateMatchingService
        {
            MatchHandler = (request, _) => Task.FromResult(new TemplateMatchBatchResult(
                TemplateMatchingEngine.Halcon,
                InspectionOutcome.Ng,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                new TemplateSearchRegion(0, 0, request.Frame.Width, request.Frame.Height),
                "rejected",
                false,
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.MatchOuterContourWeak)))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        Assert.Contains("首个拒绝", viewModel.StatusText);
        Assert.Contains(TemplateMatchingDiagnosticCodes.MatchOuterContourWeak, viewModel.StatusText);
        var rejectionOverlay = Assert.Single(
            viewModel.PreviewOverlays,
            overlay => overlay.Kind == VisionOverlayKind.Rectangle &&
                       overlay.Label.Contains(
                           TemplateMatchingDiagnosticCodes.MatchOuterContourWeak,
                           StringComparison.Ordinal));
        Assert.Equal(VisionOverlayState.Ng, rejectionOverlay.State);
        Assert.Contains("首个拒绝", rejectionOverlay.Label);
    }

    [Fact]
    public async Task FailedLearningKeepsPendingParametersUnchanged()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["operatorNote"] = "old";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                false,
                new Dictionary<string, string> { ["operatorNote"] = "new" },
                "failed",
                TemplateMatchingDiagnostics.Create(TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete)))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Equal(before, viewModel.PendingParameters);
    }

    [Fact]
    public async Task IncompleteSuccessfulHalconLearningFailsClosedWithoutPublishingOrMatching()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["operatorNote"] = "old";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                new Dictionary<string, string>
                {
                    ["operatorNote"] = "new",
                    ["halcon.modelPath"] = "incomplete/model.shm"
                },
                "learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Equal(before, viewModel.PendingParameters);
        Assert.False(viewModel.HasLearnedTemplateModel);
        Assert.Empty(service.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, viewModel.StatusText);
    }

    [Fact]
    public async Task HalconLearningResultAfterEngineSwitchDoesNotPublishOrAutoMatch()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TemplateLearningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = async (_, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();
        var learning = viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.SelectedEngine = TemplateMatchingEngine.OpenCv;
        release.TrySetResult(new TemplateLearningResult(
            TemplateMatchingEngine.Halcon,
            true,
            CreateCompleteHalconLearnedState(),
            "late",
            null));
        await learning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(before, viewModel.PendingParameters);
        Assert.False(viewModel.HasLearnedTemplateModel);
        Assert.Empty(service.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, viewModel.StatusText);
    }

    [Fact]
    public async Task HalconLearningResultAfterGenerationEditDoesNotClearRelearnOrPublish()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TemplateLearningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = async (_, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();
        var learning = viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.HalconScaleMin = "0.91";
        release.TrySetResult(new TemplateLearningResult(
            TemplateMatchingEngine.Halcon,
            true,
            CreateCompleteHalconLearnedState(),
            "late",
            null));
        await learning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(before, viewModel.PendingParameters);
        Assert.True(viewModel.RequiresRelearn);
        Assert.Empty(service.MatchingRequests);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, viewModel.StatusText);
    }

    [Fact]
    public async Task DialogCancellationPreventsLateLearningResultFromMerging()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters["operatorNote"] = "old";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TemplateLearningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = async (_, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();
        var execution = viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            InvokeVoidMethod(viewModel, "CancelPendingOperations");
        }
        finally
        {
            release.TrySetResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                new Dictionary<string, string> { ["operatorNote"] = "late" },
                "late",
                null));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(before, viewModel.PendingParameters);
    }

    [Fact]
    public async Task CancelAndDrainWaitsForInFlightNativeOperationAndReportsDeferredCancellation()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TemplateLearningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = async (_, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var learning = viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var draining = viewModel.CancelAndDrainAsync();

        Assert.False(draining.IsCompleted);
        Assert.Contains("已请求取消", viewModel.StatusText);
        Assert.Contains("安全返回", viewModel.StatusText);
        release.TrySetResult(new TemplateLearningResult(
            TemplateMatchingEngine.Halcon,
            true,
            CreateCompleteHalconLearnedState(),
            "late",
            null));
        await draining.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => learning);
    }

    [Fact]
    public async Task DialogCancellationCancelsBlockedReferenceCaptureDuringSavePreparation()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.ExactCount);
        parameters["input:PositionInput:toolId"] = "position-source";
        parameters["input:PositionInput:portKey"] = "PositionOutput";
        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);
        var source = new VisionToolDefinition
        {
            Id = "position-source",
            Kind = VisionToolKind.TemplateLocate
        };
        var recipe = new Recipe
        {
            Id = "recipe",
            CurrentFlowId = "flow",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "flow",
                    Tools = [source, tool.ToDefinition()]
                }
            ]
        };
        var pipeline = new BlockingVisionPipeline();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            recipe,
            CreateFrame(80, 60),
            pipeline: pipeline);

        var preparation = viewModel.PrepareToCloseAsync();
        await pipeline.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.CancelPendingOperations();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => preparation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(pipeline.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MultiTargetHalconPreviewUsesSharedFactoryAndShowsEveryGateMetric()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.ExactCount);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.MultiTargetMatch, parameters);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = (_, _, _) => Task.FromResult(
                new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()))
        };
        var service = new RecordingTemplateMatchingService
        {
            MatchHandler = (request, _) => Task.FromResult(new TemplateMatchBatchResult(
                TemplateMatchingEngine.Halcon,
                InspectionOutcome.Ok,
                true,
                [
                    new TemplateMatchBatchCandidate(
                        new Pose2D(40, 30, 15) { Scale = 1.05 },
                        0.923,
                        30,
                        20,
                        Array.Empty<IReadOnlyList<Point2D>>(),
                        [
                            [
                                new Point2D(25, 20),
                                new Point2D(55, 20),
                                new Point2D(55, 40),
                                new Point2D(25, 40)
                            ]
                        ])
                    {
                        OuterCoverage = 0.91,
                        InnerCoverage = 0.82,
                        EdgeDistanceP95Px = 1.75,
                        PolarityAgreement = 0.88
                    }
                ],
                new TemplateSearchRegion(0, 0, request.Frame.Width, request.Frame.Height),
                "matched",
                false))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);

        await viewModel.RunToolCommand.Execute(CancellationToken.None);

        var cross = Assert.Single(
            viewModel.PreviewOverlays,
            overlay => overlay.Kind == VisionOverlayKind.Cross);
        Assert.Contains("#1", cross.Label);
        Assert.Contains("S=0.923", cross.Label);
        Assert.Contains("O=0.910", cross.Label);
        Assert.Contains("I=0.820", cross.Label);
        Assert.Contains("P95=1.750", cross.Label);
        Assert.Contains("P=0.880", cross.Label);
    }

    [Fact]
    public async Task OpenCvLearningPublishesUniqueFileWithoutOverwritingActiveModel()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.Engine] = TemplateMatchingEngine.OpenCv.ToString();
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var runtimePaths = new RuntimePaths(tempDirectory.Path);
        var resourceDirectory = Path.Combine(
            runtimePaths.TemplateResourceDirectory,
            RuntimePaths.SanitizePathSegment(tool.Id));
        Directory.CreateDirectory(resourceDirectory);
        var activePath = Path.Combine(resourceDirectory, "template.bin");
        var activeBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(activePath, activeBytes);
        parameters["modelPath"] = activePath;
        parameters["modelVersion"] = "1.0";
        tool.ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.OpenCv,
                true,
                new Dictionary<string, string>
                {
                    ["templateWidth"] = "30",
                    ["templateHeight"] = "20",
                    ["templatePixels"] = Convert.ToBase64String([5, 6, 7, 8])
                },
                "learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Equal(activeBytes, File.ReadAllBytes(activePath));
        Assert.NotEqual(activePath, viewModel.PendingParameters["modelPath"]);
        Assert.True(File.Exists(viewModel.PendingParameters["modelPath"]));
    }

    [Fact]
    public async Task OpenCvPersistenceFailureDoesNotReactivateOldModelWithNewPixels()
    {
        using var tempDirectory = new TempDirectory();
        Directory.CreateDirectory(tempDirectory.Path);
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.Engine] = TemplateMatchingEngine.OpenCv.ToString();
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var oldModelPath = Path.Combine(tempDirectory.Path, "old-model.bin");
        File.WriteAllBytes(oldModelPath, [1, 2, 3, 4]);
        parameters["modelPath"] = oldModelPath;
        parameters["modelVersion"] = "1.0";
        tool.ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
        var runtimePaths = new RuntimePaths(tempDirectory.Path);
        Directory.CreateDirectory(runtimePaths.TemplateResourceDirectory);
        File.WriteAllText(
            Path.Combine(
                runtimePaths.TemplateResourceDirectory,
                RuntimePaths.SanitizePathSegment(tool.Id)),
            "blocks directory creation");
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.OpenCv,
                true,
                new Dictionary<string, string>
                {
                    ["templateWidth"] = "30",
                    ["templateHeight"] = "20",
                    ["templatePixels"] = Convert.ToBase64String([5, 6, 7, 8])
                },
                "learned",
                null))
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        var before = viewModel.PendingParameters.ToArray();

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Equal(before, viewModel.PendingParameters);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(oldModelPath));
        Assert.Contains("failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LateHalconResolveCannotPublishLearnedStateAfterEngineSwitch()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ResolvedTemplateModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = async (_, _, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);
        var initialization = InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedEngine = TemplateMatchingEngine.OpenCv;

        release.TrySetResult(new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()));
        await initialization;

        Assert.Equal(TemplateMatchingEngine.OpenCv, viewModel.SelectedEngine);
        Assert.False(viewModel.HasLearnedTemplateModel);
    }

    [Fact]
    public async Task LateHalconResolveCannotPublishLearnedStateAfterGenerationEdit()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ResolvedTemplateModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingTemplateModelStore
        {
            ResolveHandler = async (_, _, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            store);
        var initialization = InvokeTaskMethodAsync(viewModel, "InitializeAsync", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.HalconScaleMin = "0.91";
        release.TrySetResult(new ResolvedTemplateModel("resolved/model.shm", "{}"u8.ToArray()));
        await initialization;

        Assert.True(viewModel.RequiresRelearn);
        Assert.False(viewModel.HasLearnedTemplateModel);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ModelRelearnRequired, viewModel.StatusText);
    }

    [Fact]
    public async Task CancelAndDrainDoesNotRetireStableProductionOwner()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var resources = new RecordingTemplateModelResourceManager();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            modelResources: resources);

        await viewModel.CancelAndDrainAsync();

        Assert.Empty(resources.RetiredOwners);
    }

    [Fact]
    public async Task HalconResetRemovesOnlyKnownHalconStateAndRetiresStableOwner()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters["halcon.futureExtension"] = "keep";
        parameters["modelPath"] = "opencv/model.bin";
        parameters["modelVersion"] = "1.0";
        parameters["templateImagePng"] = "opencv-preview";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var resources = new RecordingTemplateModelResourceManager();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            modelResources: resources);

        await GetProperty<IAsyncCommand>(viewModel, "ResetTemplateCommand")
            .ExecuteAsync(null, CancellationToken.None);

        Assert.All(TemplateModelParameterCodec.Keys, key => Assert.False(viewModel.PendingParameters.ContainsKey(key)));
        Assert.Equal("keep", viewModel.PendingParameters["halcon.futureExtension"]);
        Assert.Equal("opencv/model.bin", viewModel.PendingParameters["modelPath"]);
        Assert.Equal("1.0", viewModel.PendingParameters["modelVersion"]);
        Assert.Equal("opencv-preview", viewModel.PendingParameters["templateImagePng"]);
        Assert.Equal(
            new TemplateModelOwner("recipe", "flow", tool.Id),
            Assert.Single(resources.RetiredOwners));
    }

    [Fact]
    public async Task OpenCvResetRemovesLegacyModelAndPreservesCompleteHalconState()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var halconState = CreateCompleteHalconLearnedState();
        foreach (var parameter in halconState)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters[TemplateMatchingParameterCatalog.Engine] = TemplateMatchingEngine.OpenCv.ToString();
        parameters["modelPath"] = "opencv/model.bin";
        parameters["modelVersion"] = "1.0";
        parameters["templatePixels"] = Convert.ToBase64String([1, 2, 3, 4]);
        parameters["templateImagePng"] = "opencv-preview";
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var resources = new RecordingTemplateModelResourceManager();
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            RecordingTemplateMatchingService.Successful(),
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60),
            modelResources: resources);

        await GetProperty<IAsyncCommand>(viewModel, "ResetTemplateCommand")
            .ExecuteAsync(null, CancellationToken.None);

        Assert.False(viewModel.PendingParameters.ContainsKey("modelPath"));
        Assert.False(viewModel.PendingParameters.ContainsKey("modelVersion"));
        Assert.False(viewModel.PendingParameters.ContainsKey("templatePixels"));
        Assert.False(viewModel.PendingParameters.ContainsKey("templateImagePng"));
        foreach (var parameter in halconState)
        {
            Assert.Equal(parameter.Value, viewModel.PendingParameters[parameter.Key]);
        }

        Assert.Single(resources.RetiredOwners);
    }

    [Fact]
    public async Task SuccessfulHalconLearningShowsNeutralPreviewWithoutPersistingIt()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var preview = new TemplateLearningPreview(
            new Point2D(40, 30),
            [new Point2D(-10, -5), new Point2D(10, -5), new Point2D(10, 5)],
            [[new Point2D(-2, 0), new Point2D(2, 0)]]);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                CreateCompleteHalconLearnedState(),
                "learned",
                null)
            {
                Preview = preview
            })
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));

        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);

        Assert.Contains(viewModel.PreviewOverlays, item => item.Label == "学习外轮廓");
        Assert.Contains(viewModel.PreviewOverlays, item => item.Label.StartsWith("学习内部特征", StringComparison.Ordinal));
        Assert.Contains(viewModel.PreviewOverlays, item => item.Label == "模型原点");
        Assert.DoesNotContain(viewModel.PendingParameters.Keys, key =>
            key.Contains("preview", StringComparison.OrdinalIgnoreCase) &&
            key.StartsWith("halcon.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HalconLearningPreviewIsHiddenWhileOpenCvIsActive()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var service = new RecordingTemplateMatchingService
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.Halcon,
                true,
                CreateCompleteHalconLearnedState(),
                "learned",
                null)
            {
                Preview = new TemplateLearningPreview(
                    new Point2D(40, 30),
                    [new Point2D(-5, 0), new Point2D(5, 0)],
                    Array.Empty<IReadOnlyList<Point2D>>())
            })
        };
        var viewModel = CreateInjectedViewModel(
            tool,
            tempDirectory,
            service,
            CreatePreviewRecipe("recipe", "flow", tool),
            CreateFrame(80, 60));
        await viewModel.LearnTemplateCommand.Execute(CancellationToken.None);
        Assert.Contains(viewModel.PreviewOverlays, item => item.Label == "学习外轮廓");

        viewModel.SelectedEngine = TemplateMatchingEngine.OpenCv;

        Assert.DoesNotContain(viewModel.PreviewOverlays, item => item.Label == "学习外轮廓");
        Assert.DoesNotContain(viewModel.PreviewOverlays, item => item.Label == "模型原点");
    }

    [Fact]
    public async Task ResetWithMissingOwnerDoesNotCallRetirementWithEmptyIds()
    {
        using var tempDirectory = new TempDirectory();
        var parameters = CreateHalconParametersWithTemplateRoi(TemplateMatchCardinality.Single);
        foreach (var parameter in CreateCompleteHalconLearnedState())
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var tool = CreateTool(VisionToolKind.TemplateLocate, parameters);
        var resources = new RecordingTemplateModelResourceManager();
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            Array.Empty<RoiDefinition>(),
            "Display flow",
            CreateFrame(80, 60),
            new RuntimePaths(tempDirectory.Path),
            new NullAppLogService(),
            null,
            null,
            RecordingTemplateMatchingService.Successful(),
            NoOpTemplateModelStore.Instance,
            resources);

        await viewModel.ResetTemplateCommand.Execute(CancellationToken.None);

        Assert.Empty(resources.RetiredOwners);
        Assert.Contains(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, viewModel.StatusText);
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
        ImageFrame frame,
        ITemplateModelStore? modelStore = null,
        ITemplateModelResourceManager? modelResources = null,
        IVisionPipeline? pipeline = null)
    {
        var constructor = typeof(TemplateLocateToolDialogViewModel)
            .GetConstructors()
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 12 &&
                       parameters[^3].ParameterType == typeof(ITemplateMatchingService) &&
                       parameters[^2].ParameterType == typeof(ITemplateModelStore) &&
                       parameters[^1].ParameterType == typeof(ITemplateModelResourceManager);
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
            pipeline,
            service,
            modelStore ?? NoOpTemplateModelStore.Instance,
            modelResources ?? NoOpTemplateModelResourceManager.Instance
        ]));
    }

    private sealed class BlockingVisionPipeline : IVisionPipeline
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken LastToken { get; private set; }

        public async Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame frame,
            CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking pipeline should only finish by cancellation.");
        }
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

    private static async Task InvokeTaskMethodAsync(
        object target,
        string methodName,
        CancellationToken cancellationToken)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(target, [cancellationToken]));
        await task;
    }

    private static void InvokeVoidMethod(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        Assert.NotNull(method);
        method.Invoke(target, null);
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
            TemplateMatchingService.CreateLegacyOnly(),
            NoOpTemplateModelStore.Instance,
            NoOpTemplateModelResourceManager.Instance);
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
                    CreateCompleteHalconLearnedState(),
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
                       CreateCompleteHalconLearnedState(),
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

    private sealed class RecordingTemplateModelStore : ITemplateModelStore
    {
        public Func<TemplateModelOwner, TemplateModelReference, CancellationToken, Task<ResolvedTemplateModel>>?
            ResolveHandler
        { get; init; }

        public List<(TemplateModelOwner Owner, TemplateModelReference Reference, CancellationToken Token)> Resolutions
        {
            get;
        } = new();

        public Task<ResolvedTemplateModel> ResolveAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken)
        {
            Resolutions.Add((owner, reference, cancellationToken));
            return ResolveHandler?.Invoke(owner, reference, cancellationToken)
                   ?? throw new NotSupportedException();
        }

        public Task<TemplateModelWriteSession> BeginWriteAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TemplateModelReference> CommitAsync(
            TemplateModelWriteSession session,
            ReadOnlyMemory<byte> metadataJson,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TemplateModelReference> CopyGenerationAsync(
            TemplateModelOwner sourceOwner,
            TemplateModelReference sourceReference,
            TemplateModelOwner targetOwner,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteGenerationAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteOwnerResourcesAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingTemplateModelResourceManager : ITemplateModelResourceManager
    {
        public List<TemplateModelOwner> RetiredOwners { get; } = new();

        public Task RetireToolAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken)
        {
            RetiredOwners.Add(owner);
            return Task.CompletedTask;
        }

        public Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
            Recipe source,
            string newRecipeId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteRecipeResourcesAsync(
            Recipe deletedRecipe,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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

internal sealed class NoOpTemplateModelStore : ITemplateModelStore
{
    public static NoOpTemplateModelStore Instance { get; } = new();

    private NoOpTemplateModelStore()
    {
    }

    public Task<TemplateModelWriteSession> BeginWriteAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<TemplateModelReference> CommitAsync(
        TemplateModelWriteSession session,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<ResolvedTemplateModel> ResolveAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<TemplateModelReference> CopyGenerationAsync(
        TemplateModelOwner sourceOwner,
        TemplateModelReference sourceReference,
        TemplateModelOwner targetOwner,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task DeleteGenerationAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task DeleteOwnerResourcesAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed class NoOpTemplateModelResourceManager : ITemplateModelResourceManager
{
    public static NoOpTemplateModelResourceManager Instance { get; } = new();

    private NoOpTemplateModelResourceManager()
    {
    }

    public Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task RetireToolAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task DeleteRecipeResourcesAsync(
        Recipe deletedRecipe,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}
