using System.Windows;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Vision.UI.Views;

public partial class FlowEditorWindow : Window
{
    public FlowEditorWindow(VisionDebugViewModel viewModel)
    {
        InitializeComponent();
        EditorHost.Content = new FlowEditorView(viewModel);
    }
}
