using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class ImageProcessToolTests
{
    [Fact]
    public async Task FixedThresholdProducesBinaryImageOutput()
    {
        var frame = CreateGrayFrame(4, 1, [10, 80, 180, 240]);
        using var context = CreateContext(frame);
        var tool = new VisionToolDefinition
        {
            Id = "process",
            Name = "Threshold",
            Kind = VisionToolKind.ImageProcess,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputImageToolId"] = "source",
                ["operation"] = "Threshold",
                ["thresholdMode"] = "Fixed",
                ["threshold"] = "100",
                ["polarity"] = "Bright"
            }
        };

        var result = await new ImageProcessTool().ExecuteAsync(tool, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal(PixelFormatKind.Gray8, context.ResultFrame.Format);
        Assert.Equal([0, 0, 255, 255], context.ResultFrame.Pixels);
    }

    [Fact]
    public async Task MorphologyOpenRemovesSinglePixelNoise()
    {
        var pixels = new byte[25];
        pixels[12] = 255;
        var frame = CreateGrayFrame(5, 5, pixels);
        using var context = CreateContext(frame);
        var tool = new VisionToolDefinition
        {
            Id = "process",
            Name = "Open",
            Kind = VisionToolKind.ImageProcess,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputImageToolId"] = "source",
                ["operation"] = "Morphology",
                ["morphType"] = "Open",
                ["kernelSize"] = "3"
            }
        };

        var result = await new ImageProcessTool().ExecuteAsync(tool, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.All(context.ResultFrame.Pixels, value => Assert.Equal(0, value));
    }

    private static VisionToolContext CreateContext(ImageFrame frame)
    {
        var sourceTool = new VisionToolDefinition
        {
            Id = "source",
            Name = "Source",
            Kind = VisionToolKind.AcquireImage
        };
        var context = new VisionToolContext(new Recipe
        {
            Tools = [sourceTool]
        }, frame);
        context.SetImageOutput(sourceTool, frame);
        return context;
    }

    private static ImageFrame CreateGrayFrame(int width, int height, IReadOnlyList<byte> pixels)
    {
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels.ToArray(),
            DateTimeOffset.UtcNow,
            "test");
    }
}
