using VisionStation.Application;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionExecutionContractTests
{
    [Fact]
    public void Application_exposes_inspection_execution_contracts()
    {
        var assembly = typeof(InspectionRunResult).Assembly;

        Assert.NotNull(assembly.GetType("VisionStation.Application.IInspectionExecution"));
        Assert.NotNull(assembly.GetType("VisionStation.Application.IInspectionSession"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Production.Manual")]
    [InlineData("production manual")]
    [InlineData("production/manual")]
    public void InspectionRunMode_rejects_invalid_key(string key)
    {
        Assert.Throws<ArgumentException>(() => new InspectionRunMode(key, "测试"));
    }

    [Fact]
    public void InspectionRunMode_rejects_empty_display_name()
    {
        Assert.Throws<ArgumentException>(
            () => new InspectionRunMode("custom.test", " "));
    }

    [Fact]
    public void InspectionRunMode_accepts_extension_mode_without_registration()
    {
        var mode = new InspectionRunMode("calibration.test", "标定试运行");

        Assert.Equal("calibration.test", mode.Key);
        Assert.Equal("标定试运行", mode.DisplayName);
    }
}
