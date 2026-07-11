using System.Collections.ObjectModel;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Presentation;
using VisionStation.Client.Services;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Timer = System.Timers.Timer;

namespace VisionStation.Client.ViewModels;

public sealed class ShellWindowViewModel : BindableBase
{
    private readonly IRegionManager _regionManager;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IInspectionExecution _inspectionExecution;
    private readonly IAlarmService _alarms;
    private readonly IUnsavedChangesService _unsavedChanges;
    private readonly Timer _clockTimer;
    private ProductionSnapshot _productionSnapshot;
    private string _clockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private string _pageTitle = "生产监控";
    private string _productionStateText = "停止";
    private string _productionStateBrush = "#FFA9B7C2";
    private string _deviceStatusText = "等待中";
    private string _cameraStateText = "未连接";
    private string _cameraStateBrush = "#FFA9B7C2";
    private string _plcStateText = "未连接";
    private string _plcStateBrush = "#FFA9B7C2";
    private string _axisStateText = "未连接";
    private string _axisStateBrush = "#FFA9B7C2";
    private int _activeAlarmCount;
    private string _alarmStatusText = "无报警";
    private string _alarmStatusBrush = "#FF5CE08A";

    public ShellWindowViewModel(
        IRegionManager regionManager,
        ProductionCoordinator coordinator,
        IInspectionExecution inspectionExecution,
        IUiDispatcher uiDispatcher,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IAlarmService alarms,
        IUnsavedChangesService unsavedChanges)
    {
        _regionManager = regionManager;
        _uiDispatcher = uiDispatcher;
        _inspectionExecution = inspectionExecution;
        _productionSnapshot = coordinator.Snapshot;
        _alarms = alarms;
        _unsavedChanges = unsavedChanges;
        GlobalSave = new GlobalSaveViewModel(_unsavedChanges, _uiDispatcher);

        NavigationItems =
        [
            new ShellNavigationItem(NavigationKeys.ProductionDashboard, "生产监控", "\uE9D9"),
            new ShellNavigationItem(NavigationKeys.VisionDebug, "视觉流程", "\uE722"),
            new ShellNavigationItem(NavigationKeys.RecipeManagement, "配方管理", "\uE8B7"),
            new ShellNavigationItem(NavigationKeys.VariableCenter, "变量中心", "\uE8EC"),
            new ShellNavigationItem(NavigationKeys.DeviceStatus, "设备调试", "\uE950"),
            new ShellNavigationItem(NavigationKeys.HistoryRecords, "历史记录", "\uE81C"),
            new ShellNavigationItem(NavigationKeys.SystemLogs, "系统日志", "\uE8A7")
        ];

        UtilityNavigationItems =
        [
            new ShellNavigationItem(NavigationKeys.PermissionLogin, "权限登录", "\uE8D7"),
            new ShellNavigationItem(NavigationKeys.SystemSettings, "系统设置", "\uE713")
        ];

        NavigateCommand = new DelegateCommand<string>(Navigate);
        ConfirmAlarmCommand = new DelegateCommand<string>(ConfirmAlarm);
        HideAlarmToastCommand = new DelegateCommand<string>(HideAlarmToast);
        SetActiveNavigation(NavigationKeys.ProductionDashboard);

        foreach (var alarm in _alarms.Active())
        {
            ShowAlarmToast(alarm);
        }

        RefreshAlarmSummary();
        _alarms.AlarmRaised += (_, alarm) => _uiDispatcher.Invoke(() => ShowAlarmToast(alarm));
        _alarms.AlarmChanged += (_, alarm) => _uiDispatcher.Invoke(() => ApplyAlarmChange(alarm));

        coordinator.SnapshotChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
        {
            _productionSnapshot = snapshot;
            RefreshProductionPresentation();
        });
        _inspectionExecution.Changed += (_, _) => _uiDispatcher.Invoke(RefreshProductionPresentation);
        coordinator.DeviceStateChanged += (_, snapshot) => _uiDispatcher.Invoke(() => ApplyDeviceSnapshot(snapshot));

        ApplyDeviceSnapshot(camera.Snapshot);
        ApplyDeviceSnapshot(plc.Snapshot);
        ApplyDeviceSnapshot(axis.Snapshot);
        RefreshProductionPresentation();

        _clockTimer = new Timer(1000)
        {
            AutoReset = true,
            Enabled = true
        };
        _clockTimer.Elapsed += (_, _) => _uiDispatcher.Invoke(() => ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _clockTimer.Start();
    }

    public ObservableCollection<ShellNavigationItem> NavigationItems { get; }

    public ObservableCollection<ShellNavigationItem> UtilityNavigationItems { get; }

    public ObservableCollection<AlarmToastItem> AlarmToasts { get; } = new();

    public DelegateCommand<string> NavigateCommand { get; }

    public DelegateCommand<string> ConfirmAlarmCommand { get; }

    public DelegateCommand<string> HideAlarmToastCommand { get; }

    public GlobalSaveViewModel GlobalSave { get; }

    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string ProductionStateText
    {
        get => _productionStateText;
        private set => SetProperty(ref _productionStateText, value);
    }

    public string ProductionStateBrush
    {
        get => _productionStateBrush;
        private set => SetProperty(ref _productionStateBrush, value);
    }

    public string DeviceStatusText
    {
        get => _deviceStatusText;
        private set => SetProperty(ref _deviceStatusText, value);
    }

    public string CameraStateText
    {
        get => _cameraStateText;
        private set => SetProperty(ref _cameraStateText, value);
    }

    public string CameraStateBrush
    {
        get => _cameraStateBrush;
        private set => SetProperty(ref _cameraStateBrush, value);
    }

    public string PlcStateText
    {
        get => _plcStateText;
        private set => SetProperty(ref _plcStateText, value);
    }

    public string PlcStateBrush
    {
        get => _plcStateBrush;
        private set => SetProperty(ref _plcStateBrush, value);
    }

    public string AxisStateText
    {
        get => _axisStateText;
        private set => SetProperty(ref _axisStateText, value);
    }

    public string AxisStateBrush
    {
        get => _axisStateBrush;
        private set => SetProperty(ref _axisStateBrush, value);
    }

    public int ActiveAlarmCount
    {
        get => _activeAlarmCount;
        private set => SetProperty(ref _activeAlarmCount, value);
    }

    public string AlarmStatusText
    {
        get => _alarmStatusText;
        private set => SetProperty(ref _alarmStatusText, value);
    }

    public string AlarmStatusBrush
    {
        get => _alarmStatusBrush;
        private set => SetProperty(ref _alarmStatusBrush, value);
    }

    public IReadOnlyList<UnsavedChangeItem> GetUnsavedChanges()
    {
        return _unsavedChanges.GetUnsavedChanges();
    }

    public Task SaveUnsavedChangesAsync(CancellationToken cancellationToken = default)
    {
        return _unsavedChanges.SaveAllAsync(cancellationToken);
    }

    private void Navigate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        SetActiveNavigation(key);
        _regionManager.RequestNavigate(RegionNames.MainRegion, key);
    }

    private void SetActiveNavigation(string key)
    {
        foreach (var item in NavigationItems.Concat(UtilityNavigationItems))
        {
            item.IsSelected = item.Key == key;
        }

        PageTitle = NavigationItems.Concat(UtilityNavigationItems).FirstOrDefault(item => item.Key == key)?.Title ?? "VisionStation";
    }

    private void RefreshProductionPresentation()
    {
        var uiState = ProductionRunUiState.Create(
            _productionSnapshot.State,
            _inspectionExecution.Current,
            _productionSnapshot.ActiveSessionId,
            commandBusy: false);
        ProductionStateText = uiState.StateText;
        ProductionStateBrush = uiState.StateBrush;
    }

    private void ApplyDeviceSnapshot(DeviceSnapshot snapshot)
    {
        var stateText = OperatorMessageLocalizer.LocalizeDeviceState(snapshot.State);
        var stateBrush = ResolveDeviceStateBrush(snapshot.State);
        if (snapshot.Name.Contains("相机", StringComparison.OrdinalIgnoreCase) ||
            snapshot.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) ||
            snapshot.Name.Contains("相机", StringComparison.OrdinalIgnoreCase))
        {
            CameraStateText = stateText;
            CameraStateBrush = stateBrush;
        }
        else if (snapshot.Name.Contains("PLC", StringComparison.OrdinalIgnoreCase))
        {
            PlcStateText = stateText;
            PlcStateBrush = stateBrush;
        }
        else
        {
            AxisStateText = stateText;
            AxisStateBrush = stateBrush;
        }

        DeviceStatusText = $"{OperatorMessageLocalizer.LocalizeSource(snapshot.Name)}：{OperatorMessageLocalizer.LocalizeMessage(snapshot.Message)}";
    }

    private void ShowAlarmToast(AlarmEvent alarm)
    {
        RefreshAlarmSummary();
        if (alarm.Severity == AlarmSeverity.Info || !alarm.IsActive)
        {
            return;
        }

        RemoveToast(alarm.Id);
        AlarmToasts.Insert(0, ToToast(alarm));
        while (AlarmToasts.Count > 4)
        {
            AlarmToasts.RemoveAt(AlarmToasts.Count - 1);
        }

        if (alarm.Severity == AlarmSeverity.Warning)
        {
            _ = AutoHideWarningAsync(alarm.Id);
        }
    }

    private void ApplyAlarmChange(AlarmEvent alarm)
    {
        RefreshAlarmSummary();
        if (!alarm.IsActive || alarm.Acknowledged)
        {
            RemoveToast(alarm.Id);
        }
    }

    private void ConfirmAlarm(string alarmId)
    {
        var toast = AlarmToasts.FirstOrDefault(item => item.Id == alarmId);
        if (toast is null)
        {
            return;
        }

        _alarms.Clear(alarmId);
        RemoveToast(alarmId);
        RefreshAlarmSummary();
    }

    private void HideAlarmToast(string alarmId)
    {
        var toast = AlarmToasts.FirstOrDefault(item => item.Id == alarmId);
        if (toast?.RequiresAcknowledgement == true)
        {
            return;
        }

        RemoveToast(alarmId);
    }

    private async Task AutoHideWarningAsync(string alarmId)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        _uiDispatcher.Invoke(() =>
        {
            var toast = AlarmToasts.FirstOrDefault(item => item.Id == alarmId);
            if (toast?.Severity == AlarmSeverity.Warning)
            {
                RemoveToast(alarmId);
            }
        });
    }

    private void RemoveToast(string alarmId)
    {
        var existing = AlarmToasts.FirstOrDefault(item => item.Id == alarmId);
        if (existing is not null)
        {
            AlarmToasts.Remove(existing);
        }
    }

    private void RefreshAlarmSummary()
    {
        var active = _alarms.Active();
        ActiveAlarmCount = active.Count;
        if (active.Count == 0)
        {
            AlarmStatusText = "无报警";
            AlarmStatusBrush = "#FF5CE08A";
            return;
        }

        var highest = active.OrderByDescending(alarm => alarm.Severity).First();
        AlarmStatusText = $"{active.Count} 个报警 / {FormatAlarmSeverity(highest.Severity)}";
        AlarmStatusBrush = highest.Severity switch
        {
            AlarmSeverity.Warning => "#FFFFC95A",
            AlarmSeverity.Error => "#FFFF667A",
            AlarmSeverity.Critical => "#FFFF3B4F",
            _ => "#FF7AD7FF"
        };
    }

    private static string ResolveDeviceStateBrush(DeviceConnectionState state)
    {
        return state switch
        {
            DeviceConnectionState.Connected => "#FF5CE08A",
            DeviceConnectionState.Connecting => "#FFFFC95A",
            DeviceConnectionState.Faulted => "#FFFF667A",
            _ => "#FFA9B7C2"
        };
    }

    private static string FormatAlarmSeverity(AlarmSeverity severity)
    {
        return severity switch
        {
            AlarmSeverity.Warning => "警告",
            AlarmSeverity.Error => "错误",
            AlarmSeverity.Critical => "严重",
            _ => "提示"
        };
    }

    private static AlarmToastItem ToToast(AlarmEvent alarm)
    {
        return new AlarmToastItem(
            alarm.Id,
            alarm.Severity,
            OperatorMessageLocalizer.LocalizeSource(alarm.Source),
            OperatorMessageLocalizer.LocalizeMessage(alarm.Message),
            OperatorMessageLocalizer.LocalizeDetails(alarm.Details),
            alarm.Timestamp);
    }
}
