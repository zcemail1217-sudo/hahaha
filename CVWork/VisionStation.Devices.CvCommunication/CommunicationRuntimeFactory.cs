using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Devices.CvCommunication;

public static class CommunicationRuntimeFactory
{
    public static IPlcClient CreateMainPlcClient(DeviceConfiguration configuration)
    {
        var plc = configuration.SystemSettings.Plc;
        return IsSimulatedPlcProtocol(plc.Protocol)
            ? new SimulatedPlcClient()
            : new CvCommunicationPlcClient(plc);
    }

    public static IDeviceRuntime CreateDeviceRuntime(
        DeviceConfiguration configuration,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IDigitalIoController digitalIo)
    {
        var runtime = new DeviceRuntime();
        foreach (var device in configuration.Devices.Where(device => device.Enabled))
        {
            RegisterConfiguredDevice(runtime, device, camera, plc, axis, digitalIo);
        }

        RegisterDefaultDevice(runtime, new CameraDeviceClientAdapter("camera-main", "Main camera", camera));
        RegisterDefaultDevice(runtime, new AxisDeviceClientAdapter("motion-main", "Main motion controller", axis));
        RegisterDefaultDevice(runtime, new DigitalIoDeviceClientAdapter("io-main", "Main digital IO", digitalIo));
        RegisterDefaultDevice(runtime, new PlcDeviceClientAdapter("plc-main", "Main PLC", plc));
        return runtime;
    }

    public static bool IsSimulatedPlcProtocol(string? protocol)
    {
        return string.IsNullOrWhiteSpace(protocol)
            || string.Equals(protocol.Trim(), "Simulated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol.Trim(), "Simulation", StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterConfiguredDevice(
        DeviceRuntime runtime,
        DeviceDefinition definition,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IDigitalIoController digitalIo)
    {
        if (string.IsNullOrWhiteSpace(definition.Key))
        {
            return;
        }

        switch (definition.Kind)
        {
            case DeviceKind.Camera:
                runtime.Register(new CameraDeviceClientAdapter(definition.Key, definition.Name, camera));
                break;
            case DeviceKind.Motion:
                runtime.Register(new AxisDeviceClientAdapter(definition.Key, definition.Name, axis));
                break;
            case DeviceKind.DigitalIo:
                runtime.Register(new DigitalIoDeviceClientAdapter(definition.Key, definition.Name, digitalIo));
                break;
            case DeviceKind.Plc:
                runtime.Register(new PlcDeviceClientAdapter(
                    definition.Key,
                    definition.Name,
                    ResolveAddressableClient(definition, plc),
                    DeviceKind.Plc));
                break;
            case DeviceKind.Instrument:
                runtime.Register(new PlcDeviceClientAdapter(
                    definition.Key,
                    definition.Name,
                    ResolveAddressableClient(definition, plc),
                    DeviceKind.Instrument));
                break;
        }
    }

    private static void RegisterDefaultDevice(DeviceRuntime runtime, IDeviceClient device)
    {
        if (!runtime.TryGet<IDeviceClient>(device.Key, out _))
        {
            runtime.Register(device);
        }
    }

    private static IPlcClient ResolveAddressableClient(DeviceDefinition definition, IPlcClient mainPlc)
    {
        if (IsMainPlcKey(definition.Key) || CvCommunicationDeviceFactory.IsPlcAlias(definition))
        {
            return mainPlc;
        }

        if (CvCommunicationDeviceFactory.SupportsDefinition(definition))
        {
            return CvCommunicationDeviceFactory.CreateClient(definition);
        }

        throw new NotSupportedException(
            $"Device '{definition.Key}' uses unsupported addressable driver '{definition.Driver}'.");
    }

    private static bool IsMainPlcKey(string key)
    {
        return string.Equals(key?.Trim(), "plc-main", StringComparison.OrdinalIgnoreCase);
    }
}
