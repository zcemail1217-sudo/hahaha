using VisionStation.Domain;

namespace VisionStation.Devices;

public interface IDeviceClient
{
    event EventHandler<DeviceSnapshot>? StateChanged;

    string Key { get; }

    string Name { get; }

    DeviceKind Kind { get; }

    DeviceSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IAxisDeviceClient : IDeviceClient
{
    IAxisController Controller { get; }
}

public interface IDigitalIoDeviceClient : IDeviceClient
{
    IDigitalIoController Controller { get; }
}

public interface IAddressableDeviceClient : IDeviceClient
{
    Task<string> ReadAsync(string address, CancellationToken cancellationToken = default);

    Task WriteAsync(string address, string value, CancellationToken cancellationToken = default);
}

public interface ICommandDeviceClient : IDeviceClient
{
    Task<DeviceCommandResult> InvokeAsync(DeviceCommand command, CancellationToken cancellationToken = default);
}

public interface IDeviceRuntime
{
    IReadOnlyList<IDeviceClient> Devices { get; }

    bool TryGet<TDevice>(string deviceKey, out TDevice device)
        where TDevice : class, IDeviceClient;

    TDevice GetRequired<TDevice>(string deviceKey, string capabilityName)
        where TDevice : class, IDeviceClient;
}

public sealed record DeviceCommand
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record DeviceCommandResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string ContentText { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static DeviceCommandResult Success(string contentText = "", string message = "OK")
    {
        return new DeviceCommandResult
        {
            IsSuccess = true,
            Message = message,
            ContentText = contentText
        };
    }

    public static DeviceCommandResult Failure(string message)
    {
        return new DeviceCommandResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}

public sealed class DeviceRuntime : IDeviceRuntime
{
    private readonly Dictionary<string, IDeviceClient> _devices = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IDeviceClient> Devices => _devices.Values.ToArray();

    public DeviceRuntime Register(IDeviceClient device)
    {
        if (string.IsNullOrWhiteSpace(device.Key))
        {
            throw new ArgumentException("Device key is required.", nameof(device));
        }

        _devices[device.Key] = device;
        return this;
    }

    public bool TryGet<TDevice>(string deviceKey, out TDevice device)
        where TDevice : class, IDeviceClient
    {
        device = default!;
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return false;
        }

        if (!_devices.TryGetValue(deviceKey.Trim(), out var registered) || registered is not TDevice typed)
        {
            return false;
        }

        device = typed;
        return true;
    }

    public TDevice GetRequired<TDevice>(string deviceKey, string capabilityName)
        where TDevice : class, IDeviceClient
    {
        if (TryGet<TDevice>(deviceKey, out var device))
        {
            return device;
        }

        throw new InvalidOperationException(
            $"Device '{deviceKey}' is not registered or does not support {capabilityName}.");
    }
}

public sealed class CameraDeviceClientAdapter : IDeviceClient
{
    private readonly ICameraDevice _camera;

    public CameraDeviceClientAdapter(string key, string name, ICameraDevice camera)
    {
        Key = key;
        Name = string.IsNullOrWhiteSpace(name) ? key : name;
        _camera = camera;
        _camera.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string Key { get; }

    public string Name { get; }

    public DeviceKind Kind => DeviceKind.Camera;

    public DeviceSnapshot Snapshot => _camera.Snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _camera.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _camera.DisconnectAsync(cancellationToken);
    }
}

public sealed class AxisDeviceClientAdapter : IAxisDeviceClient
{
    public AxisDeviceClientAdapter(string key, string name, IAxisController controller)
    {
        Key = key;
        Name = string.IsNullOrWhiteSpace(name) ? key : name;
        Controller = controller;
        Controller.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string Key { get; }

    public string Name { get; }

    public DeviceKind Kind => DeviceKind.Motion;

    public IAxisController Controller { get; }

    public DeviceSnapshot Snapshot => Controller.Snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Controller.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return Controller.DisconnectAsync(cancellationToken);
    }
}

public sealed class DigitalIoDeviceClientAdapter : IDigitalIoDeviceClient
{
    public DigitalIoDeviceClientAdapter(string key, string name, IDigitalIoController controller)
    {
        Key = key;
        Name = string.IsNullOrWhiteSpace(name) ? key : name;
        Controller = controller;
        Controller.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string Key { get; }

    public string Name { get; }

    public DeviceKind Kind => DeviceKind.DigitalIo;

    public IDigitalIoController Controller { get; }

    public DeviceSnapshot Snapshot => Controller.Snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Controller.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return Controller.DisconnectAsync(cancellationToken);
    }
}

public sealed class PlcDeviceClientAdapter : IAddressableDeviceClient, ICommandDeviceClient
{
    private readonly IPlcClient _plc;
    private readonly DeviceKind _kind;

    public PlcDeviceClientAdapter(string key, string name, IPlcClient plc, DeviceKind kind = DeviceKind.Plc)
    {
        Key = key;
        Name = string.IsNullOrWhiteSpace(name) ? key : name;
        _plc = plc;
        _kind = kind;
        _plc.StateChanged += (_, snapshot) => StateChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string Key { get; }

    public string Name { get; }

    public DeviceKind Kind => _kind;

    public DeviceSnapshot Snapshot => _plc.Snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _plc.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _plc.DisconnectAsync(cancellationToken);
    }

    public Task<string> ReadAsync(string address, CancellationToken cancellationToken = default)
    {
        return _plc.ReadAddressAsync(address, cancellationToken);
    }

    public Task WriteAsync(string address, string value, CancellationToken cancellationToken = default)
    {
        return _plc.WriteAddressAsync(address, value, cancellationToken);
    }

    public async Task<DeviceCommandResult> InvokeAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        var name = command.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return DeviceCommandResult.Failure("Device command name is required.");
        }

        if (name.Equals("Read", StringComparison.OrdinalIgnoreCase))
        {
            var address = GetParameter(command, "address", 0);
            if (string.IsNullOrWhiteSpace(address))
            {
                return DeviceCommandResult.Failure("Read command requires an address.");
            }

            var value = await ReadAsync(address, cancellationToken).ConfigureAwait(false);
            return DeviceCommandResult.Success(value);
        }

        if (name.Equals("Write", StringComparison.OrdinalIgnoreCase))
        {
            var address = GetParameter(command, "address", 0);
            var value = GetParameter(command, "value", 1);
            if (string.IsNullOrWhiteSpace(address))
            {
                return DeviceCommandResult.Failure("Write command requires an address.");
            }

            await WriteAsync(address, value, cancellationToken).ConfigureAwait(false);
            return DeviceCommandResult.Success(value);
        }

        if (name.Equals("ResetAlarm", StringComparison.OrdinalIgnoreCase))
        {
            await _plc.ResetAlarmAsync(cancellationToken).ConfigureAwait(false);
            return DeviceCommandResult.Success();
        }

        if (_plc is not IAdvancedPlcClient advancedPlc)
        {
            return DeviceCommandResult.Failure($"PLC does not support native command '{name}'.");
        }

        var result = await advancedPlc.InvokeNativeAsync(
            new PlcNativeCommand
            {
                MethodName = name,
                Arguments = command.Arguments
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? DeviceCommandResult.Success(result.ContentText ?? string.Empty, result.Message)
            : DeviceCommandResult.Failure(result.Message);
    }

    private static string GetParameter(DeviceCommand command, string key, int argumentIndex)
    {
        if (command.Parameters.TryGetValue(key, out var value))
        {
            return value;
        }

        return command.Arguments.Count > argumentIndex
            ? command.Arguments[argumentIndex]
            : string.Empty;
    }
}
