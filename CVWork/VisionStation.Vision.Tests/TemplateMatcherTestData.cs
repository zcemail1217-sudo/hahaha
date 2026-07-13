using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision.Tests;

internal sealed record TemplateMatcherFixture(
    ImageFrame SearchFrame,
    Dictionary<string, string> Parameters);

internal static class TemplateMatcherTestData
{
    private static readonly Point2d[][] ProductPolygons =
    [
        [new(-16, -130), new(16, -130), new(16, 130), new(-16, 130)],
        [new(-16, -115), new(38, -115), new(38, -82), new(-16, -82)],
        [new(-42, 70), new(16, 70), new(16, 110), new(-42, 110)]
    ];

    public static ImageFrame CreateTrainingFrame()
    {
        return CreateProductFrame(220, 380, new Point2d(110, 190), 0);
    }

    public static ImageFrame CreateUniformTrainingFrame()
    {
        using var image = new Mat(380, 220, MatType.CV_8UC1, Scalar.White);
        return CreateFrame(image, "synthetic-uniform-training");
    }

    public static ImageFrame CreateLowContrastGradientFrame()
    {
        const int width = 220;
        const int height = 380;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (byte)(100 + x * 10 / (width - 1) + y * 10 / (height - 1));
            }
        }

        using var image = Mat.FromPixelData(height, width, MatType.CV_8UC1, pixels);
        return CreateFrame(image, "synthetic-low-contrast-gradient");
    }

    public static Dictionary<string, string> CreateLearningParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["autoContrast"] = "false",
            ["contrast"] = "30",
            ["cannyHigh"] = "80",
            ["minScore"] = "0.85",
            ["angleStart"] = "-180",
            ["angleExtent"] = "360",
            ["angleStep"] = "2",
            ["shapeCoarseScale"] = "1",
            ["templateRoiX"] = "60",
            ["templateRoiY"] = "40",
            ["templateRoiWidth"] = "100",
            ["templateRoiHeight"] = "300"
        };
    }

    public static Dictionary<string, string> LearnRuntimeParameters()
    {
        var parameters = CreateLearningParameters();
        var learned = TemplateMatcher.Learn(CreateTrainingFrame(), null, parameters);
        foreach (var parameter in learned)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters["minScore"] = "0.85";
        return parameters;
    }

    public static ImageFrame CreateSearchFrame(bool fragmentOnly = false, bool extraEdges = false)
    {
        const int width = 220;
        const int height = 380;
        using var image = CreateProductMat(width, height, new Point2d(110, 190), 0);

        if (fragmentOnly)
        {
            const int keepTop = 145;
            const int keepBottom = 235;
            Cv2.Rectangle(image, new Rect(0, 0, width, keepTop), Scalar.White, -1);
            Cv2.Rectangle(image, new Rect(0, keepBottom + 1, width, height - keepBottom - 1), Scalar.White, -1);
        }

        if (extraEdges)
        {
            for (var index = 0; index < 8; index++)
            {
                var y = 95 + index * 20;
                Cv2.Line(image, new Point(70, y), new Point(150, y), Scalar.Black, 2);
            }
        }

        return CreateFrame(image, "synthetic-search");
    }

    public static TemplateMatcherFixture CreatePolygonTemplateFixture()
    {
        var parameters = CreateLearningParameters();
        var roi = new RoiDefinition
        {
            Id = "polygon-template-roi",
            Name = "Polygon Template ROI",
            Shape = RoiShapeKind.Polygon,
            Points =
            [
                new Point2D(60, 40),
                new Point2D(160, 40),
                new Point2D(160, 340),
                new Point2D(60, 340)
            ]
        };
        parameters["templateRoiJson"] = JsonSerializer.Serialize(roi);
        parameters["templateRoiShape"] = RoiShapeKind.Polygon.ToString();

        var learned = TemplateMatcher.Learn(CreateTrainingFrame(), null, parameters);
        foreach (var parameter in learned)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        parameters["minScore"] = 0.85.ToString(CultureInfo.InvariantCulture);
        return new TemplateMatcherFixture(CreateSearchFrame(), parameters);
    }

    public static ImageFrame CreateProductFrame(
        int width,
        int height,
        Point2d center,
        double clockwiseAngle)
    {
        using var image = CreateProductMat(width, height, center, clockwiseAngle);
        return CreateFrame(image, "synthetic-product");
    }

    public static ImageFrame CreateRotatedSearchFrameWithLocalEdge(double clockwiseAngle)
    {
        const int width = 500;
        const int height = 500;
        var center = new Point2d(250, 250);
        using var image = CreateProductMat(width, height, center, clockwiseAngle);
        var start = RotateAroundCenter(new Point2d(35, -50));
        var end = RotateAroundCenter(new Point2d(35, 50));
        Cv2.Line(image, start, end, Scalar.Black, 2);
        return CreateFrame(image, "synthetic-rotated-search");

        Point RotateAroundCenter(Point2d point)
        {
            var radians = clockwiseAngle * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new Point(
                (int)Math.Round(center.X + point.X * cos - point.Y * sin),
                (int)Math.Round(center.Y + point.X * sin + point.Y * cos));
        }
    }

    public static ImageFrame CreateRotatedFragmentSearchFrame(double clockwiseAngle)
    {
        const int width = 500;
        const int height = 500;
        var center = new Point2d(250, 250);
        using var image = CreateProductMat(width, height, center, clockwiseAngle);
        var radians = clockwiseAngle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        Point Transform(Point2d point) => new(
            (int)Math.Round(center.X + point.X * cos - point.Y * sin),
            (int)Math.Round(center.Y + point.X * sin + point.Y * cos));
        var eraser = new[]
        {
            Transform(new Point2d(-60, -150)),
            Transform(new Point2d(60, -150)),
            Transform(new Point2d(60, -60)),
            Transform(new Point2d(-60, -60))
        };
        Cv2.FillPoly(image, [eraser], Scalar.White);
        return CreateFrame(image, "synthetic-rotated-fragment-search");
    }

    private static Mat CreateProductMat(
        int width,
        int height,
        Point2d center,
        double clockwiseAngle)
    {
        var image = new Mat(height, width, MatType.CV_8UC1, Scalar.White);
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
            Cv2.FillPoly(image, [rotatedPoints], Scalar.Black);
        }

        return image;
    }

    private static ImageFrame CreateFrame(Mat image, string id)
    {
        image.GetArray(out byte[] pixels);
        return new ImageFrame(
            id,
            image.Width,
            image.Height,
            image.Width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }
}
