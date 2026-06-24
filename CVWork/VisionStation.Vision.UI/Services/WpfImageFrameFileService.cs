using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VisionStation.Domain;
using VisionStation.Infrastructure;

namespace VisionStation.Vision.UI.Services;

public sealed class WpfImageFrameFileService : IImageFrameFileService
{
    private readonly RuntimePaths _paths;

    public WpfImageFrameFileService(RuntimePaths paths)
    {
        _paths = paths;
    }

    public Task<ImageFrame?> PickImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = new OpenFileDialog
        {
            Title = "选择图像",
            Filter = "Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All Files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<ImageFrame?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ImageFrame?>(LoadImage(dialog.FileName));
    }

    public Task<ImageFrame> LoadImageAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图像路径不能为空", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("图像文件不存在", path);
        }

        return Task.FromResult(LoadImage(path));
    }

    public Task<string?> PickDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = new OpenFileDialog
        {
            Title = "选择图像目录",
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "选择此文件夹"
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(Path.GetDirectoryName(dialog.FileName));
    }

    public Task<string?> SaveImageAsync(ImageFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.ManualImageExportDirectory);

        var dialog = new SaveFileDialog
        {
            Title = "图像另存",
            Filter = "Bitmap Image|*.bmp",
            FileName = $"{frame.Timestamp:yyyyMMdd_HHmmss}_{frame.Id}.bmp",
            InitialDirectory = _paths.ManualImageExportDirectory,
            AddExtension = true,
            DefaultExt = ".bmp",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<string?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var savePath = ResolveManualExportPath(dialog.FileName, frame);
        SaveBitmap(frame, savePath);
        return Task.FromResult<string?>(savePath);
    }

    private static ImageFrame LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            converted.PixelWidth,
            converted.PixelHeight,
            stride,
            PixelFormatKind.Bgra32,
            pixels,
            DateTimeOffset.Now,
            path);
    }

    private static void SaveBitmap(ImageFrame frame, string path)
    {
        var pixelFormat = frame.Format switch
        {
            PixelFormatKind.Gray8 => PixelFormats.Gray8,
            PixelFormatKind.Bgr24 => PixelFormats.Bgr24,
            PixelFormatKind.Bgra32 => PixelFormats.Bgra32,
            _ => PixelFormats.Bgra32
        };

        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            pixelFormat,
            null,
            frame.Pixels,
            frame.Stride);
        bitmap.Freeze();

        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private string ResolveManualExportPath(string selectedPath, ImageFrame frame)
    {
        var selectedFullPath = Path.GetFullPath(selectedPath);
        var exportDirectory = Path.GetFullPath(_paths.ManualImageExportDirectory);
        if (IsInsideDirectory(selectedFullPath, exportDirectory))
        {
            return selectedFullPath;
        }

        var fileName = Path.GetFileName(selectedFullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{frame.Timestamp:yyyyMMdd_HHmmss}_{frame.Id}.bmp";
        }

        return GetAvailablePath(Path.Combine(exportDirectory, fileName));
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}_{DateTimeOffset.Now:yyyyMMddHHmmssfff}{extension}");
    }
}
