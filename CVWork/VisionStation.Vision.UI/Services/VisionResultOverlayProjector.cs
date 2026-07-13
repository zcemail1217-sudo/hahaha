using VisionStation.Vision.UI.Models;

namespace VisionStation.Vision.UI.Services;

public static class VisionResultOverlayProjector
{
    public static IReadOnlyList<VisionOverlayItem> Project(IEnumerable<VisionOverlayItem> overlays)
    {
        return overlays
            .Where(overlay => overlay.Kind != VisionOverlayKind.DirectionAxis)
            .Where(overlay => overlay.State != VisionOverlayState.Neutral)
            .Select(overlay => overlay.PreserveLabelInResult
                ? overlay
                : overlay with { Label = string.Empty })
            .ToArray();
    }
}
