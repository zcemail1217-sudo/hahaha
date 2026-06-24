using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace VisionStation.Vision.UI.Views;

public partial class BlobAnalysisToolDialog : Window
{
    public BlobAnalysisToolDialog()
    {
        InitializeComponent();
    }

    private void CommitFocusedInput_OnClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox textBox)
        {
            BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty)?.UpdateSource();
        }
    }
}
