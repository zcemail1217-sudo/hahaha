using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class GoogolBusAxisDriver : IAxisDriver
{
    private IReadOnlySet<string> _axisKeys;
    private DeviceSnapshot _snapshot = new("固高总线轴卡", DeviceConnectionState.Disconnected, "固高总线轴卡驱动尚未接入", DateTimeOffset.Now);

    public GoogolBusAxisDriver(DeviceConfiguration configuration)
    {
        _axisKeys = ResolveAxisKeys(configuration);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DriverId => "GoogolBus";

    public AxisCardDriverKind DriverKind => AxisCardDriverKind.GoogolBus;

    public IReadOnlyCollection<string> AxisKeys => _axisKeys.ToArray();

    public DeviceSnapshot Snapshot => _snapshot;

    public bool ContainsAxis(string axisKey)
    {
        return _axisKeys.Contains(axisKey);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented("连接固高总线轴卡");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(DeviceConnectionState.Disconnected, "固高总线轴卡驱动尚未接入，已忽略断开请求");
        return Task.CompletedTask;
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 伺服使能");
        return Task.CompletedTask;
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 伺服关闭");
        return Task.CompletedTask;
    }

    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 清报警");
        return Task.CompletedTask;
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 位置清零");
        return Task.CompletedTask;
    }

    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 回原");
        return Task.CompletedTask;
    }

    public Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{command.AxisKey} 回原");
        return Task.CompletedTask;
    }

    public Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{command.AxisKey} 绝对移动");
        return Task.CompletedTask;
    }

    public Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented("固高总线轴卡直线插补");
        return Task.CompletedTask;
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{command.AxisKey} Jog");
        return Task.CompletedTask;
    }

    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 停止 Jog");
        return Task.CompletedTask;
    }

    public Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 停止");
        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented("固高总线轴卡急停");
        return Task.CompletedTask;
    }

    public Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowNotImplemented($"{axisKey} 状态读取");
        return Task.FromResult(new AxisStatus { AxisKey = axisKey });
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _axisKeys = ResolveAxisKeys(configuration);
        SetState(DeviceConnectionState.Disconnected, $"固高总线轴卡配置已加载，轴数：{_axisKeys.Count}；真实总线 SDK 尚未接入。");
        return Task.CompletedTask;
    }

    private void ThrowNotImplemented(string operation)
    {
        var message = $"{operation}失败：固高总线轴卡驱动尚未接入。需要接入总线卡 SDK/API 后才能使用该轴卡。";
        SetState(DeviceConnectionState.Faulted, message);
        throw new AxisControllerException(message);
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("固高总线轴卡", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private static IReadOnlySet<string> ResolveAxisKeys(DeviceConfiguration configuration)
    {
        var cards = configuration.AxisCards
            .Where(card => card.Driver == AxisCardDriverKind.GoogolBus)
            .ToArray();

        return configuration.Axes
            .Where(axis => axis.Enabled)
            .Where(axis => ResolveAxisCard(axis, cards, configuration.GoogolCardNo) is not null)
            .Where(axis => !string.IsNullOrWhiteSpace(axis.Key))
            .Select(axis => axis.Key.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static AxisCardDefinition? ResolveAxisCard(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        var cardKey = axis.CardKey?.Trim();
        if (!string.IsNullOrWhiteSpace(cardKey))
        {
            var card = cards.FirstOrDefault(
                item => string.Equals(item.Key, cardKey, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                return card;
            }
        }

        var cardNo = axis.CardNo < 0 ? defaultCardNo : axis.CardNo;
        return cards.FirstOrDefault(card => card.CardNo == cardNo);
    }
}
