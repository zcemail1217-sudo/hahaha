using VisionStation.Domain;

namespace VisionStation.Vision.Halcon.Tests;

public enum SyntheticHalconNegativeCase
{
    BacksideInternalLayout,
    SimilarOutlineWrongInternal,
    CrossesImageBoundary,
    CrossesSearchRoiBoundary,
    PartialMiddleOnly,
    SevereOcclusion,
    OppositePolarity
}

internal sealed record SyntheticHalconNegativeScene(
    ImageFrame Frame,
    RoiDefinition? SearchRoi);

internal static class SyntheticHalconProductFactory
{
    private const int ImageWidth = 420;
    private const int ImageHeight = 420;
    private const byte BackgroundGray = 235;
    private const byte ProductGray = 28;
    private const string RuntimeRootEnvironmentVariable = "VISIONSTATION_HALCON_ROOT";
    private const string HalconRootEnvironmentVariable = "HALCONROOT";
    private const string OwnedWorkingDirectoryName = "VisionStation-HalconIntegration";

    private static readonly Point2D LearningCenter = new(210, 210);

    private static readonly Point2D[] OuterContour =
    [
        new(-18, -96),
        new(14, -96),
        new(14, -86),
        new(24, -86),
        new(24, -66),
        new(17, -66),
        new(17, -44),
        new(30, -44),
        new(30, -26),
        new(20, -26),
        new(20, -8),
        new(35, -8),
        new(35, 12),
        new(18, 12),
        new(18, 31),
        new(27, 31),
        new(27, 52),
        new(15, 52),
        new(15, 88),
        new(6, 88),
        new(6, 97),
        new(-20, 97),
        new(-20, 82),
        new(-29, 82),
        new(-29, 62),
        new(-18, 62),
        new(-18, 28),
        new(-34, 28),
        new(-34, 8),
        new(-22, 8),
        new(-22, -16),
        new(-29, -16),
        new(-29, -38),
        new(-19, -38),
        new(-19, -68),
        new(-26, -68),
        new(-26, -88),
        new(-18, -88)
    ];

    private static readonly Point2D[] TriangleMarker =
    [
        new(-15, -29),
        new(-3, -18),
        new(-18, -14)
    ];

    public static Point2D MatchCenter { get; } = new(250, 190);

    public static ImageFrame CreateLearningFrame()
    {
        return RenderFrame(
            "halcon-synthetic-learning",
            [new Pose2D(LearningCenter.X, LearningCenter.Y, 0) { Scale = 1 }]);
    }

    public static ImageFrame CreateMatchFrame(double angleDeg, double scale)
    {
        return RenderFrame(
            $"halcon-synthetic-match-{angleDeg:R}-{scale:R}",
            [new Pose2D(MatchCenter.X, MatchCenter.Y, angleDeg) { Scale = scale }]);
    }

    public static ImageFrame CreateMultiTargetFrame(params Pose2D[] poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        if (poses.Length == 0)
        {
            throw new ArgumentException("At least one synthetic product pose is required.", nameof(poses));
        }

        return RenderFrame("halcon-synthetic-multi-target", poses);
    }

    public static SyntheticHalconNegativeScene CreateNegativeScene(
        SyntheticHalconNegativeCase negativeCase)
    {
        Pose2D normalPose = new(MatchCenter.X, MatchCenter.Y, 0) { Scale = 1 };
        return negativeCase switch
        {
            SyntheticHalconNegativeCase.BacksideInternalLayout => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-backside", [normalPose], RenderVariant.BacksideInternalLayout),
                null),
            SyntheticHalconNegativeCase.SimilarOutlineWrongInternal => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-similar", [normalPose], RenderVariant.WrongInternalLayout),
                null),
            SyntheticHalconNegativeCase.CrossesImageBoundary => new SyntheticHalconNegativeScene(
                RenderFrame(
                    "halcon-negative-image-boundary",
                    [new Pose2D(61, 111, 0) { Scale = 1 }]),
                null),
            SyntheticHalconNegativeCase.CrossesSearchRoiBoundary => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-search-boundary", [normalPose]),
                CreateTightSearchRoi()),
            SyntheticHalconNegativeCase.PartialMiddleOnly => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-partial-middle", [normalPose], RenderVariant.PartialMiddle),
                null),
            SyntheticHalconNegativeCase.SevereOcclusion => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-occluded", [normalPose], RenderVariant.SevereOcclusion),
                null),
            SyntheticHalconNegativeCase.OppositePolarity => new SyntheticHalconNegativeScene(
                RenderFrame("halcon-negative-opposite-polarity", [normalPose], RenderVariant.OppositePolarity),
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(negativeCase), negativeCase, null)
        };
    }

    public static SyntheticHalconNegativeScene CreatePolarityHardGateScene()
    {
        Pose2D pose = new(MatchCenter.X, MatchCenter.Y, 0) { Scale = 1 };
        return new SyntheticHalconNegativeScene(
            RenderFrame("halcon-negative-polarity-hard-gate", [pose], RenderVariant.PolarityHardGate),
            null);
    }

    public static RoiDefinition CreateTemplateRoi()
    {
        return new RoiDefinition
        {
            Id = "synthetic-product-template",
            Name = "Synthetic product template",
            Shape = RoiShapeKind.Rectangle,
            X = 150,
            Y = 100,
            Width = 120,
            Height = 220
        };
    }

    public static HalconRuntimeConfiguration CreateRuntimeConfiguration()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = Environment.GetEnvironmentVariable(HalconRootEnvironmentVariable);
        }

        return new HalconRuntimeConfiguration
        {
            RuntimeRoot = configuredRoot?.Trim() ?? string.Empty
        };
    }

    public static string CreateWorkingDirectory()
    {
        return Path.Combine(
            GetOwnedWorkingDirectoryRoot(),
            Guid.NewGuid().ToString("N"));
    }

    public static void DeleteWorkingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string ownedRoot = GetOwnedWorkingDirectoryRoot();
        string fullPath = Path.GetFullPath(path);
        string requiredPrefix = ownedRoot.TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete non-owned HALCON integration directory '{fullPath}'.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string GetOwnedWorkingDirectoryRoot()
    {
        return Path.GetFullPath(Path.Combine(Path.GetTempPath(), OwnedWorkingDirectoryName));
    }

    private static ImageFrame RenderFrame(
        string id,
        IReadOnlyList<Pose2D> poses,
        RenderVariant variant = RenderVariant.Standard)
    {
        var pixels = new byte[ImageWidth * ImageHeight];
        Array.Fill(pixels, BackgroundGray);

        foreach (Pose2D pose in poses)
        {
            RenderProduct(pixels, pose, variant);
        }

        return new ImageFrame(
            id,
            ImageWidth,
            ImageHeight,
            ImageWidth,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "synthetic-halcon-integration");
    }

    private static void RenderProduct(
        byte[] pixels,
        Pose2D pose,
        RenderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!double.IsFinite(pose.X) ||
            !double.IsFinite(pose.Y) ||
            !double.IsFinite(pose.Angle) ||
            !double.IsFinite(pose.Scale) ||
            pose.Scale <= 0)
        {
            throw new ArgumentException("Synthetic product poses must be finite with scale greater than zero.", nameof(pose));
        }

        double radians = pose.Angle * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        for (var imageY = 0; imageY < ImageHeight; imageY++)
        {
            for (var imageX = 0; imageX < ImageWidth; imageX++)
            {
                double translatedX = (imageX - pose.X) / pose.Scale;
                double translatedY = (imageY - pose.Y) / pose.Scale;
                double localX = translatedX * cosine + translatedY * sine;
                double localY = -translatedX * sine + translatedY * cosine;
                bool inside = IsInsidePolygon(localX, localY, OuterContour);
                if (!inside &&
                    variant is not RenderVariant.OppositePolarity and
                    not RenderVariant.PolarityHardGate)
                {
                    continue;
                }

                pixels[imageY * ImageWidth + imageX] = GetVariantPixel(
                    localX,
                    localY,
                    inside,
                    variant);
            }
        }
    }

    private static byte GetVariantPixel(
        double x,
        double y,
        bool inside,
        RenderVariant variant)
    {
        if (variant == RenderVariant.OppositePolarity)
        {
            return GetOppositePolarityPixel(x, y, inside);
        }

        if (variant == RenderVariant.PolarityHardGate)
        {
            return GetPolarityHardGatePixel(x, y, inside);
        }

        if (variant == RenderVariant.PartialMiddle && y is < -82 or > 82)
        {
            return inside ? (byte)220 : BackgroundGray;
        }

        if (variant == RenderVariant.SevereOcclusion && y is >= -17 and <= 18)
        {
            return inside ? (byte)220 : BackgroundGray;
        }

        if (!inside)
        {
            return BackgroundGray;
        }

        return variant switch
        {
            RenderVariant.Standard => GetProductPixel(x, y),
            RenderVariant.BacksideInternalLayout => GetBacksideProductPixel(x, y),
            RenderVariant.WrongInternalLayout => GetWrongInternalProductPixel(x, y),
            _ => GetProductPixel(x, y)
        };
    }

    private static byte GetProductPixel(double x, double y)
    {
        if (IsInsideEllipse(x, y, centerX: -5, centerY: -78, radiusX: 6, radiusY: 6))
        {
            return 220;
        }

        if (IsInsideRectangle(x, y, left: 3, top: -59, right: 13, bottom: -48))
        {
            return 180;
        }

        if (IsInsidePolygon(x, y, TriangleMarker))
        {
            return 225;
        }

        if (IsInsideEllipse(x, y, centerX: 8, centerY: 2, radiusX: 8, radiusY: 6))
        {
            return 198;
        }

        if (IsInsideRectangle(x, y, left: -10, top: 38, right: 4, bottom: 49))
        {
            return 218;
        }

        if (IsInsideEllipse(x, y, centerX: 2, centerY: 70, radiusX: 6, radiusY: 9))
        {
            return 185;
        }

        return ProductGray;
    }

    private static byte GetBacksideProductPixel(double x, double y)
    {
        if (IsInsideEllipse(x, y, centerX: -5, centerY: -78, radiusX: 6, radiusY: 6) ||
            IsInsideRectangle(x, y, left: 3, top: -59, right: 13, bottom: -48))
        {
            return ProductGray;
        }

        if (IsInsideEllipse(x, y, centerX: 8, centerY: -71, radiusX: 5, radiusY: 5) ||
            IsInsideRectangle(x, y, left: -14, top: -62, right: -4, bottom: -51))
        {
            return 210;
        }

        return GetProductPixel(x, y);
    }

    private static byte GetWrongInternalProductPixel(double x, double y)
    {
        if (IsInsidePolygon(x, y, TriangleMarker) ||
            IsInsideEllipse(x, y, centerX: 8, centerY: 2, radiusX: 8, radiusY: 6))
        {
            return ProductGray;
        }

        if (IsInsideEllipse(x, y, centerX: 10, centerY: -31, radiusX: 6, radiusY: 5) ||
            IsInsideRectangle(x, y, left: -13, top: 15, right: -3, bottom: 25))
        {
            return 210;
        }

        return GetProductPixel(x, y);
    }

    private static byte GetOppositePolarityPixel(double x, double y, bool inside)
    {
        byte source = inside ? GetProductPixel(x, y) : BackgroundGray;
        return (byte)(byte.MaxValue - source);
    }

    private static byte GetPolarityHardGatePixel(double x, double y, bool inside)
    {
        if (y is >= -8 and <= 12)
        {
            if (inside)
            {
                return BackgroundGray;
            }

            if (DistanceToOuterContour(x, y) <= 3.5)
            {
                return 220;
            }
        }

        return inside ? GetProductPixel(x, y) : BackgroundGray;
    }

    private static double DistanceToOuterContour(double x, double y)
    {
        double minimum = double.PositiveInfinity;
        for (var index = 0; index < OuterContour.Length; index++)
        {
            Point2D start = OuterContour[index];
            Point2D end = OuterContour[(index + 1) % OuterContour.Length];
            minimum = Math.Min(minimum, DistanceToSegment(x, y, start, end));
        }

        return minimum;
    }

    private static double DistanceToSegment(
        double x,
        double y,
        Point2D start,
        Point2D end)
    {
        double segmentX = end.X - start.X;
        double segmentY = end.Y - start.Y;
        double lengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(Math.Pow(x - start.X, 2) + Math.Pow(y - start.Y, 2));
        }

        double position = Math.Clamp(
            ((x - start.X) * segmentX + (y - start.Y) * segmentY) / lengthSquared,
            0,
            1);
        double nearestX = start.X + position * segmentX;
        double nearestY = start.Y + position * segmentY;
        return Math.Sqrt(Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2));
    }

    private static RoiDefinition CreateTightSearchRoi()
    {
        return new RoiDefinition
        {
            Id = "synthetic-tight-search",
            Name = "Synthetic tight search boundary",
            Shape = RoiShapeKind.Rectangle,
            X = 205,
            Y = 84,
            Width = 90,
            Height = 214
        };
    }

    private static bool IsInsideRectangle(
        double x,
        double y,
        double left,
        double top,
        double right,
        double bottom)
    {
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private static bool IsInsideEllipse(
        double x,
        double y,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY)
    {
        double normalizedX = (x - centerX) / radiusX;
        double normalizedY = (y - centerY) / radiusY;
        return normalizedX * normalizedX + normalizedY * normalizedY <= 1;
    }

    private static bool IsInsidePolygon(
        double x,
        double y,
        IReadOnlyList<Point2D> polygon)
    {
        var inside = false;
        for (var current = 0; current < polygon.Count; current++)
        {
            Point2D first = polygon[current];
            Point2D second = polygon[(current + 1) % polygon.Count];
            bool crosses = (first.Y > y) != (second.Y > y) &&
                           x < (second.X - first.X) * (y - first.Y) /
                           (second.Y - first.Y) + first.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private enum RenderVariant
    {
        Standard,
        BacksideInternalLayout,
        WrongInternalLayout,
        PartialMiddle,
        SevereOcclusion,
        OppositePolarity,
        PolarityHardGate
    }
}
