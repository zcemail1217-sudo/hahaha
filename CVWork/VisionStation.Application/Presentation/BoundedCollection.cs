using System.Collections.ObjectModel;

namespace VisionStation.Application;

public static class BoundedCollection
{
    public static void InsertNewestFirst<T>(ObservableCollection<T> items, T item, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(items);

        items.Insert(0, item);
        TrimNewestFirst(items, maxCount);
    }

    public static void TrimNewestFirst<T>(ObservableCollection<T> items, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);

        while (items.Count > maxCount)
        {
            items.RemoveAt(items.Count - 1);
        }
    }
}
