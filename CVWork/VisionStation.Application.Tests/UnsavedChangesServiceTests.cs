using VisionStation.Application.Presentation;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class UnsavedChangesServiceTests
{
    [Fact]
    public void SetUnsavedTracksAndClearsModule()
    {
        var service = new UnsavedChangesService();

        service.SetUnsaved("recipe", "配方管理", true, _ => Task.CompletedTask, "默认配方");

        var item = Assert.Single(service.GetUnsavedChanges());
        Assert.Equal("recipe", item.Key);
        Assert.Equal("配方管理", item.Title);
        Assert.Equal("默认配方", item.Detail);
        Assert.True(service.HasUnsavedChanges);

        service.SetUnsaved("recipe", "配方管理", false);

        Assert.Empty(service.GetUnsavedChanges());
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAllAsyncSavesEveryDirtyModuleAndClearsThem()
    {
        var service = new UnsavedChangesService();
        var saved = new List<string>();

        service.SetUnsaved("vision", "视觉流程", true, _ =>
        {
            saved.Add("vision");
            return Task.CompletedTask;
        });
        service.SetUnsaved("axis", "轴卡设置", true, _ =>
        {
            saved.Add("axis");
            return Task.CompletedTask;
        });

        await service.SaveAllAsync();

        Assert.Equal(["vision", "axis"], saved);
        Assert.Empty(service.GetUnsavedChanges());
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAllAsyncLeavesFailedModuleDirty()
    {
        var service = new UnsavedChangesService();

        service.SetUnsaved("system", "系统设置", true, _ =>
            Task.FromException(new InvalidOperationException("save failed")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAllAsync());

        Assert.Equal("save failed", exception.Message);
        var item = Assert.Single(service.GetUnsavedChanges());
        Assert.Equal("system", item.Key);
    }
}
