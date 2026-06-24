namespace VisionStation.Client.Services;

public enum LoadingStageState
{
    Pending,
    Running,
    Ready,
    Reserved,
    Warning,
    Error
}

public sealed record SystemLoadingStageSnapshot(
    string Key,
    string Name,
    string Detail,
    string StateText,
    string StateBrush);

public sealed record SystemLoadingSnapshot(
    bool IsVisible,
    string Message,
    string Detail,
    IReadOnlyList<SystemLoadingStageSnapshot> Stages);

public interface ISystemLoadingService
{
    event EventHandler<SystemLoadingSnapshot>? SnapshotChanged;

    SystemLoadingSnapshot Current { get; }

    void ResetDefaultStages();

    void Show(string message, string detail);

    void Hide();

    void SetStage(string key, string name, string detail, LoadingStageState state);
}

public sealed class SystemLoadingService : ISystemLoadingService
{
    private readonly object _gate = new();
    private readonly List<MutableLoadingStage> _stages = new();
    private SystemLoadingSnapshot _current = new(false, "SYSTEM READY", "Loading panel is standing by.", []);

    public SystemLoadingService()
    {
        ResetDefaultStages();
    }

    public event EventHandler<SystemLoadingSnapshot>? SnapshotChanged;

    public SystemLoadingSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void ResetDefaultStages()
    {
        SystemLoadingSnapshot snapshot;
        lock (_gate)
        {
            _stages.Clear();
            _stages.Add(new MutableLoadingStage("axis-card", "轴卡", "等待打开运动控制卡、轴映射与配置文件。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("digital-io", "数字IO", "等待加载 DI/DO 点表与扩展 IO 模块。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("camera", "相机", "等待连接相机服务、触发源与曝光配置。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("plc", "PLC", "等待建立 PLC 通讯、心跳与读写地址映射。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("recipe-files", "配方文件", "等待加载产品配方、流程图与参数文件。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("image-files", "图像文件", "等待准备本地图像、模板资源与追溯目录。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("vision-engine", "视觉引擎", "等待注册视觉工具、OpenCV 运行时与检测管线。", LoadingStageState.Pending));
            _stages.Add(new MutableLoadingStage("workspace", "工作区", "等待创建主窗口、路由与 UI 状态。", LoadingStageState.Pending));
            snapshot = RefreshSnapshotLocked();
        }

        Publish(snapshot);
    }

    public void Show(string message, string detail)
    {
        SystemLoadingSnapshot snapshot;
        lock (_gate)
        {
            _current = _current with
            {
                IsVisible = true,
                Message = message,
                Detail = detail,
                Stages = CreateStageSnapshots()
            };
            snapshot = _current;
        }

        Publish(snapshot);
    }

    public void Hide()
    {
        SystemLoadingSnapshot snapshot;
        lock (_gate)
        {
            _current = _current with
            {
                IsVisible = false,
                Stages = CreateStageSnapshots()
            };
            snapshot = _current;
        }

        Publish(snapshot);
    }

    public void SetStage(string key, string name, string detail, LoadingStageState state)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        SystemLoadingSnapshot snapshot;
        lock (_gate)
        {
            var stage = _stages.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (stage is null)
            {
                _stages.Add(new MutableLoadingStage(key, name, detail, state));
            }
            else
            {
                stage.Name = name;
                stage.Detail = detail;
                stage.State = state;
            }

            if (_current.IsVisible)
            {
                _current = _current with
                {
                    Message = GetCurrentMessage(name, state),
                    Detail = detail
                };
            }

            snapshot = RefreshSnapshotLocked();
        }

        Publish(snapshot);
    }

    private SystemLoadingSnapshot RefreshSnapshotLocked()
    {
        _current = _current with
        {
            Stages = CreateStageSnapshots()
        };
        return _current;
    }

    private IReadOnlyList<SystemLoadingStageSnapshot> CreateStageSnapshots()
    {
        return _stages
            .Select(stage =>
            {
                var (text, brush) = GetStateDisplay(stage.State);
                return new SystemLoadingStageSnapshot(stage.Key, stage.Name, stage.Detail, text, brush);
            })
            .ToArray();
    }

    private static (string Text, string Brush) GetStateDisplay(LoadingStageState state)
    {
        return state switch
        {
            LoadingStageState.Running => ("加载中", "#FF33D6A6"),
            LoadingStageState.Ready => ("成功", "#FF5CE08A"),
            LoadingStageState.Reserved => ("预留", "#FFFFC95A"),
            LoadingStageState.Warning => ("警告", "#FFFFC95A"),
            LoadingStageState.Error => ("失败", "#FFFF667A"),
            _ => ("待加载", "#FF6F7B84")
        };
    }

    private static string GetCurrentMessage(string name, LoadingStageState state)
    {
        return state switch
        {
            LoadingStageState.Running => $"正在加载：{name}",
            LoadingStageState.Ready => $"加载成功：{name}",
            LoadingStageState.Reserved => $"已预留：{name}",
            LoadingStageState.Warning => $"加载警告：{name}",
            LoadingStageState.Error => $"加载失败：{name}",
            _ => $"等待加载：{name}"
        };
    }

    private void Publish(SystemLoadingSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private sealed class MutableLoadingStage
    {
        public MutableLoadingStage(string key, string name, string detail, LoadingStageState state)
        {
            Key = key;
            Name = name;
            Detail = detail;
            State = state;
        }

        public string Key { get; }

        public string Name { get; set; }

        public string Detail { get; set; }

        public LoadingStageState State { get; set; }
    }
}
