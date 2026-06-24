using System.Windows;

namespace VisionStation.Client.Views;

public partial class ProcessStepPropertyWindow : Window
{
    public ProcessStepPropertyWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
