using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class MultiTargetCancellationTests
{
    private static readonly string[] OutputPortKeys =
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
    ];

    [Fact]
    public void CircularBlobMatcherThrowsWhenCancellationIsAlreadyRequested()
    {
        var (frame, parameters) = CreateCircularBlobFixture();
        var validResult = MultiTargetMatcher.Match(frame, null, parameters);
        Assert.Equal(InspectionOutcome.Ok, validResult.Outcome);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MultiTargetMatcher.Match(frame, null, parameters, cancellation.Token));
    }

    [Fact]
    public async Task CircularBlobToolClearsOldOutputsAndThrowsWhenCancellationIsAlreadyRequested()
    {
        var (frame, parameters) = CreateCircularBlobFixture();
        var imageSource = new VisionToolDefinition { Id = "image-source" };
        parameters["input:ImageInput:toolId"] = imageSource.Id;
        parameters["input:ImageInput:portKey"] = "ImageOutput";
        var definition = new VisionToolDefinition
        {
            Id = "multi-target",
            Kind = VisionToolKind.MultiTargetMatch,
            Parameters = parameters
        };
        var outputConsumer = CreateOutputConsumer(definition);
        using var context = new VisionToolContext(
            new Recipe { Tools = [imageSource, definition, outputConsumer] },
            frame);
        context.SetImageOutput(imageSource, frame);
        SeedOldOutputs(context, definition);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new MultiTargetMatchTool().ExecuteAsync(definition, context, cancellation.Token));

        Assert.False(context.Properties.ContainsKey("pose"));
        foreach (var portKey in OutputPortKeys)
        {
            Assert.False(
                context.TryGetPortInputValue(outputConsumer, $"{portKey}Input", out _),
                $"Canceled execution published or retained {portKey}.");
        }
    }

    private static (ImageFrame Frame, Dictionary<string, string> Parameters) CreateCircularBlobFixture()
    {
        const int frameWidth = 96;
        const int frameHeight = 96;
        const int templateSize = 32;
        var framePixels = CreateWhiteImage(frameWidth, frameHeight);
        DrawFilledCircle(framePixels, frameWidth, frameHeight, 30, 30, 10);
        DrawFilledCircle(framePixels, frameWidth, frameHeight, 68, 64, 10);

        var templatePixels = CreateWhiteImage(templateSize, templateSize);
        DrawFilledCircle(templatePixels, templateSize, templateSize, 16, 16, 10);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["multiMatchMode"] = "CircularBlob",
            ["templateShape"] = "Circle",
            ["templateWidth"] = templateSize.ToString(),
            ["templateHeight"] = templateSize.ToString(),
            ["templatePixels"] = Convert.ToBase64String(templatePixels),
            ["minScore"] = "0.6",
            ["minCount"] = "1",
            ["matchCount"] = "4"
        };
        var frame = new ImageFrame(
            "circular-search",
            frameWidth,
            frameHeight,
            frameWidth,
            PixelFormatKind.Gray8,
            framePixels,
            DateTimeOffset.UtcNow,
            "test");
        return (frame, parameters);
    }

    private static VisionToolDefinition CreateOutputConsumer(VisionToolDefinition definition)
    {
        var consumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var portKey in OutputPortKeys)
        {
            consumer.Parameters[$"input:{portKey}Input:toolId"] = definition.Id;
            consumer.Parameters[$"input:{portKey}Input:portKey"] = portKey;
        }

        return consumer;
    }

    private static void SeedOldOutputs(VisionToolContext context, VisionToolDefinition definition)
    {
        var pose = new Pose2D(30, 30, 0);
        context.Properties["pose"] = pose;
        context.SetPortOutput(definition, "PositionOutput", pose);
        context.SetPortOutput(definition, "OriginOutput", pose);
        context.SetPortOutput(definition, "BestPositionOutput", pose);
        context.SetPortOutput(definition, "ScoreOutput", 0.9d);
        context.SetPortOutput(definition, "XOutput", pose.X);
        context.SetPortOutput(definition, "YOutput", pose.Y);
        context.SetPortOutput(definition, "AngleOutput", pose.Angle);
        context.SetPortOutput(definition, "CountOutput", 1);
        context.SetPortOutput(definition, "AllPositionsOutput", new[] { pose });
        context.SetPortOutput(definition, "ScoresOutput", new[] { 0.9d });
    }

    private static byte[] CreateWhiteImage(int width, int height)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, byte.MaxValue);
        return pixels;
    }

    private static void DrawFilledCircle(
        byte[] pixels,
        int width,
        int height,
        int centerX,
        int centerY,
        int radius)
    {
        var radiusSquared = radius * radius;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                var deltaX = x - centerX;
                var deltaY = y - centerY;
                if (deltaX * deltaX + deltaY * deltaY <= radiusSquared)
                {
                    pixels[y * width + x] = 0;
                }
            }
        }
    }
}
