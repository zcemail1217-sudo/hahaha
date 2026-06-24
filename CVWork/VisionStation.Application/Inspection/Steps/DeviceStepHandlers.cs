using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Domain.Utilities;

namespace VisionStation.Application.Inspection.Steps;

internal sealed record DeviceReadStepResult(string DeviceKey, string Address, string Value, string ResultKey);

internal sealed record DeviceWriteStepResult(string DeviceKey, string Address, string Value);

internal sealed record WritePlcStepResult(string DeviceKey, string Address, string Value);

internal sealed record DeviceCommandStepResult(string CommandName, string ContentText);

internal static class DeviceConnection
{
    public static async Task EnsureConnectedAsync(IDeviceClient device, CancellationToken cancellationToken)
    {
        if (device.Snapshot.State != DeviceConnectionState.Connected)
        {
            await device.ConnectAsync(cancellationToken);
        }
    }
}

internal sealed class DeviceReadStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.DeviceRead;

    public async Task<DeviceReadStepResult> ExecuteAsync(
        ProcessStepDefinition step,
        IAddressableDeviceClient device,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var address = ParameterParser.FirstNonEmpty(step.SignalId, step.OutputTarget);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException($"Device read address is empty for step '{step.Name}'.");
        }

        await DeviceConnection.EnsureConnectedAsync(device, cancellationToken);
        var value = await device.ReadAsync(address, cancellationToken);
        var resultKey = ParameterParser.FirstNonEmpty(step.ResultKey, $"{device.Key}:{address}");
        context.RuntimeValues[resultKey] = value;

        if (!string.IsNullOrWhiteSpace(step.OutputTarget) &&
            !string.Equals(step.OutputTarget, address, StringComparison.OrdinalIgnoreCase))
        {
            context.ResultTable[step.OutputTarget] = value;
        }

        return new DeviceReadStepResult(device.Key, address, value, resultKey);
    }

}

internal sealed class WritePlcStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.WritePlc;

    public async Task<WritePlcStepResult> ExecuteAsync(
        ProcessStepDefinition step,
        IAddressableDeviceClient device,
        string target,
        string value,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException($"PLC output target is empty for step '{step.Name}'.");
        }

        await DeviceConnection.EnsureConnectedAsync(device, cancellationToken);
        await device.WriteAsync(target, value, cancellationToken);
        context.RuntimeValues[$"{device.Key}:{target}"] = value;
        return new WritePlcStepResult(device.Key, target, value);
    }

}

internal sealed class DeviceWriteStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.DeviceWrite;

    public async Task<DeviceWriteStepResult> ExecuteAsync(
        ProcessStepDefinition step,
        IAddressableDeviceClient device,
        string value,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var address = ParameterParser.FirstNonEmpty(step.OutputTarget, step.SignalId);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException($"Device write address is empty for step '{step.Name}'.");
        }

        await DeviceConnection.EnsureConnectedAsync(device, cancellationToken);
        await device.WriteAsync(address, value, cancellationToken);
        context.RuntimeValues[$"{device.Key}:{address}"] = value;
        return new DeviceWriteStepResult(device.Key, address, value);
    }

}

internal sealed class DeviceCommandStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.DeviceCommand;

    public async Task<DeviceCommandStepResult> ExecuteAsync(
        ProcessStepDefinition step,
        ICommandDeviceClient device,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var commandName = ParameterParser.FirstNonEmpty(step.CommandName, step.SignalId);
        if (string.IsNullOrWhiteSpace(commandName))
        {
            throw new InvalidOperationException($"Device command name is empty for step '{step.Name}'.");
        }

        var parameters = new Dictionary<string, string>(step.Parameters, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(step.SignalId) && !parameters.ContainsKey("address"))
        {
            parameters["address"] = step.SignalId;
        }

        if (!string.IsNullOrWhiteSpace(step.OutputTarget) && !parameters.ContainsKey("target"))
        {
            parameters["target"] = step.OutputTarget;
        }

        await DeviceConnection.EnsureConnectedAsync(device, cancellationToken);
        var result = await device.InvokeAsync(
            new DeviceCommand
            {
                Name = commandName,
                Parameters = parameters
            },
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Device command '{commandName}' failed on '{device.Key}': {result.Message}");
        }

        if (!string.IsNullOrWhiteSpace(step.ResultKey))
        {
            context.RuntimeValues[step.ResultKey] = result.ContentText;
        }

        if (!string.IsNullOrWhiteSpace(step.OutputTarget))
        {
            context.ResultTable[step.OutputTarget] = result.ContentText;
        }

        return new DeviceCommandStepResult(commandName, result.ContentText);
    }

}
