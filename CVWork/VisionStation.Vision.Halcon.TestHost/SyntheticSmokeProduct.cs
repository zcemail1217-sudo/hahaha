using VisionStation.Domain;

namespace VisionStation.Vision.Halcon.TestHost;

internal static class SyntheticSmokeProduct
{
    private const int TemplateWidth = 260;
    private const int TemplateHeight = 180;
    private const byte BackgroundIntensity = 235;
    private const byte ProductIntensity = 35;
    private const byte FeatureIntensity = 220;

    private static readonly RasterPoint[] ProductOutline =
    [
        new(45, 57),
        new(178, 50),
        new(213, 77),
        new(190, 124),
        new(72, 129),
        new(43, 100)
    ];

    private static readonly RasterPoint[] FeatureCenters =
    [
        new(82, 78),
        new(128, 70),
        new(182, 90),
        new(112, 108),
        new(163, 111)
    ];

    public static RoiDefinition TemplateRoi { get; } = new()
    {
        Id = "testhost-template-roi",
        Name = "HALCON TestHost Template",
        Shape = RoiShapeKind.Rectangle,
        X = 20,
        Y = 20,
        Width = 220,
        Height = 140
    };

    public static ImageFrame CreateTemplateFrame()
    {
        var pixels = new byte[TemplateWidth * TemplateHeight];
        Array.Fill(pixels, BackgroundIntensity);
        DrawProduct(pixels, TemplateWidth, TemplateHeight, 0, 0);
        return CreateFrame(
            pixels,
            TemplateWidth,
            TemplateHeight,
            "halcon-testhost-template");
    }

    public static ImageFrame CreateTimeoutSearchFrame()
    {
        const int width = 1400;
        const int height = 1120;
        var pixels = new byte[width * height];
        uint state = 0x6D2B79F5u;
        for (int index = 0; index < pixels.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            pixels[index] = (byte)(state >> 24);
        }

        DrawProduct(
            pixels,
            width,
            height,
            width - TemplateWidth,
            height - TemplateHeight);
        return CreateFrame(
            pixels,
            width,
            height,
            "halcon-testhost-timeout-search");
    }

    private static void DrawProduct(
        byte[] pixels,
        int width,
        int height,
        int offsetX,
        int offsetY)
    {
        for (int localY = 50; localY <= 129; localY++)
        {
            for (int localX = 43; localX <= 213; localX++)
            {
                if (IsInsidePolygon(localX + 0.5, localY + 0.5, ProductOutline))
                {
                    SetPixel(
                        pixels,
                        width,
                        height,
                        localX + offsetX,
                        localY + offsetY,
                        ProductIntensity);
                }
            }
        }

        for (int index = 0; index < FeatureCenters.Length; index++)
        {
            RasterPoint center = FeatureCenters[index];
            if (index % 2 == 0)
            {
                FillRotatedEllipse(
                    pixels,
                    width,
                    height,
                    center.X + offsetX,
                    center.Y + offsetY,
                    7 + index,
                    4 + index / 2,
                    index * 11,
                    FeatureIntensity);
            }
            else
            {
                FillRectangle(
                    pixels,
                    width,
                    height,
                    center.X - 7 + offsetX,
                    center.Y - 4 + offsetY,
                    15,
                    9,
                    FeatureIntensity);
            }
        }
    }

    private static void FillRotatedEllipse(
        byte[] pixels,
        int width,
        int height,
        int centerX,
        int centerY,
        int radiusX,
        int radiusY,
        double angleDegrees,
        byte intensity)
    {
        double radians = angleDegrees * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        int bound = Math.Max(radiusX, radiusY);
        for (int y = centerY - bound; y <= centerY + bound; y++)
        {
            for (int x = centerX - bound; x <= centerX + bound; x++)
            {
                double dx = x - centerX;
                double dy = y - centerY;
                double localX = dx * cosine + dy * sine;
                double localY = -dx * sine + dy * cosine;
                double normalized = localX * localX / (radiusX * radiusX) +
                                    localY * localY / (radiusY * radiusY);
                if (normalized <= 1d)
                {
                    SetPixel(pixels, width, height, x, y, intensity);
                }
            }
        }
    }

    private static void FillRectangle(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        int rectangleWidth,
        int rectangleHeight,
        byte intensity)
    {
        for (int row = y; row < y + rectangleHeight; row++)
        {
            for (int column = x; column < x + rectangleWidth; column++)
            {
                SetPixel(pixels, width, height, column, row, intensity);
            }
        }
    }

    private static bool IsInsidePolygon(
        double x,
        double y,
        IReadOnlyList<RasterPoint> points)
    {
        var inside = false;
        for (int current = 0; current < points.Count; current++)
        {
            RasterPoint first = points[current];
            RasterPoint second = points[(current + 1) % points.Count];
            bool crosses = (first.Y > y) != (second.Y > y) &&
                           x < (double)(second.X - first.X) * (y - first.Y) /
                           (second.Y - first.Y) + first.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static void SetPixel(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        byte intensity)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            return;
        }

        pixels[y * width + x] = intensity;
    }

    private static ImageFrame CreateFrame(
        byte[] pixels,
        int width,
        int height,
        string id)
    {
        return new ImageFrame(
            id,
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }

    private readonly record struct RasterPoint(int X, int Y);
}
