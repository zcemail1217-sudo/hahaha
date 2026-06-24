using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace VisionStation.Vision.UI.Converters;

public sealed class Base64PngToBitmapSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string encoded || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(System.Convert.FromBase64String(encoded));
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var bitmap = decoder.Frames.FirstOrDefault();
            bitmap?.Freeze();
            return bitmap;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Template preview images are read-only.");
    }
}
