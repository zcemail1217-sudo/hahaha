using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionStation.Client.Views;

public partial class RecipeManagementView : UserControl
{
    private const int MouseWheelDeltaForOneLine = 120;

    public RecipeManagementView()
    {
        InitializeComponent();
    }

    private void TestRunLogsListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(source);
        if (scrollViewer is null || e.Delta == 0)
        {
            return;
        }

        var lineCount = Math.Max(1, Math.Abs(e.Delta) / MouseWheelDeltaForOneLine);
        for (var i = 0; i < lineCount; i++)
        {
            if (e.Delta > 0)
            {
                scrollViewer.LineUp();
            }
            else
            {
                scrollViewer.LineDown();
            }
        }

        e.Handled = true;
    }

    private void ProcessStepsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var item = ResolveListBoxItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is null)
        {
            return;
        }

        ProcessStepsListBox.SelectedItem = item.DataContext;
        ShowProcessStepPropertyWindow();
        e.Handled = true;
    }

    private void ShowProcessStepPropertyWindow()
    {
        if (ProcessStepsListBox.SelectedItem is null)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var dialog = new ProcessStepPropertyWindow
        {
            DataContext = DataContext,
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private ListBoxItem? ResolveListBoxItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item)
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
