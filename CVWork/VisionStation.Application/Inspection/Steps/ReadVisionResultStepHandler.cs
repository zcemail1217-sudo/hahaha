using VisionStation.Domain;

namespace VisionStation.Application.Inspection.Steps;

internal sealed class ReadVisionResultStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.ReadVisionResult;

    public void Execute(ProcessStepDefinition step, string value, ProcessExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(step.ResultKey))
        {
            throw new InvalidOperationException($"Vision result key is empty for step '{step.Name}'.");
        }

        context.RuntimeValues[step.ResultKey] = value;
        if (!string.IsNullOrWhiteSpace(step.OutputTarget))
        {
            context.RuntimeValues[step.OutputTarget] = value;
        }
    }
}
