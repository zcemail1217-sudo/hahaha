using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionStation.Vision.UI.ViewModels;
using VisionStation.Vision.UI.Views;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Controls;

public sealed class ZoomableImageSurface : ContentControl
{
    public static readonly DependencyProperty ImageFrameProperty = DependencyProperty.Register(
        nameof(ImageFrame),
        typeof(ImageFrame),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(null, OnImageFrameChanged));

    public static readonly DependencyProperty OverlayItemsSourceProperty = DependencyProperty.Register(
        nameof(OverlayItemsSource),
        typeof(IEnumerable),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(null, OnOverlayItemsSourceChanged));

    public static readonly DependencyProperty CanOpenFullscreenProperty = DependencyProperty.Register(
        nameof(CanOpenFullscreen),
        typeof(bool),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ShowToolbarProperty = DependencyProperty.Register(
        nameof(ShowToolbar),
        typeof(bool),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ShowPixelInfoProperty = DependencyProperty.Register(
        nameof(ShowPixelInfo),
        typeof(bool),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ShowZoomInfoProperty = DependencyProperty.Register(
        nameof(ShowZoomInfo),
        typeof(bool),
        typeof(ZoomableImageSurface),
        new PropertyMetadata(true));

    public static readonly DependencyProperty ZoomTextProperty = DependencyProperty.Register(
        nameof(ZoomText),
        typeof(string),
        typeof(ZoomableImageSurface),
        new PropertyMetadata("100%"));

    public static readonly DependencyProperty PixelInfoTextProperty = DependencyProperty.Register(
        nameof(PixelInfoText),
        typeof(string),
        typeof(ZoomableImageSurface),
        new PropertyMetadata("X:- Y:- Pixel:-"));

    private const double MinScale = 1.0;
    private const double MaxScale = 16.0;
    private const double ZoomStep = 1.18;
    private const string ContentPresenterPartName = "PART_ContentPresenter";
    private const string FitButtonPartName = "PART_FitButton";
    private const string ActualSizeButtonPartName = "PART_ActualSizeButton";
    private const string FullscreenButtonPartName = "PART_FullscreenButton";

    private static FullscreenImageWindow? _fullscreenWindow;
    private static ImagePreviewWindowViewModel? _fullscreenViewModel;
    private static ZoomableImageSurface? _fullscreenOwner;

    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new();
    private ContentPresenter? _contentPresenter;
    private Button? _fitButton;
    private Button? _actualSizeButton;
    private Button? _fullscreenButton;
    private bool _isPanning;
    private Point _lastPanPoint;

    static ZoomableImageSurface()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ZoomableImageSurface),
            new FrameworkPropertyMetadata(typeof(ZoomableImageSurface)));
    }

    public ZoomableImageSurface()
    {
        Focusable = true;
        ClipToBounds = true;
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

    public bool CanOpenFullscreen
    {
        get => (bool)GetValue(CanOpenFullscreenProperty);
        set => SetValue(CanOpenFullscreenProperty, value);
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

    public string ZoomText
    {
        get => (string)GetValue(ZoomTextProperty);
        private set => SetValue(ZoomTextProperty, value);
    }

    public string PixelInfoText
    {
        get => (string)GetValue(PixelInfoTextProperty);
        private set => SetValue(PixelInfoTextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        DetachTemplateEvents();
        base.OnApplyTemplate();
        _contentPresenter = GetTemplateChild(ContentPresenterPartName) as ContentPresenter;
        _fitButton = GetTemplateChild(FitButtonPartName) as Button;
        _actualSizeButton = GetTemplateChild(ActualSizeButtonPartName) as Button;
        _fullscreenButton = GetTemplateChild(FullscreenButtonPartName) as Button;

        AttachTemplateEvents();

        if (_contentPresenter is null)
        {
            return;
        }

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);

        _contentPresenter.RenderTransform = transformGroup;
        _contentPresenter.RenderTransformOrigin = new Point(0, 0);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_contentPresenter is null || ImageFrame is null)
        {
            return;
        }

        Focus();
        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        ZoomAt(e.GetPosition(this), factor);
        e.Handled = true;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || _scaleTransform.ScaleX <= MinScale)
        {
            return;
        }

        BeginPan(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var position = e.GetPosition(this);
        UpdatePixelInfo(position);

        if (!_isPanning || e.MiddleButton != MouseButtonState.Pressed)
        {
            return;
        }

        PanTo(position);
        e.Handled = true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        StopPan();
        e.Handled = true;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (!CanOpenFullscreen || ImageFrame is null)
        {
            return;
        }

        ShowFullscreen(ImageFrame);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ImageFrame is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), ZoomStep);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1.0 / ZoomStep);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                ResetView();
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                SetActualSize();
                e.Handled = true;
                break;
            case Key.F11:
                if (CanOpenFullscreen)
                {
                    ShowFullscreen(ImageFrame);
                    e.Handled = true;
                }
                break;
        }
    }

    private void AttachTemplateEvents()
    {
        if (_fitButton is not null)
        {
            _fitButton.Click += OnFitButtonClick;
        }

        if (_actualSizeButton is not null)
        {
            _actualSizeButton.Click += OnActualSizeButtonClick;
        }

        if (_fullscreenButton is not null)
        {
            _fullscreenButton.Click += OnFullscreenButtonClick;
        }
    }

    private void DetachTemplateEvents()
    {
        if (_fitButton is not null)
        {
            _fitButton.Click -= OnFitButtonClick;
        }

        if (_actualSizeButton is not null)
        {
            _actualSizeButton.Click -= OnActualSizeButtonClick;
        }

        if (_fullscreenButton is not null)
        {
            _fullscreenButton.Click -= OnFullscreenButtonClick;
        }
    }

    private void OnFitButtonClick(object sender, RoutedEventArgs e)
    {
        ResetView();
        e.Handled = true;
    }

    private void OnActualSizeButtonClick(object sender, RoutedEventArgs e)
    {
        SetActualSize();
        e.Handled = true;
    }

    private void OnFullscreenButtonClick(object sender, RoutedEventArgs e)
    {
        if (CanOpenFullscreen && ImageFrame is not null)
        {
            ShowFullscreen(ImageFrame);
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton != MouseButton.Left || _scaleTransform.ScaleX <= MinScale)
        {
            return;
        }

        BeginPan(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdatePixelInfo(e.GetPosition(this));

        if (!_isPanning)
        {
            return;
        }

        PanTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Left))
        {
            return;
        }

        StopPan();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        StopPan();
        PixelInfoText = "X:- Y:- Pixel:-";
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Left))
        {
            return;
        }

        ResetView();
        e.Handled = true;
    }

    private void BeginPan(Point position)
    {
        Focus();
        _isPanning = true;
        _lastPanPoint = position;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    private void PanTo(Point position)
    {
        _translateTransform.X += position.X - _lastPanPoint.X;
        _translateTransform.Y += position.Y - _lastPanPoint.Y;
        _lastPanPoint = position;
        CoerceOffset();
    }

    private void ResetView()
    {
        _scaleTransform.ScaleX = 1;
        _scaleTransform.ScaleY = 1;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;
        UpdateZoomText();
        InvalidateOverlayRender();
    }

    private void SetActualSize()
    {
        var frame = ImageFrame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var fitScale = Math.Min(ActualWidth / frame.Width, ActualHeight / frame.Height);
        if (fitScale <= 0)
        {
            return;
        }

        var targetScale = Math.Clamp(1.0 / fitScale, MinScale, MaxScale);
        _scaleTransform.ScaleX = targetScale;
        _scaleTransform.ScaleY = targetScale;
        _translateTransform.X = (ActualWidth - ActualWidth * targetScale) / 2;
        _translateTransform.Y = (ActualHeight - ActualHeight * targetScale) / 2;
        CoerceOffset();
        UpdateZoomText();
        InvalidateOverlayRender();
    }

    private void ZoomAt(Point anchorPoint, double factor)
    {
        if (_contentPresenter is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var oldScale = _scaleTransform.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < 0.001)
        {
            return;
        }

        var contentPoint = new Point(
            (anchorPoint.X - _translateTransform.X) / oldScale,
            (anchorPoint.Y - _translateTransform.Y) / oldScale);

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
        _translateTransform.X = anchorPoint.X - contentPoint.X * newScale;
        _translateTransform.Y = anchorPoint.Y - contentPoint.Y * newScale;

        CoerceOffset();
        UpdateZoomText();
        InvalidateOverlayRender();
    }

    private void StopPan()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        Cursor = null;
        ReleaseMouseCapture();
    }

    private void CoerceOffset()
    {
        if (_contentPresenter is null || _scaleTransform.ScaleX <= MinScale)
        {
            _translateTransform.X = 0;
            _translateTransform.Y = 0;
            return;
        }

        var scale = _scaleTransform.ScaleX;
        var scaledWidth = ActualWidth * scale;
        var scaledHeight = ActualHeight * scale;
        var minX = Math.Min(0, ActualWidth - scaledWidth);
        var minY = Math.Min(0, ActualHeight - scaledHeight);

        _translateTransform.X = Math.Clamp(_translateTransform.X, minX, 0);
        _translateTransform.Y = Math.Clamp(_translateTransform.Y, minY, 0);
    }

    private void UpdateZoomText()
    {
        ZoomText = $"{_scaleTransform.ScaleX * 100:0}%";
    }

    private void InvalidateOverlayRender()
    {
        if (_contentPresenter is null)
        {
            return;
        }

        InvalidateOverlayRender(_contentPresenter);
    }

    private static void InvalidateOverlayRender(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ResultOverlayCanvas overlay)
            {
                overlay.InvalidateVisual();
            }

            InvalidateOverlayRender(child);
        }
    }

    private void UpdatePixelInfo(Point surfacePoint)
    {
        var frame = ImageFrame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            PixelInfoText = "X:- Y:- Pixel:-";
            return;
        }

        var contentPoint = new Point(
            (surfacePoint.X - _translateTransform.X) / _scaleTransform.ScaleX,
            (surfacePoint.Y - _translateTransform.Y) / _scaleTransform.ScaleY);
        var baseScale = Math.Min(ActualWidth / frame.Width, ActualHeight / frame.Height);
        var viewportWidth = frame.Width * baseScale;
        var viewportHeight = frame.Height * baseScale;
        var viewportLeft = (ActualWidth - viewportWidth) / 2;
        var viewportTop = (ActualHeight - viewportHeight) / 2;
        var imageX = (contentPoint.X - viewportLeft) / baseScale;
        var imageY = (contentPoint.Y - viewportTop) / baseScale;

        if (imageX < 0 || imageY < 0 || imageX >= frame.Width || imageY >= frame.Height)
        {
            PixelInfoText = "X:- Y:- Pixel:-";
            return;
        }

        var x = Math.Clamp((int)Math.Floor(imageX), 0, frame.Width - 1);
        var y = Math.Clamp((int)Math.Floor(imageY), 0, frame.Height - 1);
        PixelInfoText = $"X:{x} Y:{y} {ReadPixelText(frame, x, y)}";
    }

    private static string ReadPixelText(ImageFrame frame, int x, int y)
    {
        var offset = y * frame.Stride;
        return frame.Format switch
        {
            PixelFormatKind.Gray8 when offset + x < frame.Pixels.Length =>
                $"Gray:{frame.Pixels[offset + x]}",
            PixelFormatKind.Bgr24 when offset + x * 3 + 2 < frame.Pixels.Length =>
                $"RGB:{frame.Pixels[offset + x * 3 + 2]},{frame.Pixels[offset + x * 3 + 1]},{frame.Pixels[offset + x * 3]}",
            PixelFormatKind.Bgra32 when offset + x * 4 + 2 < frame.Pixels.Length =>
                $"RGB:{frame.Pixels[offset + x * 4 + 2]},{frame.Pixels[offset + x * 4 + 1]},{frame.Pixels[offset + x * 4]}",
            _ => "Pixel:-"
        };
    }

    private void ShowFullscreen(ImageFrame frame)
    {
        if (_fullscreenWindow is not null)
        {
            _fullscreenOwner = this;
            _fullscreenViewModel!.CurrentFrame = frame;
            _fullscreenViewModel.OverlayItemsSource = OverlayItemsSource;
            if (_fullscreenWindow.WindowState == WindowState.Minimized)
            {
                _fullscreenWindow.WindowState = WindowState.Maximized;
            }

            _fullscreenWindow.Activate();
            return;
        }

        var viewModel = new ImagePreviewWindowViewModel(frame, OverlayItemsSource, "图像全屏预览");
        var window = new FullscreenImageWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current.MainWindow
        };

        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            _fullscreenWindow = null;
            _fullscreenViewModel = null;
            _fullscreenOwner = null;
        };

        _fullscreenViewModel = viewModel;
        _fullscreenWindow = window;
        _fullscreenOwner = this;
        window.Show();
    }

    private static void OnImageFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (ZoomableImageSurface)dependencyObject;
        control.ResetView();

        if (_fullscreenWindow is not null && ReferenceEquals(control, _fullscreenOwner))
        {
            _fullscreenViewModel!.CurrentFrame = (ImageFrame?)e.NewValue;
        }
    }

    private static void OnOverlayItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (ZoomableImageSurface)dependencyObject;
        if (_fullscreenWindow is not null && ReferenceEquals(control, _fullscreenOwner))
        {
            _fullscreenViewModel!.OverlayItemsSource = (IEnumerable?)e.NewValue;
        }
    }
}
