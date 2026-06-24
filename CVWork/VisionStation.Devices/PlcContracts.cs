namespace VisionStation.Devices;

public enum PlcValueType
{
    Auto,
    Bool,
    BoolArray,
    Byte,
    Bytes,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    String,
    DateTime
}

public sealed record PlcReadCommand
{
    public string Address { get; init; } = string.Empty;

    public PlcValueType ValueType { get; init; } = PlcValueType.Auto;

    public ushort Length { get; init; } = 1;

    public string EncodingName { get; init; } = "ASCII";
}

public sealed record PlcWriteCommand
{
    public string Address { get; init; } = string.Empty;

    public PlcValueType ValueType { get; init; } = PlcValueType.Auto;

    public string Value { get; init; } = string.Empty;

    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    public int Length { get; init; }

    public string EncodingName { get; init; } = "ASCII";
}

public sealed record PlcWaitCommand
{
    public string Address { get; init; } = string.Empty;

    public PlcValueType ValueType { get; init; } = PlcValueType.Auto;

    public string ExpectedValue { get; init; } = "1";

    public int ReadIntervalMs { get; init; } = 50;

    public int TimeoutMs { get; init; } = 3000;
}

public sealed record PlcNativeCommand
{
    public string MethodName { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

public sealed record PlcOperationResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? ContentJson { get; init; }

    public string? ContentText { get; init; }

    public static PlcOperationResult Success(string? contentText = null, string? contentJson = null)
    {
        return new PlcOperationResult
        {
            IsSuccess = true,
            Message = "OK",
            ContentText = contentText,
            ContentJson = contentJson
        };
    }

    public static PlcOperationResult Failure(string message)
    {
        return new PlcOperationResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}

public interface IAdvancedPlcClient : IPlcClient
{
    Task<PlcOperationResult> ReadAsync(PlcReadCommand command, CancellationToken cancellationToken = default);

    Task<PlcOperationResult> WriteAsync(PlcWriteCommand command, CancellationToken cancellationToken = default);

    Task<PlcOperationResult> WaitAsync(PlcWaitCommand command, CancellationToken cancellationToken = default);

    Task<PlcOperationResult> InvokeNativeAsync(PlcNativeCommand command, CancellationToken cancellationToken = default);
}
