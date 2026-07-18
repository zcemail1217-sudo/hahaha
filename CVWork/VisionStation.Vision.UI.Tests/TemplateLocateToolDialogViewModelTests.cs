using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateLocateToolDialogViewModelTests
{
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
            new NullAppLogService());

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
            new NullAppLogService());

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
            new NullAppLogService());

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
            new NullAppLogService());

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
            new NullAppLogService());

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
            new NullAppLogService());

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
            new NullAppLogService());
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
            new NullAppLogService());
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
