using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace VisionStation.Vision.UI.Controls;

public partial class TechLoadingOverlay : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(TechLoadingOverlay),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty IsOverlayVisibleProperty = DependencyProperty.Register(
        nameof(IsOverlayVisible),
        typeof(bool),
        typeof(TechLoadingOverlay),
        new PropertyMetadata(false));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(TechLoadingOverlay),
        new PropertyMetadata("VISION CORE ONLINE"));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(string),
        typeof(TechLoadingOverlay),
        new PropertyMetadata("Synchronizing camera, recipe, and inspection pipeline."));

    public static readonly DependencyProperty StepsSourceProperty = DependencyProperty.Register(
        nameof(StepsSource),
        typeof(IEnumerable),
        typeof(TechLoadingOverlay),
        new PropertyMetadata(null));

    public static readonly DependencyProperty MinimumVisibleMillisecondsProperty = DependencyProperty.Register(
        nameof(MinimumVisibleMilliseconds),
        typeof(int),
        typeof(TechLoadingOverlay),
        new PropertyMetadata(900));

    private CancellationTokenSource? _visibilityCts;
    private DateTimeOffset _visibleSince = DateTimeOffset.MinValue;

    public TechLoadingOverlay()
    {
        InitializeComponent();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsOverlayVisible
    {
        get => (bool)GetValue(IsOverlayVisibleProperty);
        private set => SetValue(IsOverlayVisibleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public IEnumerable? StepsSource
    {
        get => (IEnumerable?)GetValue(StepsSourceProperty);
        set => SetValue(StepsSourceProperty, value);
    }

    public int MinimumVisibleMilliseconds
    {
        get => (int)GetValue(MinimumVisibleMillisecondsProperty);
        set => SetValue(MinimumVisibleMillisecondsProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is TechLoadingOverlay overlay)
        {
            overlay.ApplyActiveState((bool)e.NewValue);
        }
    }

    private async void ApplyActiveState(bool isActive)
    {
        _visibilityCts?.Cancel();
        _visibilityCts = new CancellationTokenSource();
        var token = _visibilityCts.Token;

        if (isActive)
        {
            _visibleSince = DateTimeOffset.Now;
            IsOverlayVisible = true;
            return;
        }

        var minimumVisible = TimeSpan.FromMilliseconds(Math.Max(0, MinimumVisibleMilliseconds));
        var elapsed = DateTimeOffset.Now - _visibleSince;
        var remaining = minimumVisible - elapsed;

        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(remaining, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (!token.IsCancellationRequested)
        {
            IsOverlayVisible = false;
        }
    }
}
