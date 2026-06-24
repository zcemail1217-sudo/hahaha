using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application.Inspection;

internal sealed record VariableSignalBinding(
    string Source,
    string DeviceKey,
    string Address,
    string ChannelKey,
    string Payload,
    string VariableKey);

internal sealed class ProcessExecutionContext
{
    public ProcessExecutionContext(string flowName, IReadOnlyDictionary<string, string>? initialRuntimeValues = null)
    {
        FlowName = flowName;
        if (initialRuntimeValues is not null)
        {
            foreach (var pair in initialRuntimeValues)
            {
                RuntimeValues[pair.Key] = pair.Value;
            }
        }
    }

    public string FlowName { get; set; }

    public ImageFrame? OriginalFrame { get; set; }

    public ImageFrame? ResultFrame { get; set; }

    public VisionPipelineResult? LastPipelineResult { get; set; }

    public List<ToolResult> ToolResults { get; } = [];

    public List<FlowRunResult> FlowResults { get; } = [];

    public Dictionary<string, string> RuntimeValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object> RuntimeObjects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ResultTable { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record VisionRuntimeResultValue(
    string FlowId,
    string FlowName,
    ImageFrame ResultFrame,
    IReadOnlyList<ToolResult> ToolResults,
    InspectionOutcome Outcome,
    string Barcode,
    string Message);
