using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

internal static class HalconIntegrationGate
{
    private const string EnvironmentVariableName = "VISIONSTATION_HALCON_INTEGRATION";

    public const string EnableMessage =
        "Set VISIONSTATION_HALCON_INTEGRATION=1 to run licensed HALCON tests.";

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariableName),
        "1",
        StringComparison.Ordinal);
}

public sealed class HalconIntegrationFactAttribute : FactAttribute
{
    public HalconIntegrationFactAttribute()
    {
        if (!HalconIntegrationGate.IsEnabled)
        {
            Skip = HalconIntegrationGate.EnableMessage;
        }
    }
}

public sealed class HalconIntegrationTheoryAttribute : TheoryAttribute
{
    public HalconIntegrationTheoryAttribute()
    {
        if (!HalconIntegrationGate.IsEnabled)
        {
            Skip = HalconIntegrationGate.EnableMessage;
        }
    }
}
