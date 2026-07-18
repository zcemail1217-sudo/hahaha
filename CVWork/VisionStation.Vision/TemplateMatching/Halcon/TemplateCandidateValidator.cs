using System.Collections.ObjectModel;
using System.Globalization;

namespace VisionStation.Vision;

internal sealed class TemplateCandidateDecision
{
    public TemplateCandidateDecision(
        TemplateCandidateEvidence evidence,
        bool accepted,
        TemplateMatchingDiagnostic? diagnostic)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Accepted = accepted;
        Diagnostic = diagnostic;
    }

    public TemplateCandidateEvidence Evidence { get; }

    public TemplateCandidate Candidate => Evidence.Candidate;

    public bool Accepted { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }
}

internal sealed class TemplateCandidateValidationResult
{
    public TemplateCandidateValidationResult(
        IReadOnlyList<TemplateCandidateEvidence> accepted,
        IReadOnlyList<TemplateCandidateDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(decisions);
        Accepted = new ReadOnlyCollection<TemplateCandidateEvidence>(accepted.ToArray());
        Decisions = new ReadOnlyCollection<TemplateCandidateDecision>(decisions.ToArray());
    }

    public IReadOnlyList<TemplateCandidateEvidence> Accepted { get; }

    public IReadOnlyList<TemplateCandidateDecision> Decisions { get; }
}

internal sealed class TemplateCandidateValidator
{
    public TemplateCandidateValidationResult ValidateAndDeduplicate(
        IReadOnlyList<TemplateCandidateEvidence> evidence,
        HalconTemplateModelMetadata metadata,
        HalconTemplateMatchingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(parameters);
        var decisions = new TemplateCandidateDecision?[evidence.Count];
        var eligible = new List<IndexedEvidence>(evidence.Count);
        for (var index = 0; index < evidence.Count; index++)
        {
            TemplateCandidateEvidence current = evidence[index] ?? throw new ArgumentException(
                "Evidence collections cannot contain null entries.",
                nameof(evidence));
            TemplateMatchingDiagnostic? failure = ValidateHardGates(current, metadata, parameters);
            if (failure is not null)
            {
                decisions[index] = new TemplateCandidateDecision(current, accepted: false, failure);
            }
            else
            {
                eligible.Add(new IndexedEvidence(index, current));
            }
        }

        IndexedEvidence[] ordered = eligible
            .OrderByDescending(item => item.Evidence.Candidate.Score)
            .ThenByDescending(item => item.Evidence.OuterCoverage)
            .ThenBy(item => item.Evidence.Candidate.Pose.X)
            .ThenBy(item => item.Evidence.Candidate.Pose.Y)
            .ThenBy(item => item.Evidence.Candidate.SourceIndex)
            .ThenBy(item => item.InputIndex)
            .ToArray();
        var accepted = new List<TemplateCandidateEvidence>(ordered.Length);
        foreach (IndexedEvidence item in ordered)
        {
            TemplateCandidateEvidence? conflictingAccepted = null;
            double conflictingIoU = 0;
            foreach (TemplateCandidateEvidence existing in accepted)
            {
                double iou = FilledSupportMask.ComputeIoU(
                    existing.SupportMask,
                    item.Evidence.SupportMask);
                if (iou <= parameters.MaxOverlap)
                {
                    continue;
                }

                conflictingAccepted = existing;
                conflictingIoU = iou;
                break;
            }

            if (conflictingAccepted is not null)
            {
                decisions[item.InputIndex] = new TemplateCandidateDecision(
                    item.Evidence,
                    accepted: false,
                    CreateFailure(
                        TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap,
                        item.Evidence.Candidate.SourceIndex,
                        $"iou={conflictingIoU:R}",
                        $"MaxOverlap={parameters.MaxOverlap:R}",
                        conflictingAccepted.Candidate.SourceIndex));
                continue;
            }

            accepted.Add(item.Evidence);
            decisions[item.InputIndex] = new TemplateCandidateDecision(
                item.Evidence,
                accepted: true,
                diagnostic: null);
        }

        TemplateCandidateDecision[] completeDecisions = decisions
            .Select(decision => decision ?? throw new InvalidOperationException(
                "Every candidate must receive a validation decision."))
            .ToArray();
        return new TemplateCandidateValidationResult(accepted, completeDecisions);
    }

    private static TemplateMatchingDiagnostic? ValidateHardGates(
        TemplateCandidateEvidence evidence,
        HalconTemplateModelMetadata metadata,
        HalconTemplateMatchingParameters parameters)
    {
        TemplateCandidate candidate = evidence.Candidate;
        if (!evidence.GeometryUsable ||
            !double.IsFinite(candidate.Pose.X) ||
            !double.IsFinite(candidate.Pose.Y) ||
            !double.IsFinite(candidate.Pose.Angle) ||
            !double.IsFinite(candidate.Pose.Scale) ||
            candidate.Pose.Scale <= 0 ||
            !double.IsFinite(candidate.Score) ||
            candidate.Pose.Scale < metadata.GenerationParameters.ScaleMin ||
            candidate.Pose.Scale > metadata.GenerationParameters.ScaleMax ||
            !evidence.OriginInsideSearchDomain)
        {
            return CreateFailure(
                TemplateMatchingDiagnosticCodes.MatchInvalidPose,
                candidate.SourceIndex,
                $"geometryUsable={evidence.GeometryUsable}, poseX={candidate.Pose.X:R}, poseY={candidate.Pose.Y:R}, poseAngle={candidate.Pose.Angle:R}, poseScale={candidate.Pose.Scale:R}, score={candidate.Score:R}, originInsideSearchDomain={evidence.OriginInsideSearchDomain}",
                $"finiteGeometry=true, positiveScale=true, scaleRange=[{metadata.GenerationParameters.ScaleMin:R},{metadata.GenerationParameters.ScaleMax:R}], originInsideSearchDomain=true");
        }

        if (!evidence.CompleteAtBoundary)
        {
            return CreateFailure(
                TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary,
                candidate.SourceIndex,
                $"completeAtBoundary={evidence.CompleteAtBoundary}",
                $"completeAtBoundary=true");
        }

        if (!IsUnitInterval(evidence.PolarityAgreement) ||
            evidence.PolarityAgreement < parameters.PolarityAgreementMin)
        {
            return CreateFailure(
                TemplateMatchingDiagnosticCodes.MatchPolarityMismatch,
                candidate.SourceIndex,
                $"polarityAgreement={evidence.PolarityAgreement:R}",
                $"range=[0,1], polarityAgreementMin={parameters.PolarityAgreementMin:R}");
        }

        if (!IsUnitInterval(evidence.OuterCoverage) ||
            evidence.OuterCoverage < parameters.OuterCoverageMin ||
            !double.IsFinite(evidence.EdgeDistanceP95Px) ||
            evidence.EdgeDistanceP95Px < 0 ||
            evidence.EdgeDistanceP95Px > parameters.EdgeTolerancePx)
        {
            return CreateFailure(
                TemplateMatchingDiagnosticCodes.MatchOuterContourWeak,
                candidate.SourceIndex,
                $"outerCoverage={evidence.OuterCoverage:R}, edgeDistanceP95Px={evidence.EdgeDistanceP95Px:R}",
                $"outerCoverageRange=[0,1], outerCoverageMin={parameters.OuterCoverageMin:R}, edgeDistanceP95PxRange=[0,{parameters.EdgeTolerancePx:R}]");
        }

        if (!IsUnitInterval(evidence.InnerCoverage) ||
            evidence.InnerCoverage < parameters.InnerCoverageMin ||
            evidence.ValidInnerGroupCount < metadata.MinimumValidInnerGroupCount)
        {
            return CreateFailure(
                TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak,
                candidate.SourceIndex,
                $"innerCoverage={evidence.InnerCoverage:R}, validInnerGroupCount={evidence.ValidInnerGroupCount}",
                $"innerCoverageRange=[0,1], innerCoverageMin={parameters.InnerCoverageMin:R}, minimumValidInnerGroupCount={metadata.MinimumValidInnerGroupCount}");
        }

        return null;
    }

    private static TemplateMatchingDiagnostic CreateFailure(
        string code,
        int sourceIndex,
        FormattableString measured,
        FormattableString threshold,
        int? conflictingAcceptedSourceIndex = null)
    {
        string conflict = conflictingAcceptedSourceIndex.HasValue
            ? $"; conflictingAcceptedSourceIndex={conflictingAcceptedSourceIndex.Value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        string details =
            $"sourceIndex={sourceIndex.ToString(CultureInfo.InvariantCulture)}{conflict}; " +
            $"measured={measured.ToString(CultureInfo.InvariantCulture)}; " +
            $"threshold={threshold.ToString(CultureInfo.InvariantCulture)}";
        return TemplateMatchingDiagnostics.Create(code, details);
    }

    private static bool IsUnitInterval(double value)
    {
        return double.IsFinite(value) && value >= 0 && value <= 1;
    }

    private readonly record struct IndexedEvidence(
        int InputIndex,
        TemplateCandidateEvidence Evidence);
}
