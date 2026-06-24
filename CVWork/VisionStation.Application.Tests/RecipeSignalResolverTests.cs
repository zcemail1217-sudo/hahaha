using VisionStation.Application.Inspection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class RecipeSignalResolverTests
{
    [Theory]
    [InlineData("sig-1")]
    [InlineData("start")]
    [InlineData("D100")]
    public void ResolvePlcSignal_MatchesIdNameOrAddress(string key)
    {
        var recipe = new Recipe
        {
            PlcSignals =
            [
                new PlcSignalDefinition
                {
                    Id = "SIG-1",
                    Name = "Start",
                    Address = "D100"
                }
            ]
        };

        var signal = RecipeSignalResolver.ResolvePlcSignal(recipe, key);

        Assert.NotNull(signal);
        Assert.Equal("D100", signal.Address);
    }

    [Theory]
    [InlineData("logical-ready")]
    [InlineData("map-1")]
    [InlineData("Ready")]
    public void ResolveSignalMapping_MatchesEnabledMappingKeyIdOrName(string key)
    {
        var recipe = new Recipe
        {
            SignalMappings =
            [
                new SignalMappingDefinition
                {
                    Id = "MAP-1",
                    Key = "Logical-Ready",
                    Name = "Ready",
                    Address = "D200",
                    Enabled = true
                }
            ]
        };

        var mapping = RecipeSignalResolver.ResolveSignalMapping(recipe, key);

        Assert.NotNull(mapping);
        Assert.Equal("D200", mapping.Address);
    }

    [Fact]
    public void ResolveSignalMapping_IgnoresDisabledMapping()
    {
        var recipe = new Recipe
        {
            SignalMappings =
            [
                new SignalMappingDefinition
                {
                    Key = "Ready",
                    Enabled = false
                }
            ]
        };

        Assert.Null(RecipeSignalResolver.ResolveSignalMapping(recipe, "Ready"));
    }
}
