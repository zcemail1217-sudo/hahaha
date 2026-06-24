using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Vision.UI.Behaviors;

public static class FlowNodeSelectionBehavior
{
    public static readonly DependencyProperty SelectionCommandProperty = DependencyProperty.RegisterAttached(
        "SelectionCommand",
        typeof(ICommand),
        typeof(FlowNodeSelectionBehavior),
        new PropertyMetadata(null, OnSelectionCommandChanged));

    private static readonly DependencyProperty SelectStateProperty = DependencyProperty.RegisterAttached(
        "SelectState",
        typeof(SelectState),
        typeof(FlowNodeSelectionBehavior),
        new PropertyMetadata(null));

    public static void SetSelectionCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(SelectionCommandProperty, value);
    }

    public static ICommand? GetSelectionCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(SelectionCommandProperty);
    }

    private static void OnSelectionCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
        element.PreviewMouseMove -= OnPreviewMouseMove;
        element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;

        if (e.NewValue is null)
        {
            element.ClearValue(SelectStateProperty);
            return;
        }

        element.SetValue(SelectStateProperty, new SelectState());
        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        element.PreviewMouseMove += OnPreviewMouseMove;
        element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var state = GetSelectState(element);
        if (state is null)
        {
            return;
        }

        var shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (!shiftHeld && !ctrlHeld)
        {
            // Click on empty canvas — clear selection
            var clickNode = element is UIElement uiEl ? GetFlowNodeAt(uiEl, e.GetPosition(uiEl)) : null;
            if (clickNode is null)
            {
                ClearSelection(element);
            }
            else
            {
                // Click on node — single select
                SelectSingleNode(element, clickNode);
            }

            state.IsBoxSelecting = false;
            return;
        }

        if (shiftHeld && element is UIElement uiElement)
        {
            // Begin box selection
            var surface = GetDragSurface(element);
            var point = e.GetPosition(surface);
            state.StartMouse = point;
            state.IsBoxSelecting = true;
            state.SelectionRect = Rect.Empty;
        }

        if (ctrlHeld && element is UIElement ctrlElement)
        {
            // Toggle individual node selection
            var toggleNode = GetFlowNodeAt(ctrlElement, e.GetPosition(ctrlElement));
            if (toggleNode is not null)
            {
                ToggleNodeSelection(element, toggleNode);
            }

            state.IsBoxSelecting = false;
        }
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var state = GetSelectState(element);
        if (state is not { IsBoxSelecting: true })
        {
            return;
        }

        var surface = GetDragSurface(element);
        var current = e.GetPosition(surface);
        var minX = Math.Min(state.StartMouse.X, current.X);
        var minY = Math.Min(state.StartMouse.Y, current.Y);
        var maxX = Math.Max(state.StartMouse.X, current.X);
        var maxY = Math.Max(state.StartMouse.Y, current.Y);
        state.SelectionRect = new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));

        if (state.SelectionRect.Width > SystemParameters.MinimumHorizontalDragDistance ||
            state.SelectionRect.Height > SystemParameters.MinimumVerticalDragDistance)
        {
            state.HasMoved = true;
            element.CaptureMouse();
        }

        ExecuteSelectCommand(element, state.SelectionRect, false);
        e.Handled = true;
    }

    private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element is not UIElement uiElement)
        {
            return;
        }

        var node = GetFlowNodeAt(uiElement, e.GetPosition(uiElement));
        if (node is not null)
        {
            SelectSingleNode(element, node);
        }
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var state = GetSelectState(element);
        if (state is not { IsBoxSelecting: true })
        {
            return;
        }

        if (state.HasMoved)
        {
            element.ReleaseMouseCapture();
        }

        state.IsBoxSelecting = false;
        state.HasMoved = false;

        if (state.SelectionRect != Rect.Empty)
        {
            ExecuteSelectCommand(element, state.SelectionRect, true);
        }

        state.SelectionRect = Rect.Empty;
        e.Handled = true;
    }

    private static void ExecuteSelectCommand(DependencyObject element, Rect rect, bool commit)
    {
        var request = new FlowNodeSelectionRequest(rect, commit);
        var command = GetSelectionCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static void ClearSelection(DependencyObject element)
    {
        var request = new FlowNodeSelectionRequest(Rect.Empty, Commit: true, Clear: true);
        var command = GetSelectionCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static void SelectSingleNode(DependencyObject element, FlowNodeItem node)
    {
        var bounds = new Rect(node.X, node.Y, node.Width, node.Height);
        var request = new FlowNodeSelectionRequest(bounds, Commit: true, Single: true);
        var command = GetSelectionCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static void ToggleNodeSelection(DependencyObject element, FlowNodeItem node)
    {
        var bounds = new Rect(node.X, node.Y, node.Width, node.Height);
        var request = new FlowNodeSelectionRequest(bounds, Commit: true, Toggle: true);
        var command = GetSelectionCommand(element);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static FlowNodeItem? GetFlowNodeAt(UIElement element, Point point)
    {
        var current = element.InputHitTest(point) as DependencyObject;
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

    private static SelectState? GetSelectState(DependencyObject element)
    {
        return (SelectState?)element.GetValue(SelectStateProperty);
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

        return fallbackCanvas ?? (FrameworkElement)element;
    }

    private sealed class SelectState
    {
        public Point StartMouse { get; set; }

        public Rect SelectionRect { get; set; } = Rect.Empty;

        public bool IsBoxSelecting { get; set; }

        public bool HasMoved { get; set; }
    }
}
