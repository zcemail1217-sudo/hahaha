using System.Windows;
using VisionStation.Application.Presentation;

namespace VisionStation.Client.Views;

public enum UnsavedChangesDialogDecision
{
    Cancel,
    SaveAndClose,
    CloseWithoutSaving
}

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog(IReadOnlyList<UnsavedChangeItem> items)
    {
        Items = items;
        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<UnsavedChangeItem> Items { get; }

    public UnsavedChangesDialogDecision Decision { get; private set; } = UnsavedChangesDialogDecision.Cancel;

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedChangesDialogDecision.SaveAndClose;
        DialogResult = true;
    }

    private void CloseWithoutSaving_Click(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedChangesDialogDecision.CloseWithoutSaving;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedChangesDialogDecision.Cancel;
        DialogResult = false;
    }
}
