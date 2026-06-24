using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed record GoogolDigitalIoControllerOptions
{
    public short CardNo { get; init; }

    public bool OpenCardOnConnect { get; init; }

    public IReadOnlyList<ExtendedIoModuleDefinition> Modules { get; init; } = Array.Empty<ExtendedIoModuleDefinition>();

    public IReadOnlyList<IoPointDefinition> Points { get; init; } = Array.Empty<IoPointDefinition>();
}

public sealed class GoogolDigitalIoController : IDigitalIoDriver
{
    private readonly object _syncRoot = new();
    private GoogolDigitalIoControllerOptions _options;
    private Dictionary<string, IoPointDefinition> _points;
    private bool _connected;
    private DeviceSnapshot _snapshot = new("Googol IO", DeviceConnectionState.Disconnected, "Disconnected", DateTimeOffset.Now);

    private sealed record ExtendedIoRuntimeModule(
        short ParentCardNo,
        short ModuleNo,
        string Model,
        int ModuleType,
        short StartAddress,
        short WorkMode,
        int InputCount,
        int OutputCount,
        int AdChannels,
        double AdMaxVoltage,
        double AdMinVoltage,
        int DaChannels,
        double DaMaxVoltage,
        double DaMinVoltage,
        string ConfigPath,
        bool FromConfiguration);

    public GoogolDigitalIoController(GoogolDigitalIoControllerOptions options)
    {
        _options = options;
        _points = BuildPointMap(options.Points);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DriverId => "GoogolIO";

    public AxisCardDriverKind DriverKind => AxisCardDriverKind.GoogolPulse;

    public IReadOnlyCollection<string> PointKeys
    {
        get
        {
            lock (_syncRoot)
            {
                return _points.Keys.ToArray();
            }
        }
    }

    public DeviceSnapshot Snapshot => _snapshot;

    public bool ContainsPoint(string pointKey)
    {
        lock (_syncRoot)
        {
            return _points.ContainsKey(pointKey);
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware("Connect Googol IO", () =>
        {
            if (_options.OpenCardOnConnect)
            {
                Check(Native.GT_SetCardNo(_options.CardNo, _options.CardNo), "Set Googol card number");
                Check(Native.GT_Open(_options.CardNo, 0, 1), "Open Googol IO");
            }

            OpenExtendedModulesCore();
            _connected = true;
            SetState(DeviceConnectionState.Connected, $"Googol IO connected. Points={_points.Count}");
        });

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_connected)
        {
            SetState(DeviceConnectionState.Disconnected, "Googol IO disconnected");
            return Task.CompletedTask;
        }

        RunHardware("Disconnect Googol IO", () =>
        {
            CloseExtendedModulesCore();

            if (_options.OpenCardOnConnect)
            {
                Check(Native.GT_Close(_options.CardNo), "Close Googol IO");
            }

            _connected = false;
            SetState(DeviceConnectionState.Disconnected, "Googol IO disconnected");
        });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IoPointStatus>> GetAllPointStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();

        IoPointDefinition[] points;
        lock (_syncRoot)
        {
            points = _points.Values.ToArray();
        }

        return Task.FromResult<IReadOnlyList<IoPointStatus>>(points
            .OrderBy(point => point.Direction)
            .ThenBy(point => point.Source)
            .ThenBy(point => point.CardNo)
            .ThenBy(point => point.ModuleNo)
            .ThenBy(point => point.PointNo)
            .ThenBy(point => point.Key, StringComparer.OrdinalIgnoreCase)
            .Select(ReadPointCore)
            .ToArray());
    }

    public Task<IoPointStatus> GetPointStatusAsync(string pointKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        var point = GetPoint(pointKey);
        return Task.FromResult(ReadPointCore(point));
    }

    public Task WritePointAsync(string pointKey, bool value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        var point = GetPoint(pointKey);

        if (point.Direction != IoPointDirection.Output)
        {
            throw new InvalidOperationException($"{point.Key} is an input point and cannot be written.");
        }

        if (!point.Enabled)
        {
            throw new InvalidOperationException($"{point.Key} is disabled.");
        }

        RunHardware($"{point.Key} write output", () =>
        {
            var rawValue = ToRawValue(point, value);
            if (point.Source == IoPointSource.ExtendedModule)
            {
                WriteExtendedOutputCore(point, rawValue);
            }
            else
            {
                Check(Native.GT_SetDoBit(ResolveCardNo(point), Native.McGpo, point.PointNo, rawValue), $"{point.Key} write onboard output");
            }

            SetState(DeviceConnectionState.Connected, $"{point.Key} write {(value ? "ON" : "OFF")}");
        });

        return Task.CompletedTask;
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.AxisController != AxisControllerKind.Googol)
        {
            ApplyDisabledConfiguration(configuration);
            return Task.CompletedTask;
        }

        lock (_syncRoot)
        {
            var cards = ResolveGoogolPulseAxisCards(configuration);
            _options = _options with
            {
                CardNo = configuration.GoogolCardNo,
                Modules = ResolveGoogolExtendedIoModules(configuration, cards).ToArray(),
                Points = ResolveGoogolIoPoints(configuration, cards).ToArray()
            };
            _points = BuildPointMap(_options.Points);
        }

        SetState(
            _connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected,
            $"固高 IO 配置已更新，当前点位数：{_points.Count}。");
        return Task.CompletedTask;
    }

    private void ApplyDisabledConfiguration(DeviceConfiguration configuration)
    {
        lock (_syncRoot)
        {
            Exception? closeFailure = null;
            if (_connected)
            {
                try
                {
                    CloseExtendedModulesCore();
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
            }

            _options = _options with
            {
                CardNo = configuration.GoogolCardNo,
                Modules = configuration.ExtendedIoModules,
                Points = configuration.IoPoints
            };
            _points = BuildPointMap(_options.Points);
            _connected = false;

            var message = closeFailure is null
                ? $"当前配置为 {configuration.AxisController}，固高 IO 控制器已停用；切换控制器类型需要重启软件。"
                : $"当前配置为 {configuration.AxisController}，固高 IO 控制器已停用；切换控制器类型需要重启软件。上一次硬件会话关闭不完整：{DescribeHardwareException(closeFailure)}";
            SetState(DeviceConnectionState.Disconnected, message);
        }
    }

    private IoPointStatus ReadPointCore(IoPointDefinition point)
    {
        return RunHardware($"{point.Key} read status", () =>
        {
            var cardNo = ResolveCardNo(point);
            int rawStatus;
            if (point.Source == IoPointSource.ExtendedModule)
            {
                rawStatus = ReadExtendedPointRawCore(point);
            }
            else if (point.Direction == IoPointDirection.Output)
            {
                Check(Native.GT_GetDo(cardNo, Native.McGpo, out rawStatus), $"{point.Key} read onboard output");
            }
            else
            {
                Check(Native.GT_GetDi(cardNo, Native.McGpi, out rawStatus), $"{point.Key} read onboard input");
            }

            return new IoPointStatus
            {
                Key = point.Key,
                Name = point.Name,
                Direction = point.Direction,
                Address = point.Address,
                CardNo = cardNo,
                PointNo = point.PointNo,
                ActiveLow = point.ActiveLow,
                Enabled = point.Enabled,
                Value = FromRawValue(point, rawStatus),
                Message = "Googol IO status refreshed",
                Timestamp = DateTimeOffset.Now
            };
        });
    }

    private IoPointDefinition GetPoint(string pointKey)
    {
        lock (_syncRoot)
        {
            if (_points.TryGetValue(pointKey, out var point))
            {
                return point;
            }
        }

        throw new InvalidOperationException($"IO point is not configured: {pointKey}");
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Googol IO is not connected.");
        }
    }

    private short ResolveCardNo(IoPointDefinition point)
    {
        return point.CardNo == 0 ? _options.CardNo : point.CardNo;
    }

    private short ResolveParentCardNo(IoPointDefinition point)
    {
        return point.ParentCardNo >= 0 ? point.ParentCardNo : ResolveCardNo(point);
    }

    private short ResolveModuleNo(IoPointDefinition point)
    {
        return point.ModuleNo >= 0 ? point.ModuleNo : point.CardNo;
    }

    private void OpenExtendedModulesCore()
    {
        var modules = ResolveExtendedModules();
        foreach (var group in modules.GroupBy(module => module.ParentCardNo))
        {
            var parentCardNo = group.Key;
            var cardModules = group.OrderBy(module => module.ModuleNo).ToArray();
            var configPath = ResolveExtendedConfigPath(parentCardNo, cardModules);
            WriteHcb2Config(configPath, cardModules);

            Check(Native.GT_OpenExtMdl(parentCardNo, string.Empty), $"Open HCB2 extended IO on card {parentCardNo}");
            Check(Native.GT_LoadExtConfig(parentCardNo, configPath), $"Load HCB2 extended IO config on card {parentCardNo}");
            Check(Native.GT_SetExtMdlMode(parentCardNo, cardModules.Max(module => module.WorkMode)), $"Set HCB2 mode on card {parentCardNo}");
        }
    }

    private void CloseExtendedModulesCore()
    {
        foreach (var parentCardNo in ResolveExtendedModules().Select(module => module.ParentCardNo).Distinct())
        {
            Check(Native.GT_CloseExtMdl(parentCardNo), $"Close HCB2 extended IO on card {parentCardNo}");
        }
    }

    private ExtendedIoRuntimeModule[] ResolveExtendedModules()
    {
        var configuredModules = _options.Modules
            .Where(module => !string.IsNullOrWhiteSpace(module.Key))
            .Select(module => ToRuntimeModule(module, true));

        var pointModules = _points.Values
            .Where(point => point.Source == IoPointSource.ExtendedModule)
            .GroupBy(point => (
                ParentCardNo: ResolveParentCardNo(point),
                ModuleNo: ResolveModuleNo(point),
                ConfigPath: point.ModuleConfigPath?.Trim() ?? string.Empty))
            .Select(group => ToRuntimeModule(new ExtendedIoModuleDefinition
            {
                Key = $"ext-{group.Key.ParentCardNo}-{group.Key.ModuleNo}",
                ParentCardNo = group.Key.ParentCardNo,
                ModuleNo = group.Key.ModuleNo,
                Model = Hcb2ModuleCatalog.DefaultModel,
                ModuleType = 3,
                StartAddress = group.Key.ModuleNo,
                InputCount = group.Count(point => point.Direction == IoPointDirection.Input),
                OutputCount = group.Count(point => point.Direction == IoPointDirection.Output),
                ConfigPath = group.Key.ConfigPath
            }, false));

        return configuredModules
            .Concat(pointModules)
            .GroupBy(module => (module.ParentCardNo, module.ModuleNo))
            .Select(group => group
                .Where(module => module.FromConfiguration)
                .DefaultIfEmpty(group.First())
                .First())
            .OrderBy(module => module.ParentCardNo)
            .ThenBy(module => module.ModuleNo)
            .ToArray();
    }

    private ExtendedIoRuntimeModule ToRuntimeModule(ExtendedIoModuleDefinition module, bool fromConfiguration)
    {
        var profile = Hcb2ModuleCatalog.Resolve(module.Model);
        var parentCardNo = module.ParentCardNo < 0 ? _options.CardNo : module.ParentCardNo;
        var moduleNo = module.ModuleNo < 0 ? (short)0 : module.ModuleNo;
        var startAddress = module.StartAddress < 0 ? moduleNo : module.StartAddress;
        var inputCount = module.InputCount <= 0 || module.InputCount == 16 && profile.InputCount != 16
            ? profile.InputCount
            : module.InputCount;
        var outputCount = module.OutputCount < 0 || module.OutputCount == 16 && profile.OutputCount != 16
            ? profile.OutputCount
            : module.OutputCount;

        return new ExtendedIoRuntimeModule(
            parentCardNo,
            moduleNo,
            profile.Model,
            module.ModuleType <= 0 ? profile.ModuleType : module.ModuleType,
            (short)Math.Clamp(startAddress, (short)0, (short)15),
            module.WorkMode <= 0 ? (short)0 : (short)1,
            Math.Max(0, inputCount),
            Math.Max(0, outputCount),
            module.AdChannels <= 0 ? profile.AdChannels : module.AdChannels,
            module.AdMaxVoltage == 0 ? profile.AdMaxVoltage : module.AdMaxVoltage,
            module.AdMinVoltage == 0 ? profile.AdMinVoltage : module.AdMinVoltage,
            module.DaChannels <= 0 ? profile.DaChannels : module.DaChannels,
            module.DaMaxVoltage == 0 ? profile.DaMaxVoltage : module.DaMaxVoltage,
            module.DaMinVoltage == 0 ? profile.DaMinVoltage : module.DaMinVoltage,
            module.ConfigPath?.Trim() ?? string.Empty,
            fromConfiguration);
    }

    private static string ResolveExtendedConfigPath(short parentCardNo, IReadOnlyList<ExtendedIoRuntimeModule> modules)
    {
        var configPath = modules.Select(module => module.ConfigPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        return string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(AppContext.BaseDirectory, $"hcb2_extmdl_card{parentCardNo}.cfg")
            : configPath.Trim();
    }

    private static void WriteHcb2Config(string configPath, IReadOnlyList<ExtendedIoRuntimeModule> modules)
    {
        ValidateHcb2Modules(modules);

        var builder = new StringBuilder();
        foreach (var module in modules.OrderBy(module => module.ModuleNo))
        {
            builder.AppendLine($"[module{module.ModuleNo.ToString(CultureInfo.InvariantCulture)}]");
            builder.AppendLine($"type={module.ModuleType.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"address={module.StartAddress.ToString(CultureInfo.InvariantCulture)}");
            if (module.ModuleType == 6)
            {
                builder.AppendLine($"adChannels={module.AdChannels.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"adMaxVoltage={module.AdMaxVoltage.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"adMinVoltage={module.AdMinVoltage.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"daChannels={module.DaChannels.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"daMaxVoltage={module.DaMaxVoltage.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"daMinVoltage={module.DaMinVoltage.ToString(CultureInfo.InvariantCulture)}");
            }

            builder.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(configPath, builder.ToString(), Encoding.ASCII);
    }

    private static void ValidateHcb2Modules(IReadOnlyList<ExtendedIoRuntimeModule> modules)
    {
        var occupiedAddresses = new Dictionary<int, string>();
        var moduleNumbers = modules.Select(module => module.ModuleNo).OrderBy(moduleNo => moduleNo).ToArray();
        if (moduleNumbers.Length > 0 && moduleNumbers[0] != 0)
        {
            throw new AxisControllerException("HCB2 module numbers must start from 0.");
        }

        for (var index = 1; index < moduleNumbers.Length; index++)
        {
            if (moduleNumbers[index] != moduleNumbers[index - 1] + 1)
            {
                throw new AxisControllerException("HCB2 module numbers must be continuous from 0.");
            }
        }

        foreach (var module in modules)
        {
            var profile = Hcb2ModuleCatalog.Resolve(module.Model);
            var lastAddress = module.StartAddress + profile.AddressSpan - 1;
            if (module.StartAddress < 0 || lastAddress > 15)
            {
                throw new AxisControllerException($"HCB2 module {module.ModuleNo} address is out of range 0-15.");
            }

            if (profile.AddressSpan == 8 && module.StartAddress is not (0 or 8))
            {
                throw new AxisControllerException($"HCB2 analog module {module.ModuleNo} start address must be 0 or 8.");
            }

            for (var address = module.StartAddress; address <= lastAddress; address++)
            {
                if (occupiedAddresses.TryGetValue(address, out var owner))
                {
                    throw new AxisControllerException($"HCB2 address {address} is used by both {owner} and module{module.ModuleNo}.");
                }

                occupiedAddresses[address] = $"module{module.ModuleNo}";
            }
        }
    }

    private int ReadExtendedPointRawCore(IoPointDefinition point)
    {
        var parentCardNo = ResolveParentCardNo(point);
        var moduleNo = ResolveModuleNo(point);
        ushort rawStatus;
        if (point.Direction == IoPointDirection.Output)
        {
            Check(Native.GT_GetExtDoValue(parentCardNo, moduleNo, out rawStatus), $"{point.Key} read extended output");
        }
        else
        {
            Check(Native.GT_GetExtIoValue(parentCardNo, moduleNo, out rawStatus), $"{point.Key} read extended input");
        }

        return rawStatus;
    }

    private void WriteExtendedOutputCore(IoPointDefinition point, short rawValue)
    {
        Check(
            Native.GT_SetExtIoBit(ResolveParentCardNo(point), ResolveModuleNo(point), ResolveBitIndex(point), (ushort)rawValue),
            $"{point.Key} write extended output");
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("Googol IO", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private void RunHardware(string operation, Action action)
    {
        lock (_syncRoot)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                var message = $"{operation} failed: cannot load gts.dll. Check process bitness and PATH.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (SEHException ex)
            {
                var message = $"{operation} failed: gts.dll raised an unmanaged exception. Check the Googol driver, card state, extension module config, and whether another process is using the card.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (Exception ex)
            {
                SetState(DeviceConnectionState.Faulted, $"{operation} failed: {ex.Message}");
                throw;
            }
        }
    }

    private T RunHardware<T>(string operation, Func<T> action)
    {
        lock (_syncRoot)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                var message = $"{operation} failed: cannot load gts.dll. Check process bitness and PATH.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (SEHException ex)
            {
                var message = $"{operation} failed: gts.dll raised an unmanaged exception. Check the Googol driver, card state, extension module config, and whether another process is using the card.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (Exception ex)
            {
                SetState(DeviceConnectionState.Faulted, $"{operation} failed: {ex.Message}");
                throw;
            }
        }
    }

    private static Dictionary<string, IoPointDefinition> BuildPointMap(IReadOnlyList<IoPointDefinition> points)
    {
        return points
            .Where(point => point.Enabled && !string.IsNullOrWhiteSpace(point.Key))
            .GroupBy(point => point.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Key = group.Key,
                PointNo = group.First().PointNo <= 0 ? (short)1 : group.First().PointNo
            })
            .ToDictionary(point => point.Key, StringComparer.OrdinalIgnoreCase);
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
                    Driver = AxisCardDriverKind.GoogolPulse,
                    CardNo = configuration.GoogolCardNo,
                    AxisCount = Math.Max(8, configuration.IoPoints.Select(point => (int)point.PointNo).DefaultIfEmpty(1).Max()),
                    ConfigPath = configuration.GoogolConfigPath
                }
            ];
        }

        return configuration.GoogolCards
            .Select(card => new AxisCardDefinition
            {
                Key = card.Key,
                Driver = AxisCardDriverKind.GoogolPulse,
                CardNo = card.CardNo,
                AxisCount = card.AxisCount,
                InputCount = card.InputCount,
                OutputCount = card.OutputCount,
                ConfigPath = card.ConfigPath,
                Description = card.Description
            })
            .ToArray();
    }

    private static IEnumerable<ExtendedIoModuleDefinition> ResolveGoogolExtendedIoModules(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
        return configuration.ExtendedIoModules
            .Where(module => ResolveCard(module.ParentCardKey, module.ParentCardNo, cards, configuration.GoogolCardNo) is not null);
    }

    private static IEnumerable<IoPointDefinition> ResolveGoogolIoPoints(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
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

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool FromRawValue(IoPointDefinition point, int rawStatus)
    {
        var bitSet = ((rawStatus >> ResolveBitIndex(point)) & 0x01) == 1;
        return point.ActiveLow ? !bitSet : bitSet;
    }

    private static short ResolveBitIndex(IoPointDefinition point)
    {
        var bitIndex = point.PointNo - 1;
        if (bitIndex is < 0 or > 15)
        {
            throw new AxisControllerException($"{point.Key} point number is out of range. IO point numbers use 1-16.");
        }

        return (short)bitIndex;
    }

    private static short ToRawValue(IoPointDefinition point, bool value)
    {
        return point.ActiveLow ? (short)(value ? 0 : 1) : (short)(value ? 1 : 0);
    }

    private static void Check(short result, string operation)
    {
        if (result != 0)
        {
            throw new AxisControllerException($"{operation} failed. Googol return code={result}.");
        }
    }

    private static string DescribeHardwareException(Exception exception)
    {
        return exception is SEHException
            ? "gts.dll raised an unmanaged exception; verify the Googol driver/card state and close any other process using the card."
            : exception.Message;
    }

    private static class Native
    {
        public const short McGpi = 4;
        public const short McGpo = 12;

        [DllImport("gts.dll")]
        public static extern short GT_SetCardNo(short cardNum, short index);

        [DllImport("gts.dll")]
        public static extern short GT_Open(short cardNum, short channel, short param);

        [DllImport("gts.dll")]
        public static extern short GT_Close(short cardNum);

        [DllImport("gts.dll")]
        public static extern short GT_GetDi(short cardNum, short diType, out int pValue);

        [DllImport("gts.dll")]
        public static extern short GT_GetDo(short cardNum, short doType, out int pValue);

        [DllImport("gts.dll")]
        public static extern short GT_SetDoBit(short cardNum, short doType, short doIndex, short value);

        [DllImport("gts.dll")]
        public static extern short GT_OpenExtMdl(short cardNum, string pDllName);

        [DllImport("gts.dll")]
        public static extern short GT_CloseExtMdl(short cardNum);

        [DllImport("gts.dll")]
        public static extern short GT_LoadExtConfig(short cardNum, string pFileName);

        [DllImport("gts.dll")]
        public static extern short GT_GetExtIoValue(short cardNum, short mdl, out ushort pValue);

        [DllImport("gts.dll")]
        public static extern short GT_GetExtDoValue(short cardNum, short mdl, out ushort pValue);

        [DllImport("gts.dll")]
        public static extern short GT_SetExtIoBit(short cardNum, short mdl, short index, ushort value);

        [DllImport("gts.dll")]
        public static extern short GT_SetExtMdlMode(short cardNum, short mode);
    }
}
