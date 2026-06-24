using VisionStation.Domain;
using VisionStation.Domain.Utilities;

namespace VisionStation.Application.Inspection;

internal static class VisionFlowFramePolicy
{
    public static bool RequiresExternalFrame(Recipe recipe)
    {
        var acquireTool = recipe
            .GetActiveFlow()
            .Tools
            .FirstOrDefault(tool => tool.Enabled && tool.Kind == VisionToolKind.AcquireImage);

        if (acquireTool is null)
        {
            return true;
        }

        var source = ParameterParser.GetString(acquireTool.Parameters, "source", "Camera");
        return string.Equals(source, "Camera", StringComparison.OrdinalIgnoreCase);
    }
}
