using VisionStation.Domain;

namespace VisionStation.Devices;

public static class MotionControllerFactory
{
    public static MotionControllerSet Create(DeviceConfiguration configuration)
    {
        if (configuration.AxisController != AxisControllerKind.Googol)
        {
            return new MotionControllerSet(
                new AxisManager([new SimulatedAxisDriver(configuration)]),
                new SimulatedDigitalIoController(configuration));
        }

        var advantechPci1245 = HasAdvantechPci1245Cards(configuration)
            ? new AdvantechPci1245Controller(configuration)
            : null;

        return new MotionControllerSet(
            new AxisManager(CreateHardwareAxisDrivers(configuration, advantechPci1245)),
            new DigitalIoManager(CreateHardwareIoDrivers(configuration, advantechPci1245)));
    }

    private static GoogolAxisControllerOptions CreateGoogolOptions(DeviceConfiguration configuration)
    {
        var cards = ResolveGoogolPulseAxisCards(configuration);
        var axes = ResolveGoogolPulseAxes(configuration, cards).ToArray();
        return new GoogolAxisControllerOptions
        {
            CardNo = configuration.GoogolCardNo,
            AxisCount = axes.Length == 0 ? 1 : axes.Max(axis => axis.AxisNo),
            ConfigPath = configuration.GoogolConfigPath,
            Cards = CreateGoogolPulseCardOptions(cards).ToArray(),
            Axes = axes.Select(axis => new GoogolAxisDefinition
            {
                AxisKey = axis.Key,
                CardNo = ResolveAxisCardNo(axis, cards, configuration.GoogolCardNo),
                AxisNo = axis.AxisNo,
                PulsesPerUnit = axis.PulsesPerUnit,
                PositionBand = axis.PositionBand <= 0 ? 0.01 : axis.PositionBand,
                HomeMode = ResolveHomeMode(axis.HomeMode),
                HomePositive = axis.HomePositive,
                HomeOffset = axis.HomeOffset,
                EscapeDistance = axis.EscapeDistance,
                HomeHighSpeed = Math.Max(axis.DefaultSpeed, 1),
                HomeLowSpeed = Math.Max(axis.DefaultSpeed / 4, 1),
                HomeAcceleration = Math.Max(axis.DefaultAcceleration, 1),
                HomeDeceleration = Math.Max(axis.DefaultAcceleration, 1)
            }).ToArray()
        };
    }

    private static IReadOnlyList<IAxisDriver> CreateHardwareAxisDrivers(
        DeviceConfiguration configuration,
        AdvantechPci1245Controller? advantechPci1245)
    {
        var drivers = new List<IAxisDriver>();
        if (HasGoogolPulseCards(configuration))
        {
            drivers.Add(new GoogolPulseAxisDriver(CreateGoogolOptions(configuration)));
        }

        if (HasGoogolBusCards(configuration))
        {
            drivers.Add(new GoogolBusAxisDriver(configuration));
        }

        if (advantechPci1245 is not null)
        {
            drivers.Add(advantechPci1245);
        }

        return drivers;
    }

    private static IReadOnlyList<IDigitalIoDriver> CreateHardwareIoDrivers(
        DeviceConfiguration configuration,
        AdvantechPci1245Controller? advantechPci1245)
    {
        var drivers = new List<IDigitalIoDriver>();
        var googolModules = ResolveGoogolExtendedIoModules(configuration).ToArray();
        var googolPoints = ResolveGoogolIoPoints(configuration).ToArray();
        if (googolModules.Length != 0 || googolPoints.Length != 0)
        {
            drivers.Add(new GoogolDigitalIoController(new GoogolDigitalIoControllerOptions
            {
                CardNo = configuration.GoogolCardNo,
                Modules = googolModules,
                Points = googolPoints,
                OpenCardOnConnect = false
            }));
        }

        if (advantechPci1245 is not null && advantechPci1245.PointKeys.Count != 0)
        {
            drivers.Add(advantechPci1245);
        }

        return drivers;
    }

    private static IEnumerable<GoogolCardOptions> CreateGoogolPulseCardOptions(IEnumerable<AxisCardDefinition> cards)
    {
        return cards.Select(card => new GoogolCardOptions
        {
            CardNo = card.CardNo,
            AxisCount = card.AxisCount,
            ConfigPath = card.ConfigPath,
            Description = string.IsNullOrWhiteSpace(card.Description) ? card.Name : card.Description
        });
    }

    private static IEnumerable<AxisPointDefinition> ResolveGoogolPulseAxes(DeviceConfiguration configuration)
    {
        return ResolveGoogolPulseAxes(configuration, ResolveGoogolPulseAxisCards(configuration));
    }

    private static IEnumerable<AxisPointDefinition> ResolveGoogolPulseAxes(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
        return configuration.Axes
            .Where(axis => axis.Enabled)
            .Where(axis => ResolveAxisCard(axis, cards, configuration.GoogolCardNo) is not null);
    }

    private static bool HasGoogolPulseCards(DeviceConfiguration configuration)
    {
        return ResolveGoogolPulseAxisCards(configuration).Count != 0;
    }

    private static IReadOnlyList<AxisCardDefinition> ResolveGoogolPulseAxisCards(DeviceConfiguration configuration)
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
                    Name = $"Googol pulse card {configuration.GoogolCardNo}",
                    Driver = AxisCardDriverKind.GoogolPulse,
                    Vendor = "Googol",
                    CardNo = configuration.GoogolCardNo,
                    AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
                    ConfigPath = configuration.GoogolConfigPath
                }
            ];
        }

        return configuration.GoogolCards
            .Select(card => new AxisCardDefinition
            {
                Key = card.Key,
                Name = string.IsNullOrWhiteSpace(card.Description)
                    ? $"Googol pulse card {card.CardNo}"
                    : card.Description,
                Driver = AxisCardDriverKind.GoogolPulse,
                Vendor = "Googol",
                CardNo = card.CardNo,
                AxisCount = card.AxisCount,
                InputCount = card.InputCount,
                OutputCount = card.OutputCount,
                ConfigPath = card.ConfigPath,
                Description = card.Description
            })
            .ToArray();
    }

    private static bool HasGoogolBusCards(DeviceConfiguration configuration)
    {
        return configuration.AxisCards.Any(card => card.Driver == AxisCardDriverKind.GoogolBus);
    }

    private static bool HasAdvantechPci1245Cards(DeviceConfiguration configuration)
    {
        return configuration.AxisCards.Any(card => card.Driver == AxisCardDriverKind.AdvantechPci1245);
    }

    private static IEnumerable<ExtendedIoModuleDefinition> ResolveGoogolExtendedIoModules(DeviceConfiguration configuration)
    {
        var cards = ResolveGoogolPulseAxisCards(configuration);
        return configuration.ExtendedIoModules
            .Where(module => ResolveCard(module.ParentCardKey, module.ParentCardNo, cards, configuration.GoogolCardNo) is not null);
    }

    private static IEnumerable<IoPointDefinition> ResolveGoogolIoPoints(DeviceConfiguration configuration)
    {
        var cards = ResolveGoogolPulseAxisCards(configuration);
        return configuration.IoPoints
            .Where(point => point.Enabled)
            .Where(point => point.Source != IoPointSource.AxisOnboard)
            .Where(point =>
            {
                var cardKey = point.Source == IoPointSource.ExtendedModule
                    ? FirstNonEmpty(point.ParentCardKey, point.CardKey)
                    : point.CardKey;
                var cardNo = point.Source == IoPointSource.ExtendedModule
                    ? point.ParentCardNo < 0 ? point.CardNo : point.ParentCardNo
                    : point.CardNo;
                return ResolveCard(cardKey, cardNo, cards, configuration.GoogolCardNo) is not null;
            });
    }

    private static short ResolveAxisCardNo(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        return ResolveAxisCard(axis, cards, defaultCardNo)?.CardNo
            ?? (axis.CardNo < 0 ? defaultCardNo : axis.CardNo);
    }

    private static AxisCardDefinition? ResolveAxisCard(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        return ResolveCard(axis.CardKey, axis.CardNo, cards, defaultCardNo);
    }

    private static AxisCardDefinition? ResolveCard(
        string? cardKey,
        short cardNo,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        cardKey = cardKey?.Trim();
        if (!string.IsNullOrWhiteSpace(cardKey))
        {
            var card = cards.FirstOrDefault(
                item => string.Equals(item.Key, cardKey, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                return card;
            }
        }

        var resolvedCardNo = cardNo < 0 ? defaultCardNo : cardNo;
        return cards.FirstOrDefault(card => card.CardNo == resolvedCardNo);
    }

    private static AxisHomeMode ResolveHomeMode(string homeMode)
    {
        return Enum.TryParse<AxisHomeMode>(homeMode, true, out var mode)
            ? mode
            : AxisHomeMode.LimitHomeIndex;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}

public sealed record MotionControllerSet(IAxisController Axis, IDigitalIoController DigitalIo);
