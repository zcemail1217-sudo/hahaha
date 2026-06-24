using System.Collections.ObjectModel;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Client.Services;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class SystemLogsViewModel : BindableBase
{
    private readonly IAppLogService _log;
    private readonly IUiDispatcher _uiDispatcher;

    public SystemLogsViewModel(IAppLogService log, IUiDispatcher uiDispatcher)
    {
        _log = log;
        _uiDispatcher = uiDispatcher;
        foreach (var entry in _log.Recent(200))
        {
            Logs.Add(ToItem(entry));
        }

        _log.LogWritten += (_, entry) => _uiDispatcher.Invoke(() =>
        {
            Logs.Insert(0, ToItem(entry));
            while (Logs.Count > 200)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    public ObservableCollection<LogLineItem> Logs { get; } = new();

    private static LogLineItem ToItem(AppLogEntry entry)
    {
        return new LogLineItem(
            entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            entry.Level,
            OperatorMessageLocalizer.LocalizeSource(entry.Source),
            OperatorMessageLocalizer.LocalizeMessage(entry.Message));
    }

}
