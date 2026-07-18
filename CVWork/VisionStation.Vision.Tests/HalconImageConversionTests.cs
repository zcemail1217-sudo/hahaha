using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconImageConversionTests
{
    [Theory]
    [InlineData(PixelFormatKind.Gray8)]
    [InlineData(PixelFormatKind.Bgr24)]
    [InlineData(PixelFormatKind.Bgra32)]
    public void ImageConversionProducesTightGray8WithoutReadingStridePadding(
        PixelFormatKind format)
    {
        ImageFrame frame = FrameWithPadding(format);

        TightGray8Image buffer = HalconImageFactory.CreateTightGray8(frame);

        Assert.Equal(3, buffer.Width);
        Assert.Equal(2, buffer.Height);
        Assert.Equal(buffer.Width * buffer.Height, buffer.Pixels.Length);
        Assert.Equal(new byte[] { 0, 255, 76, 150, 29, 255 }, buffer.Pixels.ToArray());
    }

    [Fact]
    public void TightGray8ImageDoesNotExposeMutablePixelStorage()
    {
        byte[] source = [1, 2, 3, 4];
        var image = new TightGray8Image(2, 2, source);

        source[0] = 91;
        byte[] readCopy = image.Pixels.ToArray();
        readCopy[1] = 92;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Pixels.ToArray());
        Assert.Equal(
            typeof(ReadOnlySpan<byte>),
            typeof(TightGray8Image).GetProperty(nameof(TightGray8Image.Pixels))!.PropertyType);
    }

    [Theory]
    [InlineData(0, 2, 2, PixelFormatKind.Gray8, 2)]
    [InlineData(2, 0, 2, PixelFormatKind.Gray8, 2)]
    [InlineData(2, 2, 1, PixelFormatKind.Gray8, 4)]
    [InlineData(2, 2, 5, PixelFormatKind.Bgr24, 10)]
    [InlineData(2, 2, 7, PixelFormatKind.Bgra32, 14)]
    public void InvalidFrameLayoutIsRejectedBeforeAnyBufferRead(
        int width,
        int height,
        int stride,
        PixelFormatKind format,
        int bufferLength)
    {
        var frame = new ImageFrame(
            "invalid",
            width,
            height,
            stride,
            format,
            new byte[bufferLength],
            DateTimeOffset.UnixEpoch,
            "test");

        Assert.Throws<ArgumentException>(() => HalconImageFactory.CreateTightGray8(frame));
    }

    private static ImageFrame FrameWithPadding(PixelFormatKind format)
    {
        const int width = 3;
        const int height = 2;
        int bytesPerPixel = format switch
        {
            PixelFormatKind.Gray8 => 1,
            PixelFormatKind.Bgr24 => 3,
            PixelFormatKind.Bgra32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        int stride = width * bytesPerPixel + 5;
        var pixels = Enumerable.Repeat((byte)211, stride * height).ToArray();
        byte[][] source =
        [
            [0, 0, 0, 255],
            [255, 255, 255, 17],
            [0, 0, 255, 91],
            [0, 255, 0, 37],
            [255, 0, 0, 73],
            [255, 255, 255, 129]
        ];
        for (var index = 0; index < source.Length; index++)
        {
            int row = index / width;
            int column = index % width;
            int offset = row * stride + column * bytesPerPixel;
            if (format == PixelFormatKind.Gray8)
            {
                pixels[offset] = index switch
                {
                    0 => 0,
                    1 => 255,
                    2 => 76,
                    3 => 150,
                    4 => 29,
                    _ => 255
                };
                continue;
            }

            Array.Copy(source[index], 0, pixels, offset, bytesPerPixel);
        }

        return new ImageFrame(
            "padded",
            width,
            height,
            stride,
            format,
            pixels,
            DateTimeOffset.UnixEpoch,
            "test");
    }
}
