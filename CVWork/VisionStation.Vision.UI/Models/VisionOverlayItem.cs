using VisionStation.Domain;

namespace VisionStation.Vision.UI.Models;

public enum VisionOverlayKind
{
    Rectangle,
    RotatedRectangle,
    RotatedRectangleOutline,
    Circle,
    CircleAnnulus,
    Polygon,
    Polyline,
    PointCloud,
    XMarker,
    Cross,
    LineSegment,
    Line,
    DirectionAxis
}

public enum VisionOverlayState
{
    Neutral,
    Ok,
    Ng,
    Warning,
    Info
}

public sealed record VisionOverlayItem
{
    public VisionOverlayKind Kind { get; init; }

    public VisionOverlayState State { get; init; } = VisionOverlayState.Neutral;

    public string Label { get; init; } = string.Empty;

    public bool PreserveLabelInResult { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double Angle { get; init; }

    public double Radius { get; init; }

    public double X2 { get; init; }

    public double Y2 { get; init; }

    public IReadOnlyList<Point2D> Points { get; init; } = Array.Empty<Point2D>();

    public static VisionOverlayItem FromRoi(RoiDefinition roi, VisionOverlayState state = VisionOverlayState.Neutral)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Circle,
                State = state,
                Label = roi.Name,
                X = roi.X,
                Y = roi.Y,
                Radius = roi.Radius
            },
            RoiShapeKind.RotatedRectangle => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = state,
                Label = roi.Name,
                X = roi.X,
                Y = roi.Y,
                Width = roi.Width,
                Height = roi.Height,
                Angle = roi.Angle
            },
            RoiShapeKind.Polygon => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polygon,
                State = state,
                Label = roi.Name,
                Points = roi.Points
            },
            _ => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Rectangle,
                State = state,
                Label = roi.Name,
                X = roi.X,
                Y = roi.Y,
                Width = roi.Width,
                Height = roi.Height
            }
        };
    }
}
