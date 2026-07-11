using VisionStation.Application;
using VisionStation.Client.Presentation;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionRunUiStateTests
{
    [Theory]
    [InlineData(ProductionState.Stopped, "停止", "#FFA9B7C2")]
    [InlineData(ProductionState.Starting, "启动中", "#FFFFC95A")]
    [InlineData(ProductionState.Running, "运行", "#FF5CE08A")]
    [InlineData(ProductionState.Stopping, "停止中", "#FFFFC95A")]
    [InlineData(ProductionState.Paused, "暂停", "#FFFFC95A")]
    [InlineData(ProductionState.Faulted, "故障", "#FFFF667A")]
    public void Create_MapsProductionStateToOperatorPresentation(
        ProductionState state,
        string expectedText,
        string expectedBrush)
    {
        var uiState = ProductionRunUiState.Create(state, null, null, commandBusy: false);

        Assert.Equal(expectedText, uiState.StateText);
        Assert.Equal(expectedBrush, uiState.StateBrush);
    }

    [Fact]
    public void Create_ExternalOwnerDisablesProductionCommandsAndExplainsOccupancy()
    {
        var current = CreateActiveRun("配方试运行", "配方管理");

        var uiState = ProductionRunUiState.Create(
            ProductionState.Stopped,
            current,
            productionSessionId: null,
            commandBusy: false);

        Assert.True(uiState.IsExternallyOccupied);
        Assert.False(uiState.CanRunSingle);
        Assert.False(uiState.CanStart);
        Assert.False(uiState.CanStop);
        Assert.Equal("占用：配方试运行（配方管理）", uiState.StateText);
        Assert.Equal("#FFFFC95A", uiState.StateBrush);
        Assert.Equal("配方试运行（配方管理）正在占用检测执行", uiState.OccupancyText);
        var rejection = new RunRejection(RunRejectionReason.Busy, current);
        Assert.Equal(uiState.OccupancyText, ProductionRunUiState.FormatRejection(rejection));
    }

    [Fact]
    public void Create_OwnedFaultedRunCanRetryStopButCannotStart()
    {
        var current = CreateActiveRun("连续生产", "生产监控");

        var uiState = ProductionRunUiState.Create(
            ProductionState.Faulted,
            current,
            current.SessionId,
            commandBusy: false);

        Assert.False(uiState.IsExternallyOccupied);
        Assert.False(uiState.CanRunSingle);
        Assert.False(uiState.CanStart);
        Assert.True(uiState.CanStop);
    }

    [Fact]
    public void Create_OwnedStartingRunCanStopWhileAnotherCommandIsBusy()
    {
        var current = CreateActiveRun("连续生产", "生产监控");

        var uiState = ProductionRunUiState.Create(
            ProductionState.Starting,
            current,
            current.SessionId,
            commandBusy: true);

        Assert.False(uiState.CanRunSingle);
        Assert.False(uiState.CanStart);
        Assert.True(uiState.CanStop);
    }

    [Fact]
    public void Create_UnoccupiedFaultedRunCanRestart()
    {
        var uiState = ProductionRunUiState.Create(
            ProductionState.Faulted,
            current: null,
            productionSessionId: null,
            commandBusy: false);

        Assert.True(uiState.CanRunSingle);
        Assert.True(uiState.CanStart);
        Assert.False(uiState.CanStop);
    }

    [Fact]
    public void Create_OwnedStoppingRunCannotStopAgain()
    {
        var current = CreateActiveRun("连续生产", "生产监控");

        var uiState = ProductionRunUiState.Create(
            ProductionState.Stopping,
            current,
            current.SessionId,
            commandBusy: false);

        Assert.False(uiState.CanStop);
    }

    private static ActiveInspectionRun CreateActiveRun(string displayName, string entryPoint)
    {
        return new ActiveInspectionRun(
            Guid.NewGuid(),
            new InspectionRunIntent(new InspectionRunMode("test.mode", displayName), entryPoint),
            DateTimeOffset.UtcNow);
    }
}
