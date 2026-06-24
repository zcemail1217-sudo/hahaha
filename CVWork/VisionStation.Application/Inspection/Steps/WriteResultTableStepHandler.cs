using VisionStation.Domain;

namespace VisionStation.Application.Inspection.Steps;

internal sealed class WriteResultTableStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.WriteResultTable;

    public string Execute(ProcessStepDefinition step, string value, ProcessExecutionContext context)
    {
        var target = string.IsNullOrWhiteSpace(step.OutputTarget) ? step.ResultKey : step.OutputTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException($"Result table target is empty for step '{step.Name}'.");
        }

        context.RuntimeValues[target] = value;
        context.ResultTable[target] = value;
        return target;
    }
}
