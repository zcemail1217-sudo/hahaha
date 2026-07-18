using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class HalconCandidateBatch
{
    public HalconCandidateBatch(
        IReadOnlyList<TemplateCandidate> candidates,
        bool limitReached,
        TemplateSearchRegion searchRegion)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(searchRegion);
        if (candidates.Any(candidate => candidate is null))
        {
            throw new ArgumentException("HALCON candidate collections cannot contain null.", nameof(candidates));
        }

        Candidates = Array.AsReadOnly(candidates.ToArray());
        LimitReached = limitReached;
        SearchRegion = searchRegion;
    }

    public IReadOnlyList<TemplateCandidate> Candidates { get; }

    public bool LimitReached { get; }

    public TemplateSearchRegion SearchRegion { get; }
}

internal interface IHalconCandidateSource
{
    Task<HalconCandidateBatch> FindAsync(
        IHalconModelOperation modelOperation,
        ImageFrame frame,
        RoiDefinition? searchRoi,
        HalconTemplateMatchingParameters parameters,
        CancellationToken cancellationToken);
}
