using System.Windows.Controls;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Vision.UI.Views;

public partial class FlowEditorView : UserControl
{
    public FlowEditorView(VisionDebugViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
