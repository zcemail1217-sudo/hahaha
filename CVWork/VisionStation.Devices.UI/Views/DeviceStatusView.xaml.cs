using System.Windows.Controls;
using System.Windows.Input;
using VisionStation.Devices.UI.ViewModels;

namespace VisionStation.Devices.UI.Views;

public partial class DeviceStatusView : UserControl
{
    public DeviceStatusView()
    {
        InitializeComponent();
    }

    private async void JogButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DeviceStatusViewModel viewModel ||
            sender is not Button button ||
            !bool.TryParse(button.Tag?.ToString(), out var positive))
        {
            return;
        }

        button.CaptureMouse();
        await viewModel.StartJogAsync(positive);
        e.Handled = true;
    }

    private async void JogButtonPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DeviceStatusViewModel viewModel)
        {
            return;
        }

        if (sender is Button button && button.IsMouseCaptured)
        {
            button.ReleaseMouseCapture();
        }

        await viewModel.StopJogAsync();
        e.Handled = true;
    }

    private async void JogButtonLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (DataContext is DeviceStatusViewModel viewModel)
        {
            await viewModel.StopJogAsync();
        }
    }
}
