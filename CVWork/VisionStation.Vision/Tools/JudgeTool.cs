using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class JudgeTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.Judge;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = context.GetConnectedResults(definition);
        var failures = results
            .Where(result => result.Outcome is InspectionOutcome.Ng or InspectionOutcome.Error)
            .ToArray();
        var outcome = failures.Length == 0 ? InspectionOutcome.Ok : InspectionOutcome.Ng;
        stopwatch.Stop();

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = outcome,
            Duration = stopwatch.Elapsed,
            Message = outcome == InspectionOutcome.Ok ? "综合判定 OK" : $"综合判定 NG：{failures.Length} 项异常",
            Data = new Dictionary<string, string>
            {
                ["failedCount"] = failures.Length.ToString(),
                ["totalCount"] = results.Count.ToString()
            }
        });
    }
}
