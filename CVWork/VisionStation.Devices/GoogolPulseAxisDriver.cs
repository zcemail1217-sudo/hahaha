using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class GoogolPulseAxisDriver : IAxisDriver
{
    private readonly GoogolAxisController _controller;
    private IReadOnlySet<string> _axisKeys;

    public GoogolPulseAxisDriver(GoogolAxisControllerOptions options)
    {
        _controller = new GoogolAxisController(options);
        _axisKeys = ResolveAxisKeys(options);
        _controller.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DriverId => "GoogolPulse";

    public AxisCardDriverKind DriverKind => AxisCardDriverKind.GoogolPulse;

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

    private static IReadOnlySet<string> ResolveAxisKeys(GoogolAxisControllerOptions options)
    {
        var axisKeys = options.Axes
            .Where(axis => !string.IsNullOrWhiteSpace(axis.AxisKey))
            .Select(axis => axis.AxisKey.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (axisKeys.Count == 0)
        {
            axisKeys.Add(AxisDefaults.PrimaryAxisKey);
        }

        return axisKeys;
    }

    private static IReadOnlySet<string> ResolveAxisKeys(DeviceConfiguration configuration)
    {
        var cards = ResolvePulseCards(configuration);
        var axisKeys = configuration.Axes
            .Where(axis => axis.Enabled)
            .Where(axis => ResolveAxisCard(axis, cards, configuration.GoogolCardNo) is not null)
            .Where(axis => !string.IsNullOrWhiteSpace(axis.Key))
            .Select(axis => axis.Key.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configuration.AxisCards.Count == 0 && axisKeys.Count == 0)
        {
            axisKeys.Add(AxisDefaults.PrimaryAxisKey);
        }

        return axisKeys;
    }

    private static IReadOnlyList<AxisCardDefinition> ResolvePulseCards(DeviceConfiguration configuration)
    {
        if (configuration.AxisCards.Count != 0)
        {
            return configuration.AxisCards
                .Where(card => card.Driver == AxisCardDriverKind.GoogolPulse)
                .ToArray();
        }

        if (configuration.GoogolCards.Count == 0)
        {
            return
            [
                new AxisCardDefinition
                {
                    Key = "card1",
                    Driver = AxisCardDriverKind.GoogolPulse,
                    CardNo = configuration.GoogolCardNo
                }
            ];
        }

        return configuration.GoogolCards
            .Select(card => new AxisCardDefinition
            {
                Key = card.Key,
                Driver = AxisCardDriverKind.GoogolPulse,
                CardNo = card.CardNo
            })
            .ToArray();
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
