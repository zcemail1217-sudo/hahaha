using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class CodeReadTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.CodeRead;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        var code = $"VS-{DateTimeOffset.Now:HHmmss}-{Random.Shared.Next(100, 999)}";
        stopwatch.Stop();

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"[模拟数据] 读码成功 {code}",
            Data = new Dictionary<string, string>
            {
                ["code"] = code,
                ["symbology"] = "DataMatrix/模拟",
                ["inputFrameId"] = frame.Id,
                ["simulated"] = "true"
            }
        });
    }
}
