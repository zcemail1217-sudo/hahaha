using System.Collections.ObjectModel;
using VisionStation.Application;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class BoundedCollectionTests
{
    [Fact]
    public void InsertNewestFirst_KeepsExistingItemsUntilLimit()
    {
        var items = new ObservableCollection<int> { 3, 2, 1 };

        BoundedCollection.InsertNewestFirst(items, 4, 4);

        Assert.Equal([4, 3, 2, 1], items);
    }

    [Fact]
    public void TrimNewestFirst_RemovesOldestItemsPastLimit()
    {
        var items = new ObservableCollection<int>();
        for (var i = 1; i <= 301; i++)
        {
            items.Insert(0, i);
        }

        BoundedCollection.TrimNewestFirst(items, 300);

        Assert.Equal(300, items.Count);
        Assert.Equal(301, items[0]);
        Assert.Equal(2, items[^1]);
        Assert.DoesNotContain(1, items);
    }
}
