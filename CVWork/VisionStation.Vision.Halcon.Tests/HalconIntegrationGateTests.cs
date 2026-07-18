using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class HalconIntegrationGateTests
{
    private const string EnvironmentVariableName = "VISIONSTATION_HALCON_INTEGRATION";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void FactSkipsUnlessExplicitlyEnabled(string? value)
    {
        WithEnvironmentVariable(value, () =>
        {
            HalconIntegrationFactAttribute attribute = new();

            Assert.Equal(HalconIntegrationGate.EnableMessage, attribute.Skip);
        });
    }

    [Fact]
    public void FactDoesNotSkipWhenExplicitlyEnabled()
    {
        WithEnvironmentVariable("1", () =>
        {
            HalconIntegrationFactAttribute attribute = new();

            Assert.Null(attribute.Skip);
        });
    }

    [Fact]
    public void TheoryDoesNotSkipWhenExplicitlyEnabled()
    {
        WithEnvironmentVariable("1", () =>
        {
            HalconIntegrationTheoryAttribute attribute = new();

            Assert.Null(attribute.Skip);
        });
    }

    [Fact]
    public void TheorySkipsWithoutExplicitEnablement()
    {
        WithEnvironmentVariable("true", () =>
        {
            HalconIntegrationTheoryAttribute attribute = new();

            Assert.Equal(HalconIntegrationGate.EnableMessage, attribute.Skip);
        });
    }

    private static void WithEnvironmentVariable(string? value, Action assertion)
    {
        string? originalValue = Environment.GetEnvironmentVariable(EnvironmentVariableName);

        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, value);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, originalValue);
        }
    }
}
