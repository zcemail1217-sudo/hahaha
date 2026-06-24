using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class SimulatedAxisDriver : IAxisDriver
{
    private readonly SimulatedAxisController _controller;
    private IReadOnlySet<string> _axisKeys;

    public SimulatedAxisDriver(DeviceConfiguration configuration)
    {
        _controller = new SimulatedAxisController(configuration);
        _axisKeys = ResolveAxisKeys(configuration);
        _controller.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DriverId => "Simulated";

    public AxisCardDriverKind DriverKind => AxisCardDriverKind.Simulated;

    public IReadOnlyCollection<string> AxisKeys => _axisKeys.ToArray();

    public DeviceSnapshot Snapshot => _controller.Snapshot;

    public bool ContainsAxis(string axisKey)
    {
        return _axisKeys.Contains(axisKey);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _controller.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _controller.DisconnectAsync(cancellationToken);
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.ServoOnAsync(axisKey, cancellationToken);
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.ServoOffAsync(axisKey, cancellationToken);
    }

    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.ClearAlarmAsync(axisKey, cancellationToken);
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.ZeroPositionAsync(axisKey, cancellationToken);
    }

    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.HomeAsync(axisKey, cancellationToken);
    }

    public Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        return _controller.HomeAsync(command, cancellationToken);
    }

    public Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        return _controller.MoveAbsoluteAsync(command, cancellationToken);
    }

    public Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        return _controller.MoveLinearInterpolationAsync(command, cancellationToken);
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        return _controller.StartJogAsync(command, cancellationToken);
    }

    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return _controller.StopJogAsync(axisKey, cancellationToken);
    }

    public Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default)
    {
        return _controller.StopAsync(axisKey, stopMode, cancellationToken);
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        return _controller.EmergencyStopAsync(cancellationToken);
    }

    public Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default)
    {
        return _controller.GetAxisStatusAsync(axisKey, cancellationToken);
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _axisKeys = ResolveAxisKeys(configuration);
        return _controller.ApplyConfigurationAsync(configuration, cancellationToken);
    }

    private static IReadOnlySet<string> ResolveAxisKeys(DeviceConfiguration configuration)
    {
        var axisKeys = configuration.Axes
            .Where(axis => axis.Enabled)
            .Where(axis => !string.IsNullOrWhiteSpace(axis.Key))
            .Select(axis => axis.Key.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (axisKeys.Count == 0)
        {
            axisKeys.Add(AxisDefaults.PrimaryAxisKey);
        }

        return axisKeys;
    }
}
