using System.Windows;
using VisionStation.Vision.UI.ViewModels;
using VisionStation.Vision.UI.Views;

namespace VisionStation.Vision.UI.Services;

public sealed class WpfFlowEditorDialogService : IFlowEditorDialogService
{
    private readonly Lazy<VisionDebugViewModel> _viewModel;

    public WpfFlowEditorDialogService(Lazy<VisionDebugViewModel> viewModel)
    {
        _viewModel = viewModel;
    }

    public async Task ShowEditorAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default)
    {
        var viewModel = _viewModel.Value;
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            await viewModel.EnsureInitializedAsync(cancellationToken);
        }
        else
        {
            await viewModel.LoadRecipeAsync(recipeId, cancellationToken);
        }

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
