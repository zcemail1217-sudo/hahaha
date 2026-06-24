using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal static class ImageFrameMatFactory
{
    public static Mat ToGrayMat(ImageFrame frame)
    {
        var sourceType = frame.Format switch
        {
            PixelFormatKind.Gray8 => MatType.CV_8UC1,
            PixelFormatKind.Bgr24 => MatType.CV_8UC3,
            _ => MatType.CV_8UC4
        };

        using var source = Mat.FromPixelData(frame.Height, frame.Width, sourceType, frame.Pixels, frame.Stride);
        var gray = new Mat();
        switch (frame.Format)
        {
            case PixelFormatKind.Gray8:
                source.CopyTo(gray);
                break;
            case PixelFormatKind.Bgr24:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                break;
            default:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
                break;
        }

        return gray;
    }
}
