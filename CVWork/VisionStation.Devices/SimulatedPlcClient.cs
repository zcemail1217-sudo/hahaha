using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class SimulatedPlcClient : IAdvancedPlcClient
{
    private readonly Dictionary<string, string> _registers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M100"] = "1",
        ["M110"] = "0",
        ["D200"] = "0",
        ["D210"] = "0",
        ["D220"] = string.Empty
    };

    private DeviceSnapshot _snapshot = new("Simulated PLC", DeviceConnectionState.Disconnected, "Not connected", DateTimeOffset.Now);

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot => _snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Connected, "PLC handshake complete");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Disconnected, "PLC disconnected");
        return Task.CompletedTask;
    }

    public Task SetInspectionBusyAsync(bool busy, CancellationToken cancellationToken = default)
    {
        _registers["M110"] = busy ? "1" : "0";
        SetState(DeviceConnectionState.Connected, busy ? "Inspection busy ON" : "Inspection busy OFF");
        return Task.CompletedTask;
    }

    public Task<string> ReadAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("PLC address is required.", nameof(address));
        }

        var normalized = address.Trim();
        return Task.FromResult(_registers.TryGetValue(normalized, out var value) ? value : "0");
    }

    public Task WriteAddressAsync(string address, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("PLC address is required.", nameof(address));
        }

        var normalized = address.Trim();
        _registers[normalized] = value ?? string.Empty;
        SetState(DeviceConnectionState.Connected, $"{normalized} <= {_registers[normalized]}");
        return Task.CompletedTask;
    }

    public Task WriteInspectionResultAsync(InspectionResult result, CancellationToken cancellationToken = default)
    {
        _registers["D200"] = result.Outcome == InspectionOutcome.Ok ? "1" : "0";
        _registers["D220"] = result.Barcode ?? string.Empty;
        SetState(
            DeviceConnectionState.Connected,
            result.Outcome == InspectionOutcome.Ok
                ? $"OK result written, barcode {result.Barcode}"
                : $"NG result written, barcode {result.Barcode}");
        return Task.CompletedTask;
    }

    public async Task<PlcOperationResult> ReadAsync(PlcReadCommand command, CancellationToken cancellationToken = default)
    {
        var value = await ReadAddressAsync(command.Address, cancellationToken).ConfigureAwait(false);
        return PlcOperationResult.Success(value, ToJsonString(value));
    }

    public async Task<PlcOperationResult> WriteAsync(PlcWriteCommand command, CancellationToken cancellationToken = default)
    {
        await WriteAddressAsync(command.Address, command.Value, cancellationToken).ConfigureAwait(false);
        return PlcOperationResult.Success(command.Value, ToJsonString(command.Value));
    }

    public async Task<PlcOperationResult> WaitAsync(PlcWaitCommand command, CancellationToken cancellationToken = default)
    {
        var timeout = Math.Max(command.TimeoutMs, 1);
        var interval = Math.Max(command.ReadIntervalMs, 10);
        var startedAt = DateTimeOffset.Now;
        while ((DateTimeOffset.Now - startedAt).TotalMilliseconds <= timeout)
        {
            var value = await ReadAddressAsync(command.Address, cancellationToken).ConfigureAwait(false);
            if (string.Equals(value.Trim(), command.ExpectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return PlcOperationResult.Success(value, ToJsonString(value));
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure($"Timed out waiting for {command.Address}={command.ExpectedValue}.");
    }

    public Task<PlcOperationResult> InvokeNativeAsync(PlcNativeCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PlcOperationResult.Failure("The simulated PLC does not expose native communication-library commands."));
    }

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Connected, "Alarm reset");
        return Task.CompletedTask;
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("Simulated PLC", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private static string ToJsonString(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
