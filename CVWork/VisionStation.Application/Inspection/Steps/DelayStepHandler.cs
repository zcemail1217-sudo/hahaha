using VisionStation.Domain;

namespace VisionStation.Application.Inspection.Steps;

internal sealed class DelayStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.Delay;

    public async Task<int> ExecuteAsync(ProcessStepDefinition step, CancellationToken cancellationToken)
    {
        var delay = Math.Max(step.DelayMs, 0);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return delay;
    }
}
