using System.Collections.Concurrent;
using VisionStation.Domain;

namespace VisionStation.Vision.Tests;

internal sealed class RecordingTemplateMatchingBackend : ITemplateMatchingBackend
{
    public RecordingTemplateMatchingBackend(TemplateMatchingEngine engine)
    {
        Engine = engine;
    }

    public TemplateMatchingEngine Engine { get; }

    public ConcurrentQueue<TemplateLearningRequest> LearningRequests { get; } = new();

    public ConcurrentQueue<TemplateMatchingRequest> MatchRequests { get; } = new();

    public Func<TemplateLearningRequest, CancellationToken, Task<TemplateLearningResult>>? LearnHandler { get; set; }

    public Func<TemplateMatchingRequest, CancellationToken, Task<TemplateMatchBatchResult>>? MatchHandler { get; set; }

    public Func<ValueTask>? DisposeHandler { get; set; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    private int _disposeCount;

    public Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        LearningRequests.Enqueue(request);
        if (LearnHandler is not null)
        {
            return LearnHandler(request, cancellationToken);
        }

        return Task.FromResult(new TemplateLearningResult(
            Engine,
            true,
            new Dictionary<string, string> { ["learned"] = "true" },
            "Learned.",
            null)
        {
            Geometry = new TemplateLearnedGeometry(new Pose2D(8, 8, 0), 12, 12)
        });
    }

    public Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken)
    {
        MatchRequests.Enqueue(request);
        if (MatchHandler is not null)
        {
            return MatchHandler(request, cancellationToken);
        }

        return Task.FromResult(TemplateMatchingTestResults.NoMatch(Engine));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return DisposeHandler?.Invoke() ?? ValueTask.CompletedTask;
    }
}

internal static class TemplateMatchingTestResults
{
    public static TemplateMatchBatchResult NoMatch(
        TemplateMatchingEngine engine,
        TemplateMatchingDiagnostic? diagnostic = null)
    {
        return new TemplateMatchBatchResult(
            engine,
            InspectionOutcome.Ng,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            new TemplateSearchRegion(0, 0, 32, 32),
            diagnostic?.UserMessage ?? "No match.",
            false,
            diagnostic);
    }

    public static TemplateMatchBatchResult Match(
        TemplateMatchingEngine engine,
        TemplateMatchBatchCandidate candidate,
        InspectionOutcome outcome = InspectionOutcome.Ok,
        TemplateMatchingDiagnostic? diagnostic = null)
    {
        return new TemplateMatchBatchResult(
            engine,
            outcome,
            true,
            [candidate],
            new TemplateSearchRegion(0, 0, 256, 256),
            diagnostic?.UserMessage ?? "Match.",
            false,
            diagnostic);
    }
}
