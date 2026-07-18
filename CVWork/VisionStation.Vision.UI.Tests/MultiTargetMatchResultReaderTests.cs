using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class MultiTargetMatchResultReaderTests
{
    [Fact]
    public void ValidV2TakesPrecedenceOverConflictingLegacyData()
    {
        var data = V2Data();
        data["matches"] = "900,901,90,0.1,4,5,Circle,2";

        var matches = MultiTargetMatchResultReader.Read(data);

        var match = Assert.Single(matches);
        Assert.Equal((12d, 34d, 56d, 1.1d, 0.95d),
            (match.X, match.Y, match.Angle, match.Scale, match.Score));
        Assert.Equal((0.93d, 0.86d, 2.2d, 0.94d),
            (match.OuterCoverage, match.InnerCoverage, match.EdgeDistanceP95Px, match.PolarityAgreement));
        Assert.Equal((40, 20, "Rectangle", 0d),
            (match.Width, match.Height, match.Shape, match.Radius));
    }

    [Fact]
    public void MissingV2FallsBackToLegacyEightColumnMatches()
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matches"] = "12,34,56,0.95,40,20,Rectangle,0"
        };

        var matches = MultiTargetMatchResultReader.Read(data);

        var match = Assert.Single(matches);
        Assert.Equal((12d, 34d, 56d, 1d, 0.95d),
            (match.X, match.Y, match.Angle, match.Scale, match.Score));
        Assert.Equal((0d, 0d, 0d, 0d),
            (match.OuterCoverage, match.InnerCoverage, match.EdgeDistanceP95Px, match.PolarityAgreement));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[{\"x\":12,\"y\":34,\"angle\":56,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    [InlineData("[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":\"NaN\",\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    [InlineData("[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2}]")]
    public void PresentButInvalidV2FailsClosedWithoutLegacyFallback(string matchesV2)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matchSchemaVersion"] = "2",
            ["matchesV2"] = matchesV2,
            ["matches"] = "12,34,56,0.95,40,20,Rectangle,0"
        };

        var matches = MultiTargetMatchResultReader.Read(data);

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("1", "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    [InlineData("2", "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":0,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    [InlineData("2", "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":\"Infinity\",\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    [InlineData("2", "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":1e400,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}]")]
    public void UnsupportedSchemaAndUnsafeNumbersFailClosed(string version, string matchesV2)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matchSchemaVersion"] = version,
            ["matchesV2"] = matchesV2,
            ["matches"] = "12,34,56,0.95,40,20,Rectangle,0"
        };

        Assert.Empty(MultiTargetMatchResultReader.Read(data));
    }

    [Fact]
    public void MissingV2SchemaVersionFailsClosedWithoutLegacyFallback()
    {
        var data = V2Data();
        data.Remove("matchSchemaVersion");
        data["matches"] = "12,34,56,0.95,40,20,Rectangle,0";

        Assert.Empty(MultiTargetMatchResultReader.Read(data));
    }

    [Fact]
    public void OneMalformedV2ItemRejectsTheWholeArray()
    {
        var data = V2Data();
        data["matchesV2"] =
            "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94}," +
            "{\"x\":90,\"y\":91,\"angle\":0,\"scale\":-1,\"score\":0.9,\"outerCoverage\":0.9,\"innerCoverage\":0.8,\"edgeDistanceP95Px\":2,\"polarityAgreement\":0.9}]";

        Assert.Empty(MultiTargetMatchResultReader.Read(data));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("12,34,56")]
    [InlineData("NaN,34,56,0.95,40,20,Rectangle,0")]
    [InlineData("Infinity,34,56,0.95,40,20,Rectangle,0")]
    [InlineData("12,34,56,0.95,40,20,Rectangle,-1")]
    public void OneMalformedLegacyItemRejectsTheWholeArray(string malformedItem)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matches"] = $"12,34,56,0.95,40,20,Rectangle,0;{malformedItem}"
        };

        Assert.Empty(MultiTargetMatchResultReader.Read(data));
    }

    [Fact]
    public void V2CandidateKeepsScaleAndMetricsInUiPointItem()
    {
        var candidate = Assert.Single(MultiTargetMatchResultReader.Read(V2Data()));

        var item = MultiTargetMatchPointItem.FromCandidate(1, candidate);

        Assert.Equal(1.1, item.Scale, 12);
        Assert.Equal(1.1, item.Pose.Scale, 12);
        Assert.Equal(0.93, item.OuterCoverage, 12);
        Assert.Equal(0.86, item.InnerCoverage, 12);
        Assert.Equal(2.2, item.EdgeDistanceP95Px, 12);
        Assert.Equal(0.94, item.PolarityAgreement, 12);
    }

    [Fact]
    public void OverlayBuilderUsesTheSameV2ReaderAndAppliesScale()
    {
        var data = V2Data();
        data["matches"] = "900,901,90,0.1,4,5,Rectangle,0";
        var result = new ToolResult
        {
            ToolId = "multi",
            ToolName = "Multi",
            Kind = VisionToolKind.MultiTargetMatch,
            Outcome = InspectionOutcome.Ok,
            Data = data
        };

        var overlays = new VisionOverlayBuilder().Build(
            new Recipe(),
            new ImageFrame("frame", 2, 2, 1, PixelFormatKind.Gray8, [0, 0, 0, 0], DateTimeOffset.Now, "test"),
            [result],
            InspectionOutcome.Ok);

        var rectangle = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
        Assert.Equal((12d, 34d, 44d, 22d, 56d),
            (rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, rectangle.Angle));
    }

    [Theory]
    [InlineData("Rectangle", 0d)]
    [InlineData("Circle", 2d)]
    public void OverlayBuilderSuppressesOverflowedShapeButKeepsFiniteDiagnosticCross(
        string shape,
        double radius)
    {
        var data = V2Data();
        data["matchesV2"] =
            $"[{{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1e308,\"score\":0.95," +
            $"\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2," +
            $"\"polarityAgreement\":0.94,\"width\":2,\"height\":2,\"shape\":\"{shape}\",\"radius\":{radius}}}]";

        var overlays = BuildOverlays(data);

        Assert.DoesNotContain(
            overlays,
            item => item.Kind is VisionOverlayKind.Circle or VisionOverlayKind.RotatedRectangle);
        var cross = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
        AssertFiniteGeometry(cross);
    }

    [Fact]
    public void OverlayBuilderNormalizesExtremeFiniteAngleBeforePublishingGeometry()
    {
        var data = V2Data();
        data["matchesV2"] =
            "[{\"x\":12,\"y\":34,\"angle\":1.7976931348623157E+308,\"scale\":1,\"score\":0.95," +
            "\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2," +
            "\"polarityAgreement\":0.94,\"width\":40,\"height\":20,\"shape\":\"Rectangle\",\"radius\":0}]";

        var overlays = BuildOverlays(data);

        var rectangle = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
        var cross = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
        var expectedAngle = double.MaxValue % 360d;
        Assert.Equal(expectedAngle, rectangle.Angle, 12);
        Assert.Equal(expectedAngle, cross.Angle, 12);
        AssertFiniteGeometry(rectangle);
        AssertFiniteGeometry(cross);
    }

    private static IReadOnlyList<VisionOverlayItem> BuildOverlays(Dictionary<string, string> data)
    {
        var result = new ToolResult
        {
            ToolId = "multi",
            ToolName = "Multi",
            Kind = VisionToolKind.MultiTargetMatch,
            Outcome = InspectionOutcome.Ok,
            Data = data
        };

        return new VisionOverlayBuilder().Build(
            new Recipe(),
            new ImageFrame("frame", 2, 2, 1, PixelFormatKind.Gray8, [0, 0, 0, 0], DateTimeOffset.Now, "test"),
            [result],
            InspectionOutcome.Ok);
    }

    private static void AssertFiniteGeometry(VisionOverlayItem overlay)
    {
        Assert.True(double.IsFinite(overlay.X));
        Assert.True(double.IsFinite(overlay.Y));
        Assert.True(double.IsFinite(overlay.Width));
        Assert.True(double.IsFinite(overlay.Height));
        Assert.True(double.IsFinite(overlay.Angle));
        Assert.True(double.IsFinite(overlay.Radius));
    }

    private static Dictionary<string, string> V2Data()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matchSchemaVersion"] = "2",
            ["matchesV2"] = "[{\"x\":12,\"y\":34,\"angle\":56,\"scale\":1.1,\"score\":0.95,\"outerCoverage\":0.93,\"innerCoverage\":0.86,\"edgeDistanceP95Px\":2.2,\"polarityAgreement\":0.94,\"width\":40,\"height\":20,\"shape\":\"Rectangle\",\"radius\":0}]",
            ["templateWidth"] = "40",
            ["templateHeight"] = "20"
        };
    }
}
