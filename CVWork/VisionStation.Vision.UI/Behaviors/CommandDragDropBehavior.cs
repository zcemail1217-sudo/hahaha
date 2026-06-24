using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace VisionStation.Vision.UI.Behaviors;

public static class CommandDragDropBehavior
{
    public static readonly DependencyProperty EnableDragProperty = DependencyProperty.RegisterAttached(
        "EnableDrag",
        typeof(bool),
        typeof(CommandDragDropBehavior),
        new PropertyMetadata(false, OnEnableDragChanged));

    public static readonly DependencyProperty DragDataProperty = DependencyProperty.RegisterAttached(
        "DragData",
        typeof(object),
        typeof(CommandDragDropBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.RegisterAttached(
        "DropCommand",
        typeof(ICommand),
        typeof(CommandDragDropBehavior),
        new PropertyMetadata(null, OnDropCommandChanged));

    private static readonly DependencyProperty DragStateProperty = DependencyProperty.RegisterAttached(
        "DragState",
        typeof(DragState),
        typeof(CommandDragDropBehavior),
        new PropertyMetadata(null));

    public static void SetEnableDrag(DependencyObject element, bool value)
    {
        element.SetValue(EnableDragProperty, value);
    }

    public static bool GetEnableDrag(DependencyObject element)
    {
        return (bool)element.GetValue(EnableDragProperty);
    }

    public static void SetDragData(DependencyObject element, object? value)
    {
        element.SetValue(DragDataProperty, value);
    }

    public static object? GetDragData(DependencyObject element)
    {
        return element.GetValue(DragDataProperty);
    }

    public static void SetDropCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(DropCommandProperty, value);
    }

    public static ICommand? GetDropCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(DropCommandProperty);
    }

    private static void OnEnableDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        element.PreviewMouseMove -= OnPreviewMouseMove;

        if (e.NewValue is not true)
        {
            element.ClearValue(DragStateProperty);
            return;
        }

        element.SetValue(DragStateProperty, new DragState());
        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        element.PreviewMouseMove += OnPreviewMouseMove;
    }

    private static void OnDropCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.AllowDrop = e.NewValue is ICommand;
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;

        if (e.NewValue is not ICommand)
        {
            return;
        }

        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        if (element.GetValue(DragStateProperty) is not DragState state)
        {
            return;
        }

        state.StartPoint = e.GetPosition(element);
        state.IsPressed = true;
        state.IsDragging = false;
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResetState(sender as UIElement);
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement element || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (element.GetValue(DragStateProperty) is not DragState state || !state.IsPressed)
        {
            return;
        }

        var current = e.GetPosition(element);
        if (!state.IsDragging &&
            Math.Abs(current.X - state.StartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - state.StartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = GetDragData(element);
        if (data is null)
        {
            ResetState(element);
            return;
        }

        state.IsDragging = true;
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);
        ResetState(element);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject element)
        {
            return;
        }

        var command = GetDropCommand(element);
        var data = GetDragPayload(e.Data);

        e.Effects = command is not null && command.CanExecute(data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject element)
        {
            return;
        }

        var command = GetDropCommand(element);
        var data = GetDragPayload(e.Data);
        if (command is null || !command.CanExecute(data))
        {
            return;
        }

        command.Execute(data);
        e.Handled = true;
    }

    private static object? GetDragPayload(IDataObject dataObject)
    {
        foreach (var format in dataObject.GetFormats())
        {
            var value = dataObject.GetData(format);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static void ResetState(UIElement? element)
    {
        if (element?.GetValue(DragStateProperty) is not DragState state)
        {
            return;
        }

        state.IsPressed = false;
        state.IsDragging = false;
        state.StartPoint = default;
    }

    private sealed class DragState
    {
        public Point StartPoint { get; set; }

        public bool IsPressed { get; set; }

        public bool IsDragging { get; set; }
    }
}
