using VisionStation.Domain;

namespace VisionStation.Vision;

public static class PoseSimilarityTransform
{
    public static bool IsValidScale(double scale)
    {
        return double.IsFinite(scale) && scale > 0;
    }

    public static Point2D MapPoint(Point2D point, Pose2D referencePose, Pose2D currentPose)
    {
        var scaleRatio = GetScaleRatio(referencePose, currentPose);
        var dx = (point.X - referencePose.X) * scaleRatio;
        var dy = (point.Y - referencePose.Y) * scaleRatio;
        var radians = (currentPose.Angle - referencePose.Angle) * Math.PI / 180.0;

        return new Point2D(
            currentPose.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            currentPose.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    public static RoiDefinition MapRoi(RoiDefinition roi, Pose2D referencePose, Pose2D currentPose)
    {
        var scaleRatio = GetScaleRatio(referencePose, currentPose);
        var angle = currentPose.Angle - referencePose.Angle;
        return roi.Shape switch
        {
            RoiShapeKind.Circle => roi with
            {
                X = MapPoint(new Point2D(roi.X, roi.Y), referencePose, currentPose).X,
                Y = MapPoint(new Point2D(roi.X, roi.Y), referencePose, currentPose).Y,
                Radius = roi.Radius * scaleRatio
            },
            RoiShapeKind.RotatedRectangle => MapRotatedRectangle(roi, referencePose, currentPose, scaleRatio, angle),
            RoiShapeKind.Polygon => roi with
            {
                Points = roi.Points.Select(point => MapPoint(point, referencePose, currentPose)).ToArray()
            },
            _ => MapRectangle(roi, referencePose, currentPose, scaleRatio, angle)
        };
    }

    private static RoiDefinition MapRectangle(
        RoiDefinition roi,
        Pose2D referencePose,
        Pose2D currentPose,
        double scaleRatio,
        double angle)
    {
        var center = MapPoint(
            new Point2D(roi.X + roi.Width / 2.0, roi.Y + roi.Height / 2.0),
            referencePose,
            currentPose);
        return roi with
        {
            Shape = RoiShapeKind.RotatedRectangle,
            X = center.X,
            Y = center.Y,
            Width = roi.Width * scaleRatio,
            Height = roi.Height * scaleRatio,
            Angle = angle
        };
    }

    private static RoiDefinition MapRotatedRectangle(
        RoiDefinition roi,
        Pose2D referencePose,
        Pose2D currentPose,
        double scaleRatio,
        double angle)
    {
        var center = MapPoint(new Point2D(roi.X, roi.Y), referencePose, currentPose);
        return roi with
        {
            X = center.X,
            Y = center.Y,
            Width = roi.Width * scaleRatio,
            Height = roi.Height * scaleRatio,
            Angle = roi.Angle + angle
        };
    }

    private static double GetScaleRatio(Pose2D referencePose, Pose2D currentPose)
    {
        if (!IsValidScale(referencePose.Scale))
        {
            throw new ArgumentOutOfRangeException(nameof(referencePose), "Reference pose scale must be finite and greater than zero.");
        }

        if (!IsValidScale(currentPose.Scale))
        {
            throw new ArgumentOutOfRangeException(nameof(currentPose), "Current pose scale must be finite and greater than zero.");
        }

        return currentPose.Scale / referencePose.Scale;
    }
}
