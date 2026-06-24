using VisionStation.Domain;
using VisionStation.Infrastructure;
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
