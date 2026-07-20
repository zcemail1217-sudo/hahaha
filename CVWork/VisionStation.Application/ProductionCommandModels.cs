namespace VisionStation.Application;

public enum ProductionCommandDisposition
{
    Completed,
    Canceled,
    Rejected,
    NoOp
}

public sealed record ProductionCommandResult(
    ProductionCommandDisposition Disposition,
    RunRejection? Rejection = null);

public sealed record ProductionCommandResult<T>(
    ProductionCommandDisposition Disposition,
    T? Value = default,
    RunRejection? Rejection = null);
