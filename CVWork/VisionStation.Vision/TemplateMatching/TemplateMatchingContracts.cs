using System.Collections.ObjectModel;
using VisionStation.Domain;

namespace VisionStation.Vision;

public sealed record TemplateLearningRequest
{
    public TemplateLearningRequest(
        TemplateModelOwner Owner,
        ImageFrame Frame,
        RoiDefinition TemplateRoi,
        RoiDefinition? SearchRoi,
        IReadOnlyDictionary<string, string> Parameters)
    {
        this.Owner = Owner ?? throw new ArgumentNullException(nameof(Owner));
        this.Frame = Frame ?? throw new ArgumentNullException(nameof(Frame));
        this.TemplateRoi = TemplateMatchingSnapshots.Roi(
            TemplateRoi ?? throw new ArgumentNullException(nameof(TemplateRoi)));
        this.SearchRoi = SearchRoi is null ? null : TemplateMatchingSnapshots.Roi(SearchRoi);
        this.Parameters = TemplateMatchingSnapshots.Parameters(Parameters);
    }

    public TemplateModelOwner Owner { get; }

    /// <summary>
    /// The pixel buffer is borrowed for the request lifetime and must not be mutated until the operation completes.
    /// </summary>
    public ImageFrame Frame { get; }

    public RoiDefinition TemplateRoi { get; }

    public RoiDefinition? SearchRoi { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }
}

public sealed record TemplateMatchingRequest
{
    public TemplateMatchingRequest(
        TemplateModelOwner Owner,
        ImageFrame Frame,
        RoiDefinition? SearchRoi,
        IReadOnlyDictionary<string, string> Parameters,
        TemplateMatchCardinality Cardinality,
        int ExpectedCount)
    {
        this.Owner = Owner ?? throw new ArgumentNullException(nameof(Owner));
        this.Frame = Frame ?? throw new ArgumentNullException(nameof(Frame));
        this.SearchRoi = SearchRoi is null ? null : TemplateMatchingSnapshots.Roi(SearchRoi);
        this.Parameters = TemplateMatchingSnapshots.Parameters(Parameters);
        this.Cardinality = Cardinality is TemplateMatchCardinality.Single or TemplateMatchCardinality.ExactCount
            ? Cardinality
            : throw new ArgumentOutOfRangeException(nameof(Cardinality), Cardinality, "Unsupported match cardinality.");
        this.ExpectedCount = ExpectedCount is >= 1 and <= 100
            ? ExpectedCount
            : throw new ArgumentOutOfRangeException(
                nameof(ExpectedCount),
                ExpectedCount,
                "ExpectedCount must be between 1 and 100.");
    }

    public TemplateModelOwner Owner { get; }

    /// <summary>
    /// The pixel buffer is borrowed for the request lifetime and must not be mutated until the operation completes.
    /// </summary>
    public ImageFrame Frame { get; }

    public RoiDefinition? SearchRoi { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public TemplateMatchCardinality Cardinality { get; }

    public int ExpectedCount { get; }
}

public sealed record TemplateLearningResult
{
    public TemplateLearningResult(
        TemplateMatchingEngine Engine,
        bool Success,
        IReadOnlyDictionary<string, string> Parameters,
        string Message,
        TemplateMatchingDiagnostic? Diagnostic)
    {
        this.Engine = Engine;
        this.Success = Success;
        this.Parameters = TemplateMatchingSnapshots.Parameters(Parameters);
        this.Message = Message ?? throw new ArgumentNullException(nameof(Message));
        this.Diagnostic = Diagnostic;
    }

    public bool Success { get; init; }

    public TemplateMatchingEngine Engine { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public string Message { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }

    public TemplateLearnedGeometry? Geometry { get; init; }

    /// <summary>
    /// Neutral, managed-only geometry for rendering the just-learned template. It is not a
    /// runtime model input and is intentionally not persisted into tool parameters.
    /// </summary>
    public TemplateLearningPreview? Preview { get; init; }
}

public sealed record TemplateLearningPreview
{
    public TemplateLearningPreview(
        Point2D origin,
        IReadOnlyList<Point2D> outerContour,
        IReadOnlyList<IReadOnlyList<Point2D>> innerFeatureGroups)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
        {
            throw new ArgumentException("The learning-preview origin must be finite.", nameof(origin));
        }

        Origin = new Point2D(origin.X, origin.Y);
        OuterContour = TemplateMatchingSnapshots.Points(outerContour, nameof(outerContour));
        InnerFeatureGroups = TemplateMatchingSnapshots.Contours(innerFeatureGroups);
    }

    /// <summary>
    /// Full-image template reference point. Contour points are relative to this origin.
    /// </summary>
    public Point2D Origin { get; }

    public IReadOnlyList<Point2D> OuterContour { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> InnerFeatureGroups { get; }
}

public sealed record TemplateMatchBatchCandidate
{
    public TemplateMatchBatchCandidate(
        Pose2D Pose,
        double Score,
        int TemplateWidth,
        int TemplateHeight,
        IReadOnlyList<IReadOnlyList<Point2D>> ShapeContours,
        IReadOnlyList<IReadOnlyList<Point2D>> TemplateRoiContours)
    {
        this.Pose = Pose ?? throw new ArgumentNullException(nameof(Pose));
        this.Score = Score;
        this.TemplateWidth = TemplateWidth;
        this.TemplateHeight = TemplateHeight;
        this.ShapeContours = TemplateMatchingSnapshots.Contours(ShapeContours);
        this.TemplateRoiContours = TemplateMatchingSnapshots.Contours(TemplateRoiContours);
    }

    public Pose2D Pose { get; }

    public double Score { get; }

    public int TemplateWidth { get; }

    public int TemplateHeight { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> ShapeContours { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> TemplateRoiContours { get; }

    public double OuterCoverage { get; init; }

    public double InnerCoverage { get; init; }

    public double EdgeDistanceP95Px { get; init; }

    public double PolarityAgreement { get; init; }

    public double? ShapeCoverage { get; init; }

    public double? ShapeReverseScore { get; init; }

    public string Shape { get; init; } = "Rectangle";

    public double Radius { get; init; }
}

public sealed record TemplateMatchBatchResult
{
    public TemplateMatchBatchResult(
        TemplateMatchingEngine Engine,
        InspectionOutcome Outcome,
        bool HasMatch,
        IReadOnlyList<TemplateMatchBatchCandidate> Matches,
        TemplateSearchRegion SearchRegion,
        string Message,
        bool UsedAutoTemplate,
        TemplateMatchingDiagnostic? Diagnostic = null)
    {
        this.Engine = Engine;
        this.Outcome = Outcome;
        this.HasMatch = HasMatch;
        this.Matches = new ReadOnlyCollection<TemplateMatchBatchCandidate>(
            (Matches ?? throw new ArgumentNullException(nameof(Matches))).ToArray());
        this.SearchRegion = SearchRegion ?? throw new ArgumentNullException(nameof(SearchRegion));
        this.Message = Message ?? throw new ArgumentNullException(nameof(Message));
        this.UsedAutoTemplate = UsedAutoTemplate;
        this.Diagnostic = Diagnostic;
    }

    public TemplateMatchingEngine Engine { get; init; }

    public InspectionOutcome Outcome { get; }

    public bool HasMatch { get; }

    public IReadOnlyList<TemplateMatchBatchCandidate> Matches { get; }

    public TemplateSearchRegion SearchRegion { get; }

    public string Message { get; }

    public bool UsedAutoTemplate { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }
}

internal static class TemplateMatchingSnapshots
{
    public static IReadOnlyList<Point2D> Points(
        IReadOnlyList<Point2D> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var copy = source.ToArray();
        foreach (Point2D point in copy)
        {
            ArgumentNullException.ThrowIfNull(point);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentException("Point coordinates must be finite.", parameterName);
            }
        }

        return new ReadOnlyCollection<Point2D>(copy);
    }

    public static IReadOnlyDictionary<string, string> Parameters(
        IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (pair.Key is null || pair.Value is null)
            {
                throw new ArgumentException(
                    "Template matching parameter keys and values cannot be null.",
                    nameof(source));
            }

            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"Template matching parameters contain an ambiguous duplicate key '{pair.Key}'.",
                    nameof(source));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static IReadOnlyList<IReadOnlyList<Point2D>> Contours(
        IReadOnlyList<IReadOnlyList<Point2D>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new IReadOnlyList<Point2D>[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var contour = source[index] ?? throw new ArgumentException(
                "Contour collection cannot contain null entries.",
                nameof(source));
            copy[index] = new ReadOnlyCollection<Point2D>(contour.ToArray());
        }

        return new ReadOnlyCollection<IReadOnlyList<Point2D>>(copy);
    }

    public static RoiDefinition Roi(RoiDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Points is null)
        {
            throw new ArgumentException("ROI point collection cannot be null.", nameof(source));
        }

        return source with
        {
            Points = new ReadOnlyCollection<Point2D>(source.Points.ToArray())
        };
    }
}
