using VisionStation.Domain;

namespace VisionStation.Vision.UI.Services;

public sealed record ToolParameterDialogResult(
    bool Accepted,
    ImageFrame? OutputFrame = null,
    bool RunFlowRequested = false,
    IReadOnlyList<RoiDefinition>? CreatedRois = null,
    IReadOnlyList<string>? RemovedRoiIds = null)
{
    public static ToolParameterDialogResult Cancelled { get; } = new(false);
}
