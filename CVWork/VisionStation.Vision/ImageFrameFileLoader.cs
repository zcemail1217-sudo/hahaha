using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal static class ImageFrameFileLoader
{
    private static readonly string[] SupportedImageExtensions =
    [
        ".bmp",
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff"
    ];

    public static ImageFrame LoadFile(string path, bool convertColorToGray)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Image path cannot be empty.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Image file does not exist.", fullPath);
        }

        using var source = Cv2.ImRead(
            fullPath,
            convertColorToGray ? ImreadModes.Grayscale : ImreadModes.Unchanged);
        if (source.Empty())
        {
            throw new InvalidOperationException($"Unable to read image file '{fullPath}'.");
        }

        using var normalized = NormalizeMat(source, convertColorToGray);
        return CreateFrame(normalized, fullPath);
    }

    public static ImageFrame LoadFirstFromDirectory(string directory, bool convertColorToGray)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Image directory cannot be empty.", nameof(directory));
        }

        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Image directory does not exist: {fullDirectory}");
        }

        var file = Directory
            .EnumerateFiles(fullDirectory)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return file is null
            ? throw new FileNotFoundException($"No supported image files found in directory '{fullDirectory}'.")
            : LoadFile(file, convertColorToGray);
    }

    public static ImageFrame ToGray8(ImageFrame frame)
    {
        if (frame.Format == PixelFormatKind.Gray8)
        {
            return frame;
        }

        using var source = CreateMat(frame);
        using var gray = new Mat();
        Cv2.CvtColor(
            source,
            gray,
            frame.Format == PixelFormatKind.Bgra32
                ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.BGR2GRAY);

        return CreateFrame(gray, frame.Source);
    }

    private static Mat NormalizeMat(Mat source, bool convertColorToGray)
    {
        Mat? converted = null;
        var normalized = source;
        if (source.Depth() != MatType.CV_8U)
        {
            converted = new Mat();
            source.ConvertTo(converted, MatType.CV_8U);
            normalized = converted;
        }

        try
        {
            if (convertColorToGray && normalized.Channels() != 1)
            {
                var gray = new Mat();
                Cv2.CvtColor(
                    normalized,
                    gray,
                    normalized.Channels() == 4
                        ? ColorConversionCodes.BGRA2GRAY
                        : ColorConversionCodes.BGR2GRAY);
                return gray;
            }

            if (normalized.Channels() is not (1 or 3 or 4))
            {
                var gray = new Mat();
                Cv2.CvtColor(normalized, gray, ColorConversionCodes.BGR2GRAY);
                return gray;
            }

            return normalized.Clone();
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static Mat CreateMat(ImageFrame frame)
    {
        var type = frame.Format switch
        {
            PixelFormatKind.Gray8 => MatType.CV_8UC1,
            PixelFormatKind.Bgr24 => MatType.CV_8UC3,
            PixelFormatKind.Bgra32 => MatType.CV_8UC4,
            _ => MatType.CV_8UC1
        };

        return Mat.FromPixelData(frame.Height, frame.Width, type, frame.Pixels, frame.Stride).Clone();
    }

    private static ImageFrame CreateFrame(Mat mat, string source)
    {
        using var continuous = mat.Clone();
        var format = continuous.Channels() switch
        {
            1 => PixelFormatKind.Gray8,
            3 => PixelFormatKind.Bgr24,
            4 => PixelFormatKind.Bgra32,
            _ => PixelFormatKind.Gray8
        };
        var stride = (int)continuous.Step();
        continuous.GetArray(out byte[] pixels);

        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            continuous.Width,
            continuous.Height,
            stride,
            format,
            pixels,
            DateTimeOffset.Now,
            source);
    }
}
