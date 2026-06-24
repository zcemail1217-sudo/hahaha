namespace VisionStation.Application.Inspection;

internal static class SignalSourceMapper
{
    public static string MapSignalSourceType(string sourceType)
    {
        return sourceType.Trim() switch
        {
            "TCP" or "tcp" => "tcp",
            "串口" or "Serial" or "serial" => "serial",
            "轴卡 IO" or "轴卡IO" or "IO" or "DigitalIo" or "digitalIo" or "AxisInput" or "axisInput" => "digitalIo",
            "变量" or "变量/参数" or "参数" or "RuntimeValue" or "RuntimeValues" or "runtimeValue" or "runtimeValues" => "runtimeValue",
            _ => "device"
        };
    }

    public static bool IsTcpSourceName(string sourceType)
    {
        return string.Equals(sourceType, "TCP", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSerialSourceName(string sourceType)
    {
        return string.Equals(sourceType, "Serial", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceType, "串口", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDigitalIoSourceName(string sourceType)
    {
        return string.Equals(sourceType, "IO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceType, "DigitalIo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceType, "AxisInput", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceType, "轴卡IO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceType, "轴卡 IO", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRuntimeValueSource(string source)
    {
        return string.Equals(source, "runtimeValue", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "runtimeValues", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "variable", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "parameter", StringComparison.OrdinalIgnoreCase);
    }
}
