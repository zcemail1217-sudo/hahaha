using System.Collections.Concurrent;
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
            finished = queuedContext.RunUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10));
            queuedContext.Drain();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.True(startedBusy, "Second template matching completed before its queued continuation could be observed.");
        Assert.True(oldResultWasCleared, "The previous result overlays remained visible after the new run started.");
        Assert.True(finished, $"Second template matching did not finish: {viewModel.StatusText}");
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
