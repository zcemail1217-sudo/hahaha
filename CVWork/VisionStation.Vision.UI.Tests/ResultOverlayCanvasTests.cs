using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionStation.Domain;
using VisionStation.Vision.UI.Controls;
using VisionStation.Vision.UI.Models;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ResultOverlayCanvasTests
{
    [Fact]
    public void LineOverlayKeepsScreenThicknessWhenAncestorIsZoomed()
    {
        RunOnSta(() =>
        {
            var surface = RenderLine(scale: 4);
            var bitmap = surface.Bitmap;
            var thickness = MeasureMaximumGreenRun(bitmap, y: 200);

            Assert.InRange(thickness, 1, 3);
        });
    }

    [Fact]
    public void LineOverlayKeepsScreenThicknessAfterAncestorZoomChanges()
    {
        RunOnSta(() =>
        {
            var surface = RenderLine(scale: 1);
            surface.ScaledContent.RenderTransform = new ScaleTransform(4, 4);
            surface.ScaledContent.InvalidateVisual();

            var bitmap = RenderRoot(surface.Root);
            var thickness = MeasureMaximumGreenRun(bitmap, y: 200);

            Assert.InRange(thickness, 1, 3);
        });
    }

    private static TestSurface RenderLine(double scale)
    {
        var canvas = new ResultOverlayCanvas
        {
            Width = 100,
            Height = 100,
            ImageFrame = CreateFrame(100, 100),
            ItemsSource = new[]
            {
                new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.LineSegment,
                    State = VisionOverlayState.Ok,
                    X = 50,
                    Y = 0,
                    X2 = 50,
                    Y2 = 100
                }
            }
        };

        var scaledContent = new Grid
        {
            Width = 100,
            Height = 100,
            RenderTransform = new ScaleTransform(scale, scale),
            RenderTransformOrigin = new Point(0, 0)
        };
        scaledContent.Children.Add(canvas);

        var root = new Grid
        {
            Width = 400,
            Height = 400,
            Background = Brushes.Transparent
        };
        root.Children.Add(scaledContent);

        root.Measure(new Size(400, 400));
        root.Arrange(new Rect(0, 0, 400, 400));
        root.UpdateLayout();

        return new TestSurface(root, scaledContent, RenderRoot(root));
    }

    private static RenderTargetBitmap RenderRoot(UIElement root)
    {
        var bitmap = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static int MeasureMaximumGreenRun(BitmapSource bitmap, int y)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

        var current = 0;
        var max = 0;
        for (var x = 0; x < bitmap.PixelWidth; x++)
        {
            var offset = (y * bitmap.PixelWidth + x) * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var alpha = pixels[offset + 3];
            if (alpha > 0 && green > 120 && red < 140 && blue < 170)
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }

        return max;
    }

    private static ImageFrame CreateFrame(int width, int height)
    {
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            new byte[width * height],
            DateTimeOffset.UtcNow,
            "test");
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed record TestSurface(Grid Root, Grid ScaledContent, RenderTargetBitmap Bitmap);
}
