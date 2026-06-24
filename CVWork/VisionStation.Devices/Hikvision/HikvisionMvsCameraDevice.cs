using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MvCamCtrl.NET;
using VisionStation.Domain;

namespace VisionStation.Devices.Hikvision
{


    public sealed class HikvisionMvsCameraDevice : ICameraDevice, ICameraDeviceDiscovery, IConfigurableCameraDevice, ICameraDiagnosticsProvider, IDisposable
    {
        private const int GrabTimeoutMilliseconds = 3000;
        private const int DefaultHeartbeatTimeoutMilliseconds = 3000;
        private const int DefaultImageNodeCount = 3;
        private const int ReconnectRetryCount = 2;
        private const int ReconnectDelayMilliseconds = 300;
        private readonly SemaphoreSlim _syncRoot = new SemaphoreSlim(1, 1);
        private readonly object _diagnosticsLock = new object();
        private readonly MyCamera.cbExceptiondelegate _exceptionCallback;
        private MyCamera? _camera;
        private IntPtr _driverBuffer;
        private uint _driverBufferSize;
        private IntPtr _convertBuffer;
        private uint _convertBufferSize;
        private bool _isGrabbing;
        private bool _disposed;
        private bool _manualDisconnectRequested;
        private int _backgroundReconnectRunning;
        private int _heartbeatTimeoutMilliseconds = DefaultHeartbeatTimeoutMilliseconds;
        private bool _clearBufferBeforeTrigger = true;
        private string _selectedDeviceId = string.Empty;
        private string _selectedDisplayName = "海康 MVS 相机";
        private string _triggerSource = "连续采集";
        private double _exposureTimeMs;
        private DeviceSnapshot _snapshot = new DeviceSnapshot("海康 MVS 相机", DeviceConnectionState.Disconnected, "未连接", DateTimeOffset.Now);
        private CameraDiagnostics _diagnostics = new CameraDiagnostics
        {
            DisplayName = "海康 MVS 相机",
            ConnectionState = DeviceConnectionState.Disconnected,
            HeartbeatTimeoutMs = DefaultHeartbeatTimeoutMilliseconds,
            ClearBufferBeforeTrigger = true,
            ImageNodeCount = DefaultImageNodeCount,
            LastMessage = "未连接"
        };

        public HikvisionMvsCameraDevice()
        {
            _exceptionCallback = OnCameraException;
        }

        public event EventHandler<DeviceSnapshot>? StateChanged;

        public string DeviceId => string.IsNullOrWhiteSpace(_selectedDeviceId) ? "HIK-MVS" : _selectedDeviceId;

        public string SelectedDeviceId => _selectedDeviceId;

        public DeviceSnapshot Snapshot => _snapshot;

        public CameraDiagnostics GetDiagnostics()
        {
            lock (_diagnosticsLock)
            {
                return _diagnostics;
            }
        }

        public Task<IReadOnlyList<CameraDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<CameraDeviceInfo>>(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return EnumerateDevices().Select(device => device.Info).ToArray();
                },
                cancellationToken);
        }

        public async Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            await _syncRoot.WaitAsync(cancellationToken);
            try
            {
                var normalized = deviceId.Trim();
                if (string.Equals(_selectedDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var wasConnected = _camera != null;
                DisconnectCore();
                _selectedDeviceId = normalized;
                SetState(DeviceConnectionState.Disconnected, $"已选择相机 {normalized}");

                if (wasConnected)
                {
                    TryConnectCore(cancellationToken);
                }
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task ApplyAcquisitionSettingsAsync(CameraAcquisitionSettings settings, CancellationToken cancellationToken = default)
        {
            await _syncRoot.WaitAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.DeviceId) &&
                    !string.Equals(_selectedDeviceId, settings.DeviceId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    DisconnectCore();
                    _selectedDeviceId = settings.DeviceId.Trim();
                }

                _exposureTimeMs = settings.ExposureTimeMs;
                if (!string.IsNullOrWhiteSpace(settings.TriggerSource))
                {
                    _triggerSource = settings.TriggerSource.Trim();
                }

                _heartbeatTimeoutMilliseconds = settings.HeartbeatTimeoutMs > 0
                    ? Math.Clamp(settings.HeartbeatTimeoutMs, 1000, 60000)
                    : DefaultHeartbeatTimeoutMilliseconds;
                _clearBufferBeforeTrigger = settings.ClearBufferBeforeTrigger;
                UpdateDiagnostics(current => current with
                {
                    HeartbeatTimeoutMs = _heartbeatTimeoutMilliseconds,
                    ClearBufferBeforeTrigger = _clearBufferBeforeTrigger
                });

                if (_camera != null)
                {
                    ApplySettingsCore();
                }
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _syncRoot.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrWhiteSpace(_selectedDeviceId))
                {
                    SetState(DeviceConnectionState.Connecting, "正在枚举海康 MVS 相机");
                    var devices = await Task.Run(EnumerateDevices, cancellationToken).ConfigureAwait(false);
                    if (devices.Count == 0)
                    {
                        SetState(DeviceConnectionState.Faulted, "未发现海康 MVS 相机，请先确认 MVS 客户端可见相机");
                    }
                    else
                    {
                        SetState(DeviceConnectionState.Disconnected, $"发现 {devices.Count} 台海康 MVS 相机，尚未选择设备，请在采图工具中选择后连接");
                    }

                    return;
                }

                if (_camera != null)
                {
                    return;
                }

                TryConnectCore(cancellationToken);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await _syncRoot.WaitAsync(cancellationToken);
            try
            {
                _manualDisconnectRequested = true;
                DisconnectCore();
                SetState(DeviceConnectionState.Disconnected, "已断开");
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task<ImageFrame> GrabAsync(CancellationToken cancellationToken = default)
        {
            await _syncRoot.WaitAsync(cancellationToken);
            try
            {
                for (var attempt = 0; attempt <= ReconnectRetryCount; attempt++)
                {
                    try
                    {
                        if (_camera is null && !TryConnectCore(cancellationToken))
                        {
                            throw new InvalidOperationException(Snapshot.Message);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsSoftwareTrigger(_triggerSource))
                        {
                            DrainBufferedFrames(_camera!);
                            EnsureOk(_camera!.MV_CC_SetCommandValue_NET("TriggerSoftware"), "执行软触发");
                        }

                        return GrabFrameCore(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RememberException(ex);
                        if (attempt < ReconnectRetryCount && IsRecoverableGrabError(ex))
                        {
                            SetState(DeviceConnectionState.Connecting, $"取图失败，正在重连相机（{attempt + 1}/{ReconnectRetryCount}）：{ex.Message}");
                            DisconnectCore();
                            await Task.Delay(ReconnectDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (IsRecoverableGrabError(ex))
                        {
                            DisconnectCore();
                            SetState(DeviceConnectionState.Faulted, $"取图失败，重连后仍未恢复：{ex.Message}");
                        }
                        else
                        {
                            SetState(DeviceConnectionState.Faulted, ex.Message);
                        }

                        throw;
                    }
                }

                throw new InvalidOperationException("取图失败，重连后仍未获取图像");
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public void Dispose()
        {
            _syncRoot.Wait();
            try
            {
                if (_disposed)
                {
                    return;
                }

                _manualDisconnectRequested = true;
                DisconnectCore();
                FreeUnmanagedBuffer(ref _driverBuffer, ref _driverBufferSize);
                FreeUnmanagedBuffer(ref _convertBuffer, ref _convertBufferSize);
                _disposed = true;
            }
            finally
            {
                _syncRoot.Release();
                _syncRoot.Dispose();
            }
        }

        private bool TryConnectCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manualDisconnectRequested = false;
            SetState(DeviceConnectionState.Connecting, "正在枚举海康 MVS 相机");

            try
            {
                var devices = EnumerateDevices();
                if (devices.Count == 0)
                {
                    SetState(DeviceConnectionState.Faulted, "未发现海康 MVS 相机，请先确认 MVS 客户端可见相机");
                    return false;
                }

                var selected = SelectDiscoveredDevice(devices);
                _selectedDeviceId = selected.Info.DeviceId;
                _selectedDisplayName = selected.Info.DisplayName;

                _camera = new MyCamera();
                var device = selected.NativeInfo;
                EnsureOk(_camera.MV_CC_CreateDevice_NET(ref device), "创建设备句柄");
                EnsureOk(_camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Control, 0), "打开相机");

                ApplyTransportSettingsCore(_camera, selected);
                RegisterExceptionCallbackCore(_camera);

                _camera.MV_CC_SetEnumValue_NET("AcquisitionMode", 2);
                ApplySettingsCore();
                EnsureOk(_camera.MV_CC_StartGrabbing_NET(), "开始取流");
                _isGrabbing = true;
                UpdateDiagnostics(current => current with
                {
                    IsGrabbing = true,
                    LastError = string.Empty,
                    LastErrorCode = string.Empty,
                    LastReconnectAt = DateTimeOffset.Now
                });
                SetState(DeviceConnectionState.Connected, $"已连接 {_selectedDisplayName}");
                return true;
            }
            catch (Exception ex)
            {
                RememberException(ex);
                DisconnectCore();
                SetState(DeviceConnectionState.Faulted, ex.Message);
                return false;
            }
        }

        private DiscoveredCameraDevice SelectDiscoveredDevice(IReadOnlyList<DiscoveredCameraDevice> devices)
        {
            if (string.IsNullOrWhiteSpace(_selectedDeviceId))
            {
                return devices[0];
            }

            return devices.FirstOrDefault(device => Matches(device.Info, _selectedDeviceId)) ?? devices[0];
        }

        private void ApplyTransportSettingsCore(MyCamera camera, DiscoveredCameraDevice selected)
        {
            var packetSize = 0u;
            if (selected.NativeInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
            {
                var optimalPacketSize = camera.MV_CC_GetOptimalPacketSize_NET();
                if (optimalPacketSize > 0)
                {
                    var packetResult = camera.MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)optimalPacketSize);
                    if (packetResult == MyCamera.MV_OK)
                    {
                        packetSize = (uint)optimalPacketSize;
                    }
                    else
                    {
                        RememberSdkError(packetResult, "设置 GigE 包大小");
                    }
                }

                var heartbeatResult = camera.MV_CC_SetIntValue_NET("GevHeartbeatTimeout", (uint)_heartbeatTimeoutMilliseconds);
                if (heartbeatResult != MyCamera.MV_OK)
                {
                    RememberSdkError(heartbeatResult, "设置 GigE 心跳超时");
                }
            }

            var nodeResult = camera.MV_CC_SetImageNodeNum_NET(DefaultImageNodeCount);
            if (nodeResult != MyCamera.MV_OK)
            {
                RememberSdkError(nodeResult, "设置 SDK 图像缓存节点数");
            }

            var payloadSize = TryGetIntValue(camera, "PayloadSize");
            UpdateDiagnostics(current => current with
            {
                DeviceId = selected.Info.DeviceId,
                DisplayName = selected.Info.DisplayName,
                TransportLayer = selected.Info.TransportLayer,
                IpAddress = selected.Info.IpAddress,
                HeartbeatTimeoutMs = _heartbeatTimeoutMilliseconds,
                ClearBufferBeforeTrigger = _clearBufferBeforeTrigger,
                PacketSize = packetSize,
                PayloadSize = payloadSize,
                ImageNodeCount = nodeResult == MyCamera.MV_OK ? DefaultImageNodeCount : current.ImageNodeCount
            });
        }

        private void RegisterExceptionCallbackCore(MyCamera camera)
        {
            var result = camera.MV_CC_RegisterExceptionCallBack_NET(_exceptionCallback, IntPtr.Zero);
            if (result != MyCamera.MV_OK)
            {
                RememberSdkError(result, "注册相机异常回调");
            }

            GC.KeepAlive(_exceptionCallback);
        }

        private void OnCameraException(uint messageType, IntPtr user)
        {
            if (messageType != MyCamera.MV_EXCEPTION_DEV_DISCONNECT || _manualDisconnectRequested || _disposed)
            {
                return;
            }

            _ = Task.Run(ReconnectFromExceptionAsync);
        }

        private async Task ReconnectFromExceptionAsync()
        {
            if (Interlocked.Exchange(ref _backgroundReconnectRunning, 1) == 1)
            {
                return;
            }

            try
            {
                for (var attempt = 1; attempt <= ReconnectRetryCount + 1; attempt++)
                {
                    await _syncRoot.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_disposed || _manualDisconnectRequested || string.IsNullOrWhiteSpace(_selectedDeviceId))
                        {
                            return;
                        }

                        UpdateDiagnostics(current => current with
                        {
                            ReconnectAttempts = current.ReconnectAttempts + 1
                        });
                        SetState(DeviceConnectionState.Connecting, $"SDK 检测到相机离线，正在自动重连（{attempt}/{ReconnectRetryCount + 1}）");
                        DisconnectCore();
                        if (TryConnectCore(CancellationToken.None))
                        {
                            return;
                        }
                    }
                    finally
                    {
                        _syncRoot.Release();
                    }

                    await Task.Delay(ReconnectDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);
                }

                await _syncRoot.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_disposed && !_manualDisconnectRequested)
                    {
                        SetState(DeviceConnectionState.Faulted, "SDK 检测到相机离线，自动重连后仍未恢复");
                    }
                }
                finally
                {
                    _syncRoot.Release();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundReconnectRunning, 0);
            }
        }

        private void DisconnectCore()
        {
            if (_camera is null)
            {
                _isGrabbing = false;
                UpdateDiagnostics(current => current with { IsGrabbing = false });
                return;
            }

            try
            {
                if (_isGrabbing)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                }

                _camera.MV_CC_CloseDevice_NET();
                _camera.MV_CC_DestroyDevice_NET();
            }
            finally
            {
                _camera = null;
                _isGrabbing = false;
                FreeUnmanagedBuffer(ref _driverBuffer, ref _driverBufferSize);
                FreeUnmanagedBuffer(ref _convertBuffer, ref _convertBufferSize);
                UpdateDiagnostics(current => current with
                {
                    IsGrabbing = false,
                    PayloadSize = 0
                });
            }
        }

        private void ApplySettingsCore()
        {
            if (_camera is null)
            {
                return;
            }

            if (_exposureTimeMs > 0)
            {
                _camera.MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                _camera.MV_CC_SetFloatValue_NET("ExposureTime", (float)(_exposureTimeMs * 1000));
            }

            if (IsContinuous(_triggerSource))
            {
                _camera.MV_CC_SetEnumValue_NET("TriggerMode", 0);
                return;
            }

            _camera.MV_CC_SetEnumValue_NET("TriggerMode", 1);
            _camera.MV_CC_SetEnumValue_NET("TriggerSource", ResolveTriggerSource(_triggerSource));
        }

        private void DrainBufferedFrames(MyCamera camera)
        {
            if (!_clearBufferBeforeTrigger)
            {
                return;
            }

            var payloadSize = GetPayloadSize(camera);
            EnsureUnmanagedBuffer(ref _driverBuffer, ref _driverBufferSize, payloadSize);

            for (var index = 0; index < DefaultImageNodeCount; index++)
            {
                var staleFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
                var result = camera.MV_CC_GetOneFrameTimeout_NET(_driverBuffer, _driverBufferSize, ref staleFrameInfo, 1);
                if (result == MyCamera.MV_OK)
                {
                    UpdateLastFrameDiagnostics(staleFrameInfo, payloadSize);
                    continue;
                }

                if (result != MyCamera.MV_E_NODATA)
                {
                    RememberSdkError(result, "清理旧图缓存");
                }

                return;
            }
        }

        private ImageFrame GrabFrameCore(CancellationToken cancellationToken)
        {
            var camera = _camera ?? throw new InvalidOperationException("相机未连接");
            var payloadSize = GetPayloadSize(camera);
            EnsureUnmanagedBuffer(ref _driverBuffer, ref _driverBufferSize, payloadSize);

            var frameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
            var ret = camera.MV_CC_GetOneFrameTimeout_NET(_driverBuffer, _driverBufferSize, ref frameInfo, GrabTimeoutMilliseconds);
            EnsureOk(ret, "获取图像");
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLastFrameDiagnostics(frameInfo, payloadSize);
            return CreateImageFrame(camera, frameInfo, _driverBuffer);
        }

        private ImageFrame CreateImageFrame(MyCamera camera, MyCamera.MV_FRAME_OUT_INFO_EX frameInfo, IntPtr sourcePointer)
        {
            var width = checked((int)frameInfo.nWidth);
            var height = checked((int)frameInfo.nHeight);

            if (frameInfo.enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
            {
                var pixels = new byte[width * height];
                Marshal.Copy(sourcePointer, pixels, 0, pixels.Length);
                return CreateFrame(width, height, width, PixelFormatKind.Gray8, pixels);
            }

            if (IsMonoData(frameInfo.enPixelType))
            {
                var pixels = ConvertPixelType(camera, frameInfo, sourcePointer, MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8, checked((uint)(width * height)));
                return CreateFrame(width, height, width, PixelFormatKind.Gray8, pixels);
            }

            if (IsColorData(frameInfo.enPixelType))
            {
                var bgr = ConvertPixelType(camera, frameInfo, sourcePointer, MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed, checked((uint)(width * height * 3)));
                for (var i = 0; i < bgr.Length; i += 3)
                {
                    var blue = bgr[i];
                    bgr[i] = bgr[i + 2];
                    bgr[i + 2] = blue;
                }

                return CreateFrame(width, height, width * 3, PixelFormatKind.Bgr24, bgr);
            }

            throw new NotSupportedException($"不支持的海康像素格式：{frameInfo.enPixelType}");
        }

        private byte[] ConvertPixelType(
            MyCamera camera,
            MyCamera.MV_FRAME_OUT_INFO_EX frameInfo,
            IntPtr sourcePointer,
            MyCamera.MvGvspPixelType targetPixelType,
            uint targetBufferSize)
        {
            EnsureUnmanagedBuffer(ref _convertBuffer, ref _convertBufferSize, targetBufferSize);
            var convert = new MyCamera.MV_PIXEL_CONVERT_PARAM
            {
                nWidth = frameInfo.nWidth,
                nHeight = frameInfo.nHeight,
                pSrcData = sourcePointer,
                nSrcDataLen = frameInfo.nFrameLen,
                enSrcPixelType = frameInfo.enPixelType,
                enDstPixelType = targetPixelType,
                pDstBuffer = _convertBuffer,
                nDstBufferSize = _convertBufferSize
            };

            EnsureOk(camera.MV_CC_ConvertPixelType_NET(ref convert), "转换像素格式");
            var pixels = new byte[checked((int)targetBufferSize)];
            Marshal.Copy(_convertBuffer, pixels, 0, pixels.Length);
            return pixels;
        }

        private ImageFrame CreateFrame(int width, int height, int stride, PixelFormatKind format, byte[] pixels)
        {
            return new ImageFrame(
                Guid.NewGuid().ToString("N"),
                width,
                height,
                stride,
                format,
                pixels,
                DateTimeOffset.Now,
                DeviceId);
        }

        private static uint GetPayloadSize(MyCamera camera)
        {
            var value = new MyCamera.MVCC_INTVALUE();
            EnsureOk(camera.MV_CC_GetIntValue_NET("PayloadSize", ref value), "读取 PayloadSize");
            return Math.Max(value.nCurValue, 1u);
        }

        private static uint TryGetIntValue(MyCamera camera, string key)
        {
            var value = new MyCamera.MVCC_INTVALUE();
            return camera.MV_CC_GetIntValue_NET(key, ref value) == MyCamera.MV_OK
                ? value.nCurValue
                : 0;
        }

        private void UpdateLastFrameDiagnostics(MyCamera.MV_FRAME_OUT_INFO_EX frameInfo, uint payloadSize)
        {
            UpdateDiagnostics(current => current with
            {
                PayloadSize = payloadSize,
                Width = frameInfo.nWidth,
                Height = frameInfo.nHeight,
                FrameNumber = frameInfo.nFrameNum,
                FrameLength = frameInfo.nFrameLen,
                PixelFormat = frameInfo.enPixelType.ToString(),
                LostPacketCount = frameInfo.nLostPacket,
                LastFrameAt = DateTimeOffset.Now,
                Timestamp = DateTimeOffset.Now
            });
        }

        private void RememberException(Exception exception)
        {
            var sdkException = exception as HikvisionMvsException;
            if (sdkException is not null)
            {
                RememberSdkError(sdkException.ResultCode, exception.Message);
                return;
            }

            UpdateDiagnostics(current => current with
            {
                LastErrorCode = string.Empty,
                LastError = exception.Message,
                Timestamp = DateTimeOffset.Now
            });
        }

        private void RememberSdkError(int result, string operation)
        {
            UpdateDiagnostics(current => current with
            {
                LastErrorCode = $"0x{result:X8}",
                LastError = $"{operation}：{FormatError(result)}",
                Timestamp = DateTimeOffset.Now
            });
        }

        private static void EnsureUnmanagedBuffer(ref IntPtr buffer, ref uint currentSize, uint requiredSize)
        {
            if (requiredSize <= currentSize && buffer != IntPtr.Zero)
            {
                return;
            }

            FreeUnmanagedBuffer(ref buffer, ref currentSize);
            buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
            currentSize = requiredSize;
        }

        private static void FreeUnmanagedBuffer(ref IntPtr buffer, ref uint currentSize)
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }

            currentSize = 0;
        }

        private static IReadOnlyList<DiscoveredCameraDevice> EnumerateDevices()
        {
            var list = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            EnsureOk(MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref list), "枚举相机");

            var devices = new List<DiscoveredCameraDevice>();
            for (var index = 0; index < list.nDeviceNum; index++)
            {
                var pointer = list.pDeviceInfo[index];
                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                var native = Marshal.PtrToStructure<MyCamera.MV_CC_DEVICE_INFO>(pointer);
                devices.Add(new DiscoveredCameraDevice(CreateDeviceInfo(native, index), native));
            }

            return devices;
        }

        private static CameraDeviceInfo CreateDeviceInfo(MyCamera.MV_CC_DEVICE_INFO device, int index)
        {
            var accessible = IsDeviceAccessible(device);
            var accessStatus = accessible ? "可控制" : "被占用/无控制权限";
            if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
            {
                var gige = ReadSpecialInfo<MyCamera.MV_GIGE_DEVICE_INFO>(device.SpecialInfo.stGigEInfo);
                var vendor = Clean(gige.chManufacturerName);
                var model = Clean(gige.chModelName);
                var serial = Clean(gige.chSerialNumber);
                var userDefinedName = Clean(gige.chUserDefinedName);
                var ipAddress = FormatIp(gige.nCurrentIp);
                var name = CreateCameraName(userDefinedName, vendor, model);
                var id = CreateDeviceId(serial, ipAddress, "GigE", index);

                return new CameraDeviceInfo
                {
                    DeviceId = id,
                    DisplayName = CreateDisplayName("GigE", name, serial, ipAddress),
                    Vendor = vendor,
                    Model = model,
                    SerialNumber = serial,
                    UserDefinedName = userDefinedName,
                    TransportLayer = "GigE",
                    IpAddress = ipAddress,
                    SubnetMask = FormatIp(gige.nCurrentSubNetMask),
                    Gateway = FormatIp(gige.nDefultGateWay),
                    IsAccessible = accessible,
                    AccessStatus = accessStatus
                };
            }

            if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
            {
                var usb = ReadSpecialInfo<MyCamera.MV_USB3_DEVICE_INFO>(device.SpecialInfo.stUsb3VInfo);
                var vendor = Clean(usb.chManufacturerName);
                var model = Clean(usb.chModelName);
                var serial = Clean(usb.chSerialNumber);
                var userDefinedName = Clean(usb.chUserDefinedName);
                var name = CreateCameraName(userDefinedName, vendor, model);

                return new CameraDeviceInfo
                {
                    DeviceId = CreateDeviceId(serial, string.Empty, "USB3", index),
                    DisplayName = CreateDisplayName("USB3", name, serial, string.Empty),
                    Vendor = vendor,
                    Model = model,
                    SerialNumber = serial,
                    UserDefinedName = userDefinedName,
                    TransportLayer = "USB3",
                    IsAccessible = accessible,
                    AccessStatus = accessStatus
                };
            }

            return new CameraDeviceInfo
            {
                DeviceId = $"MVS-{index + 1}",
                DisplayName = $"MVS 相机 {index + 1}",
                TransportLayer = device.nTLayerType.ToString(),
                IsAccessible = accessible,
                AccessStatus = accessStatus
            };
        }

        private static bool IsDeviceAccessible(MyCamera.MV_CC_DEVICE_INFO device)
        {
            var native = device;
            return MyCamera.MV_CC_IsDeviceAccessible_NET(ref native, MyCamera.MV_ACCESS_Control);
        }

        private static T ReadSpecialInfo<T>(byte[] bytes)
            where T : struct
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static bool Matches(CameraDeviceInfo info, string value)
        {
            return string.Equals(info.DeviceId, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(info.SerialNumber, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(info.UserDefinedName, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(info.DisplayName, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(info.IpAddress, value, StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateCameraName(string userDefinedName, string vendor, string model)
        {
            if (!string.IsNullOrWhiteSpace(userDefinedName))
            {
                return userDefinedName;
            }

            var name = $"{vendor} {model}".Trim();
            return string.IsNullOrWhiteSpace(name) ? "Hikvision Camera" : name;
        }

        private static string CreateDeviceId(string serial, string ipAddress, string transportLayer, int index)
        {
            if (!string.IsNullOrWhiteSpace(serial))
            {
                return serial;
            }

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                return $"{transportLayer}:{ipAddress}";
            }

            return $"{transportLayer}:{index + 1}";
        }

        private static string CreateDisplayName(string transportLayer, string name, string serial, string ipAddress)
        {
            var suffix = string.IsNullOrWhiteSpace(serial) ? string.Empty : $" ({serial})";
            var ip = string.IsNullOrWhiteSpace(ipAddress) ? string.Empty : $" {ipAddress}";
            return $"{transportLayer}: {name}{suffix}{ip}";
        }

        private static string Clean(string? value)
        {
            return value?.Trim('\0', ' ', '\t', '\r', '\n') ?? string.Empty;
        }

        private static string FormatIp(uint value)
        {
            if (value == 0)
            {
                return string.Empty;
            }

            return $"{(value & 0xff000000) >> 24}.{(value & 0x00ff0000) >> 16}.{(value & 0x0000ff00) >> 8}.{value & 0x000000ff}";
        }

        private static bool IsContinuous(string triggerSource)
        {
            return string.IsNullOrWhiteSpace(triggerSource) ||
                   triggerSource.Contains("连续", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(triggerSource, "Continuous", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSoftwareTrigger(string triggerSource)
        {
            return triggerSource.Contains("软件", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(triggerSource, "Software", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(triggerSource, "SoftwareTrigger", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsRecoverableGrabError(Exception exception)
        {
            var sdkException = exception as HikvisionMvsException;
            if (sdkException is null)
            {
                return false;
            }

            switch (sdkException.ResultCode)
            {
                case MyCamera.MV_E_HANDLE:
                case MyCamera.MV_E_BUSY:
                case MyCamera.MV_E_NETER:
                case MyCamera.MV_E_PACKET:
                case MyCamera.MV_E_BUFOVER:
                case MyCamera.MV_E_GC_TIMEOUT:
                case MyCamera.MV_E_PRECONDITION:
                case MyCamera.MV_E_CALLORDER:
                case MyCamera.MV_E_ABNORMAL_IMAGE:
                case MyCamera.MV_E_USB_READ:
                case MyCamera.MV_E_USB_WRITE:
                case MyCamera.MV_E_USB_DEVICE:
                case MyCamera.MV_E_USB_DRIVER:
                case MyCamera.MV_E_USB_BANDWIDTH:
                    return true;
                case MyCamera.MV_E_NODATA:
                    return IsContinuous(_triggerSource) || IsSoftwareTrigger(_triggerSource);
                default:
                    return false;
            }
        }

        private static uint ResolveTriggerSource(string triggerSource)
        {
            if (IsSoftwareTrigger(triggerSource))
            {
                return 7;
            }

            return triggerSource.Trim().ToUpperInvariant() switch
            {
                "LINE1" => 1,
                "LINE2" => 2,
                "LINE3" => 3,
                "COUNTER" => 4,
                _ => 0
            };
        }

        private static bool IsMonoData(MyCamera.MvGvspPixelType pixelType)
        {
            return pixelType is
                MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed;
        }

        private static bool IsColorData(MyCamera.MvGvspPixelType pixelType)
        {
            return pixelType is
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12 or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_YUYV_Packed or
                MyCamera.MvGvspPixelType.PixelType_Gvsp_YCBCR411_8_CBYYCRYY;
        }

        private static void EnsureOk(int result, string operation)
        {
            if (result == MyCamera.MV_OK)
            {
                return;
            }

            throw new HikvisionMvsException(result, $"{operation}失败：{FormatError(result)}");
        }

        private static string FormatError(int result)
        {
            var code = $"0x{result:X8}";
            return result switch
            {
                MyCamera.MV_E_ACCESS_DENIED => $"{code}，设备无控制权限或已被其他程序占用，请关闭 MVS 预览/BasicDemo/其他视觉软件后重试",
                MyCamera.MV_E_BUSY => $"{code}，设备忙或网络连接异常，请确认相机没有被其他程序取流并检查网线/网卡",
                MyCamera.MV_E_NETER => $"{code}，网络错误，请确认 GigE 相机与本机网卡在同一网段且无 IP 冲突",
                MyCamera.MV_E_IP_CONFLICT => $"{code}，相机 IP 冲突，请在 MVS 中重新配置相机 IP",
                MyCamera.MV_E_NODATA => $"{code}，未取到图像，请检查触发模式、曝光和光源",
                MyCamera.MV_E_NOENOUGH_BUF => $"{code}，取图缓冲区不足",
                MyCamera.MV_E_PARAMETER => $"{code}，SDK 参数错误",
                MyCamera.MV_E_GC_ACCESS => $"{code}，相机节点当前不可写，请检查采集状态或触发模式",
                _ => code
            };
        }

        private void SetState(DeviceConnectionState state, string message)
        {
            var snapshot = new DeviceSnapshot(_selectedDisplayName, state, message, DateTimeOffset.Now);
            _snapshot = snapshot;
            UpdateDiagnostics(current => current with
            {
                DeviceId = DeviceId,
                DisplayName = _selectedDisplayName,
                ConnectionState = state,
                IsGrabbing = _isGrabbing,
                HeartbeatTimeoutMs = _heartbeatTimeoutMilliseconds,
                ClearBufferBeforeTrigger = _clearBufferBeforeTrigger,
                LastMessage = message,
                Timestamp = snapshot.Timestamp
            });
            StateChanged?.Invoke(this, snapshot);
        }

        private void UpdateDiagnostics(Func<CameraDiagnostics, CameraDiagnostics> update)
        {
            lock (_diagnosticsLock)
            {
                _diagnostics = update(_diagnostics);
            }
        }

        private sealed class HikvisionMvsException : InvalidOperationException
        {
            public HikvisionMvsException(int resultCode, string message)
                : base(message)
            {
                ResultCode = resultCode;
            }

            public int ResultCode { get; }
        }

        private sealed class DiscoveredCameraDevice
        {
            public DiscoveredCameraDevice(CameraDeviceInfo info, MyCamera.MV_CC_DEVICE_INFO nativeInfo)
            {
                Info = info;
                NativeInfo = nativeInfo;
            }

            public CameraDeviceInfo Info { get; }

            public MyCamera.MV_CC_DEVICE_INFO NativeInfo { get; }
        }
    }
}
