using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using VisionStation.Domain;
using VisionStation.Vision.UI.Controls;
using VisionStation.Vision.UI.Models;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class RoiEditorCanvasTests
{
    [Fact]
    public void ArmedPlacementClickInsideExistingRoiExecutesPlacementCommand()
    {
        RunOnSta(() =>
        {
            var placementCount = 0;
            var canvas = new RoiEditorCanvas
            {
                Width = 100,
                Height = 100,
                ImageFrame = CreateFrame(100, 100),
                ItemsSource = new[]
                {
                    new RoiEditorItem
                    {
                        Id = "search-roi",
                        Name = "Search ROI",
                        Shape = RoiShapeKind.Rectangle,
                        X = 0,
                        Y = 0,
                        Width = 100,
                        Height = 100
                    }
                },
                IsPlacementArmed = true,
                PlacementCommand = new TestCommand(_ => placementCount++)
            };
            canvas.Measure(new Size(100, 100));
            canvas.Arrange(new Rect(0, 0, 100, 100));
            canvas.UpdateLayout();

            canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseDownEvent,
                Source = canvas
            });

            Assert.Equal(1, placementCount);
        });
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

    private sealed class TestCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public void Execute(object? parameter)
        {
            execute(parameter);
        }
    }
}
