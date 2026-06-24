using VisionStation.Application.Inspection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VariableSignalBindingResolverTests
{
    [Theory]
    [InlineData("TCP:tcp-a:READ:SN", "tcp", "tcp-a", "Code", "tcp-a", "READ:SN")]
    [InlineData("Serial:serial-a:Q", "serial", "serial-a", "Code", "serial-a", "Q")]
    [InlineData("PLC:plc-a:D100", "device", "plc-a", "D100", "", "")]
    [InlineData("IO:io-a:X1", "digitalIo", "io-a", "X1", "", "")]
    [InlineData("", "runtimeValue", "", "Code", "", "")]
    public void Resolve_ParsesVariableSource(
        string source,
        string expectedSource,
        string expectedDeviceKey,
        string expectedAddress,
        string expectedChannelKey,
        string expectedPayload)
    {
        var recipe = new Recipe
        {
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "Code",
                    Name = "条码",
                    Source = source,
                    Expression = "EXPR",
                    Target = "TARGET"
                }
            ]
        };

        var binding = VariableSignalBindingResolver.Resolve(recipe, "条码");

        Assert.NotNull(binding);
        Assert.Equal(expectedSource, binding.Source);
        Assert.Equal(expectedDeviceKey, binding.DeviceKey);
        Assert.Equal(expectedAddress, binding.Address);
        Assert.Equal(expectedChannelKey, binding.ChannelKey);
        Assert.Equal(expectedPayload, binding.Payload);
        Assert.Equal("Code", binding.VariableKey);
    }

    [Fact]
    public void Resolve_UsesExpressionWhenCommunicationPayloadIsMissing()
    {
        var recipe = new Recipe
        {
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "Code",
                    Source = "TCP:tcp-a",
                    Expression = "READ"
                }
            ]
        };

        var binding = VariableSignalBindingResolver.Resolve(recipe, "Code");

        Assert.NotNull(binding);
        Assert.Equal("READ", binding.Payload);
    }
}
