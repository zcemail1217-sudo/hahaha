using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Devices.CvCommunication;

public static class CvCommunicationDeviceFactory
{
    public static bool IsPlcAlias(DeviceDefinition definition)
    {
        var driver = NormalizeKey(definition.Driver);
        return driver is "plcalias" or "mainplc" or "defaultplc";
    }

    public static bool SupportsDefinition(DeviceDefinition definition)
    {
        if (definition.Kind is not (DeviceKind.Plc or DeviceKind.Instrument))
        {
            return false;
        }

        var protocol = ResolveProtocol(definition);
        return IsSimulated(protocol) ||
               IsSimulated(definition.Driver) ||
               CvCommunicationPlcClient.SupportsProtocol(protocol);
    }

    public static IPlcClient CreateClient(DeviceDefinition definition)
    {
        var protocol = ResolveProtocol(definition);
        if (IsSimulated(protocol) || IsSimulated(definition.Driver))
        {
            return new SimulatedPlcClient();
        }

        if (!CvCommunicationPlcClient.SupportsProtocol(protocol))
        {
            throw new NotSupportedException(
                $"Device '{definition.Key}' uses unsupported CVCommunication protocol '{protocol}'.");
        }

        return new CvCommunicationPlcClient(CreateSettings(definition, protocol));
    }

    public static PlcCommunicationSettings CreateSettings(DeviceDefinition definition)
    {
        return CreateSettings(definition, ResolveProtocol(definition));
    }

    private static PlcCommunicationSettings CreateSettings(DeviceDefinition definition, string protocol)
    {
        var connection = definition.Connection ?? new DeviceConnectionDefinition();
        var options = CreateOptions(definition);
        var port = connection.Port > 0
            ? connection.Port
            : ParseFirstInt(0, GetOption(options, "port"));

        return new PlcCommunicationSettings
        {
            Protocol = protocol,
            IpAddress = FirstNonEmpty(
                connection.IpAddress,
                GetOption(options, "ipAddress"),
                GetOption(options, "ip"),
                GetOption(options, "host")),
            Port = port,
            StationNo = ParseFirstInt(
                1,
                connection.StationNo,
                GetOption(options, "stationNo"),
                GetOption(options, "station"),
                GetOption(options, "unitId"),
                GetOption(options, "slaveId")),
            Model = FirstNonEmpty(
                GetOption(options, "model"),
                GetOption(options, "plcType"),
                GetOption(options, "series")),
            ConnectTimeoutMs = ParseFirstInt(
                3000,
                GetOption(options, "connectTimeoutMs"),
                GetOption(options, "timeoutMs"),
                GetOption(options, "timeout")),
            HeartbeatIntervalMs = ParseFirstInt(
                1000,
                GetOption(options, "heartbeatIntervalMs"),
                GetOption(options, "heartbeatMs")),
            HeartbeatAddress = FirstNonEmpty(
                GetOption(options, "heartbeatAddress"),
                GetOption(options, "busyAddress")),
            ResultAddress = GetOption(options, "resultAddress"),
            Options = options
        };
    }

    private static string ResolveProtocol(DeviceDefinition definition)
    {
        var options = definition.Options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connection = definition.Connection;
        var driver = definition.Driver;
        var protocol = FirstNonEmpty(
            GetOption(options, "protocol"),
            GetOption(options, "plcProtocol"),
            CvCommunicationPlcClient.SupportsProtocol(connection?.Resource)
                ? connection!.Resource
                : string.Empty,
            driver);

        return IsGenericCvCommunicationDriver(protocol)
            ? "ModbusTcp"
            : protocol;
    }

    private static IReadOnlyDictionary<string, string> CreateOptions(DeviceDefinition definition)
    {
        var options = new Dictionary<string, string>(
            definition.Options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        AddOption(options, "deviceKey", definition.Key);
        AddOption(options, "deviceName", definition.Name);
        AddOption(options, "driver", definition.Driver);
        AddOption(options, "resource", definition.Connection?.Resource);
        AddOption(options, "serialPort", definition.Connection?.SerialPort);
        AddOption(options, "baudRate", definition.Connection?.BaudRate.ToString(CultureInfo.InvariantCulture));
        AddOption(options, "protocol", ResolveProtocol(definition));
        return options;
    }

    private static void AddOption(IDictionary<string, string> options, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !options.ContainsKey(key))
        {
            options[key] = value.Trim();
        }
    }

    private static string GetOption(IReadOnlyDictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static int ParseFirstInt(int fallback, params string?[] values)
    {
        foreach (var value in values)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                int.TryParse(value, out parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static bool IsSimulated(string? value)
    {
        return NormalizeKey(value) is "" or "simulated" or "simulation";
    }

    private static bool IsGenericCvCommunicationDriver(string? value)
    {
        return NormalizeKey(value) is "cvcommunication" or "cvcomm";
    }

    private static string NormalizeKey(string? value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
