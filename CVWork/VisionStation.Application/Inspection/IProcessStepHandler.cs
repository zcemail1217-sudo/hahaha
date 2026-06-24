using VisionStation.Domain;

namespace VisionStation.Application.Inspection;

internal interface IProcessStepHandler
{
    ProcessStepType StepType { get; }
}
