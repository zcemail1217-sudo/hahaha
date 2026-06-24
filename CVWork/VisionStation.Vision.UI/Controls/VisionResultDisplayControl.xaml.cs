using System.Collections;
using System.Windows;
using System.Windows.Controls;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Controls;

public partial class VisionResultDisplayControl : UserControl
{
    public static readonly DependencyProperty ImageFrameProperty = DependencyProperty.Register(
        nameof(ImageFrame),
        typeof(ImageFrame),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty OverlayItemsSourceProperty = DependencyProperty.Register(
        nameof(OverlayItemsSource),
        typeof(IEnumerable),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty HasResultProperty = DependencyProperty.Register(
        nameof(HasResult),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ResultTextProperty = DependencyProperty.Register(
        nameof(ResultText),
        typeof(string),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata("READY"));

    public static readonly DependencyProperty ResultBrushProperty = DependencyProperty.Register(
        nameof(ResultBrush),
        typeof(string),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata("#FF33D6A6"));

    public static readonly DependencyProperty ShowIdleStateProperty = DependencyProperty.Register(
        nameof(ShowIdleState),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ShowToolbarProperty = DependencyProperty.Register(
        nameof(ShowToolbar),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ShowPixelInfoProperty = DependencyProperty.Register(
        nameof(ShowPixelInfo),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ShowZoomInfoProperty = DependencyProperty.Register(
        nameof(ShowZoomInfo),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(true));

    public static readonly DependencyProperty CanOpenFullscreenProperty = DependencyProperty.Register(
        nameof(CanOpenFullscreen),
        typeof(bool),
        typeof(VisionResultDisplayControl),
        new PropertyMetadata(true));

    public VisionResultDisplayControl()
    {
        InitializeComponent();
    }

    public ImageFrame? ImageFrame
    {
        get => (ImageFrame?)GetValue(ImageFrameProperty);
        set => SetValue(ImageFrameProperty, value);
    }

    public IEnumerable? OverlayItemsSource
    {
        get => (IEnumerable?)GetValue(OverlayItemsSourceProperty);
        set => SetValue(OverlayItemsSourceProperty, value);
    }

    public bool HasResult
    {
        get => (bool)GetValue(HasResultProperty);
        set => SetValue(HasResultProperty, value);
    }

    public string ResultText
    {
        get => (string)GetValue(ResultTextProperty);
        set => SetValue(ResultTextProperty, value);
    }

    public string ResultBrush
    {
        get => (string)GetValue(ResultBrushProperty);
        set => SetValue(ResultBrushProperty, value);
    }

    public bool ShowIdleState
    {
        get => (bool)GetValue(ShowIdleStateProperty);
        set => SetValue(ShowIdleStateProperty, value);
    }

    public bool ShowToolbar
    {
        get => (bool)GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    public bool ShowPixelInfo
    {
        get => (bool)GetValue(ShowPixelInfoProperty);
        set => SetValue(ShowPixelInfoProperty, value);
    }

    public bool ShowZoomInfo
    {
        get => (bool)GetValue(ShowZoomInfoProperty);
        set => SetValue(ShowZoomInfoProperty, value);
    }

    public bool CanOpenFullscreen
    {
        get => (bool)GetValue(CanOpenFullscreenProperty);
        set => SetValue(CanOpenFullscreenProperty, value);
    }
}
