using VisionStation.Domain;

namespace VisionStation.Vision;

public static class TemplateMatchResultProjector
{
    public static TemplateMatchResult ToSingle(TemplateMatchBatchResult batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateBatch(batch);
        if (batch.Matches.Count > 1)
        {
            throw new InvalidOperationException("Single template projection received multiple candidates.");
        }

        var candidate = batch.Matches.FirstOrDefault();
        var pose = candidate?.Pose ?? new Pose2D(
            batch.SearchRegion.X + batch.SearchRegion.Width / 2d,
            batch.SearchRegion.Y + batch.SearchRegion.Height / 2d,
            0);
        var matchX = 0;
        var matchY = 0;
        if (candidate is not null)
        {
            (matchX, matchY) = GetAxisAlignedOrigin(candidate.TemplateRoiContours);
        }

        IReadOnlyList<Point2D>? shapePoints = candidate is null || candidate.ShapeContours.Count == 0
            ? null
            : candidate.ShapeContours.SelectMany(contour => contour).ToArray();
        return new TemplateMatchResult(
            batch.HasMatch,
            batch.Outcome,
            candidate?.Score ?? 0,
            pose,
            matchX,
            matchY,
            candidate?.TemplateWidth ?? 0,
            candidate?.TemplateHeight ?? 0,
            batch.SearchRegion,
            batch.Message,
            batch.UsedAutoTemplate,
            shapePoints,
            candidate?.ShapeContours)
        {
            MatchedTemplateRoiContours = candidate?.TemplateRoiContours,
            ShapeCoverage = candidate?.ShapeCoverage,
            ShapeReverseScore = candidate?.ShapeReverseScore,
            OuterCoverage = candidate?.OuterCoverage ?? 0,
            InnerCoverage = candidate?.InnerCoverage ?? 0,
            EdgeDistanceP95Px = candidate?.EdgeDistanceP95Px ?? 0,
            PolarityAgreement = candidate?.PolarityAgreement ?? 0,
            Engine = batch.Engine,
            FailureCode = batch.Diagnostic?.Code,
            FailureStage = batch.Diagnostic?.FailureStage,
            TechnicalDetails = batch.Diagnostic?.TechnicalDetails,
            Diagnostic = batch.Diagnostic
        };
    }

    public static MultiTargetMatchResult ToMulti(TemplateMatchBatchResult batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateBatch(batch);
        var matches = batch.Matches
            .Select(source => new MultiTargetMatchCandidate(
                source.Pose.X,
                source.Pose.Y,
                source.Pose.Angle,
                source.Score,
                source.TemplateWidth,
                source.TemplateHeight,
                source.Shape,
                source.Radius)
            {
                Scale = source.Pose.Scale,
                OuterCoverage = source.OuterCoverage,
                InnerCoverage = source.InnerCoverage,
                EdgeDistanceP95Px = source.EdgeDistanceP95Px,
                PolarityAgreement = source.PolarityAgreement
            })
            .ToArray();
        return new MultiTargetMatchResult(
            batch.Outcome,
            batch.Message,
            matches,
            batch.SearchRegion,
            batch.UsedAutoTemplate)
        {
            Engine = batch.Engine,
            FailureCode = batch.Diagnostic?.Code,
            FailureStage = batch.Diagnostic?.FailureStage,
            TechnicalDetails = batch.Diagnostic?.TechnicalDetails,
            Diagnostic = batch.Diagnostic
        };
    }

    internal static void ValidateCandidate(TemplateMatchBatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!double.IsFinite(candidate.Pose.X) ||
            !double.IsFinite(candidate.Pose.Y) ||
            !double.IsFinite(candidate.Pose.Angle) ||
            !double.IsFinite(candidate.Pose.Scale) ||
            candidate.Pose.Scale <= 0 ||
            !double.IsFinite(candidate.Score) ||
            candidate.TemplateWidth <= 0 ||
            candidate.TemplateHeight <= 0)
        {
            throw new InvalidOperationException("Template matching backend returned invalid candidate geometry.");
        }

        if (candidate.TemplateRoiContours.Count == 0 ||
            candidate.TemplateRoiContours.Any(contour =>
                contour.Count < 3 ||
                contour.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))))
        {
            throw new InvalidOperationException(
                "Template matching backend returned incomplete or non-finite TemplateRoiContours.");
        }

        if (!IsUnitInterval(candidate.OuterCoverage) ||
            !IsUnitInterval(candidate.InnerCoverage) ||
            !double.IsFinite(candidate.EdgeDistanceP95Px) ||
            candidate.EdgeDistanceP95Px < 0 ||
            !IsUnitInterval(candidate.PolarityAgreement))
        {
            throw new InvalidOperationException(
                "Template matching backend returned invalid validation metrics.");
        }
    }

    private static bool IsUnitInterval(double value)
    {
        return double.IsFinite(value) && value is >= 0 and <= 1;
    }

    private static void ValidateBatch(TemplateMatchBatchResult batch)
    {
        if (batch.HasMatch && batch.Matches.Count == 0)
        {
            throw new InvalidOperationException("Template matching backend reported HasMatch without a candidate.");
        }

        if (batch.Outcome == InspectionOutcome.Ok && batch.Matches.Count == 0)
        {
            throw new InvalidOperationException("Template matching backend reported an OK outcome without a candidate.");
        }

        if (batch.Outcome == InspectionOutcome.Ok && !batch.HasMatch)
        {
            throw new InvalidOperationException("Template matching backend reported an OK outcome without HasMatch.");
        }

        foreach (var candidate in batch.Matches)
        {
            ValidateCandidate(candidate);
        }
    }

    private static (int X, int Y) GetAxisAlignedOrigin(
        IReadOnlyList<IReadOnlyList<Point2D>> contours)
    {
        var minX = contours.SelectMany(contour => contour).Min(point => point.X);
        var minY = contours.SelectMany(contour => contour).Min(point => point.Y);
        var floorX = Math.Floor(minX);
        var floorY = Math.Floor(minY);
        if (floorX < int.MinValue || floorX > int.MaxValue || floorY < int.MinValue || floorY > int.MaxValue)
        {
            throw new InvalidOperationException("TemplateRoiContours axis-aligned bounds exceed Int32 range.");
        }

        return ((int)floorX, (int)floorY);
    }
}
