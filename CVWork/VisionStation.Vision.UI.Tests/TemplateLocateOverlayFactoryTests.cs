using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateLocateOverlayFactoryTests
{
    [Fact]
    public void CreateV2ResultSeparatesScoredEdgesAndTemplateRoi()
    {
        var result = CreateV2ToolResult();

        var overlays = TemplateLocateOverlayFactory.Create(result);

        var warning = Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        var info = Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);
        Assert.Equal([new Point2D(1, 2), new Point2D(3, 4)], warning.Points);
        Assert.Equal([new Point2D(10, 20), new Point2D(30, 40)], info.Points);

        var cross = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
        Assert.True(cross.PreserveLabelInResult);
        Assert.Contains("S=0.923", cross.Label);
        Assert.Contains("C=0.887", cross.Label);
    }

    [Fact]
    public void CreateLegacyResultKeepsMixedContoursOrange()
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["shapeContours"] = $"{source.Data["shapeContours"]}|{source.Data["matchedTemplateRoiContours"]}"
        };
        data.Remove("overlaySchemaVersion");
        data.Remove("hasMatch");
        data.Remove("matchedTemplateRoiContours");
        var legacy = source with { Data = data };

        var overlays = TemplateLocateOverlayFactory.Create(legacy);

        var polylines = overlays.Where(item => item.Kind == VisionOverlayKind.Polyline).ToArray();
        Assert.Equal(2, polylines.Length);
        Assert.All(polylines, item => Assert.Equal(VisionOverlayState.Warning, item.State));
        Assert.DoesNotContain(overlays, item => item.State == VisionOverlayState.Info);
    }

    [Fact]
    public void StructuredAndPersistedResultsUseTheSameRoles()
    {
        var structured = new TemplateMatchResult(
            HasMatch: true,
            Outcome: InspectionOutcome.Ok,
            Score: 0.923,
            Pose: new Pose2D(100, 120, 35),
            MatchX: 0,
            MatchY: 0,
            TemplateWidth: 300,
            TemplateHeight: 100,
            SearchRegion: new TemplateSearchRegion(0, 0, 640, 480),
            Message: "OK",
            UsedAutoTemplate: false,
            ShapeContours: [[new Point2D(1, 2), new Point2D(3, 4)]])
        {
            MatchedTemplateRoiContours = [[new Point2D(10, 20), new Point2D(30, 40)]],
            ShapeCoverage = 0.887
        };

        var structuredRoles = TemplateLocateOverlayFactory.Create(structured)
            .Select(item => (item.Kind, item.State));
        var persistedRoles = TemplateLocateOverlayFactory.Create(CreateV2ToolResult())
            .Select(item => (item.Kind, item.State));

        Assert.Equal(structuredRoles, persistedRoles);
    }

    [Fact]
    public void CreateSkipsMalformedAndNonFiniteDiagnosticPoints()
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["shapePoints"] = "bad;NaN,2;5,6",
            ["shapeContours"] = "1,2;bad;3,4|Infinity,1;5,6",
            ["matchedTemplateRoiContours"] = "garbage|10,20;broken;30,40"
        };

        var overlays = TemplateLocateOverlayFactory.Create(source with { Data = data });

        var pointCloud = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.PointCloud);
        Assert.Equal([new Point2D(5, 6)], pointCloud.Points);
        var warning = Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        Assert.Equal([new Point2D(1, 2), new Point2D(3, 4)], warning.Points);
        var info = Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);
        Assert.Equal([new Point2D(10, 20), new Point2D(30, 40)], info.Points);
    }

    [Fact]
    public void MissingInputResultDoesNotCreatePositionOverlay()
    {
        var result = new ToolResult
        {
            ToolId = "locate",
            Kind = VisionToolKind.TemplateLocate,
            Outcome = InspectionOutcome.Ng,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["overlaySchemaVersion"] = "2",
                ["hasMatch"] = "False",
                ["missingInput"] = "ImageInput"
            }
        };

        var projected = VisionResultOverlayProjector.Project(TemplateLocateOverlayFactory.Create(result));

        Assert.Empty(projected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateInvalidPrimaryFieldsKeepsContoursWithoutPositionOverlay(bool includesHasMatch)
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase);
        data.Remove("x");
        data["score"] = "NaN";
        if (!includesHasMatch)
        {
            data.Remove("hasMatch");
        }

        var overlays = TemplateLocateOverlayFactory.Create(source with { Data = data });

        Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);
        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.Cross);
        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
    }

    [Theory]
    [InlineData("False")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("garbage")]
    public void CreateExplicitMissingOrInvalidMatchFlagFailsClosed(string rawHasMatch)
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["hasMatch"] = rawHasMatch
        };
        data.Remove("shapeContours");
        data.Remove("matchedTemplateRoiContours");

        var overlays = TemplateLocateOverlayFactory.Create(source with { Data = data });

        Assert.Empty(overlays);
    }

    [Fact]
    public void CreateLegacyFiniteLowScoreCandidateKeepsCrossAndFallback()
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["score"] = ".123"
        };
        data.Remove("hasMatch");
        data.Remove("shapeContours");
        data.Remove("matchedTemplateRoiContours");
        var legacy = source with { Outcome = InspectionOutcome.Ng, Data = data };

        var overlays = TemplateLocateOverlayFactory.Create(legacy);

        var rectangle = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
        var cross = Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
        Assert.Equal(VisionOverlayState.Ng, rectangle.State);
        Assert.Equal(VisionOverlayState.Ng, cross.State);
        Assert.Contains("S=0.123", cross.Label);
    }

    [Fact]
    public void CreateStructuredNoMatchKeepsContoursWithoutPositionOverlay()
    {
        var result = new TemplateMatchResult(
            HasMatch: false,
            Outcome: InspectionOutcome.Ng,
            Score: 0.923,
            Pose: new Pose2D(100, 120, 35),
            MatchX: 0,
            MatchY: 0,
            TemplateWidth: 300,
            TemplateHeight: 100,
            SearchRegion: new TemplateSearchRegion(0, 0, 640, 480),
            Message: "No match",
            UsedAutoTemplate: false,
            ShapeContours: [[new Point2D(1, 2), new Point2D(3, 4)]])
        {
            MatchedTemplateRoiContours = [[new Point2D(10, 20), new Point2D(30, 40)]]
        };

        var overlays = TemplateLocateOverlayFactory.Create(result);

        Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Warning);
        Assert.Single(overlays, item =>
            item.Kind == VisionOverlayKind.Polyline && item.State == VisionOverlayState.Info);
        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.Cross);
        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
    }

    [Theory]
    [InlineData("0", "100")]
    [InlineData("300", "NaN")]
    public void CreateValidMatchWithInvalidTemplateSizeKeepsCrossWithoutFallback(
        string templateWidth,
        string templateHeight)
    {
        var source = CreateV2ToolResult();
        var data = new Dictionary<string, string>(source.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["templateWidth"] = templateWidth,
            ["templateHeight"] = templateHeight
        };
        data.Remove("shapeContours");
        data.Remove("matchedTemplateRoiContours");

        var overlays = TemplateLocateOverlayFactory.Create(source with { Data = data });

        Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
    }

    [Fact]
    public void CreateStructuredSinglePointContourSuppressesFallbackRectangle()
    {
        var result = new TemplateMatchResult(
            HasMatch: true,
            Outcome: InspectionOutcome.Ok,
            Score: 0.923,
            Pose: new Pose2D(100, 120, 35),
            MatchX: 0,
            MatchY: 0,
            TemplateWidth: 300,
            TemplateHeight: 100,
            SearchRegion: new TemplateSearchRegion(0, 0, 640, 480),
            Message: "OK",
            UsedAutoTemplate: false,
            ShapeContours: [[new Point2D(1, 2)]]);

        var overlays = TemplateLocateOverlayFactory.Create(result);

        Assert.DoesNotContain(overlays, item => item.Kind == VisionOverlayKind.RotatedRectangle);
        Assert.Single(overlays, item => item.Kind == VisionOverlayKind.Cross);
    }

    private static ToolResult CreateV2ToolResult()
    {
        return new ToolResult
        {
            ToolId = "locate",
            ToolName = "Template locate",
            Kind = VisionToolKind.TemplateLocate,
            Outcome = InspectionOutcome.Ok,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = "100",
                ["y"] = "120",
                ["angle"] = "35",
                ["templateWidth"] = "300",
                ["templateHeight"] = "100",
                ["score"] = ".923",
                ["shapeCoverage"] = ".887",
                ["overlaySchemaVersion"] = "2",
                ["hasMatch"] = "True",
                ["shapeContours"] = "1,2;3,4",
                ["matchedTemplateRoiContours"] = "10,20;30,40"
            }
        };
    }
}
