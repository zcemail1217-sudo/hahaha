using System.Windows;
using VisionStation.Vision.UI.ViewModels;
using VisionStation.Vision.UI.Views;

namespace VisionStation.Vision.UI.Services;

public sealed class WpfFlowEditorDialogService : IFlowEditorDialogService
{
    public void ShowEditor(VisionDebugViewModel viewModel)
    {
        var existing = System.Windows.Application.Current.Windows
            .OfType<FlowEditorWindow>()
            .FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new FlowEditorWindow(viewModel)
        {
            Owner = GetOwner()
        };
        window.Show();
    }

    private static Window? GetOwner()
    {
        return System.Windows.Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive) ?? System.Windows.Application.Current.MainWindow;
    }
}
