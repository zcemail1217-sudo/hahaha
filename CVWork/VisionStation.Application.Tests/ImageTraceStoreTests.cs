using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ImageTraceStoreTests
{
    [Fact]
    public async Task SaveAsync_WhenTracePolicyUsesPng_WritesPngFiles()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BmpImageTraceStore(new RuntimePaths(baseDirectory));
            var frame = new ImageFrame(
                "frame",
                2,
                2,
                2,
                PixelFormatKind.Gray8,
                [0, 64, 128, 255],
                DateTimeOffset.Now,
                "test");
            var recipe = new Recipe
            {
                Id = "recipe",
                TracePolicy = new TracePolicy { ImageFormat = "Png" }
            };
            var result = new InspectionResult
            {
                Id = "result",
                RecipeId = recipe.Id,
                BatchId = "batch",
                Outcome = InspectionOutcome.Ok
            };

            var paths = await store.SaveAsync(recipe, frame, frame, result);

            Assert.EndsWith(".png", paths.OriginalImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], File.ReadAllBytes(paths.OriginalImagePath).Take(8).ToArray());
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], File.ReadAllBytes(paths.ResultImagePath).Take(8).ToArray());
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }
}
