using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class SimulatedCameraDevice : ICameraDevice, ICameraDeviceDiscovery, IConfigurableCameraDevice, ICameraDiagnosticsProvider
{
    private readonly object _syncRoot = new();
    private int _frameIndex;
    private string _selectedDeviceId = "SIM-CAM-01";
    private DeviceSnapshot _snapshot = new("模拟相机 01", DeviceConnectionState.Disconnected, "未连接", DateTimeOffset.Now);

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DeviceId => _selectedDeviceId;

    public string SelectedDeviceId => _selectedDeviceId;

    public DeviceSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot;
            }
        }
    }

    public CameraDiagnostics GetDiagnostics()
    {
        lock (_syncRoot)
        {
            return new CameraDiagnostics
            {
                DeviceId = DeviceId,
                DisplayName = _snapshot.Name,
                ConnectionState = _snapshot.State,
                TransportLayer = "Simulated",
                IsGrabbing = _snapshot.State == DeviceConnectionState.Connected,
                Width = 1280,
                Height = 720,
                FrameNumber = (uint)Math.Max(_frameIndex, 0),
                PixelFormat = PixelFormatKind.Bgra32.ToString(),
                LastMessage = _snapshot.Message,
                Timestamp = DateTimeOffset.Now
            };
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Connected, "模拟取流就绪");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Disconnected, "已断开");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CameraDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CameraDeviceInfo> devices =
        [
            new CameraDeviceInfo
            {
                DeviceId = "SIM-CAM-01",
                DisplayName = "模拟相机 01",
                Vendor = "VisionStation",
                Model = "SimulatedCamera",
                SerialNumber = "SIM-CAM-01",
                TransportLayer = "Simulated"
            }
        ];

        return Task.FromResult(devices);
    }

    public Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            _selectedDeviceId = deviceId.Trim();
        }

        return Task.CompletedTask;
    }

    public Task ApplyAcquisitionSettingsAsync(CameraAcquisitionSettings settings, CancellationToken cancellationToken = default)
    {
        return SelectDeviceAsync(settings.DeviceId, cancellationToken);
    }

    public Task<ImageFrame> GrabAsync(CancellationToken cancellationToken = default)
    {
        if (Snapshot.State != DeviceConnectionState.Connected)
        {
            SetState(DeviceConnectionState.Connected, "自动连接模拟相机");
        }

        var index = Interlocked.Increment(ref _frameIndex);
        var width = 1280;
        var height = 720;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        FillBackground(pixels, width, height, stride, index);
        DrawWorkpiece(pixels, width, height, stride, index);
        DrawMeasurementMarks(pixels, width, height, stride);
        DrawBarcode(pixels, width, height, stride, index);

        var frame = new ImageFrame(
            Guid.NewGuid().ToString("N"),
            width,
            height,
            stride,
            PixelFormatKind.Bgra32,
            pixels,
            DateTimeOffset.Now,
            DeviceId);

        return Task.FromResult(frame);
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        var snapshot = new DeviceSnapshot("模拟相机 01", state, message, DateTimeOffset.Now);
        lock (_syncRoot)
        {
            _snapshot = snapshot;
        }

        StateChanged?.Invoke(this, snapshot);
    }

    private static void FillBackground(byte[] pixels, int width, int height, int stride, int frameIndex)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var glow = (byte)Math.Clamp(20 + x * 30 / width + y * 18 / height, 0, 255);
                pixels[offset] = (byte)(28 + glow / 4);
                pixels[offset + 1] = (byte)(36 + glow / 2);
                pixels[offset + 2] = (byte)(42 + frameIndex % 20);
                pixels[offset + 3] = 255;
            }
        }
    }

    private static void DrawWorkpiece(byte[] pixels, int width, int height, int stride, int frameIndex)
    {
        var centerX = width / 2 + (int)(Math.Sin(frameIndex / 8.0) * 22);
        var centerY = height / 2 + (int)(Math.Cos(frameIndex / 10.0) * 14);
        FillRect(pixels, width, height, stride, centerX - 250, centerY - 130, 500, 260, 44, 74, 82);
        DrawRect(pixels, width, height, stride, centerX - 250, centerY - 130, 500, 260, 56, 210, 232);
        FillRect(pixels, width, height, stride, centerX - 160, centerY - 62, 320, 124, 78, 96, 102);
        DrawRect(pixels, width, height, stride, centerX - 160, centerY - 62, 320, 124, 120, 240, 190);
        FillCircle(pixels, width, height, stride, centerX - 180, centerY - 86, 34, 24, 34, 42);
        DrawCircle(pixels, width, height, stride, centerX - 180, centerY - 86, 34, 92, 230, 255);
        FillCircle(pixels, width, height, stride, centerX + 180, centerY + 86, 34, 24, 34, 42);
        DrawCircle(pixels, width, height, stride, centerX + 180, centerY + 86, 34, 92, 230, 255);
    }

    private static void DrawMeasurementMarks(byte[] pixels, int width, int height, int stride)
    {
        DrawLine(pixels, width, height, stride, 390, 530, 890, 530, 90, 235, 170);
        DrawLine(pixels, width, height, stride, 390, 514, 390, 546, 90, 235, 170);
        DrawLine(pixels, width, height, stride, 890, 514, 890, 546, 90, 235, 170);
        DrawRect(pixels, width, height, stride, 450, 265, 380, 190, 255, 190, 80);
    }

    private static void DrawBarcode(byte[] pixels, int width, int height, int stride, int frameIndex)
    {
        var startX = 920;
        var startY = 235;
        FillRect(pixels, width, height, stride, startX - 14, startY - 14, 220, 94, 230, 238, 236);

        for (var i = 0; i < 24; i++)
        {
            var barWidth = 2 + ((i + frameIndex) % 4);
            var x = startX + i * 8;
            var h = 58 - (i % 3) * 8;
            FillRect(pixels, width, height, stride, x, startY, barWidth, h, 18, 24, 30);
        }
    }

    private static void FillRect(byte[] pixels, int width, int height, int stride, int x, int y, int rectWidth, int rectHeight, byte r, byte g, byte b)
    {
        var x0 = Math.Clamp(x, 0, width - 1);
        var y0 = Math.Clamp(y, 0, height - 1);
        var x1 = Math.Clamp(x + rectWidth, 0, width);
        var y1 = Math.Clamp(y + rectHeight, 0, height);

        for (var yy = y0; yy < y1; yy++)
        {
            for (var xx = x0; xx < x1; xx++)
            {
                SetPixel(pixels, stride, xx, yy, r, g, b);
            }
        }
    }

    private static void DrawRect(byte[] pixels, int width, int height, int stride, int x, int y, int rectWidth, int rectHeight, byte r, byte g, byte b)
    {
        DrawLine(pixels, width, height, stride, x, y, x + rectWidth, y, r, g, b);
        DrawLine(pixels, width, height, stride, x + rectWidth, y, x + rectWidth, y + rectHeight, r, g, b);
        DrawLine(pixels, width, height, stride, x + rectWidth, y + rectHeight, x, y + rectHeight, r, g, b);
        DrawLine(pixels, width, height, stride, x, y + rectHeight, x, y, r, g, b);
    }

    private static void DrawLine(byte[] pixels, int width, int height, int stride, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                SetPixel(pixels, stride, x0, y0, r, g, b);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void FillCircle(byte[] pixels, int width, int height, int stride, int centerX, int centerY, int radius, byte r, byte g, byte b)
    {
        var radiusSquared = radius * radius;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy <= radiusSquared && x >= 0 && x < width && y >= 0 && y < height)
                {
                    SetPixel(pixels, stride, x, y, r, g, b);
                }
            }
        }
    }

    private static void DrawCircle(byte[] pixels, int width, int height, int stride, int centerX, int centerY, int radius, byte r, byte g, byte b)
    {
        for (var angle = 0; angle < 360; angle++)
        {
            var radians = angle * Math.PI / 180.0;
            var x = centerX + (int)(Math.Cos(radians) * radius);
            var y = centerY + (int)(Math.Sin(radians) * radius);
            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                SetPixel(pixels, stride, x, y, r, g, b);
            }
        }
    }

    private static void SetPixel(byte[] pixels, int stride, int x, int y, byte r, byte g, byte b)
    {
        var offset = y * stride + x * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = 255;
    }
}
