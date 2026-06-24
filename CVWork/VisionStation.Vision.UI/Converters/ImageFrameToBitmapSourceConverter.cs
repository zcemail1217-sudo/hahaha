using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Converters;

public sealed class ImageFrameToBitmapSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ImageFrame frame)
        {
            return null;
        }

        var pixelFormat = frame.Format switch
        {
            PixelFormatKind.Bgra32 => PixelFormats.Bgra32,
            PixelFormatKind.Bgr24 => PixelFormats.Bgr24,
            PixelFormatKind.Gray8 => PixelFormats.Gray8,
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
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("图像显示转换器不支持反向转换。");
    }
}
