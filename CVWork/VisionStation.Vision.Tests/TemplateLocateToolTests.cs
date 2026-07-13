using System.Globalization;
using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateLocateToolTests
{
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
    public async Task ExecuteAsyncSerializesVersionedSeparatedOverlayDiagnostics()
    {
        var fixture = TemplateMatcherTestData.CreatePolygonTemplateFixture();
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
        var recipe = new Recipe
        {
            Tools = [sourceTool, locateTool]
        };
        using var context = new VisionToolContext(recipe, fixture.SearchFrame);
        context.SetImageOutput(sourceTool, fixture.SearchFrame);

        var result = await new TemplateLocateTool().ExecuteAsync(locateTool, context);

        Assert.Equal("2", result.Data["overlaySchemaVersion"]);
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
        Assert.Equal(
            matchedTemplateRoiContours,
            deserialized.Data["matchedTemplateRoiContours"]);
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

        var result = await new TemplateLocateTool().ExecuteAsync(locateTool, context);

        Assert.Equal("locate", result.ToolId);
        Assert.Equal(VisionToolKind.TemplateLocate, result.Kind);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("ImageInput", result.Data["missingInput"]);
        Assert.Equal("2", result.Data["overlaySchemaVersion"]);
    }
}
