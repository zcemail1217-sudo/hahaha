using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class RoiMapTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.RoiMap;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var poseText = context.TryGetPortInput<Pose2D>(definition, "PositionInput", out var connectedPose)
            ? connectedPose.ToString()
            : context.Properties.TryGetValue("pose", out var pose) ? pose.ToString() : "未定位";

        context.Properties["mappedRoiCount"] = context.Recipe.Rois.Count;
        context.SetPortOutput(definition, "RoiOutput", context.Recipe.Rois);
        stopwatch.Stop();

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = context.Recipe.Rois.Count > 0 ? InspectionOutcome.Ok : InspectionOutcome.Ng,
            Duration = stopwatch.Elapsed,
            Message = $"ROI 映射完成，数量 {context.Recipe.Rois.Count}",
            Data = new Dictionary<string, string>
            {
                ["roiCount"] = context.Recipe.Rois.Count.ToString(),
                ["pose"] = poseText ?? string.Empty
            }
        });
    }
}
