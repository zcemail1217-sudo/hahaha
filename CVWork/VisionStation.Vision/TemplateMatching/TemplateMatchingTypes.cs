using VisionStation.Domain;

namespace VisionStation.Vision;

public enum TemplateMatchingEngine
{
    Unknown,
    ManagedNcc,
    OpenCv,
    Halcon
}

public enum TemplateMatchCardinality
{
    Single,
    ExactCount
}

public enum TemplateMatchingPreset
{
    Strict,
    Balanced,
    HighRecall
}

public sealed record TemplateModelOwner(string RecipeId, string FlowId, string ToolId);

public sealed record TemplateLearnedGeometry(
    Pose2D StandardPose,
    int TemplateWidth,
    int TemplateHeight);

public sealed record HalconTemplateMatchingParameters(
    double AngleStartDeg,
    double AngleExtentDeg,
    double ScaleMin,
    double ScaleMax,
    double CandidateMinScore,
    double OuterCoverageMin,
    double InnerCoverageMin,
    double EdgeTolerancePx,
    double PolarityAgreementMin,
    double CandidateMaxOverlap,
    double MaxOverlap,
    double Greediness,
    string SubPixel,
    int NumLevels,
    int CandidateLimit,
    int OperatorTimeoutMs,
    int ExpectedCount);
