using System.Windows;
using System.Windows.Controls;

namespace VisionStation.Vision.UI.Behaviors;

public static class TreeViewSelectedItemBehavior
{
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.RegisterAttached(
        "SelectedItem",
        typeof(object),
        typeof(TreeViewSelectedItemBehavior),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public static object? GetSelectedItem(DependencyObject obj)
    {
        return obj.GetValue(SelectedItemProperty);
    }

    public static void SetSelectedItem(DependencyObject obj, object? value)
    {
        obj.SetValue(SelectedItemProperty, value);
    }

    private static void OnSelectedItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TreeView treeView)
        {
            return;
        }

        treeView.SelectedItemChanged -= OnSelectedItemChanged;
        treeView.SelectedItemChanged += OnSelectedItemChanged;
    }

    private static void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView treeView)
        {
            SetSelectedItem(treeView, e.NewValue);
        }
    }
}
