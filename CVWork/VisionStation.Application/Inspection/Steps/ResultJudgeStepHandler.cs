using VisionStation.Domain;

namespace VisionStation.Application.Inspection.Steps;

internal sealed class ResultJudgeStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.ResultJudge;

    public InspectionOutcome Execute(
        ProcessStepDefinition step,
        string value,
        ProcessExecutionContext context)
    {
        var judgeResult = Evaluate(step, value);
        var target = string.IsNullOrWhiteSpace(step.OutputTarget) ? "OverallResult" : step.OutputTarget;
        context.RuntimeValues[target] = judgeResult.ToString();

        if (judgeResult is InspectionOutcome.Ng or InspectionOutcome.Error ||
            !context.RuntimeValues.TryGetValue("OverallResult", out var existingResult) ||
            string.Equals(existingResult, InspectionOutcome.Ok.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            context.RuntimeValues["OverallResult"] = judgeResult.ToString();
        }

        context.ResultTable[$"Judge:{step.ResultKey}"] = judgeResult.ToString();
        return judgeResult;
    }

    private static InspectionOutcome Evaluate(ProcessStepDefinition step, string value)
    {
        if (!double.TryParse(value, out var numericValue))
        {
            return InspectionOutcome.Error;
        }

        if (step.LowerLimit.HasValue && numericValue < step.LowerLimit.Value)
        {
            return InspectionOutcome.Ng;
        }

        if (step.UpperLimit.HasValue && numericValue > step.UpperLimit.Value)
        {
            return InspectionOutcome.Ng;
        }

        return InspectionOutcome.Ok;
    }
}
