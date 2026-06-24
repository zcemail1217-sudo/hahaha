using System.Globalization;
using System.IO.Ports;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CVCommunication;
using CVCommunication.Core;
using CVCommunication.Core.Device;
using CVCommunication.ModBus;
using CVCommunication.Profinet.AllenBradley;
using CVCommunication.Profinet.Beckhoff;
using CVCommunication.Profinet.Delta;
using CVCommunication.Profinet.FATEK;
using CVCommunication.Profinet.Fuji;
using CVCommunication.Profinet.GE;
using CVCommunication.Profinet.Inovance;
using CVCommunication.Profinet.Keyence;
using CVCommunication.Profinet.LSIS;
using CVCommunication.Profinet.Melsec;
using CVCommunication.Profinet.Omron;
using CVCommunication.Profinet.Panasonic;
using CVCommunication.Profinet.Siemens;
using CVCommunication.Profinet.Vigor;
using CVCommunication.Profinet.XINJE;
using CVCommunication.Profinet.YASKAWA;
using CVCommunication.Profinet.Yokogawa;
using VisionStation.Domain;

namespace VisionStation.Devices.CvCommunication;

public sealed class CvCommunicationPlcClient : IAdvancedPlcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PlcCommunicationSettings _settings;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private object? _device;
    private IReadWriteNet? _readWrite;
    private DeviceSnapshot _snapshot;

    public CvCommunicationPlcClient(PlcCommunicationSettings settings)
    {
        _settings = settings;
        _snapshot = new DeviceSnapshot(DisplayName, DeviceConnectionState.Disconnected, "Not connected", DateTimeOffset.Now);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot => _snapshot;

    public static bool SupportsProtocol(string? protocol)
    {
        var key = NormalizeProtocol(protocol);
        return key is "modbus" or "modbustcp" or "modbustcpnet"
            or "modbusudp" or "udp"
            or "modbusrtu" or "rtu"
            or "modbusascii" or "ascii"
            or "modbusrtuovertcp" or "rtuovertcp"
            or "modbusasciiovertcp" or "asciiovertcp"
            or "inovancetcp" or "inovancetcpnet" or "huichuan"
            or "inovanceserial" or "inovancertu"
            or "inovanceserialovertcp" or "inovancertuovertcp"
            or "melsecmc" or "mitsubishimc"
            or "melsecmcascii" or "mitsubishimcascii"
            or "melsecmcr" or "melsecmcudp" or "melsecmcasciiudp"
            or "melseca1e" or "melseca1eascii"
            or "melseca3c" or "melseca3covertcp"
            or "melsecfxserial" or "melsecfxserialovertcp"
            or "melsecfxlinks" or "melsecfxlinksovertcp"
            or "melseccip"
            or "siemenss7"
            or "siemenss7s1200" or "s7s1200"
            or "siemenss7s1500" or "s7s1500"
            or "siemenss7s300" or "s7s300"
            or "siemenss7s400" or "s7s400"
            or "siemenss7200" or "s7s200"
            or "siemenss7200smart" or "s7s200smart"
            or "siemensppi" or "siemensppiovertcp" or "siemensmpi"
            or "siemensfetchwrite" or "siemenswebapi"
            or "omronfins" or "omronfinsudp"
            or "omronhostlink" or "omronhostlinkovertcp"
            or "omronhostlinkcmode" or "omronhostlinkcmodeovertcp"
            or "omroncip" or "omronconnectedcip"
            or "keyencemc" or "keyencemcascii"
            or "keyencenano" or "keyencenanoserial" or "keyencenanoserialovertcp" or "keyencenanoovertcp"
            or "panasonicmc" or "panasonicmewtocol" or "panasonicmewtocolovertcp"
            or "deltatcp" or "deltaserial" or "deltaserialascii"
            or "deltaserialovertcp" or "deltaserialasciiovertcp"
            or "allenbradley" or "allenbradleycip" or "ab" or "abcip"
            or "allenbradleymicrocip" or "abmicrocip"
            or "allenbradleyconnectedcip" or "abconnectedcip"
            or "allenbradleypccc" or "abpccc"
            or "allenbradleyslc" or "abslc"
            or "allenbradleydf1" or "abdf1"
            or "beckhoffads"
            or "lsfastenet" or "lsfenet" or "lscnet" or "lscnetovertcp"
            or "fatek" or "fatekprogram" or "fatekserial" or "fatekprogramovertcp"
            or "fujisph" or "fujispb" or "fujispbovertcp" or "fujicommandsettingtype"
            or "ge" or "gesrtp"
            or "xinje" or "xinjetcp" or "xinjeserial" or "xinjeserialovertcp" or "xinjeinternal"
            or "vigorserial" or "vigorserialovertcp"
            or "yaskawa" or "memobus" or "memobustcp"
            or "yokogawa" or "yokogawalink";
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _device ??= CreateDevice(_settings);
            _readWrite = (IReadWriteNet)_device;

            if (_device is DeviceTcpNet tcpDevice)
            {
                var result = await tcpDevice.ConnectServerAsync().ConfigureAwait(false);
                EnsureSuccess(result, "PLC connect failed");
            }
            else if (_device is DeviceSerialPort serialDevice)
            {
                var result = serialDevice.Open();
                EnsureSuccess(result, "PLC serial open failed");
                if (serialDevice is SiemensMPI siemensMpi)
                {
                    EnsureSuccess(siemensMpi.Handle(), "PLC MPI handshake failed");
                }
            }
            else if (_device is SiemensWebApi siemensWebApi)
            {
                var result = await siemensWebApi.ConnectServerAsync().ConfigureAwait(false);
                EnsureSuccess(result, "PLC WebApi connect failed");
            }
            else if (_device is not DeviceUdpNet)
            {
                throw new NotSupportedException($"Unsupported CVCommunication device type '{_device.GetType().Name}'.");
            }

            SetState(DeviceConnectionState.Connected, $"{_device} connected");
        }
        catch (Exception ex)
        {
            SetState(DeviceConnectionState.Faulted, ex.Message);
            throw;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_device is null)
            {
                SetState(DeviceConnectionState.Disconnected, "PLC disconnected");
                return;
            }

            if (_device is DeviceTcpNet tcpDevice)
            {
                var result = await tcpDevice.ConnectCloseAsync().ConfigureAwait(false);
                EnsureSuccess(result, "PLC disconnect failed");
            }
            else if (_device is DeviceSerialPort serialDevice)
            {
                serialDevice.Close();
            }
            else if (_device is SiemensWebApi siemensWebApi)
            {
                var result = await siemensWebApi.ConnectCloseAsync().ConfigureAwait(false);
                EnsureSuccess(result, "PLC WebApi disconnect failed");
            }

            SetState(DeviceConnectionState.Disconnected, "PLC disconnected");
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SetInspectionBusyAsync(bool busy, CancellationToken cancellationToken = default)
    {
        var address = GetOption("busyAddress", _settings.HeartbeatAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await WriteAddressAsync(address, busy ? "1" : "0", cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        var (valueType, actualAddress) = ExtractTypedAddress(address, PlcValueType.Auto);
        if (valueType == PlcValueType.Auto)
        {
            valueType = IsLikelyBitAddress(actualAddress) ? PlcValueType.Bool : PlcValueType.Int16;
        }

        var result = await ReadAsync(
            new PlcReadCommand
            {
                Address = actualAddress,
                ValueType = valueType,
                Length = 1
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Message);
        }

        return result.ContentText ?? string.Empty;
    }

    public async Task WriteAddressAsync(string address, string value, CancellationToken cancellationToken = default)
    {
        var (valueType, actualAddress) = ExtractTypedAddress(address, PlcValueType.Auto);
        if (valueType == PlcValueType.Auto)
        {
            valueType = InferWriteType(actualAddress, value);
        }

        var result = await WriteAsync(
            new PlcWriteCommand
            {
                Address = actualAddress,
                ValueType = valueType,
                Value = value
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    public async Task WriteInspectionResultAsync(InspectionResult result, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ResultAddress))
        {
            await WriteAddressAsync(
                _settings.ResultAddress,
                result.Outcome == InspectionOutcome.Ok ? "1" : "0",
                cancellationToken).ConfigureAwait(false);
        }

        var barcodeAddress = GetOption("barcodeAddress", string.Empty);
        if (!string.IsNullOrWhiteSpace(barcodeAddress) && !string.IsNullOrWhiteSpace(result.Barcode))
        {
            await WriteAddressAsync(barcodeAddress, result.Barcode, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        var command = GetOption("resetAlarmCommand", string.Empty);
        if (!string.IsNullOrWhiteSpace(command))
        {
            await InvokeNativeAsync(new PlcNativeCommand { MethodName = command }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PlcOperationResult> ReadAsync(PlcReadCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Address))
        {
            return PlcOperationResult.Failure("PLC address is required.");
        }

        try
        {
            var readWrite = await EnsureReadWriteAsync(cancellationToken).ConfigureAwait(false);
            var (valueType, address) = ExtractTypedAddress(command.Address, command.ValueType);
            if (valueType == PlcValueType.Auto)
            {
                valueType = IsLikelyBitAddress(address) ? PlcValueType.Bool : PlcValueType.Int16;
            }

            var result = valueType switch
            {
                PlcValueType.Bool => await readWrite.ReadBoolAsync(address).ConfigureAwait(false),
                PlcValueType.BoolArray => await readWrite.ReadBoolAsync(address, NormalizeLength(command.Length)).ConfigureAwait(false),
                PlcValueType.Byte => await ReadByteAsync(readWrite, address).ConfigureAwait(false),
                PlcValueType.Bytes => await readWrite.ReadAsync(address, NormalizeLength(command.Length)).ConfigureAwait(false),
                PlcValueType.Int16 => await readWrite.ReadInt16Async(address).ConfigureAwait(false),
                PlcValueType.UInt16 => await readWrite.ReadUInt16Async(address).ConfigureAwait(false),
                PlcValueType.Int32 => await readWrite.ReadInt32Async(address).ConfigureAwait(false),
                PlcValueType.UInt32 => await readWrite.ReadUInt32Async(address).ConfigureAwait(false),
                PlcValueType.Int64 => await readWrite.ReadInt64Async(address).ConfigureAwait(false),
                PlcValueType.UInt64 => await readWrite.ReadUInt64Async(address).ConfigureAwait(false),
                PlcValueType.Float => await readWrite.ReadFloatAsync(address).ConfigureAwait(false),
                PlcValueType.Double => await readWrite.ReadDoubleAsync(address).ConfigureAwait(false),
                PlcValueType.String => await readWrite.ReadStringAsync(address, NormalizeLength(command.Length), ResolveEncoding(command.EncodingName)).ConfigureAwait(false),
                PlcValueType.DateTime => await InvokeDateReadAsync(address).ConfigureAwait(false),
                _ => await readWrite.ReadInt16Async(address).ConfigureAwait(false)
            };

            return ToOperationResult(result);
        }
        catch (Exception ex)
        {
            return PlcOperationResult.Failure(ex.Message);
        }
    }

    public async Task<PlcOperationResult> WriteAsync(PlcWriteCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Address))
        {
            return PlcOperationResult.Failure("PLC address is required.");
        }

        try
        {
            var readWrite = await EnsureReadWriteAsync(cancellationToken).ConfigureAwait(false);
            var (valueType, address) = ExtractTypedAddress(command.Address, command.ValueType);
            if (valueType == PlcValueType.Auto)
            {
                valueType = InferWriteType(address, command.Value);
            }

            var result = valueType switch
            {
                PlcValueType.Bool => await readWrite.WriteAsync(address, ParseBool(command.Value)).ConfigureAwait(false),
                PlcValueType.BoolArray => await readWrite.WriteAsync(address, ParseArray(command, ParseBool)).ConfigureAwait(false),
                PlcValueType.Byte => await readWrite.WriteAsync(address, new[] { ParseByte(command.Value) }).ConfigureAwait(false),
                PlcValueType.Bytes => await readWrite.WriteAsync(address, ParseBytes(command)).ConfigureAwait(false),
                PlcValueType.Int16 => await readWrite.WriteAsync(address, short.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.UInt16 => await readWrite.WriteAsync(address, ushort.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.Int32 => await readWrite.WriteAsync(address, int.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.UInt32 => await readWrite.WriteAsync(address, uint.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.Int64 => await readWrite.WriteAsync(address, long.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.UInt64 => await readWrite.WriteAsync(address, ulong.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.Float => await readWrite.WriteAsync(address, float.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.Double => await readWrite.WriteAsync(address, double.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                PlcValueType.String when command.Length > 0 => await readWrite.WriteAsync(address, command.Value, command.Length, ResolveEncoding(command.EncodingName)).ConfigureAwait(false),
                PlcValueType.String => await readWrite.WriteAsync(address, command.Value, ResolveEncoding(command.EncodingName)).ConfigureAwait(false),
                PlcValueType.DateTime => await InvokeDateWriteAsync(address, DateTime.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false),
                _ => await readWrite.WriteAsync(address, short.Parse(command.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false)
            };

            return ToOperationResult(result);
        }
        catch (Exception ex)
        {
            return PlcOperationResult.Failure(ex.Message);
        }
    }

    public async Task<PlcOperationResult> WaitAsync(PlcWaitCommand command, CancellationToken cancellationToken = default)
    {
        var timeout = Math.Max(command.TimeoutMs, 1);
        var interval = Math.Max(command.ReadIntervalMs, 10);
        var startedAt = DateTimeOffset.Now;

        while ((DateTimeOffset.Now - startedAt).TotalMilliseconds <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadAsync(
                new PlcReadCommand
                {
                    Address = command.Address,
                    ValueType = command.ValueType,
                    Length = 1
                },
                cancellationToken).ConfigureAwait(false);

            if (!read.IsSuccess)
            {
                return read;
            }

            if (string.Equals(read.ContentText?.Trim(), command.ExpectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return read;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure($"Timed out waiting for {command.Address}={command.ExpectedValue}.");
    }

    public async Task<PlcOperationResult> InvokeNativeAsync(PlcNativeCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.MethodName))
        {
            return PlcOperationResult.Failure("Native PLC method name is required.");
        }

        try
        {
            var device = await EnsureDeviceAsync(cancellationToken).ConfigureAwait(false);
            var result = await InvokeMethodAsync(device, command.MethodName.Trim(), command.Arguments).ConfigureAwait(false);
            return ToOperationResult(result);
        }
        catch (Exception ex)
        {
            return PlcOperationResult.Failure(ex.Message);
        }
    }

    private async Task<IReadWriteNet> EnsureReadWriteAsync(CancellationToken cancellationToken)
    {
        if (_readWrite is not null && Snapshot.State == DeviceConnectionState.Connected)
        {
            return _readWrite;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _readWrite ?? throw new InvalidOperationException("PLC driver does not support IReadWriteNet.");
    }

    private async Task<object> EnsureDeviceAsync(CancellationToken cancellationToken)
    {
        if (_device is not null && Snapshot.State == DeviceConnectionState.Connected)
        {
            return _device;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _device ?? throw new InvalidOperationException("PLC driver is not initialized.");
    }

    private static object CreateDevice(PlcCommunicationSettings settings)
    {
        var protocol = NormalizeProtocol(settings.Protocol);
        var ip = settings.IpAddress;
        var port = settings.Port;
        var station = (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue);

        object device = protocol switch
        {
            "modbus" or "modbustcp" or "modbustcpnet" => CreateModbusTcp(settings, ip, port <= 0 ? 502 : port, station),
            "modbusudp" or "udp" => CreateModbusUdp(settings, ip, port <= 0 ? 502 : port, station),
            "modbusrtu" or "rtu" => CreateModbusRtu(settings, station),
            "modbusascii" or "ascii" => CreateModbusAscii(settings, station),
            "modbusrtuovertcp" or "rtuovertcp" => CreateModbusRtuOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "modbusasciiovertcp" or "asciiovertcp" => CreateModbusAsciiOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "inovancetcp" or "inovancetcpnet" or "huichuan" => CreateInovance(settings, ip, port <= 0 ? 502 : port, station),
            "inovanceserial" or "inovancertu" => CreateInovanceSerial(settings, station),
            "inovanceserialovertcp" or "inovancertuovertcp" => CreateInovanceSerialOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "melsecmc" or "mitsubishimc" => CreateMelsec(settings, ip, port <= 0 ? 5000 : port),
            "melsecmcascii" or "mitsubishimcascii" => CreateMelsecAscii(settings, ip, port <= 0 ? 5000 : port),
            "melsecmcr" => CreateMelsecR(settings, ip, port <= 0 ? 5000 : port),
            "melsecmcudp" => CreateMelsecUdp(settings, ip, port <= 0 ? 5000 : port),
            "melsecmcasciiudp" => CreateMelsecAsciiUdp(settings, ip, port <= 0 ? 5000 : port),
            "melseca1e" => new MelsecA1ENet(ip, port <= 0 ? 5000 : port),
            "melseca1eascii" => new MelsecA1EAsciiNet(ip, port <= 0 ? 5000 : port),
            "melseca3c" => CreateMelsecA3C(settings),
            "melseca3covertcp" => CreateMelsecA3COverTcp(settings, ip, port <= 0 ? 5000 : port),
            "melsecfxserial" => CreateMelsecFxSerial(settings),
            "melsecfxserialovertcp" => CreateMelsecFxSerialOverTcp(settings, ip, port <= 0 ? 5000 : port),
            "melsecfxlinks" => CreateMelsecFxLinks(settings),
            "melsecfxlinksovertcp" => CreateMelsecFxLinksOverTcp(settings, ip, port <= 0 ? 5000 : port),
            "melseccip" => CreateAllenBradleyLike(new MelsecCipNet(ip, port <= 0 ? 44818 : port), settings),
            "siemenss7" => CreateSiemens(settings, ip, port <= 0 ? 102 : port),
            "siemenss7s1200" or "s7s1200" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S1200),
            "siemenss7s1500" or "s7s1500" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S1500),
            "siemenss7s300" or "s7s300" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S300),
            "siemenss7s400" or "s7s400" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S400),
            "siemenss7200" or "s7s200" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S200),
            "siemenss7200smart" or "s7s200smart" => CreateSiemens(settings, ip, port <= 0 ? 102 : port, SiemensPLCS.S200Smart),
            "siemensppi" => CreateSiemensPpi(settings),
            "siemensppiovertcp" => CreateSiemensPpiOverTcp(settings, ip, port <= 0 ? 102 : port),
            "siemensmpi" => CreateSiemensMpi(settings),
            "siemensfetchwrite" => new SiemensFetchWriteNet(ip, port <= 0 ? 102 : port),
            "siemenswebapi" => CreateSiemensWebApi(settings, ip, port <= 0 ? 443 : port),
            "omronfins" => CreateOmron(settings, ip, port <= 0 ? 9600 : port),
            "omronfinsudp" => CreateOmronUdp(settings, ip, port <= 0 ? 9600 : port),
            "omronhostlink" => CreateOmronHostLink(settings),
            "omronhostlinkovertcp" => CreateOmronHostLinkOverTcp(settings, ip, port <= 0 ? 9600 : port),
            "omronhostlinkcmode" => CreateOmronHostLinkCMode(settings),
            "omronhostlinkcmodeovertcp" => CreateOmronHostLinkCModeOverTcp(settings, ip, port <= 0 ? 9600 : port),
            "omroncip" => CreateAllenBradleyLike(new OmronCipNet(ip, port <= 0 ? 44818 : port), settings),
            "omronconnectedcip" => new OmronConnectedCipNet(ip, port <= 0 ? 44818 : port),
            "keyencemc" => CreateMelsecLike(new KeyenceMcNet(ip, port <= 0 ? 5000 : port), settings),
            "keyencemcascii" => CreateMelsecLike(new KeyenceMcAsciiNet(ip, port <= 0 ? 5000 : port), settings),
            "keyencenano" or "keyencenanoserial" => CreateKeyenceNanoSerial(settings),
            "keyencenanoserialovertcp" or "keyencenanoovertcp" => CreateKeyenceNanoSerialOverTcp(settings, ip, port <= 0 ? 8501 : port),
            "panasonicmc" => CreateMelsecLike(new PanasonicMcNet(ip, port <= 0 ? 5000 : port), settings),
            "panasonicmewtocol" => CreatePanasonicMewtocol(settings, station),
            "panasonicmewtocolovertcp" => CreatePanasonicMewtocolOverTcp(settings, ip, port <= 0 ? 9094 : port, station),
            "deltatcp" => CreateDeltaTcp(settings, ip, port <= 0 ? 502 : port, station),
            "deltaserial" => CreateDeltaSerial(settings, station),
            "deltaserialascii" => CreateDeltaSerialAscii(settings, station),
            "deltaserialovertcp" => CreateDeltaSerialOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "deltaserialasciiovertcp" => CreateDeltaSerialAsciiOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "allenbradley" or "allenbradleycip" or "ab" or "abcip" => CreateAllenBradleyLike(new AllenBradleyNet(ip, port <= 0 ? 44818 : port), settings),
            "allenbradleymicrocip" or "abmicrocip" => CreateAllenBradleyLike(new AllenBradleyMicroCip(ip, port <= 0 ? 44818 : port), settings),
            "allenbradleyconnectedcip" or "abconnectedcip" => new AllenBradleyConnectedCipNet(ip, port <= 0 ? 44818 : port),
            "allenbradleypccc" or "abpccc" => new AllenBradleyPcccNet(ip, port <= 0 ? 44818 : port),
            "allenbradleyslc" or "abslc" => new AllenBradleySLCNet(ip, port <= 0 ? 44818 : port),
            "allenbradleydf1" or "abdf1" => CreateAllenBradleyDf1(settings),
            "beckhoffads" => CreateBeckhoffAds(settings, ip, port <= 0 ? 48898 : port),
            "lsfastenet" or "lsfenet" => CreateLsFastEnet(settings, ip, port <= 0 ? 2004 : port),
            "lscnet" => CreateLsCnet(settings, station),
            "lscnetovertcp" => CreateLsCnetOverTcp(settings, ip, port <= 0 ? 2004 : port, station),
            "fatek" or "fatekprogram" or "fatekserial" => CreateFatekProgram(settings, station),
            "fatekprogramovertcp" => CreateFatekProgramOverTcp(settings, ip, port <= 0 ? 5000 : port, station),
            "fujisph" => CreateFujiSph(settings, ip, port <= 0 ? 18245 : port),
            "fujispb" => CreateFujiSpb(settings),
            "fujispbovertcp" => new FujiSPBOverTcp(ip, port <= 0 ? 18245 : port),
            "fujicommandsettingtype" => new FujiCommandSettingType(ip, port <= 0 ? 18245 : port),
            "ge" or "gesrtp" => new GeSRTPNet(ip, port <= 0 ? 18245 : port),
            "xinje" or "xinjetcp" => CreateXinJeTcp(settings, ip, port <= 0 ? 502 : port, station),
            "xinjeserial" => CreateXinJeSerial(settings, station),
            "xinjeserialovertcp" => CreateXinJeSerialOverTcp(settings, ip, port <= 0 ? 502 : port, station),
            "xinjeinternal" => CreateXinJeInternal(settings, ip, port <= 0 ? 502 : port, station),
            "vigorserial" => CreateVigorSerial(settings, station),
            "vigorserialovertcp" => CreateVigorSerialOverTcp(settings, ip, port <= 0 ? 5000 : port, station),
            "yaskawa" or "memobus" or "memobustcp" => CreateMemobus(settings, ip, port <= 0 ? 502 : port),
            "yokogawa" or "yokogawalink" => CreateYokogawa(settings, ip, port <= 0 ? 12289 : port),
            _ => throw new NotSupportedException($"Unsupported CVCommunication PLC protocol '{settings.Protocol}'.")
        };

        ConfigureCommonDevice(device, settings);

        return device;
    }

    private static DeviceTcpNet CreateModbusTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var device = new ModbusTcpNet(ip, port, station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            IsCheckMessageId = ParseBoolOption(settings, "checkMessageId", true),
            IsStringReverse = ParseBoolOption(settings, "isStringReverse", false)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        return device;
    }

    private static DeviceTcpNet CreateModbusUdp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var device = new ModbusUdpNet(ip, port, station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            IsCheckMessageId = ParseBoolOption(settings, "checkMessageId", true),
            IsStringReverse = ParseBoolOption(settings, "isStringReverse", false)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        return device;
    }

    private static DeviceSerialPort CreateModbusRtu(PlcCommunicationSettings settings, byte station)
    {
        var device = new ModbusRtu(station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            StationCheckMacth = ParseBoolOption(settings, "stationCheckMatch", true)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceSerialPort CreateModbusAscii(PlcCommunicationSettings settings, byte station)
    {
        var device = new ModbusAscii(station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            StationCheckMacth = ParseBoolOption(settings, "stationCheckMatch", true)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateModbusRtuOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var device = new ModbusRtuOverTcp(ip, port, station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            StationCheckMacth = ParseBoolOption(settings, "stationCheckMatch", true)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        return device;
    }

    private static DeviceTcpNet CreateModbusAsciiOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var device = new ModbusAsciiOverTcp(ip, port, station)
        {
            AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true),
            StationCheckMacth = ParseBoolOption(settings, "stationCheckMatch", true)
        };

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }

        return device;
    }

    private static void ConfigureSerialPort(DeviceSerialPort device, PlcCommunicationSettings settings)
    {
        var portName = FirstValue(
            GetOption(settings, "serialPort"),
            GetOption(settings, "portName"),
            settings.IpAddress);
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("Serial instrument requires a serialPort option, for example COM3.");
        }

        var baudRate = ParseInt(settings, "baudRate", 9600);
        var dataBits = ParseInt(settings, "dataBits", 8);
        var stopBits = ParseEnumOption(settings, "stopBits", StopBits.One);
        var parity = ParseEnumOption(settings, "parity", Parity.None);

        device.SerialPortInni(portName, baudRate, dataBits, stopBits, parity);
        device.ReceiveTimeOut = ParseInt(settings, "receiveTimeoutMs", settings.ConnectTimeoutMs <= 0 ? 3000 : settings.ConnectTimeoutMs);
        if (TryGetInt(settings, "sleepTime", out var sleepTime))
        {
            device.SleepTime = Math.Max(0, sleepTime);
        }
    }

    private static void ConfigureCommonDevice(object device, PlcCommunicationSettings settings)
    {
        if (device is DeviceTcpNet tcpDevice)
        {
            tcpDevice.ConnectTimeOut = settings.ConnectTimeoutMs <= 0 ? 3000 : settings.ConnectTimeoutMs;
        }

        if (device is DeviceCommunication communication)
        {
            communication.ReceiveTimeOut = ParseInt(
                settings,
                "receiveTimeoutMs",
                settings.ConnectTimeoutMs <= 0 ? 3000 : settings.ConnectTimeoutMs);

            if (TryParseEnumOption<DataFormat>(settings, "dataFormat", out var dataFormat))
            {
                communication.ByteTransform.DataFormat = dataFormat;
            }

            if (settings.Options.ContainsKey("isStringReverse"))
            {
                communication.ByteTransform.IsStringReverseByteWord = ParseBoolOption(settings, "isStringReverse", false);
            }

            if (settings.Options.ContainsKey("isStringReverseByteWord"))
            {
                communication.ByteTransform.IsStringReverseByteWord = ParseBoolOption(settings, "isStringReverseByteWord", false);
            }

            if (TryGetInt(settings, "sleepTime", out var sleepTime))
            {
                communication.SleepTime = Math.Max(0, sleepTime);
            }
        }
    }

    private static DeviceTcpNet CreateInovance(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var series = ResolveInovanceSeries(settings);
        var device = new InovanceTcpNet(series, ip, port, station);
        ConfigureModbusDevice(device, settings);
        return device;
    }

    private static DeviceSerialPort CreateInovanceSerial(PlcCommunicationSettings settings, byte station)
    {
        var series = ResolveInovanceSeries(settings);
        var device = new InovanceSerial(series, station)
        {
            Series = series
        };
        ConfigureModbusDevice(device, settings);
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateInovanceSerialOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        var series = ResolveInovanceSeries(settings);
        var device = new InovanceSerialOverTcp(series, ip, port, station)
        {
            Series = series
        };
        ConfigureModbusDevice(device, settings);
        return device;
    }

    private static InovanceSeries ResolveInovanceSeries(PlcCommunicationSettings settings)
    {
        var seriesText = FirstValue(settings.Model, GetOption(settings, "series"), "AM");
        return Enum.TryParse<InovanceSeries>(seriesText, true, out var parsed) ? parsed : InovanceSeries.AM;
    }

    private static void ConfigureModbusDevice(IModbus device, PlcCommunicationSettings settings)
    {
        device.AddressStartWithZero = ParseBoolOption(settings, "addressStartWithZero", true);
        device.IsStringReverse = ParseBoolOption(settings, "isStringReverse", false);
        device.EnableWriteMaskCode = ParseBoolOption(settings, "enableWriteMaskCode", device.EnableWriteMaskCode);

        if (TryParseEnumOption<DataFormat>(settings, "dataFormat", out var dataFormat))
        {
            device.DataFormat = dataFormat;
        }

        if (TryGetInt(settings, "broadcastStation", out var broadcastStation))
        {
            device.BroadcastStation = broadcastStation;
        }
    }

    private static DeviceTcpNet CreateMelsec(PlcCommunicationSettings settings, string ip, int port)
    {
        return CreateMelsecLike(new MelsecMcNet(ip, port), settings);
    }

    private static DeviceTcpNet CreateMelsecAscii(PlcCommunicationSettings settings, string ip, int port)
    {
        return CreateMelsecLike(new MelsecMcAsciiNet(ip, port), settings);
    }

    private static DeviceTcpNet CreateMelsecR(PlcCommunicationSettings settings, string ip, int port)
    {
        return new MelsecMcRNet(ip, port)
        {
            NetworkNumber = ParseByte(settings, "networkNumber", 0),
            PLCNumber = ParseByte(settings, "plcNumber", byte.MaxValue),
            NetworkStationNumber = ParseByte(settings, "networkStationNumber", 0),
            TargetIOStation = ParseUShort(settings, "targetIOStation", 1023),
            EnableWriteBitToWordRegister = ParseBoolOption(settings, "enableWriteBitToWordRegister", false)
        };
    }

    private static DeviceTcpNet CreateMelsecUdp(PlcCommunicationSettings settings, string ip, int port)
    {
        return CreateMelsecLike(new MelsecMcUdp(ip, port), settings);
    }

    private static DeviceTcpNet CreateMelsecAsciiUdp(PlcCommunicationSettings settings, string ip, int port)
    {
        return CreateMelsecLike(new MelsecMcAsciiUdp(ip, port), settings);
    }

    private static T CreateMelsecLike<T>(T device, PlcCommunicationSettings settings)
        where T : MelsecMcNet
    {
        device.NetworkNumber = ParseByte(settings, "networkNumber", 0);
        device.PLCNumber = ParseByte(settings, "plcNumber", byte.MaxValue);
        device.NetworkStationNumber = ParseByte(settings, "networkStationNumber", 0);
        device.TargetIOStation = ParseUShort(settings, "targetIOStation", 1023);
        device.EnableWriteBitToWordRegister = ParseBoolOption(settings, "enableWriteBitToWordRegister", false);
        return device;
    }

    private static DeviceSerialPort CreateMelsecA3C(PlcCommunicationSettings settings)
    {
        var device = new MelsecA3CNet
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            SumCheck = ParseBoolOption(settings, "sumCheck", true),
            Format = ParseInt(settings, "format", 1),
            EnableWriteBitToWordRegister = ParseBoolOption(settings, "enableWriteBitToWordRegister", false)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateMelsecA3COverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new MelsecA3CNetOverTcp(ip, port)
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            SumCheck = ParseBoolOption(settings, "sumCheck", true),
            Format = ParseInt(settings, "format", 1),
            EnableWriteBitToWordRegister = ParseBoolOption(settings, "enableWriteBitToWordRegister", false)
        };
    }

    private static DeviceSerialPort CreateMelsecFxSerial(PlcCommunicationSettings settings)
    {
        var device = new MelsecFxSerial
        {
            IsNewVersion = ParseBoolOption(settings, "isNewVersion", false),
            AutoChangeBaudRate = ParseBoolOption(settings, "autoChangeBaudRate", false)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateMelsecFxSerialOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new MelsecFxSerialOverTcp(ip, port)
        {
            IsNewVersion = ParseBoolOption(settings, "isNewVersion", false),
            UseGOT = ParseBoolOption(settings, "useGOT", ParseBoolOption(settings, "got", false))
        };
    }

    private static DeviceSerialPort CreateMelsecFxLinks(PlcCommunicationSettings settings)
    {
        var device = new MelsecFxLinks
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            SumCheck = ParseBoolOption(settings, "sumCheck", true),
            Format = ParseInt(settings, "format", 1),
            WaittingTime = ParseByte(settings, "waittingTime", 0)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateMelsecFxLinksOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new MelsecFxLinksOverTcp(ip, port)
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            SumCheck = ParseBoolOption(settings, "sumCheck", true),
            Format = ParseInt(settings, "format", 1),
            WaittingTime = ParseByte(settings, "waittingTime", 0)
        };
    }

    private static DeviceTcpNet CreateSiemens(PlcCommunicationSettings settings, string ip, int port, SiemensPLCS? forcedPlcType = null)
    {
        var modelText = FirstValue(settings.Model, GetOption(settings, "plcType"), "S1200");
        var plcType = forcedPlcType ?? ResolveSiemensPlcType(modelText);
        var device = new SiemensS7Net(plcType, ip)
        {
            Port = port,
            Rack = ParseByte(settings, "rack", 0),
            Slot = ParseByte(settings, "slot", plcType == SiemensPLCS.S300 ? (byte)2 : (byte)0)
        };

        if (TryGetInt(settings, "connectionType", out var connectionType))
        {
            device.ConnectionType = (byte)Math.Clamp(connectionType, 0, byte.MaxValue);
        }

        if (TryGetIntOrHex(settings, "localTSAP", out var localTsap))
        {
            device.LocalTSAP = localTsap;
        }

        if (TryGetIntOrHex(settings, "destTSAP", out var destTsap))
        {
            device.DestTSAP = destTsap;
        }

        return device;
    }

    private static SiemensPLCS ResolveSiemensPlcType(string modelText)
    {
        var key = NormalizeProtocol(modelText);
        return key switch
        {
            "s1200" or "s7s1200" or "1200" => SiemensPLCS.S1200,
            "s1500" or "s7s1500" or "1500" => SiemensPLCS.S1500,
            "s300" or "s7s300" or "300" => SiemensPLCS.S300,
            "s400" or "s7s400" or "400" => SiemensPLCS.S400,
            "s200" or "s7s200" or "200" => SiemensPLCS.S200,
            "s200smart" or "s7s200smart" or "200smart" => SiemensPLCS.S200Smart,
            _ => Enum.TryParse<SiemensPLCS>(modelText, true, out var parsed) ? parsed : SiemensPLCS.S1200
        };
    }

    private static DeviceSerialPort CreateSiemensPpi(PlcCommunicationSettings settings)
    {
        var device = new SiemensPPI
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo <= 0 ? 2 : settings.StationNo, 0, byte.MaxValue))
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateSiemensPpiOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new SiemensPPIOverTcp(ip, port)
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo <= 0 ? 2 : settings.StationNo, 0, byte.MaxValue))
        };
    }

    private static DeviceSerialPort CreateSiemensMpi(PlcCommunicationSettings settings)
    {
        var device = new SiemensMPI
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo <= 0 ? 2 : settings.StationNo, 0, byte.MaxValue))
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static SiemensWebApi CreateSiemensWebApi(PlcCommunicationSettings settings, string ip, int port)
    {
        return new SiemensWebApi(ip, port)
        {
            UserName = FirstValue(GetOption(settings, "userName"), GetOption(settings, "username"), GetOption(settings, "account")),
            Password = GetOption(settings, "password"),
            UseHttps = ParseBoolOption(settings, "useHttps", port != 80)
        };
    }

    private static DeviceTcpNet CreateOmron(PlcCommunicationSettings settings, string ip, int port)
    {
        var device = new OmronFinsNet(ip, port)
        {
            ICF = ParseByte(settings, "icf", 128),
            GCT = ParseByte(settings, "gct", 2),
            DNA = ParseByte(settings, "dna", 0),
            DA1 = ParseByte(settings, "da1", 0),
            DA2 = ParseByte(settings, "da2", 0),
            SNA = ParseByte(settings, "sna", 0),
            SA1 = ParseByte(settings, "sa1", 1),
            SA2 = ParseByte(settings, "sa2", 0),
            SID = ParseByte(settings, "sid", 0),
            ReadSplits = ParseInt(settings, "readSplits", 500),
            ReceiveUntilEmpty = ParseBoolOption(settings, "receiveUntilEmpty", false)
        };

        var modelText = FirstValue(settings.Model, GetOption(settings, "plcType"), string.Empty);
        if (Enum.TryParse<OmronPlcType>(modelText, true, out var plcType))
        {
            device.PlcType = plcType;
        }

        return device;
    }

    private static DeviceUdpNet CreateOmronUdp(PlcCommunicationSettings settings, string ip, int port)
    {
        var device = new OmronFinsUdp(ip, port)
        {
            ICF = ParseByte(settings, "icf", 128),
            GCT = ParseByte(settings, "gct", 2),
            DNA = ParseByte(settings, "dna", 0),
            DA1 = ParseByte(settings, "da1", 0),
            DA2 = ParseByte(settings, "da2", 0),
            SNA = ParseByte(settings, "sna", 0),
            SA1 = ParseByte(settings, "sa1", 13),
            SA2 = ParseByte(settings, "sa2", 0),
            SID = ParseByte(settings, "sid", 0),
            ReadSplits = ParseInt(settings, "readSplits", 500)
        };

        var modelText = FirstValue(settings.Model, GetOption(settings, "plcType"), string.Empty);
        if (Enum.TryParse<OmronPlcType>(modelText, true, out var plcType))
        {
            device.PlcType = plcType;
        }

        return device;
    }

    private static DeviceSerialPort CreateOmronHostLink(PlcCommunicationSettings settings)
    {
        var device = new OmronHostLink();
        ConfigureOmronHostLink(device, settings);
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateOmronHostLinkOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        var device = new OmronHostLinkOverTcp(ip, port);
        ConfigureOmronHostLink(device, settings);
        return device;
    }

    private static DeviceSerialPort CreateOmronHostLinkCMode(PlcCommunicationSettings settings)
    {
        var device = new OmronHostLinkCMode
        {
            UnitNumber = ParseByte(settings, "unitNumber", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue))
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateOmronHostLinkCModeOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new OmronHostLinkCModeOverTcp(ip, port)
        {
            UnitNumber = ParseByte(settings, "unitNumber", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue))
        };
    }

    private static void ConfigureOmronHostLink(OmronHostLink device, PlcCommunicationSettings settings)
    {
        device.ICF = ParseByte(settings, "icf", 0);
        device.SID = ParseByte(settings, "sid", 0);
        device.DA2 = ParseByte(settings, "da2", 0);
        device.SA2 = ParseByte(settings, "sa2", 0);
        device.ResponseWaitTime = ParseByte(settings, "responseWaitTime", 48);
        device.UnitNumber = ParseByte(settings, "unitNumber", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue));
        device.ReadSplits = ParseInt(settings, "readSplits", 260);

        var modelText = FirstValue(settings.Model, GetOption(settings, "plcType"), string.Empty);
        if (Enum.TryParse<OmronPlcType>(modelText, true, out var plcType))
        {
            device.PlcType = plcType;
        }
    }

    private static void ConfigureOmronHostLink(OmronHostLinkOverTcp device, PlcCommunicationSettings settings)
    {
        device.ICF = ParseByte(settings, "icf", 0);
        device.SID = ParseByte(settings, "sid", 0);
        device.DA2 = ParseByte(settings, "da2", 0);
        device.SA2 = ParseByte(settings, "sa2", 0);
        device.ResponseWaitTime = ParseByte(settings, "responseWaitTime", 48);
        device.UnitNumber = ParseByte(settings, "unitNumber", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue));
        device.ReadSplits = ParseInt(settings, "readSplits", 260);

        var modelText = FirstValue(settings.Model, GetOption(settings, "plcType"), string.Empty);
        if (Enum.TryParse<OmronPlcType>(modelText, true, out var plcType))
        {
            device.PlcType = plcType;
        }
    }

    private static DeviceSerialPort CreateKeyenceNanoSerial(PlcCommunicationSettings settings)
    {
        var device = new KeyenceNanoSerial
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            UseStation = ParseBoolOption(settings, "useStation", settings.StationNo > 0)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateKeyenceNanoSerialOverTcp(PlcCommunicationSettings settings, string ip, int port)
    {
        return new KeyenceNanoSerialOverTcp(ip, port)
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            UseStation = ParseBoolOption(settings, "useStation", settings.StationNo > 0)
        };
    }

    private static DeviceSerialPort CreatePanasonicMewtocol(PlcCommunicationSettings settings, byte station)
    {
        var device = new PanasonicMewtocol(station);
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreatePanasonicMewtocolOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new PanasonicMewtocolOverTcp(ip, port, station);
    }

    private static DeviceTcpNet CreateDeltaTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new DeltaTcpNet(ip, port, station)
        {
            Series = ParseEnumOption(settings, "series", DeltaSeries.Dvp)
        };
    }

    private static DeviceSerialPort CreateDeltaSerial(PlcCommunicationSettings settings, byte station)
    {
        var device = new DeltaSerial(station)
        {
            Series = ParseEnumOption(settings, "series", DeltaSeries.Dvp)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceSerialPort CreateDeltaSerialAscii(PlcCommunicationSettings settings, byte station)
    {
        var device = new DeltaSerialAscii(station)
        {
            Series = ParseEnumOption(settings, "series", DeltaSeries.Dvp)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateDeltaSerialOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new DeltaSerialOverTcp(ip, port, station)
        {
            Series = ParseEnumOption(settings, "series", DeltaSeries.Dvp)
        };
    }

    private static DeviceTcpNet CreateDeltaSerialAsciiOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new DeltaSerialAsciiOverTcp(ip, port, station)
        {
            Series = ParseEnumOption(settings, "series", DeltaSeries.Dvp)
        };
    }

    private static T CreateAllenBradleyLike<T>(T device, PlcCommunicationSettings settings)
        where T : AllenBradleyNet
    {
        device.Slot = ParseByte(settings, "slot", 0);
        if (TryGetInt(settings, "cipCommand", out var cipCommand))
        {
            device.CipCommand = (ushort)Math.Clamp(cipCommand, 0, ushort.MaxValue);
        }

        var router = FirstValue(GetOption(settings, "messageRouter"), GetOption(settings, "router"));
        if (!string.IsNullOrWhiteSpace(router))
        {
            device.MessageRouter = new MessageRouter(router);
        }

        return device;
    }

    private static DeviceSerialPort CreateAllenBradleyDf1(PlcCommunicationSettings settings)
    {
        var device = new AllenBradleyDF1Serial
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue)),
            DstNode = ParseByte(settings, "dstNode", 0),
            SrcNode = ParseByte(settings, "srcNode", 0)
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateBeckhoffAds(PlcCommunicationSettings settings, string ip, int port)
    {
        var device = new BeckhoffAdsNet(ip, port)
        {
            AmsPort = ParseInt(settings, "amsPort", 851),
            UseAutoAmsNetID = ParseBoolOption(settings, "useAutoAmsNetId", false),
            UseTagCache = ParseBoolOption(settings, "useTagCache", false)
        };

        var target = FirstValue(GetOption(settings, "amsNetId"), GetOption(settings, "targetAmsNetId"));
        if (!string.IsNullOrWhiteSpace(target))
        {
            device.SetTargetAMSNetId(target);
        }

        var sender = FirstValue(GetOption(settings, "senderAmsNetId"), GetOption(settings, "sourceAmsNetId"));
        if (!string.IsNullOrWhiteSpace(sender))
        {
            device.SetSenderAMSNetId(sender);
        }

        return device;
    }

    private static DeviceTcpNet CreateLsFastEnet(PlcCommunicationSettings settings, string ip, int port)
    {
        return new LSFastEnet(ip, port)
        {
            BaseNo = ParseByte(settings, "baseNo", 0),
            SlotNo = ParseByte(settings, "slotNo", 3),
            CpuInfo = ParseEnumOption(settings, "cpuInfo", LSCpuInfo.XGK),
            SetCpuType = FirstValue(GetOption(settings, "cpuType"), settings.Model),
            CompanyID = FirstValue(GetOption(settings, "companyId"), "LSIS-XGT")
        };
    }

    private static DeviceSerialPort CreateLsCnet(PlcCommunicationSettings settings, byte station)
    {
        var device = new LSCnet
        {
            Station = station
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateLsCnetOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new LSCnetOverTcp(ip, port)
        {
            Station = station
        };
    }

    private static DeviceSerialPort CreateFatekProgram(PlcCommunicationSettings settings, byte station)
    {
        var device = new FatekProgram
        {
            Station = station
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateFatekProgramOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new FatekProgramOverTcp(ip, port)
        {
            Station = station
        };
    }

    private static DeviceTcpNet CreateFujiSph(PlcCommunicationSettings settings, string ip, int port)
    {
        return new FujiSPHNet(ip, port)
        {
            ConnectionID = ParseByte(settings, "connectionId", 254)
        };
    }

    private static DeviceSerialPort CreateFujiSpb(PlcCommunicationSettings settings)
    {
        var device = new FujiSPB
        {
            Station = ParseByte(settings, "station", (byte)Math.Clamp(settings.StationNo, 0, byte.MaxValue))
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateXinJeTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new XinJETcpNet(ParseEnumOption(settings, "series", XinJESeries.XC), ip, port, station);
    }

    private static DeviceSerialPort CreateXinJeSerial(PlcCommunicationSettings settings, byte station)
    {
        var device = new XinJESerial(ParseEnumOption(settings, "series", XinJESeries.XC), station);
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateXinJeSerialOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new XinJESerialOverTcp(ParseEnumOption(settings, "series", XinJESeries.XC), ip, port, station);
    }

    private static DeviceTcpNet CreateXinJeInternal(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new XinJEInternalNet(ip, port, station)
        {
            IsStringReverse = ParseBoolOption(settings, "isStringReverse", false)
        };
    }

    private static DeviceSerialPort CreateVigorSerial(PlcCommunicationSettings settings, byte station)
    {
        var device = new VigorSerial
        {
            Station = station
        };
        ConfigureSerialPort(device, settings);
        return device;
    }

    private static DeviceTcpNet CreateVigorSerialOverTcp(PlcCommunicationSettings settings, string ip, int port, byte station)
    {
        return new VigorSerialOverTcp(ip, port)
        {
            Station = station
        };
    }

    private static DeviceTcpNet CreateMemobus(PlcCommunicationSettings settings, string ip, int port)
    {
        return new MemobusTcpNet(ip, port)
        {
            CpuTo = ParseByte(settings, "cpuTo", 2),
            CpuFrom = ParseByte(settings, "cpuFrom", 1)
        };
    }

    private static DeviceTcpNet CreateYokogawa(PlcCommunicationSettings settings, string ip, int port)
    {
        return new YokogawaLinkTcp(ip, port)
        {
            CpuNumber = ParseByte(settings, "cpuNumber", 1)
        };
    }

    private async Task<OperateResult<byte>> ReadByteAsync(IReadWriteNet readWrite, string address)
    {
        if (readWrite is InovanceTcpNet inovance)
        {
            return await inovance.ReadByteAsync(address).ConfigureAwait(false);
        }

        var result = await readWrite.ReadAsync(address, 1).ConfigureAwait(false);
        return result.IsSuccess
            ? OperateResult.CreateSuccessResult(result.Content.Length == 0 ? (byte)0 : result.Content[0])
            : OperateResult.CreateFailedResult<byte>(result);
    }

    private async Task<object> InvokeDateReadAsync(string address)
    {
        if (_device is SiemensS7Net siemens)
        {
            return await siemens.ReadDateTimeAsync(address).ConfigureAwait(false);
        }

        return await (_readWrite ?? throw new InvalidOperationException("PLC is not connected.")).ReadStringAsync(address, 8).ConfigureAwait(false);
    }

    private async Task<OperateResult> InvokeDateWriteAsync(string address, DateTime value)
    {
        if (_device is SiemensS7Net siemens)
        {
            return await siemens.WriteAsync(address, value).ConfigureAwait(false);
        }

        return await (_readWrite ?? throw new InvalidOperationException("PLC is not connected.")).WriteAsync(address, value.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private async Task<object?> InvokeMethodAsync(object target, string methodName, IReadOnlyList<string> arguments)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(method => method.GetParameters().Length)
            .ToArray();

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Count)
            {
                continue;
            }

            object?[] converted;
            try
            {
                converted = parameters
                    .Select((parameter, index) => ConvertArgument(arguments[index], parameter.ParameterType))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            var result = method.Invoke(target, converted);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                if (task.GetType().IsGenericType)
                {
                    return task.GetType().GetProperty("Result")?.GetValue(task);
                }

                return OperateResult.CreateSuccessResult();
            }

            return result;
        }

        throw new MissingMethodException(target.GetType().Name, $"{methodName}({arguments.Count} args)");
    }

    private static object? ConvertArgument(string value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type == typeof(string))
        {
            return value;
        }

        if (type == typeof(bool))
        {
            return ParseBool(value);
        }

        if (type == typeof(byte))
        {
            return ParseByte(value);
        }

        if (type == typeof(short))
        {
            return short.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(ushort))
        {
            return ushort.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(int))
        {
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(uint))
        {
            return uint.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(long))
        {
            return long.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(ulong))
        {
            return ulong.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(float))
        {
            return float.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(double))
        {
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(DateTime))
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(byte[]))
        {
            return ParseBytes(value);
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType() ?? typeof(string);
            var items = SplitList(value)
                .Select(item => ConvertArgument(item, elementType))
                .ToArray();
            var array = Array.CreateInstance(elementType, items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                array.SetValue(items[i], i);
            }

            return array;
        }

        if (type.IsEnum)
        {
            return Enum.Parse(type, value, true);
        }

        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static PlcOperationResult ToOperationResult(object? result)
    {
        if (result is null)
        {
            return PlcOperationResult.Success();
        }

        if (result is OperateResult operateResult)
        {
            if (!operateResult.IsSuccess)
            {
                return PlcOperationResult.Failure(operateResult.Message);
            }

            var content = result.GetType().GetProperty("Content")?.GetValue(result);
            return PlcOperationResult.Success(FormatContent(content), SerializeContent(content));
        }

        return PlcOperationResult.Success(FormatContent(result), SerializeContent(result));
    }

    private static string? FormatContent(object? content)
    {
        return content switch
        {
            null => null,
            bool value => value ? "1" : "0",
            byte[] bytes => Convert.ToHexString(bytes),
            bool[] values => string.Join(",", values.Select(item => item ? "1" : "0")),
            Array values => string.Join(",", values.Cast<object?>().Select(FormatContent)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => content.ToString()
        };
    }

    private static string? SerializeContent(object? content)
    {
        if (content is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(content, JsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(FormatContent(content), JsonOptions);
        }
    }

    private static void EnsureSuccess(OperateResult result, string fallback)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message);
        }
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot(DisplayName, state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private string DisplayName => $"CVCommunication PLC ({_settings.Protocol})";

    private string GetOption(string key, string fallback)
    {
        return GetOption(_settings, key, fallback);
    }

    private static string GetOption(PlcCommunicationSettings settings, string key, string fallback = "")
    {
        return settings.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static string FirstValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string NormalizeProtocol(string? protocol)
    {
        return new string((protocol ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static (PlcValueType ValueType, string Address) ExtractTypedAddress(string address, PlcValueType fallback)
    {
        var trimmed = address.Trim();
        var index = trimmed.IndexOf(':');
        if (index <= 0)
        {
            return (fallback, trimmed);
        }

        var prefix = trimmed[..index].Trim();
        if (Enum.TryParse<PlcValueType>(prefix, true, out var type))
        {
            return (type, trimmed[(index + 1)..].Trim());
        }

        return (fallback, trimmed);
    }

    private static PlcValueType InferWriteType(string address, string value)
    {
        if (IsLikelyBitAddress(address) && IsBoolText(value))
        {
            return PlcValueType.Bool;
        }

        if (short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return PlcValueType.Int16;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return PlcValueType.Double;
        }

        return PlcValueType.String;
    }

    private static bool IsLikelyBitAddress(string address)
    {
        var value = address.Trim();
        if (value.Contains('.') && !value.StartsWith("D", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Length > 0 && char.ToUpperInvariant(value[0]) is 'X' or 'Y' or 'M' or 'B' or 'S' or 'I' or 'Q';
    }

    private static ushort NormalizeLength(ushort length)
    {
        return length == 0 ? (ushort)1 : length;
    }

    private static Encoding ResolveEncoding(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.ASCII;
        }

        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch
        {
            return Encoding.ASCII;
        }
    }

    private static byte ParseByte(string value)
    {
        return byte.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool ParseBool(string value)
    {
        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw new FormatException($"'{value}' is not a valid bool value.")
        };
    }

    private static bool IsBoolText(string value)
    {
        var text = value.Trim();
        return text is "1" or "0" ||
            bool.TryParse(text, out _);
    }

    private static T[] ParseArray<T>(PlcWriteCommand command, Func<string, T> parser)
    {
        return (command.Values.Count > 0 ? command.Values : SplitList(command.Value))
            .Select(parser)
            .ToArray();
    }

    private static byte[] ParseBytes(PlcWriteCommand command)
    {
        return command.Values.Count > 0
            ? command.Values.Select(ParseByte).ToArray()
            : ParseBytes(command.Value);
    }

    private static byte[] ParseBytes(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (text.Contains(',') || text.Contains(' '))
        {
            return SplitList(text).Select(ParseByte).ToArray();
        }

        return Convert.FromHexString(text);
    }

    private static IReadOnlyList<string> SplitList(string value)
    {
        return value
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static byte ParseByte(PlcCommunicationSettings settings, string key, byte fallback)
    {
        return TryGetInt(settings, key, out var value)
            ? (byte)Math.Clamp(value, 0, byte.MaxValue)
            : fallback;
    }

    private static ushort ParseUShort(PlcCommunicationSettings settings, string key, ushort fallback)
    {
        return TryGetInt(settings, key, out var value)
            ? (ushort)Math.Clamp(value, 0, ushort.MaxValue)
            : fallback;
    }

    private static int ParseInt(PlcCommunicationSettings settings, string key, int fallback)
    {
        return TryGetInt(settings, key, out var value) ? value : fallback;
    }

    private static bool ParseBoolOption(PlcCommunicationSettings settings, string key, bool fallback)
    {
        return settings.Options.TryGetValue(key, out var value) && IsBoolText(value)
            ? ParseBool(value)
            : fallback;
    }

    private static TEnum ParseEnumOption<TEnum>(PlcCommunicationSettings settings, string key, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!settings.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(value, true, out var parsed))
        {
            return parsed;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
            : fallback;
    }

    private static bool TryParseEnumOption<TEnum>(PlcCommunicationSettings settings, string key, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (!settings.Options.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (Enum.TryParse<TEnum>(text, true, out value))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return true;
        }

        return false;
    }

    private static bool TryGetInt(PlcCommunicationSettings settings, string key, out int value)
    {
        value = 0;
        return settings.Options.TryGetValue(key, out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetIntOrHex(PlcCommunicationSettings settings, string key, out int value)
    {
        value = 0;
        if (!settings.Options.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return trimmed.Any(c => c is >= 'A' and <= 'F' or >= 'a' and <= 'f')
            ? int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
