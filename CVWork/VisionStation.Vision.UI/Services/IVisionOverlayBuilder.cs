using VisionStation.Vision.UI.Models;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Services;

public interface IVisionOverlayBuilder
{
    IReadOnlyList<VisionOverlayItem> Build(
        Recipe recipe,
        ImageFrame frame,
        IReadOnlyList<ToolResult> toolResults,
        InspectionOutcome outcome);
}
