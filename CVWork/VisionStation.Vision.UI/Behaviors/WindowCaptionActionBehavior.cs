using System.Windows;
using System.Windows.Controls.Primitives;

namespace VisionStation.Vision.UI.Behaviors;

public enum WindowCaptionAction
{
    None,
    Minimize,
    ToggleMaximize,
    Close
}

public static class WindowCaptionActionBehavior
{
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached(
            "Action",
            typeof(WindowCaptionAction),
            typeof(WindowCaptionActionBehavior),
            new PropertyMetadata(WindowCaptionAction.None, OnActionChanged));

    public static WindowCaptionAction GetAction(DependencyObject obj)
    {
        return (WindowCaptionAction)obj.GetValue(ActionProperty);
    }

    public static void SetAction(DependencyObject obj, WindowCaptionAction value)
    {
        obj.SetValue(ActionProperty, value);
    }

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnButtonClick;

        if ((WindowCaptionAction)e.NewValue != WindowCaptionAction.None)
        {
            button.Click += OnButtonClick;
        }
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element)
        {
            return;
        }

        var window = Window.GetWindow(element);
        if (window is null)
        {
            return;
        }

        switch (GetAction(element))
        {
            case WindowCaptionAction.Minimize:
                window.WindowState = WindowState.Minimized;
                break;
            case WindowCaptionAction.ToggleMaximize:
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                break;
            case WindowCaptionAction.Close:
                window.Close();
                break;
        }
    }
}
