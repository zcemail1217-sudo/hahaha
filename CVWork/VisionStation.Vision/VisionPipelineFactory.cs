using VisionStation.Domain;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision;

public static class VisionPipelineFactory
{
    public static IVisionPipeline CreateDefault(
        IDeviceConfigurationRepository deviceConfigurationRepository,
        ICommunicationChannelRuntime? communicationChannels = null)
    {
        return new ConfiguredVisionPipeline(
        [
            new AcquireImageTool(),
            new ImageProcessTool(),
            new TemplateLocateTool(),
            new MultiTargetMatchTool(),
            new CoordinateTransformTool(),
            new RoiMapTool(),
            new FindLineTool(),
            new FindCircleTool(),
            new MeasureDistanceTool(),
            new LineAngleTool(),
            new LineIntersectionTool(),
            new FitLineFromPointsTool(),
            new TemplatePointTool(),
            new CodeReadTool(),
            new DefectDetectTool(),
            new JudgeTool(),
            new ResultTool(),
            new TcpCommunicationTool(deviceConfigurationRepository, communicationChannels),
            new SerialCommunicationTool(deviceConfigurationRepository, communicationChannels)
        ]);
    }
}
