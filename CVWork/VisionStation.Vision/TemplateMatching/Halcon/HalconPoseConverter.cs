using VisionStation.Domain;

namespace VisionStation.Vision;

internal static class HalconPoseConverter
{
    public static Pose2D ToPose(
        HalconNativeCandidate candidate,
        TemplateSearchRegion searchRegion)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(searchRegion);

        double angle = candidate.AngleRadians;
        double scale = candidate.Scale;
        // HALCON transforms use a coordinate system whose pixel origin lies at a pixel corner.
        // Correct the reported model origin to the pixel-center convention before adding the
        // one and only crop-to-image offset.
        double cosine = Math.Cos(angle);
        double sine = Math.Sin(angle);
        double correctedRow = candidate.Row +
                              0.5 * (scale * (cosine - sine) - 1);
        double correctedColumn = candidate.Column +
                                 0.5 * (scale * (sine + cosine) - 1);
        double uiAngle = NormalizeDegrees(-angle * 180d / Math.PI);
        return new Pose2D(
            searchRegion.X + correctedColumn,
            searchRegion.Y + correctedRow,
            uiAngle)
        {
            Scale = scale
        };
    }

    internal static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            return degrees;
        }

        double normalized = (degrees + 180d) % 360d;
        if (normalized < 0)
        {
            normalized += 360d;
        }

        normalized -= 180d;
        return normalized == 0 ? 0 : normalized;
    }
}
