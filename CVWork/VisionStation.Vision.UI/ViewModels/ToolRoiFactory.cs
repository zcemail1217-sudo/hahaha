using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

internal static class ToolRoiFactory
{
    public static IReadOnlyList<RoiShapeOptionItem> ShapeOptions { get; } =
    [
        new(RoiShapeKind.Rectangle, "矩形"),
        new(RoiShapeKind.RotatedRectangle, "旋转矩形"),
        new(RoiShapeKind.Circle, "圆形"),
        new(RoiShapeKind.Polygon, "多边形")
    ];

    public static bool RequiresRoi(VisionToolKind kind)
    {
        return kind is VisionToolKind.TemplateLocate
            or VisionToolKind.MultiTargetMatch
            or VisionToolKind.FindLine
            or VisionToolKind.FindCircle
            or VisionToolKind.CodeRead
            or VisionToolKind.Ocr
            or VisionToolKind.DefectDetect;
    }

    public static RoiDefinition CreateDefaultRoi(
        string toolName,
        VisionToolKind toolKind,
        RoiShapeKind shape,
        ImageFrame? frame,
        int index)
    {
        return CreateRoiAt(toolName, toolKind, shape, frame, index, null);
    }

    public static RoiDefinition CreateRoiAt(
        string toolName,
        VisionToolKind toolKind,
        RoiShapeKind shape,
        ImageFrame? frame,
        int index,
        Point2D? center)
    {
        var imageWidth = frame?.Width ?? 1280;
        var imageHeight = frame?.Height ?? 720;
        var roiWidth = Math.Max(80, imageWidth * 0.22);
        var roiHeight = Math.Max(60, imageHeight * 0.20);
        var centerX = Math.Clamp(center?.X ?? imageWidth / 2.0, 0, imageWidth);
        var centerY = Math.Clamp(center?.Y ?? imageHeight / 2.0, 0, imageHeight);
        var rectX = Math.Clamp(centerX - roiWidth / 2.0, 0, Math.Max(0, imageWidth - roiWidth));
        var rectY = Math.Clamp(centerY - roiHeight / 2.0, 0, Math.Max(0, imageHeight - roiHeight));
        var safeName = string.IsNullOrWhiteSpace(toolName) ? toolKind.ToString() : toolName.Trim();
        var isInteractivePlacement = center is not null;

        return new RoiDefinition
        {
            Id = $"roi-{Guid.NewGuid():N}",
            Name = $"{safeName} ROI {index}",
            Shape = shape,
            X = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? centerX : rectX,
            Y = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? centerY : rectY,
            Width = roiWidth,
            Height = roiHeight,
            Radius = Math.Min(roiWidth, roiHeight) / 2.0,
            Angle = shape == RoiShapeKind.RotatedRectangle ? 8 : 0,
            Points = shape == RoiShapeKind.Polygon && !isInteractivePlacement
                ?
                [
                    ClampPoint(centerX, centerY - roiHeight / 2.0, imageWidth, imageHeight),
                    ClampPoint(centerX + roiWidth / 2.0, centerY, imageWidth, imageHeight),
                    ClampPoint(centerX, centerY + roiHeight / 2.0, imageWidth, imageHeight),
                    ClampPoint(centerX - roiWidth / 2.0, centerY, imageWidth, imageHeight)
                ]
                : Array.Empty<Point2D>()
        };
    }

    private static Point2D ClampPoint(double x, double y, int imageWidth, int imageHeight)
    {
        return new Point2D(Math.Clamp(x, 0, imageWidth), Math.Clamp(y, 0, imageHeight));
    }
}
