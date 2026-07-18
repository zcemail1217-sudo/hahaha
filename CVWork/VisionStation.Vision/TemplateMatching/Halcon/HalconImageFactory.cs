using System.Runtime.InteropServices;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class TightGray8Image
{
    private readonly byte[] _pixels;

    public TightGray8Image(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != checked((long)width * height))
        {
            throw new ArgumentException("A tight Gray8 buffer must contain width * height bytes.", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<byte> Pixels => _pixels;

    internal TResult UsePinnedPixels<TResult>(Func<IntPtr, TResult> usePixels)
    {
        ArgumentNullException.ThrowIfNull(usePixels);
        GCHandle handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            return usePixels(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }
}

internal static class HalconImageFactory
{
    public static TightGray8Image CreateTightGray8(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int bytesPerPixel = frame.Format switch
        {
            PixelFormatKind.Gray8 => 1,
            PixelFormatKind.Bgr24 => 3,
            PixelFormatKind.Bgra32 => 4,
            _ => throw new ArgumentException("The image pixel format is unsupported.", nameof(frame))
        };
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels is null)
        {
            throw new ArgumentException("The image dimensions and pixel buffer must be valid.", nameof(frame));
        }

        long minimumStride;
        long requiredBytes;
        try
        {
            minimumStride = checked((long)frame.Width * bytesPerPixel);
            requiredBytes = checked((long)frame.Stride * frame.Height);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The image layout exceeds supported buffer limits.", nameof(frame), exception);
        }
        if (frame.Stride < minimumStride || requiredBytes < 0 || frame.Pixels.LongLength < requiredBytes)
        {
            throw new ArgumentException("The image stride or pixel buffer length is invalid.", nameof(frame));
        }

        byte[] gray;
        try
        {
            gray = new byte[checked(frame.Width * frame.Height)];
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The image dimensions exceed supported buffer limits.", nameof(frame), exception);
        }
        for (var row = 0; row < frame.Height; row++)
        {
            int sourceRow = checked(row * frame.Stride);
            int targetRow = checked(row * frame.Width);
            if (frame.Format == PixelFormatKind.Gray8)
            {
                Buffer.BlockCopy(frame.Pixels, sourceRow, gray, targetRow, frame.Width);
                continue;
            }

            for (var column = 0; column < frame.Width; column++)
            {
                int source = checked(sourceRow + column * bytesPerPixel);
                int blue = frame.Pixels[source];
                int green = frame.Pixels[source + 1];
                int red = frame.Pixels[source + 2];
                gray[targetRow + column] = (byte)(
                    (1868 * blue + 9617 * green + 4899 * red + 8192) >> 14);
            }
        }

        return new TightGray8Image(frame.Width, frame.Height, gray);
    }

    public static TightGray8Image Crop(
        TightGray8Image source,
        int x,
        int y,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > source.Width || (long)y + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The Gray8 crop must lie inside the source image.");
        }

        var pixels = new byte[checked(width * height)];
        ReadOnlySpan<byte> sourcePixels = source.Pixels;
        for (var row = 0; row < height; row++)
        {
            sourcePixels
                .Slice(checked((y + row) * source.Width + x), width)
                .CopyTo(pixels.AsSpan(row * width, width));
        }

        return new TightGray8Image(width, height, pixels);
    }
}
