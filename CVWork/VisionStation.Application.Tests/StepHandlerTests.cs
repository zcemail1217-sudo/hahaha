using VisionStation.Application.Inspection;
using VisionStation.Application.Inspection.Steps;
using VisionStation.Devices;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class StepHandlerTests
{
    [Fact]
    public async Task DelayStepHandler_NormalizesNegativeDelayToZero()
    {
        var handler = new DelayStepHandler();

        var delay = await handler.ExecuteAsync(new ProcessStepDefinition { DelayMs = -10 }, CancellationToken.None);

        Assert.Equal(0, delay);
    }

    [Theory]
    [InlineData("Split", "A,B,C", "B")]
    [InlineData("Regex", "SN=ABC123", "ABC123")]
    [InlineData("Substring", "abcdef", "bcd")]
    [InlineData("Replace", "a-b-c", "a_b_c")]
    [InlineData("Upper", "abc", "ABC")]
    [InlineData("Contains", "status=ok", "PASS")]
    public void StringProcessStepHandler_ExecutesSupportedOperations(
        string operation,
        string input,
        string expected)
    {
        var handler = new StringProcessStepHandler();
        var step = operation switch
        {
            "Split" => new ProcessStepDefinition
            {
                CommandName = operation,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["separator"] = ",",
                    ["index"] = "1"
                }
            },
            "Regex" => new ProcessStepDefinition
            {
                CommandName = operation,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pattern"] = "SN=([A-Z0-9]+)",
                    ["group"] = "1"
                }
            },
            "Substring" => new ProcessStepDefinition
            {
                CommandName = operation,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["start"] = "1",
                    ["length"] = "3"
                }
            },
            "Replace" => new ProcessStepDefinition
            {
                CommandName = operation,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oldValue"] = "-",
                    ["newValue"] = "_"
                }
            },
            "Contains" => new ProcessStepDefinition
            {
                CommandName = operation,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pattern"] = "OK",
                    ["trueValue"] = "PASS",
                    ["falseValue"] = "FAIL"
                }
            },
            _ => new ProcessStepDefinition { CommandName = operation }
        };

        var actual = handler.Execute(step, input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("9.9", InspectionOutcome.Ng)]
    [InlineData("10", InspectionOutcome.Ok)]
    [InlineData("15", InspectionOutcome.Ok)]
    [InlineData("20.1", InspectionOutcome.Ng)]
    [InlineData("not-number", InspectionOutcome.Error)]
    public void ResultJudgeStepHandler_EvaluatesLimitsAndUpdatesContext(
        string value,
        InspectionOutcome expected)
    {
        var handler = new ResultJudgeStepHandler();
        var context = new ProcessExecutionContext(
            "flow",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OverallResult"] = InspectionOutcome.Ok.ToString()
            });
        var step = new ProcessStepDefinition
        {
            ResultKey = "Width",
            LowerLimit = 10,
            UpperLimit = 20
        };

        var actual = handler.Execute(step, value, context);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.ToString(), context.ResultTable["Judge:Width"]);
        if (expected is InspectionOutcome.Ng or InspectionOutcome.Error)
        {
            Assert.Equal(expected.ToString(), context.RuntimeValues["OverallResult"]);
        }
    }

    [Fact]
    public void ReadVisionResultStepHandler_WritesResolvedValueToRuntimeValues()
    {
        var handler = new ReadVisionResultStepHandler();
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition
        {
            Name = "read width",
            ResultKey = "Width"
        };

        handler.Execute(step, "12.5", context);

        Assert.Equal("12.5", context.RuntimeValues["Width"]);
        Assert.Empty(context.ResultTable);
    }

    [Fact]
    public void ReadVisionResultStepHandler_WritesResolvedValueToOutputTargetWhenConfigured()
    {
        var handler = new ReadVisionResultStepHandler();
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition
        {
            Name = "read width",
            ResultKey = "Width",
            OutputTarget = "NextTool.Width"
        };

        handler.Execute(step, "12.5", context);

        Assert.Equal("12.5", context.RuntimeValues["Width"]);
        Assert.Equal("12.5", context.RuntimeValues["NextTool.Width"]);
        Assert.Empty(context.ResultTable);
    }

    [Fact]
    public void WriteResultTableStepHandler_WritesResolvedValueToRuntimeAndResultTable()
    {
        var handler = new WriteResultTableStepHandler();
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition
        {
            ResultKey = "Width",
            OutputTarget = "ResultTable.Width"
        };

        var target = handler.Execute(step, "12.5", context);

        Assert.Equal("ResultTable.Width", target);
        Assert.Equal("12.5", context.RuntimeValues["ResultTable.Width"]);
        Assert.Equal("12.5", context.ResultTable["ResultTable.Width"]);
    }

    [Fact]
    public async Task DeviceReadStepHandler_ReadsAddressAndStoresResult()
    {
        var handler = new DeviceReadStepHandler();
        var device = new FakeDeviceClient { ReadValue = "42" };
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition
        {
            SignalId = "D100",
            ResultKey = "ReadValue",
            OutputTarget = "ReadTable"
        };

        var result = await handler.ExecuteAsync(step, device, context, CancellationToken.None);

        Assert.True(device.ConnectCalled);
        Assert.Equal("D100", result.Address);
        Assert.Equal("42", context.RuntimeValues["ReadValue"]);
        Assert.Equal("42", context.ResultTable["ReadTable"]);
    }

    [Fact]
    public async Task DeviceWriteStepHandler_WritesValueAndStoresRuntimeEcho()
    {
        var handler = new DeviceWriteStepHandler();
        var device = new FakeDeviceClient();
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition { OutputTarget = "D200" };

        var result = await handler.ExecuteAsync(step, device, "OK", context, CancellationToken.None);

        Assert.True(device.ConnectCalled);
        Assert.Equal("D200", result.Address);
        Assert.Equal(("D200", "OK"), device.LastWrite);
        Assert.Equal("OK", context.RuntimeValues["device-main:D200"]);
    }

    [Fact]
    public async Task WritePlcStepHandler_WritesTargetAndStoresRuntimeEcho()
    {
        var handler = new WritePlcStepHandler();
        var device = new FakeDeviceClient();
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition { Name = "write plc" };

        var result = await handler.ExecuteAsync(step, device, "D300", "NG", context, CancellationToken.None);

        Assert.True(device.ConnectCalled);
        Assert.Equal("D300", result.Address);
        Assert.Equal(("D300", "NG"), device.LastWrite);
        Assert.Equal("NG", context.RuntimeValues["device-main:D300"]);
    }

    [Fact]
    public async Task DeviceCommandStepHandler_InvokesCommandAndStoresOutputs()
    {
        var handler = new DeviceCommandStepHandler();
        var device = new FakeDeviceClient { CommandResult = DeviceCommandResult.Success("DONE") };
        var context = new ProcessExecutionContext("flow");
        var step = new ProcessStepDefinition
        {
            CommandName = "Reset",
            ResultKey = "CommandResult",
            OutputTarget = "CommandTable"
        };

        var result = await handler.ExecuteAsync(step, device, context, CancellationToken.None);

        Assert.Equal("Reset", result.CommandName);
        Assert.Equal("DONE", context.RuntimeValues["CommandResult"]);
        Assert.Equal("DONE", context.ResultTable["CommandTable"]);
    }

    private sealed class FakeDeviceClient : IAddressableDeviceClient, ICommandDeviceClient
    {
        public event EventHandler<DeviceSnapshot>? StateChanged;

        public string Key => "device-main";

        public string Name => "Device";

        public DeviceKind Kind => DeviceKind.Plc;

        public DeviceSnapshot Snapshot { get; private set; } = new(
            "Device",
            DeviceConnectionState.Disconnected,
            "Disconnected",
            DateTimeOffset.Now);

        public bool ConnectCalled { get; private set; }

        public string ReadValue { get; init; } = string.Empty;

        public (string Address, string Value) LastWrite { get; private set; }

        public DeviceCommandResult CommandResult { get; init; } = DeviceCommandResult.Success();

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalled = true;
            Snapshot = Snapshot with
            {
                State = DeviceConnectionState.Connected,
                Message = "Connected"
            };
            StateChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with
            {
                State = DeviceConnectionState.Disconnected,
                Message = "Disconnected"
            };
            StateChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task<string> ReadAsync(string address, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReadValue);
        }

        public Task WriteAsync(string address, string value, CancellationToken cancellationToken = default)
        {
            LastWrite = (address, value);
            return Task.CompletedTask;
        }

        public Task<DeviceCommandResult> InvokeAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CommandResult);
        }
    }
}
