using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VariableCenterLiveValueTests
{
    [Fact]
    public void TryApplyLiveValue_UsesSourceBindingAndKeepsPersistedCurrentValue()
    {
        var variable = new RecipeVariableItem
        {
            Key = "MeasuredWidth",
            Name = "Measured Width",
            Source = "RuntimeValues:Vision.Width",
            CurrentValue = "50",
            DefaultValue = "45"
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Vision.Width"] = "50.25"
        };
        var updatedAt = new DateTimeOffset(2026, 6, 17, 10, 30, 0, TimeSpan.Zero);

        var applied = variable.TryApplyLiveValue(values, updatedAt);

        Assert.True(applied);
        Assert.Equal("50.25", variable.LiveValue);
        Assert.Equal("50.25", variable.DisplayedCurrentValue);
        Assert.Equal("50", variable.CurrentValue);
        Assert.Equal(updatedAt, variable.LiveValueUpdatedAt);
    }

    [Fact]
    public void RuntimeVariableLiveValue_FallsBackToVariableKeyAndName()
    {
        var variable = new RecipeVariableItem
        {
            Key = "OverallResult",
            Name = "Overall Result",
            Source = "RuntimeValues",
            CurrentValue = "Unknown"
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Overall Result"] = "Ok"
        };

        var applied = variable.TryApplyLiveValue(values, DateTimeOffset.Now);

        Assert.True(applied);
        Assert.Equal("Ok", variable.DisplayedCurrentValue);
        Assert.Equal("Unknown", variable.CurrentValue);
    }

    [Fact]
    public void ManualParameter_DoesNotUseRuntimeValueAndHasNoSourceBinding()
    {
        var variable = new RecipeVariableItem
        {
            Key = "Param1",
            Source = "Manual",
            CurrentValue = "configured"
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Param1"] = "runtime"
        };

        var applied = variable.TryApplyLiveValue(values, DateTimeOffset.Now);

        Assert.False(applied);
        Assert.False(variable.UsesSourceBinding);
        Assert.Empty(variable.SourceBindingOptions);
        Assert.Equal("configured", variable.DisplayedCurrentValue);
    }

    [Fact]
    public void ClearLiveValue_ReturnsDisplayToPersistedCurrentValue()
    {
        var variable = new RecipeVariableItem
        {
            Key = "Code",
            Source = "RuntimeValues",
            CurrentValue = "manual"
        };

        variable.TryApplyLiveValue(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = "runtime"
            },
            DateTimeOffset.Now);
        variable.ClearLiveValue();

        Assert.Equal(string.Empty, variable.LiveValue);
        Assert.Equal("manual", variable.DisplayedCurrentValue);
    }

    [Fact]
    public void EmptyLiveValue_IsDisplayedAsLiveValue()
    {
        var variable = new RecipeVariableItem
        {
            Key = "OptionalCode",
            CurrentValue = "fallback"
        };

        variable.SetLiveValue(string.Empty, DateTimeOffset.Now);

        Assert.Equal(string.Empty, variable.DisplayedCurrentValue);
    }

    [Fact]
    public void LiveValueStatus_DistinguishesRuntimeValueFromConfiguredValue()
    {
        var variable = new RecipeVariableItem
        {
            Key = "MeasuredWidth",
            CurrentValue = "50"
        };
        var updatedAt = new DateTimeOffset(2026, 6, 22, 17, 16, 40, TimeSpan.Zero);

        Assert.Equal("配置值", variable.ValueStateText);
        Assert.Equal("-", variable.LiveValueUpdatedAtText);

        variable.SetLiveValue("50.25", updatedAt);

        Assert.Equal("实时", variable.ValueStateText);
        Assert.Equal("17:16:40", variable.LiveValueUpdatedAtText);
    }
}
