using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Vision.UI.Behaviors;

public static class FlowPortDragDropBehavior
{
    public static readonly DependencyProperty ConnectionCommandProperty = DependencyProperty.RegisterAttached(
        "ConnectionCommand",
        typeof(ICommand),
        typeof(FlowPortDragDropBehavior),
        new PropertyMetadata(null, OnConnectionCommandChanged));

    private static Point _dragStartPoint;
    private static FlowPortItem? _dragSource;

    public static void SetConnectionCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(ConnectionCommandProperty, value);
    }

    public static ICommand? GetConnectionCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(ConnectionCommandProperty);
    }

    private static void OnConnectionCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseMove -= OnPreviewMouseMove;
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;

        if (e.NewValue is null)
        {
            element.AllowDrop = false;
            return;
        }

        element.AllowDrop = true;
        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseMove += OnPreviewMouseMove;
        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition((IInputElement)sender);
        _dragSource = GetFlowPort(e.OriginalSource);
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || e.LeftButton != MouseButtonState.Pressed || _dragSource is null)
        {
            return;
        }

        if (!_dragSource.IsOutput)
        {
            return;
        }

        var currentPosition = e.GetPosition(element);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(element, _dragSource, DragDropEffects.Link);
        _dragSource = null;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(FlowPortItem)) as FlowPortItem;
        var target = GetFlowPort(e.OriginalSource);
        e.Effects = CanConnect(source, target) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var source = e.Data.GetData(typeof(FlowPortItem)) as FlowPortItem;
        var target = GetFlowPort(e.OriginalSource);
        if (!CanConnect(source, target))
        {
            return;
        }

        var request = new CanvasFlowPortConnectionRequest(source!, target!);
        var command = GetConnectionCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
            e.Handled = true;
        }
    }

    private static bool CanConnect(FlowPortItem? source, FlowPortItem? target)
    {
        return source is { IsOutput: true } &&
               target is { IsInput: true } &&
               string.Equals(source.DataType, target.DataType, StringComparison.OrdinalIgnoreCase) &&
               !ReferenceEquals(source.OwnerTool, target.OwnerTool);
    }

    private static FlowPortItem? GetFlowPort(object originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FlowPortItem item })
            {
                return item;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
