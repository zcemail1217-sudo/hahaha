using VisionStation.Application;
using VisionStation.Domain;

namespace VisionStation.Client.Presentation;

internal sealed record ProductionRunUiState(
    string StateText,
    string StateBrush,
    bool CanRunSingle,
    bool CanStart,
    bool CanStop,
    bool IsExternallyOccupied,
    string OccupancyText)
{
    public static ProductionRunUiState Create(
        ProductionState state,
        ActiveInspectionRun? current,
        Guid? productionSessionId,
        bool commandBusy)
    {
        var ownsExecution = current is not null && productionSessionId == current.SessionId;
        var externallyOccupied = current is not null && !ownsExecution;
        var canBegin = current is null && !commandBusy &&
                       state is ProductionState.Stopped or ProductionState.Faulted;
        var occupancyText = current is null ? string.Empty : FormatOccupancy(current);

        if (externallyOccupied)
        {
            return new ProductionRunUiState(
                $"占用：{FormatOwner(current!)}",
                "#FFFFC95A",
                CanRunSingle: false,
                CanStart: false,
                CanStop: false,
                IsExternallyOccupied: true,
                occupancyText);
        }

        var (stateText, stateBrush) = FormatState(state);
        return new ProductionRunUiState(
            stateText,
            stateBrush,
            CanRunSingle: canBegin,
            CanStart: canBegin,
            CanStop: ownsExecution && state != ProductionState.Stopping,
            IsExternallyOccupied: false,
            occupancyText);
    }

    public static string FormatRejection(RunRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return FormatOccupancy(rejection.Active);
    }

    private static (string Text, string Brush) FormatState(ProductionState state)
    {
        return state switch
        {
            ProductionState.Starting => ("启动中", "#FFFFC95A"),
            ProductionState.Running => ("运行", "#FF5CE08A"),
            ProductionState.Stopping => ("停止中", "#FFFFC95A"),
            ProductionState.Paused => ("暂停", "#FFFFC95A"),
            ProductionState.Faulted => ("故障", "#FFFF667A"),
            _ => ("停止", "#FFA9B7C2")
        };
    }

    private static string FormatOwner(ActiveInspectionRun active)
    {
        return $"{active.Intent.Mode.DisplayName}（{active.Intent.EntryPoint}）";
    }

    private static string FormatOccupancy(ActiveInspectionRun active)
    {
        return $"{FormatOwner(active)}正在占用检测执行";
    }
}
