using VisionStation.Domain;

namespace VisionStation.Vision;

public sealed class ConfiguredVisionPipeline : IVisionPipeline
{
    private readonly IReadOnlyDictionary<VisionToolKind, IVisionTool> _tools;

    public ConfiguredVisionPipeline(IEnumerable<IVisionTool> tools)
    {
        _tools = tools.ToDictionary(tool => tool.Kind);
    }

    public async Task<VisionPipelineResult> ExecuteAsync(Recipe recipe, ImageFrame frame, CancellationToken cancellationToken = default)
    {
        var activeFlow = recipe.GetActiveFlow();
        var runtimeRecipe = recipe with
        {
            CurrentFlowId = activeFlow.Id,
            Rois = activeFlow.Rois,
            Tools = activeFlow.Tools
        };
        using var context = new VisionToolContext(runtimeRecipe, frame);

        foreach (var definition in activeFlow.Tools.Where(tool => tool.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_tools.TryGetValue(definition.Kind, out var tool))
            {
                context.ToolResults.Add(new ToolResult
                {
                    ToolId = definition.Id,
                    ToolName = definition.Name,
                    Kind = definition.Kind,
                    Outcome = InspectionOutcome.Error,
                    Message = "视觉工具未注册"
                });
                continue;
            }

            var result = await tool.ExecuteAsync(definition, context, cancellationToken);
            context.SetPortOutput(definition, "ResultOutput", result);
            if (definition.Kind == VisionToolKind.Judge)
            {
                context.SetPortOutput(definition, "OverallResultOutput", result);
            }

            context.ToolResults.Add(result);
            context.CaptureToolFrame(definition);
        }

        var judge = context.ToolResults.LastOrDefault(result => result.Kind == VisionToolKind.Judge);
        var outcome = judge?.Outcome ?? (context.ToolResults.Any(result => result.Outcome is InspectionOutcome.Ng or InspectionOutcome.Error)
            ? InspectionOutcome.Ng
            : InspectionOutcome.Ok);
        var barcode = context.ToolResults
            .LastOrDefault(result => result.Kind is VisionToolKind.CodeRead or VisionToolKind.Ocr)?
            .Data.GetValueOrDefault("code") ?? string.Empty;
        var message = judge?.Message ?? (outcome == InspectionOutcome.Ok ? "检测通过" : "检测不通过");

        return new VisionPipelineResult(
            context.ResultFrame,
            context.ToolResults,
            outcome,
            barcode,
            message,
            new Dictionary<string, ImageFrame>(context.CapturedToolFrames, StringComparer.OrdinalIgnoreCase));
    }
}
