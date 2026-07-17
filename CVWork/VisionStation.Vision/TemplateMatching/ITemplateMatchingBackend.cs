namespace VisionStation.Vision;

internal interface ITemplateMatchingBackend : IAsyncDisposable
{
    TemplateMatchingEngine Engine { get; }

    Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken);

    Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken);
}

public interface ITemplateMatchingService : IAsyncDisposable
{
    Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken);

    Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken);
}
