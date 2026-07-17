using System.Globalization;
using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateLocateToolTests : IDisposable
{
    private readonly TemplateMatchingService _matchingService = TemplateMatchingService.CreateLegacyOnly();

    [Fact]
    public void MatchSeparatesScoringContoursFromMatchedTemplateRoiContours()
    {
        var fixture = TemplateMatcherTestData.CreatePolygonTemplateFixture();

        var match = TemplateMatcher.Match(fixture.SearchFrame, null, fixture.Parameters);

        Assert.True(match.HasMatch, match.Message);
        var shapeContours = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyList<Point2D>>>(match.ShapeContours);
        var matchedTemplateRoiContours = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyList<Point2D>>>(
            match.MatchedTemplateRoiContours);
        Assert.NotEmpty(shapeContours);
        Assert.NotEmpty(matchedTemplateRoiContours);
        Assert.NotSame(shapeContours, matchedTemplateRoiContours);
        Assert.DoesNotContain(
            shapeContours,
            shapeContour => matchedTemplateRoiContours.Any(roiContour => shapeContour.SequenceEqual(roiContour)));
    }

    [Fact]
    public void MatchReturnsCompleteRectangleTemplateRoiContour()
    {
        var parameters = TemplateMatcherTestData.LearnRuntimeParameters();

        var match = TemplateMatcher.Match(TemplateMatcherTestData.CreateSearchFrame(), null, parameters);

        Assert.True(match.HasMatch, match.Message);
        var contour = Assert.Single(match.MatchedTemplateRoiContours!);
        Assert.Equal(4, contour.Count);
        Assert.Equal(60, contour.Min(point => point.X), 3);
        Assert.Equal(160, contour.Max(point => point.X), 3);
        Assert.Equal(40, contour.Min(point => point.Y), 3);
        Assert.Equal(340, contour.Max(point => point.Y), 3);
    }

    [Fact]
    public async Task ExecuteAsyncSerializesVersionedSeparatedOverlayDiagnostics()
    {
        var fixture = new TemplateMatcherFixture(
            TemplateMatcherTestData.CreateSearchFrame(),
            TemplateMatcherTestData.LearnRuntimeParameters());
        fixture.Parameters["inputImageToolId"] = "source";
        var sourceTool = new VisionToolDefinition
        {
            Id = "source",
            Kind = VisionToolKind.AcquireImage
        };
        var locateTool = new VisionToolDefinition
        {
            Id = "locate",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = fixture.Parameters
        };
        var outputConsumer = new VisionToolDefinition
        {
            Id = "output-consumer",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PositionInput:toolId"] = locateTool.Id,
                ["input:PositionInput:portKey"] = "PositionOutput"
            }
        };
        var recipe = new Recipe
        {
            Tools = [sourceTool, locateTool, outputConsumer]
        };
        using var context = new VisionToolContext(recipe, fixture.SearchFrame);
        context.SetImageOutput(sourceTool, fixture.SearchFrame);

        var result = await new TemplateLocateTool(_matchingService)
            .ExecuteAsync(locateTool, context);

        Assert.True(result.Data.TryGetValue("OVERLAYSCHEMAVERSION", out var schemaVersion));
        Assert.Equal("2", schemaVersion);
        Assert.Equal("2", result.Data["overlaySchemaVersion"]);
        Assert.Equal("True", result.Data["hasMatch"]);
        Assert.True(context.TryGetPortInput<Pose2D>(outputConsumer, "PositionInput", out var outputPose));
        Assert.Equal(outputPose.Scale.ToString(CultureInfo.InvariantCulture), result.Data["scale"]);
        var shapeContours = result.Data["shapeContours"];
        var matchedTemplateRoiContours = result.Data["matchedTemplateRoiContours"];
        Assert.False(string.IsNullOrWhiteSpace(shapeContours));
        Assert.False(string.IsNullOrWhiteSpace(matchedTemplateRoiContours));
        Assert.NotEqual(shapeContours, matchedTemplateRoiContours);

        Assert.True(double.TryParse(
            result.Data["shapeCoverage"],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var shapeCoverage));
        Assert.InRange(shapeCoverage, 0, 1);
        Assert.True(double.TryParse(
            result.Data["shapeReverseScore"],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var shapeReverseScore));
        Assert.InRange(shapeReverseScore, 0, 1);

        var serialized = JsonSerializer.Serialize(result);
        var deserialized = JsonSerializer.Deserialize<ToolResult>(serialized);

        Assert.NotNull(deserialized);
        foreach (var key in new[]
                 {
                     "overlaySchemaVersion",
                     "hasMatch",
                     "shapeContours",
                     "matchedTemplateRoiContours",
                     "shapeCoverage",
                     "shapeReverseScore"
                 })
        {
            Assert.Equal(result.Data[key], deserialized.Data[key]);
        }
    }

    [Fact]
    public async Task ExecuteAsyncMissingInputStillWritesOverlaySchemaVersion()
    {
        var fixture = TemplateMatcherTestData.CreatePolygonTemplateFixture();
        var locateTool = new VisionToolDefinition
        {
            Id = "locate",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = fixture.Parameters
        };
        var recipe = new Recipe
        {
            Tools = [locateTool]
        };
        using var context = new VisionToolContext(recipe, fixture.SearchFrame);

        var result = await new TemplateLocateTool(_matchingService)
            .ExecuteAsync(locateTool, context);

        Assert.Equal("locate", result.ToolId);
        Assert.Equal(VisionToolKind.TemplateLocate, result.Kind);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("ImageInput", result.Data["missingInput"]);
        Assert.Equal("2", result.Data["overlaySchemaVersion"]);
        Assert.Equal("False", result.Data["hasMatch"]);
    }

    public void Dispose()
    {
        _matchingService.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
