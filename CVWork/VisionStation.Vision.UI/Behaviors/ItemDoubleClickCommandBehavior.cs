using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionStation.Vision.UI.Behaviors;

public static class ItemDoubleClickCommandBehavior
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command",
        typeof(ICommand),
        typeof(ItemDoubleClickCommandBehavior),
        new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.RegisterAttached(
        "CommandParameter",
        typeof(object),
        typeof(ItemDoubleClickCommandBehavior),
        new PropertyMetadata(null));

    public static ICommand? GetCommand(DependencyObject obj)
    {
        return (ICommand?)obj.GetValue(CommandProperty);
    }

    public static void SetCommand(DependencyObject obj, ICommand? value)
    {
        obj.SetValue(CommandProperty, value);
    }

    public static object? GetCommandParameter(DependencyObject obj)
    {
        return obj.GetValue(CommandParameterProperty);
    }

    public static void SetCommandParameter(DependencyObject obj, object? value)
    {
        obj.SetValue(CommandParameterProperty, value);
    }

    private static void OnCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Control control)
        {
            return;
        }

        control.MouseDoubleClick -= OnMouseDoubleClick;
        if (e.NewValue is ICommand)
        {
            control.MouseDoubleClick += OnMouseDoubleClick;
        }
    }

    private static void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Control control || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var command = GetCommand(control);
        if (command is null)
        {
            return;
        }

        var parameter = GetCommandParameter(control) ?? ResolveItem(control, e.OriginalSource as DependencyObject);
        if (parameter is null)
        {
            return;
        }

        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }

    private static object? ResolveItem(Control owner, DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, owner))
        {
            switch (current)
            {
                case ListBoxItem listBoxItem:
                    return listBoxItem.DataContext;
                case TreeViewItem treeViewItem:
                    return treeViewItem.DataContext;
                case DataGridRow dataGridRow:
                    return dataGridRow.Item;
                case FrameworkElement { DataContext: not null } element when !ReferenceEquals(element.DataContext, owner.DataContext):
                    return element.DataContext;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        if (owner is Selector selector &&
            source is not null &&
            ItemsControl.ContainerFromElement(selector, source) is FrameworkElement container)
        {
            return container.DataContext;
        }

        return null;
    }
}
