using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class JsonDeviceConfigurationRepository : IDeviceConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RuntimePaths _paths;

    public JsonDeviceConfigurationRepository(RuntimePaths paths)
    {
        _paths = paths;
    }

    public event EventHandler<DeviceConfiguration>? ConfigurationSaved;

    public DeviceConfiguration Get()
    {
        if (!File.Exists(_paths.DeviceConfigPath))
        {
            var created = CreateDefault();
            Save(created);
            return created;
        }

        using var stream = File.OpenRead(_paths.DeviceConfigPath);
        var configuration = JsonSerializer.Deserialize<DeviceConfiguration>(stream, JsonOptions);
        if (configuration is not null)
        {
            return Normalize(configuration);
        }

        configuration = CreateDefault();
        Save(configuration);
        return configuration;
    }

    public async Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DeviceConfigPath))
        {
            var created = CreateDefault();
            await SaveAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        await using var stream = File.OpenRead(_paths.DeviceConfigPath);
        var configuration = await JsonSerializer.DeserializeAsync<DeviceConfiguration>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null)
        {
            configuration = CreateDefault();
            await SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        return Normalize(configuration);
    }

    public void Save(DeviceConfiguration configuration)
    {
        var normalized = Normalize(configuration);
        using var stream = File.Create(_paths.DeviceConfigPath);
        JsonSerializer.Serialize(stream, normalized, JsonOptions);
        ConfigurationSaved?.Invoke(this, normalized);
    }

    public async Task SaveAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(configuration);
        await using var stream = File.Create(_paths.DeviceConfigPath);
        await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
        ConfigurationSaved?.Invoke(this, normalized);
    }

    private DeviceConfiguration Normalize(DeviceConfiguration configuration)
    {
        var axisCards = NormalizeAxisCards(configuration);
        var cards = NormalizeGoogolCards(configuration, axisCards);
        var legacySingleModuleParentCards = GetLegacySingleModuleParentCards(configuration);
        var modules = NormalizeExtendedIoModules(configuration, axisCards, GetDefaultExtendedIoConfigPath, legacySingleModuleParentCards);
        var devices = NormalizeDevices(configuration);

        var axes = configuration.Axes
            .Where(axis => !string.IsNullOrWhiteSpace(axis.Key))
            .GroupBy(axis => axis.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => NormalizeAxis(group.Key, group.First(), axisCards, configuration.GoogolCardNo))
            .OrderBy(axis => axis.CardKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(axis => axis.CardNo)
            .ThenBy(axis => axis.AxisNo)
            .ThenBy(axis => axis.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (axes.Length == 0)
        {
            axes = CreateDefault().Axes.ToArray();
        }

        var ioPoints = configuration.IoPoints
            .Where(point => !string.IsNullOrWhiteSpace(point.Key))
            .GroupBy(point => point.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => NormalizeIoPoint(group.Key, group.First(), axisCards, legacySingleModuleParentCards, configuration.GoogolCardNo, GetDefaultExtendedIoConfigPath))
            .OrderBy(point => point.Direction)
            .ThenBy(point => point.Source)
            .ThenBy(point => point.ParentCardKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.CardKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.CardNo)
            .ThenBy(point => point.ModuleNo)
            .ThenBy(point => point.PointNo)
            .ThenBy(point => point.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ioPoints.Length == 0)
        {
            ioPoints = CreateDefault().IoPoints.ToArray();
        }

        return configuration with
        {
            AxisController = configuration.AxisController,
            AxisCards = axisCards,
            GoogolCards = cards,
            ExtendedIoModules = modules,
            Axes = axes,
            IoPoints = ioPoints,
            Devices = devices,
            Debug = NormalizeDebugConfiguration(configuration.Debug, devices),
            SystemSettings = NormalizeSystemSettings(configuration.SystemSettings)
        };
    }

    private static DeviceDebugConfiguration NormalizeDebugConfiguration(
        DeviceDebugConfiguration? debug,
        IReadOnlyList<DeviceDefinition> devices)
    {
        var defaults = CreateDefault().Debug;
        debug ??= defaults;
        var selectedDeviceKey = debug.SelectedDeviceKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedDeviceKey) ||
            devices.All(device => !string.Equals(device.Key, selectedDeviceKey, StringComparison.OrdinalIgnoreCase)))
        {
            selectedDeviceKey = ResolveDefaultDebugDeviceKey(debug.WorkbenchKind, devices);
        }

        return debug with
        {
            SelectedDeviceKey = selectedDeviceKey
        };
    }

    private static string ResolveDefaultDebugDeviceKey(
        DeviceDebugWorkbenchKind workbenchKind,
        IReadOnlyList<DeviceDefinition> devices)
    {
        var preferredKind = workbenchKind switch
        {
            DeviceDebugWorkbenchKind.AxisCard => DeviceKind.Motion,
            DeviceDebugWorkbenchKind.Plc => DeviceKind.Plc,
            DeviceDebugWorkbenchKind.Modbus or DeviceDebugWorkbenchKind.Serial => DeviceKind.Instrument,
            _ => DeviceKind.Other
        };

        return devices.FirstOrDefault(device => device.Kind == preferredKind)?.Key
            ?? devices.FirstOrDefault()?.Key
            ?? CreateDefault().Debug.SelectedDeviceKey;
    }

    private static DeviceDefinition[] NormalizeDevices(DeviceConfiguration configuration)
    {
        var devices = configuration.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Key))
            .GroupBy(device => device.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => NormalizeDevice(group.Key, group.First()))
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return devices.Length == 0
            ? CreateDefault().Devices.ToArray()
            : devices;
    }

    private static DeviceDefinition NormalizeDevice(string key, DeviceDefinition device)
    {
        return device with
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(device.Name) ? key : device.Name.Trim(),
            Driver = string.IsNullOrWhiteSpace(device.Driver) ? device.Kind.ToString() : device.Driver.Trim(),
            Connection = NormalizeConnection(device.Connection),
            Options = NormalizeOptions(device.Options),
            Description = device.Description?.Trim() ?? string.Empty
        };
    }

    private static DeviceConnectionDefinition NormalizeConnection(DeviceConnectionDefinition? connection)
    {
        connection ??= new DeviceConnectionDefinition();
        return connection with
        {
            IpAddress = connection.IpAddress?.Trim() ?? string.Empty,
            Port = Math.Max(0, connection.Port),
            SerialPort = connection.SerialPort?.Trim() ?? string.Empty,
            BaudRate = connection.BaudRate <= 0 ? 9600 : connection.BaudRate,
            StationNo = connection.StationNo?.Trim() ?? string.Empty,
            Resource = connection.Resource?.Trim() ?? string.Empty
        };
    }

    private static AxisCardDefinition[] NormalizeAxisCards(DeviceConfiguration configuration)
    {
        var cards = configuration.AxisCards
            .Where(card => !string.IsNullOrWhiteSpace(card.Key))
            .GroupBy(card => card.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Key = group.Key,
                Name = string.IsNullOrWhiteSpace(group.First().Name) ? group.Key : group.First().Name.Trim(),
                Vendor = string.IsNullOrWhiteSpace(group.First().Vendor)
                    ? ResolveDefaultVendor(group.First().Driver)
                    : group.First().Vendor.Trim(),
                Model = group.First().Model?.Trim() ?? string.Empty,
                CardNo = group.First().CardNo < 0 ? (short)0 : group.First().CardNo,
                AxisCount = group.First().AxisCount <= 0 ? 8 : group.First().AxisCount,
                InputCount = group.First().InputCount < 0 ? 0 : group.First().InputCount,
                OutputCount = group.First().OutputCount < 0 ? 0 : group.First().OutputCount,
                ConfigPath = group.First().ConfigPath?.Trim() ?? string.Empty,
                Connection = group.First().Connection?.Trim() ?? string.Empty,
                Options = NormalizeOptions(group.First().Options),
                Description = group.First().Description?.Trim() ?? string.Empty,
            })
            .OrderBy(card => card.CardNo)
            .ThenBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cards.Length != 0)
        {
            return cards;
        }

        var legacyCards = configuration.GoogolCards
            .Where(card => !string.IsNullOrWhiteSpace(card.Key))
            .GroupBy(card => card.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AxisCardDefinition
            {
                Key = group.Key,
                Name = string.IsNullOrWhiteSpace(group.First().Description)
                    ? $"Googol pulse card {group.First().CardNo}"
                    : group.First().Description.Trim(),
                Driver = AxisCardDriverKind.GoogolPulse,
                Vendor = "Googol",
                CardNo = group.First().CardNo < 0 ? (short)0 : group.First().CardNo,
                AxisCount = group.First().AxisCount <= 0 ? 8 : group.First().AxisCount,
                InputCount = group.First().InputCount < 0 ? 0 : group.First().InputCount,
                OutputCount = group.First().OutputCount < 0 ? 0 : group.First().OutputCount,
                ConfigPath = group.First().ConfigPath?.Trim() ?? string.Empty,
                Description = group.First().Description?.Trim() ?? string.Empty
            })
            .OrderBy(card => card.CardNo)
            .ThenBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (legacyCards.Length != 0)
        {
            return legacyCards;
        }

        return
        [
            new AxisCardDefinition
            {
                Key = "card1",
                Name = "Googol pulse card 0",
                Driver = AxisCardDriverKind.GoogolPulse,
                Vendor = "Googol",
                CardNo = configuration.GoogolCardNo,
                AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
                InputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Input),
                OutputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Output),
                ConfigPath = configuration.GoogolConfigPath?.Trim() ?? string.Empty,
                Description = "Default Googol pulse card"
            }
        ];
    }

    private static GoogolCardDefinition[] NormalizeGoogolCards(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> axisCards)
    {
        var cards = axisCards
            .Where(card => card.Driver == AxisCardDriverKind.GoogolPulse)
            .Select(card => new GoogolCardDefinition
            {
                Key = card.Key,
                CardNo = card.CardNo < 0 ? (short)0 : card.CardNo,
                AxisCount = card.AxisCount <= 0 ? 8 : card.AxisCount,
                InputCount = card.InputCount < 0 ? 0 : card.InputCount,
                OutputCount = card.OutputCount < 0 ? 0 : card.OutputCount,
                Description = card.Description?.Trim() ?? card.Name?.Trim() ?? string.Empty,
                ConfigPath = card.ConfigPath?.Trim() ?? string.Empty
            })
            .OrderBy(card => card.CardNo)
            .ThenBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cards.Length != 0)
        {
            return cards;
        }

        return
        [
            new GoogolCardDefinition
            {
                Key = "card1",
                CardNo = configuration.GoogolCardNo,
                AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
                InputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Input),
                OutputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Output),
                Description = "Default Googol pulse card",
                ConfigPath = configuration.GoogolConfigPath?.Trim() ?? string.Empty
            }
        ];
    }

    private static AxisPointDefinition NormalizeAxis(
        string key,
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> axisCards,
        short defaultCardNo)
    {
        var card = ResolveAxisCard(axis, axisCards, defaultCardNo);
        var cardNo = card?.CardNo ?? (axis.CardNo < 0 ? defaultCardNo : axis.CardNo);

        return axis with
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(axis.Name) ? key : axis.Name.Trim(),
            CardKey = card?.Key ?? axis.CardKey?.Trim() ?? string.Empty,
            CardNo = cardNo < 0 ? (short)0 : cardNo,
            AxisNo = axis.AxisNo <= 0 ? (short)1 : axis.AxisNo,
            PulsesPerUnit = axis.PulsesPerUnit <= 0 ? 1 : axis.PulsesPerUnit,
            PositionBand = axis.PositionBand <= 0 ? 0.01 : axis.PositionBand,
            DefaultSpeed = axis.DefaultSpeed <= 0 ? 80 : axis.DefaultSpeed,
            DefaultAcceleration = axis.DefaultAcceleration <= 0 ? 120 : axis.DefaultAcceleration
        };
    }

    private static AxisCardDefinition? ResolveAxisCard(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> axisCards,
        short defaultCardNo)
    {
        return ResolveCard(axis.CardKey, axis.CardNo, axisCards, defaultCardNo);
    }

    private static IoPointDefinition NormalizeIoPoint(
        string key,
        IoPointDefinition point,
        IReadOnlyList<AxisCardDefinition> axisCards,
        IReadOnlySet<short> legacySingleModuleParentCards,
        short defaultCardNo,
        Func<short, string> getDefaultConfigPath)
    {
        var card = ResolveCard(point.CardKey, point.CardNo, axisCards, defaultCardNo);
        var cardNo = card?.CardNo ?? (point.CardNo < 0 ? defaultCardNo : point.CardNo);
        var parentCard = point.Source == IoPointSource.ExtendedModule
            ? ResolveCard(point.ParentCardKey, point.ParentCardNo < 0 ? cardNo : point.ParentCardNo, axisCards, defaultCardNo)
            : null;
        var parentCardNo = parentCard?.CardNo ?? (point.ParentCardNo < 0 ? cardNo : point.ParentCardNo);
        var moduleNo = point.Source == IoPointSource.ExtendedModule
            ? legacySingleModuleParentCards.Contains(parentCardNo) ? (short)0 : point.ModuleNo < 0 ? (short)0 : point.ModuleNo
            : point.ModuleNo;
        var axisNo = point.Source == IoPointSource.AxisOnboard
            ? point.AxisNo <= 0 ? (short)1 : point.AxisNo
            : (short)-1;

        return point with
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(point.Name) ? key : point.Name.Trim(),
            Address = point.Address?.Trim() ?? string.Empty,
            CardKey = point.Source == IoPointSource.ExtendedModule
                ? parentCard?.Key ?? point.ParentCardKey?.Trim() ?? point.CardKey?.Trim() ?? string.Empty
                : card?.Key ?? point.CardKey?.Trim() ?? string.Empty,
            CardNo = point.Source == IoPointSource.ExtendedModule ? parentCardNo : cardNo < 0 ? (short)0 : cardNo,
            ParentCardKey = point.Source == IoPointSource.ExtendedModule
                ? parentCard?.Key ?? point.ParentCardKey?.Trim() ?? string.Empty
                : string.Empty,
            ParentCardNo = point.Source == IoPointSource.ExtendedModule ? parentCardNo : (short)-1,
            ModuleNo = point.Source == IoPointSource.ExtendedModule ? moduleNo : (short)-1,
            AxisNo = axisNo,
            ModuleConfigPath = point.Source == IoPointSource.ExtendedModule
                ? string.IsNullOrWhiteSpace(point.ModuleConfigPath) ? getDefaultConfigPath(parentCardNo) : point.ModuleConfigPath.Trim()
                : string.Empty,
            PointNo = point.PointNo <= 0 ? (short)1 : point.PointNo
        };
    }

    private static AxisCardDefinition? ResolveCard(
        string? cardKey,
        short cardNo,
        IReadOnlyList<AxisCardDefinition> axisCards,
        short defaultCardNo)
    {
        cardKey = cardKey?.Trim();
        if (!string.IsNullOrWhiteSpace(cardKey))
        {
            var card = axisCards.FirstOrDefault(
                item => string.Equals(item.Key, cardKey, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                return card;
            }
        }

        var resolvedCardNo = cardNo < 0 ? defaultCardNo : cardNo;
        return axisCards.FirstOrDefault(card => card.CardNo == resolvedCardNo) ?? axisCards.FirstOrDefault();
    }

    private static string ResolveDefaultVendor(AxisCardDriverKind driver)
    {
        return driver switch
        {
            AxisCardDriverKind.GoogolPulse or AxisCardDriverKind.GoogolBus => "Googol",
            AxisCardDriverKind.AdvantechPci1245 => "Advantech",
            AxisCardDriverKind.Simulated => "Simulated",
            _ => string.Empty
        };
    }

    private static ExtendedIoModuleDefinition[] NormalizeExtendedIoModules(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> axisCards,
        Func<short, string> getDefaultConfigPath,
        IReadOnlySet<short> legacySingleModuleParentCards)
    {
        var modules = configuration.ExtendedIoModules
            .Where(module => !string.IsNullOrWhiteSpace(module.Key))
            .GroupBy(module => module.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => NormalizeExtendedIoModule(group.Key, group.First(), axisCards, configuration.GoogolCardNo, getDefaultConfigPath))
            .OrderBy(module => module.ParentCardKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.ParentCardNo)
            .ThenBy(module => module.ModuleNo)
            .ThenBy(module => module.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (modules.Length != 0)
        {
            return modules;
        }

        return configuration.IoPoints
            .Where(point => point.Source == IoPointSource.ExtendedModule)
            .Select(point =>
            {
                var fallbackCardNo = point.CardNo < 0 ? configuration.GoogolCardNo : point.CardNo;
                var parentCardNo = point.ParentCardNo < 0 ? fallbackCardNo : point.ParentCardNo;
                var parentCard = ResolveCard(point.ParentCardKey, parentCardNo, axisCards, configuration.GoogolCardNo);
                parentCardNo = parentCard?.CardNo ?? parentCardNo;
                return (
                    ParentCardKey: parentCard?.Key ?? point.ParentCardKey?.Trim() ?? string.Empty,
                    ParentCardNo: parentCardNo,
                    ModuleNo: legacySingleModuleParentCards.Contains(parentCardNo) ? (short)0 : point.ModuleNo < 0 ? (short)0 : point.ModuleNo,
                    Direction: point.Direction,
                    ConfigPath: point.ModuleConfigPath?.Trim() ?? string.Empty);
            })
            .GroupBy(point => (
                point.ParentCardKey,
                point.ParentCardNo,
                point.ModuleNo,
                point.ConfigPath))
            .Select((group, index) => new ExtendedIoModuleDefinition
            {
                Key = $"ext{index + 1}",
                ParentCardKey = group.Key.ParentCardKey,
                ParentCardNo = group.Key.ParentCardNo,
                ModuleNo = group.Key.ModuleNo,
                Model = Hcb2ModuleCatalog.DefaultModel,
                ModuleType = 3,
                StartAddress = group.Key.ModuleNo,
                InputCount = group.Count(point => point.Direction == IoPointDirection.Input),
                OutputCount = group.Count(point => point.Direction == IoPointDirection.Output),
                ConfigPath = string.IsNullOrWhiteSpace(group.Key.ConfigPath)
                    ? getDefaultConfigPath(group.Key.ParentCardNo)
                    : group.Key.ConfigPath,
                Description = $"Extended IO module {group.Key.ModuleNo}"
            })
            .OrderBy(module => module.ParentCardKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.ParentCardNo)
            .ThenBy(module => module.ModuleNo)
            .ToArray();
    }

    private static ExtendedIoModuleDefinition NormalizeExtendedIoModule(
        string key,
        ExtendedIoModuleDefinition module,
        IReadOnlyList<AxisCardDefinition> axisCards,
        short defaultParentCardNo,
        Func<short, string> getDefaultConfigPath)
    {
        var parentCard = ResolveCard(module.ParentCardKey, module.ParentCardNo, axisCards, defaultParentCardNo);
        var parentCardNo = parentCard?.CardNo ?? (module.ParentCardNo < 0 ? defaultParentCardNo : module.ParentCardNo);
        var profile = Hcb2ModuleCatalog.Resolve(module.Model);
        var startAddress = module.StartAddress < 0 ? module.ModuleNo : module.StartAddress;
        startAddress = (short)Math.Clamp(startAddress, (short)0, (short)15);
        var inputCount = module.InputCount <= 0 || module.InputCount == 16 && profile.InputCount != 16
            ? profile.InputCount
            : module.InputCount;
        var outputCount = module.OutputCount < 0 || module.OutputCount == 16 && profile.OutputCount != 16
            ? profile.OutputCount
            : module.OutputCount;

        return module with
        {
            Key = key,
            ParentCardKey = parentCard?.Key ?? module.ParentCardKey?.Trim() ?? string.Empty,
            ParentCardNo = parentCardNo,
            ModuleNo = module.ModuleNo < 0 ? (short)0 : module.ModuleNo,
            Model = profile.Model,
            ModuleType = module.ModuleType <= 0 ? profile.ModuleType : module.ModuleType,
            StartAddress = startAddress,
            WorkMode = module.WorkMode <= 0 ? (short)0 : (short)1,
            InputCount = Math.Max(0, inputCount),
            OutputCount = Math.Max(0, outputCount),
            AdChannels = module.AdChannels <= 0 ? profile.AdChannels : module.AdChannels,
            AdMaxVoltage = module.AdMaxVoltage == 0 ? profile.AdMaxVoltage : module.AdMaxVoltage,
            AdMinVoltage = module.AdMinVoltage == 0 ? profile.AdMinVoltage : module.AdMinVoltage,
            DaChannels = module.DaChannels <= 0 ? profile.DaChannels : module.DaChannels,
            DaMaxVoltage = module.DaMaxVoltage == 0 ? profile.DaMaxVoltage : module.DaMaxVoltage,
            DaMinVoltage = module.DaMinVoltage == 0 ? profile.DaMinVoltage : module.DaMinVoltage,
            ConfigPath = string.IsNullOrWhiteSpace(module.ConfigPath)
                ? getDefaultConfigPath(parentCardNo)
                : module.ConfigPath.Trim(),
            Description = module.Description?.Trim() ?? string.Empty
        };
    }

    private static IReadOnlySet<short> GetLegacySingleModuleParentCards(DeviceConfiguration configuration)
    {
        if (configuration.ExtendedIoModules.Count != 0)
        {
            return new HashSet<short>();
        }

        return configuration.IoPoints
            .Where(point => point.Source == IoPointSource.ExtendedModule)
            .GroupBy(point => point.ParentCardNo < 0 ? configuration.GoogolCardNo : point.ParentCardNo)
            .Where(group => group
                .Select(point => point.ModuleNo < 0 ? (short)0 : point.ModuleNo)
                .Distinct()
                .Count() == 1)
            .Select(group => group.Key)
            .ToHashSet();
    }

    private string GetDefaultExtendedIoConfigPath(short parentCardNo)
    {
        return Path.Combine(_paths.ConfigDirectory, $"hcb2_extmdl_card{parentCardNo}.cfg");
    }

    private static SystemSettingsConfiguration NormalizeSystemSettings(SystemSettingsConfiguration? settings)
    {
        settings ??= new SystemSettingsConfiguration();
        var defaults = CreateDefault().SystemSettings;

        var mes = settings.Mes ?? defaults.Mes;
        mes = mes with
        {
            Endpoint = string.IsNullOrWhiteSpace(mes.Endpoint) ? defaults.Mes.Endpoint : mes.Endpoint.Trim(),
            LineCode = string.IsNullOrWhiteSpace(mes.LineCode) ? defaults.Mes.LineCode : mes.LineCode.Trim(),
            StationCode = string.IsNullOrWhiteSpace(mes.StationCode) ? defaults.Mes.StationCode : mes.StationCode.Trim(),
            EquipmentCode = string.IsNullOrWhiteSpace(mes.EquipmentCode) ? defaults.Mes.EquipmentCode : mes.EquipmentCode.Trim(),
            ProcessCode = string.IsNullOrWhiteSpace(mes.ProcessCode) ? defaults.Mes.ProcessCode : mes.ProcessCode.Trim(),
            ProductCode = mes.ProductCode?.Trim() ?? string.Empty,
            UploadMode = string.IsNullOrWhiteSpace(mes.UploadMode) ? defaults.Mes.UploadMode : mes.UploadMode.Trim(),
            ApiToken = mes.ApiToken?.Trim() ?? string.Empty
        };

        var plc = settings.Plc ?? defaults.Plc;
        plc = plc with
        {
            Protocol = string.IsNullOrWhiteSpace(plc.Protocol) ? defaults.Plc.Protocol : plc.Protocol.Trim(),
            IpAddress = string.IsNullOrWhiteSpace(plc.IpAddress) ? defaults.Plc.IpAddress : plc.IpAddress.Trim(),
            Port = plc.Port <= 0 ? defaults.Plc.Port : plc.Port,
            StationNo = plc.StationNo <= 0 ? defaults.Plc.StationNo : plc.StationNo,
            Model = plc.Model?.Trim() ?? string.Empty,
            ConnectTimeoutMs = plc.ConnectTimeoutMs <= 0 ? defaults.Plc.ConnectTimeoutMs : plc.ConnectTimeoutMs,
            HeartbeatIntervalMs = plc.HeartbeatIntervalMs <= 0 ? defaults.Plc.HeartbeatIntervalMs : plc.HeartbeatIntervalMs,
            HeartbeatAddress = string.IsNullOrWhiteSpace(plc.HeartbeatAddress) ? defaults.Plc.HeartbeatAddress : plc.HeartbeatAddress.Trim(),
            ResultAddress = string.IsNullOrWhiteSpace(plc.ResultAddress) ? defaults.Plc.ResultAddress : plc.ResultAddress.Trim(),
            Options = NormalizeOptions(plc.Options)
        };

        var production = settings.Production ?? defaults.Production;
        production = production with
        {
            CycleDelayMs = production.CycleDelayMs <= 0 ? defaults.Production.CycleDelayMs : production.CycleDelayMs,
            MaxConsecutiveFailures = production.MaxConsecutiveFailures <= 0
                ? defaults.Production.MaxConsecutiveFailures
                : production.MaxConsecutiveFailures,
            CleanupTimeoutMs = production.CleanupTimeoutMs <= 0 ? defaults.Production.CleanupTimeoutMs : production.CleanupTimeoutMs,
            StopWaitTimeoutMs = production.StopWaitTimeoutMs <= 0
                ? defaults.Production.StopWaitTimeoutMs
                : production.StopWaitTimeoutMs
        };

        var logging = settings.Logging ?? defaults.Logging;
        logging = logging with
        {
            RetentionDays = logging.RetentionDays <= 0 ? defaults.Logging.RetentionDays : logging.RetentionDays,
            MaxRecentEntries = logging.MaxRecentEntries <= 0 ? defaults.Logging.MaxRecentEntries : logging.MaxRecentEntries
        };

        var communication = settings.Communication ?? defaults.Communication;
        communication = communication with
        {
            TcpChannels = NormalizeTcpChannels(communication.TcpChannels, defaults.Communication.TcpChannels),
            SerialChannels = NormalizeSerialChannels(communication.SerialChannels, defaults.Communication.SerialChannels)
        };

        var parameters = settings.Parameters ?? defaults.Parameters;
        var rawItems = parameters.Items ?? defaults.Parameters.Items;
        var items = rawItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Key = group.Key,
                Name = string.IsNullOrWhiteSpace(group.First().Name) ? group.Key : group.First().Name.Trim(),
                Value = group.First().Value?.Trim() ?? string.Empty,
                Unit = group.First().Unit?.Trim() ?? string.Empty,
                Description = group.First().Description?.Trim() ?? string.Empty
            })
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            items = defaults.Parameters.Items.ToArray();
        }

        parameters = parameters with
        {
            MachineName = string.IsNullOrWhiteSpace(parameters.MachineName) ? defaults.Parameters.MachineName : parameters.MachineName.Trim(),
            InspectionTimeoutMs = parameters.InspectionTimeoutMs <= 0 ? defaults.Parameters.InspectionTimeoutMs : parameters.InspectionTimeoutMs,
            ImageRetentionDays = parameters.ImageRetentionDays <= 0 ? defaults.Parameters.ImageRetentionDays : parameters.ImageRetentionDays,
            Items = items
        };

        var access = settings.AccessControl ?? defaults.AccessControl;
        var rawRoles = access.Roles ?? defaults.AccessControl.Roles;
        var roles = rawRoles
            .Where(role => !string.IsNullOrWhiteSpace(role.Key))
            .GroupBy(role => role.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Key = group.Key,
                Name = string.IsNullOrWhiteSpace(group.First().Name) ? group.Key : group.First().Name.Trim(),
                Description = group.First().Description?.Trim() ?? string.Empty
            })
            .OrderBy(role => role.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (roles.Length == 0)
        {
            roles = defaults.AccessControl.Roles.ToArray();
        }

        access = access with
        {
            SessionTimeoutMinutes = access.SessionTimeoutMinutes <= 0 ? defaults.AccessControl.SessionTimeoutMinutes : access.SessionTimeoutMinutes,
            DefaultRole = string.IsNullOrWhiteSpace(access.DefaultRole) ? defaults.AccessControl.DefaultRole : access.DefaultRole.Trim(),
            Roles = roles
        };

        return settings with
        {
            Mes = mes,
            Plc = plc,
            Production = production,
            Logging = logging,
            Communication = communication,
            Parameters = parameters,
            AccessControl = access
        };
    }

    private static TcpCommunicationChannelSettings[] NormalizeTcpChannels(
        IReadOnlyList<TcpCommunicationChannelSettings>? channels,
        IReadOnlyList<TcpCommunicationChannelSettings> defaults)
    {
        var normalized = (channels ?? defaults)
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Key))
            .GroupBy(channel => channel.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var channel = group.First();
                return channel with
                {
                    Key = group.Key,
                    Name = string.IsNullOrWhiteSpace(channel.Name) ? group.Key : channel.Name.Trim(),
                    ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy),
                    Mode = string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase) ? "Server" : "Client",
                    Host = string.IsNullOrWhiteSpace(channel.Host) ? "127.0.0.1" : channel.Host.Trim(),
                    Port = channel.Port <= 0 ? 502 : channel.Port,
                    ConnectTimeoutMs = channel.ConnectTimeoutMs <= 0 ? 3000 : channel.ConnectTimeoutMs,
                    ReceiveTimeoutMs = channel.ReceiveTimeoutMs <= 0 ? 1000 : channel.ReceiveTimeoutMs,
                    FrameMode = NormalizeFrameMode(channel.FrameMode),
                    Delimiter = string.IsNullOrWhiteSpace(channel.Delimiter) ? "\\r\\n" : channel.Delimiter.Trim(),
                    FixedFrameLength = channel.FixedFrameLength <= 0 ? 8 : channel.FixedFrameLength,
                    LengthPrefixBytes = channel.LengthPrefixBytes is 1 or 2 or 4 ? channel.LengthPrefixBytes : 2,
                    MaxFrameLength = channel.MaxFrameLength <= 0 ? 4096 : channel.MaxFrameLength,
                    Description = channel.Description?.Trim() ?? string.Empty
                };
            })
            .OrderBy(channel => channel.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? defaults.ToArray() : normalized;
    }

    private static SerialCommunicationChannelSettings[] NormalizeSerialChannels(
        IReadOnlyList<SerialCommunicationChannelSettings>? channels,
        IReadOnlyList<SerialCommunicationChannelSettings> defaults)
    {
        var normalized = (channels ?? defaults)
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Key))
            .GroupBy(channel => channel.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var channel = group.First();
                return channel with
                {
                    Key = group.Key,
                    Name = string.IsNullOrWhiteSpace(channel.Name) ? group.Key : channel.Name.Trim(),
                    ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy),
                    PortName = string.IsNullOrWhiteSpace(channel.PortName) ? "COM3" : channel.PortName.Trim(),
                    BaudRate = channel.BaudRate <= 0 ? 9600 : channel.BaudRate,
                    DataBits = channel.DataBits <= 0 ? 8 : channel.DataBits,
                    Parity = string.IsNullOrWhiteSpace(channel.Parity) ? "None" : channel.Parity.Trim(),
                    StopBits = string.IsNullOrWhiteSpace(channel.StopBits) ? "One" : channel.StopBits.Trim(),
                    ReceiveTimeoutMs = channel.ReceiveTimeoutMs <= 0 ? 1000 : channel.ReceiveTimeoutMs,
                    FrameMode = NormalizeFrameMode(channel.FrameMode),
                    Delimiter = string.IsNullOrWhiteSpace(channel.Delimiter) ? "\\r\\n" : channel.Delimiter.Trim(),
                    FixedFrameLength = channel.FixedFrameLength <= 0 ? 8 : channel.FixedFrameLength,
                    LengthPrefixBytes = channel.LengthPrefixBytes is 1 or 2 or 4 ? channel.LengthPrefixBytes : 2,
                    MaxFrameLength = channel.MaxFrameLength <= 0 ? 4096 : channel.MaxFrameLength,
                    Description = channel.Description?.Trim() ?? string.Empty
                };
            })
            .OrderBy(channel => channel.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? defaults.ToArray() : normalized;
    }

    private static string NormalizeFrameMode(string? value)
    {
        return value?.Trim() switch
        {
            "Delimiter" => "Delimiter",
            "FixedLength" => "FixedLength",
            "LengthPrefix" => "LengthPrefix",
            _ => "Raw"
        };
    }

    private static IReadOnlyDictionary<string, string> NormalizeOptions(IReadOnlyDictionary<string, string>? options)
    {
        return (options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static DeviceConfiguration CreateDefault()
    {
        return new DeviceConfiguration
        {
            AxisController = AxisControllerKind.Simulated,
            AxisCards =
            [
                new AxisCardDefinition
                {
                    Key = "card1",
                    Name = "固高脉冲轴卡 C0",
                    Driver = AxisCardDriverKind.GoogolPulse,
                    Vendor = "Googol",
                    CardNo = 0,
                    AxisCount = 8,
                    InputCount = 16,
                    OutputCount = 16,
                    Description = "默认固高脉冲轴卡"
                }
            ],
            GoogolCards =
            [
                new GoogolCardDefinition
                {
                    Key = "card1",
                    CardNo = 0,
                    AxisCount = 8,
                    InputCount = 16,
                    OutputCount = 16,
                    Description = "默认固高脉冲轴卡"
                }
            ],
            Devices =
            [
                new DeviceDefinition
                {
                    Key = "camera-main",
                    Name = "Main camera",
                    Kind = DeviceKind.Camera,
                    Driver = "HikvisionMvs",
                    Description = "Primary inspection camera"
                },
                new DeviceDefinition
                {
                    Key = "motion-main",
                    Name = "Main motion controller",
                    Kind = DeviceKind.Motion,
                    Driver = "Simulated",
                    Description = "Primary motion capability used by process steps"
                },
                new DeviceDefinition
                {
                    Key = "io-main",
                    Name = "Main digital IO",
                    Kind = DeviceKind.DigitalIo,
                    Driver = "Simulated",
                    Description = "Primary digital IO capability"
                },
                new DeviceDefinition
                {
                    Key = "plc-main",
                    Name = "Main PLC",
                    Kind = DeviceKind.Plc,
                    Driver = "Simulated",
                    Connection = new DeviceConnectionDefinition
                    {
                        IpAddress = "192.168.1.10",
                        Port = 502,
                        StationNo = "1"
                    },
                    Description = "Primary addressable PLC device"
                },
                new DeviceDefinition
                {
                    Key = "instrument-gauge-1",
                    Name = "Sample Modbus instrument",
                    Kind = DeviceKind.Instrument,
                    Driver = "ModbusTcp",
                    Enabled = false,
                    Connection = new DeviceConnectionDefinition
                    {
                        IpAddress = "192.168.1.20",
                        Port = 502,
                        StationNo = "1"
                    },
                    Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["protocol"] = "ModbusTcp",
                        ["resultAddress"] = "D300"
                    },
                    Description = "Enable and point recipe steps to this DeviceKey when a meter or instrument exposes Modbus TCP registers"
                },
                new DeviceDefinition
                {
                    Key = "instrument-rtu-1",
                    Name = "Sample serial RTU instrument",
                    Kind = DeviceKind.Instrument,
                    Driver = "ModbusRtu",
                    Enabled = false,
                    Connection = new DeviceConnectionDefinition
                    {
                        SerialPort = "COM3",
                        BaudRate = 9600,
                        StationNo = "1"
                    },
                    Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["protocol"] = "ModbusRtu",
                        ["dataBits"] = "8",
                        ["stopBits"] = "One",
                        ["parity"] = "None"
                    },
                    Description = "Enable and point recipe steps to this DeviceKey when an instrument exposes Modbus RTU registers"
                }
            ],
            Debug = new DeviceDebugConfiguration
            {
                WorkbenchKind = DeviceDebugWorkbenchKind.AxisCard,
                SelectedDeviceKey = "motion-main"
            },
            SystemSettings = new SystemSettingsConfiguration
            {
                Production = new ProductionSettingsConfiguration
                {
                    CycleDelayMs = 900,
                    MaxConsecutiveFailures = 1,
                    AutoStopOnAlarm = true,
                    CleanupTimeoutMs = 2000,
                    StopWaitTimeoutMs = 10000
                },
                Logging = new AppLoggingSettingsConfiguration
                {
                    RetentionDays = 30,
                    MaxRecentEntries = 300,
                    IncludeThreadId = true
                },
                Communication = new CommunicationChannelSettings
                {
                    TcpChannels =
                    [
                        new TcpCommunicationChannelSettings
                        {
                            Key = "tcp-main",
                            Name = "默认 TCP 通道",
                            Mode = "Client",
                            Host = "127.0.0.1",
                            Port = 502,
                            FrameMode = "Raw",
                            Description = "配方 TCP 通讯工具默认使用的通道"
                        }
                    ],
                    SerialChannels =
                    [
                        new SerialCommunicationChannelSettings
                        {
                            Key = "serial-main",
                            Name = "默认串口通道",
                            PortName = "COM3",
                            BaudRate = 9600,
                            DataBits = 8,
                            Parity = "None",
                            StopBits = "One",
                            FrameMode = "Raw",
                            Description = "配方串口通讯工具默认使用的通道"
                        }
                    ]
                },
                Parameters = new RuntimeParameterSettings
                {
                    Items =
                    [
                        new SystemParameterDefinition
                        {
                            Key = "FixtureCode",
                            Name = "治具编号",
                            Value = "FIX-001",
                            Description = "MES或报表使用的固定治具信息"
                        },
                        new SystemParameterDefinition
                        {
                            Key = "CycleTargetMs",
                            Name = "目标节拍",
                            Value = "2500",
                            Unit = "ms",
                            Description = "预留工艺节拍参数"
                        },
                        new SystemParameterDefinition
                        {
                            Key = "RetryCount",
                            Name = "重试次数",
                            Value = "1",
                            Description = "预留流程重试参数"
                        }
                    ]
                },
                AccessControl = new AccessControlSettings
                {
                    Roles =
                    [
                        new AccessRoleDefinition
                        {
                            Key = "Operator",
                            Name = "操作员",
                            Description = "生产运行和查看权限"
                        },
                        new AccessRoleDefinition
                        {
                            Key = "Engineer",
                            Name = "工程师",
                            Description = "配方、流程和参数维护权限"
                        },
                        new AccessRoleDefinition
                        {
                            Key = "Administrator",
                            Name = "管理员",
                            Description = "系统设置和权限管理权限"
                        }
                    ]
                }
            },
            Axes =
            [
                new AxisPointDefinition
                {
                    Key = "AxisX",
                    Name = "X轴",
                    CardKey = "card1",
                    AxisNo = 1,
                    PulsesPerUnit = 1000,
                    PositionBand = 0.01,
                    SoftLimitNegative = -500,
                    SoftLimitPositive = 500,
                    DefaultSpeed = 80,
                    DefaultAcceleration = 120,
                    Description = "默认水平运动轴"
                },
                new AxisPointDefinition
                {
                    Key = "AxisY",
                    Name = "Y轴",
                    CardKey = "card1",
                    AxisNo = 2,
                    PulsesPerUnit = 1000,
                    PositionBand = 0.01,
                    SoftLimitNegative = -300,
                    SoftLimitPositive = 300,
                    DefaultSpeed = 70,
                    DefaultAcceleration = 120,
                    Description = "默认前后运动轴"
                },
                new AxisPointDefinition
                {
                    Key = "AxisZ",
                    Name = "Z轴",
                    CardKey = "card1",
                    AxisNo = 3,
                    PulsesPerUnit = 1000,
                    PositionBand = 0.01,
                    SoftLimitNegative = -80,
                    SoftLimitPositive = 120,
                    DefaultSpeed = 40,
                    DefaultAcceleration = 100,
                    Description = "默认升降轴"
                }
            ],
            IoPoints =
            [
                new IoPointDefinition
                {
                    Key = "StartButton",
                    Name = "启动按钮",
                    Direction = IoPointDirection.Input,
                    Address = "DI1",
                    CardKey = "card1",
                    PointNo = 1,
                    ActiveLow = true,
                    Description = "人工启动信号"
                },
                new IoPointDefinition
                {
                    Key = "PartInPlace",
                    Name = "产品到位",
                    Direction = IoPointDirection.Input,
                    Address = "DI2",
                    CardKey = "card1",
                    PointNo = 2,
                    ActiveLow = true,
                    Description = "治具到位/产品有料"
                },
                new IoPointDefinition
                {
                    Key = "DoorClosed",
                    Name = "安全门关闭",
                    Direction = IoPointDirection.Input,
                    Address = "DI3",
                    CardKey = "card1",
                    PointNo = 3,
                    ActiveLow = true,
                    InitialValue = true
                },
                new IoPointDefinition
                {
                    Key = "EmergencyStop",
                    Name = "急停输入",
                    Direction = IoPointDirection.Input,
                    Address = "DI4",
                    CardKey = "card1",
                    PointNo = 4,
                    ActiveLow = true
                },
                new IoPointDefinition
                {
                    Key = "BusyLamp",
                    Name = "运行指示灯",
                    Direction = IoPointDirection.Output,
                    Address = "DO1",
                    CardKey = "card1",
                    PointNo = 1,
                    ActiveLow = true
                },
                new IoPointDefinition
                {
                    Key = "OkLamp",
                    Name = "OK指示灯",
                    Direction = IoPointDirection.Output,
                    Address = "DO2",
                    CardKey = "card1",
                    PointNo = 2,
                    ActiveLow = true
                },
                new IoPointDefinition
                {
                    Key = "NgLamp",
                    Name = "NG指示灯",
                    Direction = IoPointDirection.Output,
                    Address = "DO3",
                    CardKey = "card1",
                    PointNo = 3,
                    ActiveLow = true
                },
                new IoPointDefinition
                {
                    Key = "RejectCylinder",
                    Name = "剔除气缸",
                    Direction = IoPointDirection.Output,
                    Address = "DO4",
                    CardKey = "card1",
                    PointNo = 4,
                    ActiveLow = true
                }
            ]
        };
    }
}
