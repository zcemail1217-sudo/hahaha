using System.Diagnostics;
using VisionStation.Domain;
using VisionStation.Domain.Utilities;

namespace VisionStation.Vision.Tools;

public sealed class AcquireImageTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.AcquireImage;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceMode = ParameterParser.GetString(definition.Parameters, "source", "Camera");
            var convertColorToGray = ParameterParser.GetBool(definition.Parameters, "convertColorToGray", false);
            var frame = ResolveFrame(definition, context, sourceMode, convertColorToGray);
            context.SetImageOutput(definition, frame);
            stopwatch.Stop();

            return Task.FromResult(new ToolResult
            {
                ToolId = definition.Id,
                ToolName = definition.Name,
                Kind = Kind,
                Outcome = InspectionOutcome.Ok,
                Duration = stopwatch.Elapsed,
                Message = $"Image acquired {frame.Width}x{frame.Height}",
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceMode"] = sourceMode,
                    ["source"] = frame.Source,
                    ["frameId"] = frame.Id,
                    ["width"] = frame.Width.ToString(),
                    ["height"] = frame.Height.ToString(),
                    ["format"] = frame.Format.ToString()
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return Task.FromResult(new ToolResult
            {
                ToolId = definition.Id,
                ToolName = definition.Name,
                Kind = Kind,
                Outcome = InspectionOutcome.Error,
                Duration = stopwatch.Elapsed,
                Message = $"Image acquisition failed: {ex.Message}",
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceMode"] = ParameterParser.GetString(definition.Parameters, "source", "Camera"),
                    ["error"] = ex.Message
                }
            });
        }
    }

    private static ImageFrame ResolveFrame(
        VisionToolDefinition definition,
        VisionToolContext context,
        string sourceMode,
        bool convertColorToGray)
    {
        if (string.Equals(sourceMode, "File", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFrameFileLoader.LoadFile(
                ParameterParser.GetString(definition.Parameters, "filePath"),
                convertColorToGray);
        }

        if (string.Equals(sourceMode, "Directory", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFrameFileLoader.LoadFirstFromDirectory(
                ParameterParser.GetString(definition.Parameters, "directoryPath"),
                convertColorToGray);
        }

        return convertColorToGray
            ? ImageFrameFileLoader.ToGray8(context.OriginalFrame)
            : context.OriginalFrame;
    }
}
