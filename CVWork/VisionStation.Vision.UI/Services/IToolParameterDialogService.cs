using VisionStation.Vision.UI.ViewModels;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Services;

public interface IToolParameterDialogService
{
    ToolParameterDialogResult EditTool(
        VisionToolItem tool,
        IReadOnlyList<RoiChoiceItem> roiChoices,
        IReadOnlyList<RoiDefinition> rois,
        IReadOnlyList<VisionToolKind> toolKinds,
        string flowName,
        Action<ImageFrame>? imageUpdated = null,
        ImageFrame? currentFrame = null,
        Recipe? previewRecipe = null);
}
