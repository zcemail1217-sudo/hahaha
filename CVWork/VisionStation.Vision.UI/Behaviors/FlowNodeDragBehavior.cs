using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Vision.UI.Behaviors;

public static class FlowNodeDragBehavior
{
    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.RegisterAttached(
        "MoveCommand",
        typeof(ICommand),
        typeof(FlowNodeDragBehavior),
        new PropertyMetadata(null, OnMoveCommandChanged));

    private static readonly DependencyProperty DragStateProperty = DependencyProperty.RegisterAttached(
        "DragState",
        typeof(DragState),
        typeof(FlowNodeDragBehavior),
        new PropertyMetadata(null));

    public static void SetMoveCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(MoveCommandProperty, value);
    }

    public static ICommand? GetMoveCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(MoveCommandProperty);
    }

    private static void OnMoveCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseMove -= OnPreviewMouseMove;
        element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;

        if (e.NewValue is null)
        {
            element.ClearValue(DragStateProperty);
            return;
        }

        element.SetValue(DragStateProperty, new DragState());
        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseMove += OnPreviewMouseMove;
        element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            GetFlowPort(e.OriginalSource) is not null)
        {
            return;
        }

        var node = GetFlowNode(e.OriginalSource);
        if (node is null)
        {
            return;
        }

        var state = GetDragState(element);
        if (state is null)
        {
            return;
        }

        state.Node = node;
        state.StartMouse = e.GetPosition(GetDragSurface(element));
        state.StartX = node.X;
        state.StartY = node.Y;
        state.IsPending = true;
        state.IsDragging = false;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var state = GetDragState(element);
        if (state is not { IsPending: true, Node: not null })
        {
            return;
        }

        var current = e.GetPosition(GetDragSurface(element));
        var deltaX = current.X - state.StartMouse.X;
        var deltaY = current.Y - state.StartMouse.Y;
        if (!state.IsDragging &&
            Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!state.IsDragging)
        {
            state.IsDragging = true;
            element.CaptureMouse();
        }

        state.Node.X = Math.Max(8, state.StartX + deltaX);
        state.Node.Y = Math.Max(8, state.StartY + deltaY);
        ExecuteMoveCommand(element, state.Node, commit: false);
        e.Handled = true;
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var state = GetDragState(element);
        if (state is not { IsPending: true, Node: not null })
        {
            return;
        }

        if (!state.IsDragging)
        {
            ResetState(state);
            return;
        }

        state.IsDragging = false;
        element.ReleaseMouseCapture();

        ExecuteMoveCommand(element, state.Node, commit: true);

        ResetState(state);
        e.Handled = true;
    }

    private static void ExecuteMoveCommand(DependencyObject element, FlowNodeItem node, bool commit)
    {
        var request = new FlowNodeMoveRequest(node, node.X, node.Y, commit);
        var command = GetMoveCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static void ResetState(DragState state)
    {
        state.Node = null;
        state.IsPending = false;
        state.IsDragging = false;
    }

    private static DragState? GetDragState(DependencyObject element)
    {
        return (DragState?)element.GetValue(DragStateProperty);
    }

    private static IInputElement GetDragSurface(DependencyObject element)
    {
        var current = element;
        Canvas? fallbackCanvas = null;
        while (current is not null)
        {
            if (current is Canvas canvas)
            {
                fallbackCanvas = canvas;
                if (string.Equals(canvas.Name, "FlowCanvasRoot", StringComparison.Ordinal))
                {
                    return canvas;
                }
            }

            var parent = VisualTreeHelper.GetParent(current);
            if (parent is null && current is FrameworkElement frameworkElement)
            {
                parent = frameworkElement.Parent as DependencyObject;
            }

            current = parent;
        }

        return fallbackCanvas ?? (IInputElement)element;
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

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static FlowNodeItem? GetFlowNode(object originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FlowNodeItem item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private sealed class DragState
    {
        public FlowNodeItem? Node { get; set; }

        public Point StartMouse { get; set; }

        public double StartX { get; set; }

        public double StartY { get; set; }

        public bool IsPending { get; set; }

        public bool IsDragging { get; set; }
    }
}
