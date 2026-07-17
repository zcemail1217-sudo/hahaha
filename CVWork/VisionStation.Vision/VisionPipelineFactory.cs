using VisionStation.Domain;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision;

public static class VisionPipelineFactory
{
    public static IVisionPipeline CreateDefault(
        IDeviceConfigurationRepository deviceConfigurationRepository,
        ITemplateMatchingService templateMatchingService,
        ICommunicationChannelRuntime? communicationChannels = null)
    {
        ArgumentNullException.ThrowIfNull(deviceConfigurationRepository);
        ArgumentNullException.ThrowIfNull(templateMatchingService);
        return new ConfiguredVisionPipeline(
        [
            new AcquireImageTool(),
            new ImageProcessTool(),
            new TemplateLocateTool(templateMatchingService),
            new MultiTargetMatchTool(templateMatchingService),
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
