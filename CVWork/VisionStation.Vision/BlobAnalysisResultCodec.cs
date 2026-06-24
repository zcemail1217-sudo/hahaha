using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision;

public sealed record BlobAnalysisBlob(
    double X,
    double Y,
    double Area,
    double Left,
    double Top,
    double Width,
    double Height,
    double Circularity,
    double AspectRatio,
    double Perimeter,
    double CircleX,
    double CircleY,
    double CircleRadius,
    IReadOnlyList<Point2D> Contour)
{
    public Point2D Center => new(X, Y);

    public Point2D CircleCenter => new(CircleX, CircleY);

    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public static class BlobAnalysisResultCodec
{
    public static string FormatBlobs(IEnumerable<BlobAnalysisBlob> blobs)
    {
        return string.Join(";", blobs.Select(FormatBlob));
    }

    public static IReadOnlyList<BlobAnalysisBlob> ParseBlobs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<BlobAnalysisBlob>();
        }

        var blobs = new List<BlobAnalysisBlob>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 7 ||
                !TryParse(parts[0], out var x) ||
                !TryParse(parts[1], out var y) ||
                !TryParse(parts[2], out var area) ||
                !TryParse(parts[3], out var left) ||
                !TryParse(parts[4], out var top) ||
                !TryParse(parts[5], out var width) ||
                !TryParse(parts[6], out var height))
            {
                continue;
            }

            var circularity = parts.Length >= 8 && TryParse(parts[7], out var parsedCircularity)
                ? parsedCircularity
                : 0;
            var aspectRatio = parts.Length >= 9 && TryParse(parts[8], out var parsedAspectRatio)
                ? parsedAspectRatio
                : Math.Max(width, height) / Math.Max(1, Math.Min(width, height));
            var perimeter = parts.Length >= 10 && TryParse(parts[9], out var parsedPerimeter)
                ? parsedPerimeter
                : 0;
            var circleX = parts.Length >= 11 && TryParse(parts[10], out var parsedCircleX)
                ? parsedCircleX
                : x;
            var circleY = parts.Length >= 12 && TryParse(parts[11], out var parsedCircleY)
                ? parsedCircleY
                : y;
            var circleRadius = parts.Length >= 13 && TryParse(parts[12], out var parsedCircleRadius)
                ? parsedCircleRadius
                : Math.Max(width, height) / 2.0;
            var contour = parts.Length >= 14
                ? ParseContour(parts[13])
                : parts.Length >= 9
                    ? ParseContour(parts[8])
                    : Array.Empty<Point2D>();

            blobs.Add(new BlobAnalysisBlob(
                x,
                y,
                area,
                left,
                top,
                width,
                height,
                circularity,
                aspectRatio,
                perimeter,
                circleX,
                circleY,
                circleRadius,
                contour));
        }

        return blobs;
    }

    public static string FormatContour(IReadOnlyList<Point2D> points)
    {
        return string.Join("|", points.Select(point => $"{ToInvariant(point.X)}:{ToInvariant(point.Y)}"));
    }

    public static IReadOnlyList<Point2D> ParseContour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Point2D>();
        }

        var points = new List<Point2D>();
        foreach (var item in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && TryParse(parts[0], out var x) && TryParse(parts[1], out var y))
            {
                points.Add(new Point2D(x, y));
            }
        }

        return points;
    }

    private static string FormatBlob(BlobAnalysisBlob blob)
    {
        return string.Join(
            ',',
            ToInvariant(blob.X),
            ToInvariant(blob.Y),
            ToInvariant(blob.Area),
            ToInvariant(blob.Left),
            ToInvariant(blob.Top),
            ToInvariant(blob.Width),
            ToInvariant(blob.Height),
            ToInvariant(blob.Circularity),
            ToInvariant(blob.AspectRatio),
            ToInvariant(blob.Perimeter),
            ToInvariant(blob.CircleX),
            ToInvariant(blob.CircleY),
            ToInvariant(blob.CircleRadius),
            FormatContour(blob.Contour));
    }

    private static bool TryParse(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ToInvariant(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
