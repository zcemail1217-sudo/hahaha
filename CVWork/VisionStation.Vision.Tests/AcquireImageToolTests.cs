using OpenCvSharp;
using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class AcquireImageToolTests
{
    [Fact]
    public async Task ExecuteAsync_LoadsFileSourceFrame()
    {
        var directory = CreateTempDirectory();
        var imagePath = Path.Combine(directory, "source.bmp");
        WriteGrayImage(imagePath, 3, 2, 90);

        try
        {
            using var context = new VisionToolContext(new Recipe(), CreatePlaceholderFrame());
            var definition = new VisionToolDefinition
            {
                Id = "acquire",
                Name = "Acquire",
                Kind = VisionToolKind.AcquireImage,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "File",
                    ["filePath"] = imagePath,
                    ["convertColorToGray"] = "true"
                }
            };

            var result = await new AcquireImageTool().ExecuteAsync(definition, context);

            Assert.Equal(InspectionOutcome.Ok, result.Outcome);
            Assert.Equal(3, context.ResultFrame.Width);
            Assert.Equal(2, context.ResultFrame.Height);
            Assert.Equal(PixelFormatKind.Gray8, context.ResultFrame.Format);
            Assert.Equal(imagePath, context.ResultFrame.Source);
            Assert.Equal(imagePath, result.Data["source"]);
            Assert.Equal("File", result.Data["sourceMode"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LoadsFirstSupportedDirectoryFrame()
    {
        var directory = CreateTempDirectory();
        var firstImagePath = Path.Combine(directory, "a.bmp");
        var secondImagePath = Path.Combine(directory, "b.bmp");
        WriteGrayImage(secondImagePath, 5, 4, 120);
        WriteGrayImage(firstImagePath, 2, 1, 30);

        try
        {
            using var context = new VisionToolContext(new Recipe(), CreatePlaceholderFrame());
            var definition = new VisionToolDefinition
            {
                Id = "acquire",
                Name = "Acquire",
                Kind = VisionToolKind.AcquireImage,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "Directory",
                    ["directoryPath"] = directory,
                    ["convertColorToGray"] = "true"
                }
            };

            var result = await new AcquireImageTool().ExecuteAsync(definition, context);

            Assert.Equal(InspectionOutcome.Ok, result.Outcome);
            Assert.Equal(2, context.ResultFrame.Width);
            Assert.Equal(1, context.ResultFrame.Height);
            Assert.Equal(firstImagePath, context.ResultFrame.Source);
            Assert.Equal("Directory", result.Data["sourceMode"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionStation.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteGrayImage(string path, int width, int height, byte value)
    {
        using var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(value));
        Cv2.ImWrite(path, image);
    }

    private static ImageFrame CreatePlaceholderFrame()
    {
        return new ImageFrame(
            "placeholder",
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.UtcNow,
            "placeholder");
    }
}
