using System.ComponentModel;
using System.Windows;
using VisionStation.Client.ViewModels;

namespace VisionStation.Client.Views;

public partial class ShellWindow : Window
{
    private bool _closeConfirmed;
    private bool _closePromptActive;
    private GlobalSaveViewModel? _globalSave;

    public ShellWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeRestoreIcon();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_globalSave is not null)
        {
            _globalSave.SaveFailed -= OnGlobalSaveFailed;
        }

        _globalSave = (e.NewValue as ShellWindowViewModel)?.GlobalSave;
        if (_globalSave is not null)
        {
            _globalSave.SaveFailed += OnGlobalSaveFailed;
        }
    }

    private void OnGlobalSaveFailed(object? sender, string message)
    {
        MessageBox.Show(
            this,
            $"保存未完成，请检查仍处于未保存状态的页面。\n\n{message}",
            "保存失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (_closePromptActive || DataContext is not ShellWindowViewModel viewModel)
        {
            e.Cancel = _closePromptActive;
            return;
        }

        var unsavedChanges = viewModel.GetUnsavedChanges();
        if (unsavedChanges.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        _closePromptActive = true;
        var shouldClose = false;

        try
        {
            var dialog = new UnsavedChangesDialog(unsavedChanges)
            {
                Owner = this
            };
            dialog.ShowDialog();

            switch (dialog.Decision)
            {
                case UnsavedChangesDialogDecision.SaveAndClose:
                    try
                    {
                        await viewModel.SaveUnsavedChangesAsync();
                        shouldClose = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            this,
                            $"保存未完成，软件将继续保持打开。\n\n{ex.Message}",
                            "保存失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }

                    break;
                case UnsavedChangesDialogDecision.CloseWithoutSaving:
                    shouldClose = true;
                    break;
            }
        }
        finally
        {
            _closePromptActive = false;
        }

        if (!shouldClose)
        {
            return;
        }

        _closeConfirmed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeRestoreIcon is null)
        {
            return;
        }

        MaximizeRestoreIcon.Text = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }
}
