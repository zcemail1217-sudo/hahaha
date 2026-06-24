using System.Windows;
using System.Windows.Controls;

namespace VisionStation.Vision.UI.Controls;

public partial class SimpleLoadingOverlay : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(SimpleLoadingOverlay),
        new PropertyMetadata(false));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(SimpleLoadingOverlay),
        new PropertyMetadata("加载中..."));

    public SimpleLoadingOverlay()
    {
        InitializeComponent();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
