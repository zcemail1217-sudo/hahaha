using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class OpenCvTemplateMatcherGeometryTests
{
    private static readonly Point2d[][] ProductPolygons =
    [
        [new(-16, -130), new(16, -130), new(16, 130), new(-16, 130)],
        [new(-16, -115), new(38, -115), new(38, -82), new(-16, -82)],
        [new(-42, 70), new(16, 70), new(16, 110), new(-42, 110)]
    ];

    private static readonly (int Start, int End)[] BoundarySegments =
    [
        (8, 19),
        (33, 48),
        (67, 84),
        (109, 137),
        (173, 191),
        (226, 259),
        (276, 289)
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(90)]
    [InlineData(-135)]
    public void WholeShapeRotationPreservesCanvasCenterAndPose(double clockwiseAngle)
    {
        var expectedCenter = new Point2d(430, 330);
        var trainingFrame = CreateProductFrame(220, 380, new Point2d(110, 190), 0);
        var searchFrame = CreateProductFrame(700, 700, expectedCenter, clockwiseAngle);

        var result = LearnAndMatch(trainingFrame, searchFrame, clockwiseAngle, forceDirectPass: true);

        var expectedCanvas = ExpectedCanvasSize(100, 300, clockwiseAngle);
        Assert.True(result.HasMatch, result.Message);
        Assert.Equal(expectedCanvas.Width, result.TemplateWidth);
        Assert.Equal(expectedCanvas.Height, result.TemplateHeight);
        Assert.InRange(Math.Abs(result.Pose.X - expectedCenter.X), 0, 2);
        Assert.InRange(Math.Abs(result.Pose.Y - expectedCenter.Y), 0, 2);
        Assert.InRange(AngleDistance(result.Pose.Angle, clockwiseAngle), 0, 1);
    }

    [Fact]
    public void ShapeCanMatchWhenOnlyRotatedCanvasFitsSearchRegion()
    {
        var trainingFrame = CreateProductFrame(220, 380, new Point2d(110, 190), 0);
        var searchFrame = CreateProductFrame(320, 120, new Point2d(160, 60), 90);

        var result = LearnAndMatch(trainingFrame, searchFrame, 90, forceDirectPass: true);

        Assert.True(result.HasMatch, result.Message);
        Assert.Equal(300, result.TemplateWidth);
        Assert.Equal(100, result.TemplateHeight);
    }

    [Fact]
    public void CoarseToRefinePreservesRotatedCenterAndCanvas()
    {
        var expectedCenter = new Point2d(780, 480);
        var trainingFrame = CreateProductFrame(220, 380, new Point2d(110, 190), 0);
        var searchFrame = CreateProductFrame(1200, 960, expectedCenter, 90);

        var result = LearnAndMatch(trainingFrame, searchFrame, 90, forceDirectPass: false);

        Assert.True(result.HasMatch, result.Message);
        Assert.Equal(300, result.TemplateWidth);
        Assert.Equal(100, result.TemplateHeight);
        Assert.InRange(Math.Abs(result.Pose.X - expectedCenter.X), 0, 2);
        Assert.InRange(Math.Abs(result.Pose.Y - expectedCenter.Y), 0, 2);
    }

    [Fact]
    public void QuarterTurnKeepsBoundaryPixelsInsideEvenSizedCanvas()
    {
        var parameters = CreateParameters(clockwiseAngle: -90, direct: true);
        var learnedParameters = TemplateMatcher.Learn(CreateBoundaryTrainingFrame(), null, parameters);
        foreach (var parameter in learnedParameters)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters["templateMaskPng"] = CreateLeftBoundaryMaskPng();
        var result = TemplateMatcher.Match(CreateBoundaryRotatedSearchFrame(), null, parameters);

        // The mask retains only source x=0 edges, so a match proves the quarter-turn kept them in bounds.
        Assert.True(result.HasMatch, result.Message);
        Assert.Equal(300, result.TemplateWidth);
        Assert.Equal(100, result.TemplateHeight);
        Assert.InRange(Math.Abs(result.Pose.X - 150), 0, 0.001);
        Assert.InRange(Math.Abs(result.Pose.Y - 50), 0, 0.001);
        Assert.InRange(AngleDistance(result.Pose.Angle, -90), 0, 0.001);
    }

    private static TemplateMatchResult LearnAndMatch(
        ImageFrame trainingFrame,
        ImageFrame searchFrame,
        double clockwiseAngle,
        bool forceDirectPass)
    {
        var parameters = CreateParameters(clockwiseAngle, forceDirectPass);
        var learnedParameters = TemplateMatcher.Learn(trainingFrame, null, parameters);
        foreach (var parameter in learnedParameters)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        return TemplateMatcher.Match(searchFrame, null, parameters);
    }

    private static Dictionary<string, string> CreateParameters(double clockwiseAngle, bool direct)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["autoContrast"] = "false",
            ["contrast"] = "30",
            ["cannyHigh"] = "80",
            ["minScore"] = "0",
            ["angleStart"] = (-clockwiseAngle).ToString(CultureInfo.InvariantCulture),
            ["angleExtent"] = "0.5",
            ["angleStep"] = "1",
            ["shapeCoarseScale"] = direct ? "1" : "0",
            ["templateRoiX"] = "60",
            ["templateRoiY"] = "40",
            ["templateRoiWidth"] = "100",
            ["templateRoiHeight"] = "300"
        };
    }

    private static ImageFrame CreateProductFrame(
        int width,
        int height,
        Point2d center,
        double clockwiseAngle)
    {
        using var image = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        var radians = clockwiseAngle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        foreach (var polygon in ProductPolygons)
        {
            var rotatedPoints = polygon
                .Select(point => new Point(
                    (int)Math.Round(center.X + point.X * cos - point.Y * sin),
                    (int)Math.Round(center.Y + point.X * sin + point.Y * cos)))
                .ToArray();
            Cv2.FillPoly(image, [rotatedPoints], Scalar.White);
        }

        image.GetArray(out byte[] pixels);
        return new ImageFrame(
            "synthetic-product",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }

    private static ImageFrame CreateBoundaryTrainingFrame()
    {
        const int width = 220;
        const int height = 380;
        var pixels = new byte[width * height];
        foreach (var (start, end) in BoundarySegments)
        {
            for (var y = start; y <= end; y++)
            {
                pixels[(40 + y) * width + 60] = 255;
            }
        }

        return CreateGrayFrame(width, height, pixels);
    }

    private static ImageFrame CreateBoundaryRotatedSearchFrame()
    {
        const int width = 300;
        const int height = 100;
        var pixels = new byte[width * height];
        foreach (var (start, end) in BoundarySegments)
        {
            for (var sourceY = start; sourceY <= end; sourceY++)
            {
                // A +90 degree OpenCV rotation about pixel centers maps (0, y) to (y, 99).
                pixels[99 * width + sourceY] = 255;
            }
        }

        return CreateGrayFrame(width, height, pixels);
    }

    private static string CreateLeftBoundaryMaskPng()
    {
        using var mask = new Mat(300, 100, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(mask, new Point(0, 0), new Point(0, 299), Scalar.White);
        return Convert.ToBase64String(mask.ToBytes(".png"));
    }

    private static ImageFrame CreateGrayFrame(int width, int height, byte[] pixels)
    {
        return new ImageFrame(
            "boundary-pixels",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }

    private static (int Width, int Height) ExpectedCanvasSize(int width, int height, double angle)
    {
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Abs(SnapTrig(Math.Cos(radians)));
        var sin = Math.Abs(SnapTrig(Math.Sin(radians)));
        return (
            (int)Math.Ceiling(width * cos + height * sin),
            (int)Math.Ceiling(width * sin + height * cos));
    }

    private static double SnapTrig(double value)
    {
        if (Math.Abs(value) < 1e-12)
        {
            return 0;
        }

        if (Math.Abs(Math.Abs(value) - 1) < 1e-12)
        {
            return Math.Sign(value);
        }

        return value;
    }

    private static double AngleDistance(double first, double second)
    {
        var distance = Math.Abs(first - second) % 360;
        return distance > 180 ? 360 - distance : distance;
    }
}
