using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ShellGlobalSaveViewModelTests
{
    [Fact]
    public void InitialState_HasDisabledSaveAllButton()
    {
        var viewModel = new GlobalSaveViewModel(new UnsavedChangesService(), new ImmediateUiDispatcher());

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(0, viewModel.UnsavedChangeCount);
        Assert.False(viewModel.CanSaveAll);
        Assert.Equal("保存全部", viewModel.SaveAllButtonText);
        Assert.Equal("暂无未保存修改", viewModel.SaveAllToolTip);
        Assert.False(viewModel.SaveAllCommand.CanExecute());
    }

    [Fact]
    public void UnsavedChangesChanged_RefreshesButtonState()
    {
        var service = new UnsavedChangesService();
        var viewModel = new GlobalSaveViewModel(service, new ImmediateUiDispatcher());

        service.SetUnsaved("recipe", "配方管理", true, _ => Task.CompletedTask);
        service.SetUnsaved("system", "系统设置", true, _ => Task.CompletedTask);

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(2, viewModel.UnsavedChangeCount);
        Assert.True(viewModel.CanSaveAll);
        Assert.Equal("保存全部 (2)", viewModel.SaveAllButtonText);
        Assert.Equal("保存 2 项未保存修改", viewModel.SaveAllToolTip);
        Assert.True(viewModel.SaveAllCommand.CanExecute());

        service.Clear("recipe");

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(1, viewModel.UnsavedChangeCount);
        Assert.Equal("保存全部 (1)", viewModel.SaveAllButtonText);
    }

    [Fact]
    public async Task SaveAllAsync_SavesEveryDirtyModuleAndRefreshesButtonState()
    {
        var service = new UnsavedChangesService();
        var saved = new List<string>();
        var viewModel = new GlobalSaveViewModel(service, new ImmediateUiDispatcher());
        service.SetUnsaved("recipe", "配方管理", true, _ =>
        {
            saved.Add("recipe");
            return Task.CompletedTask;
        });
        service.SetUnsaved("system", "系统设置", true, _ =>
        {
            saved.Add("system");
            return Task.CompletedTask;
        });

        await viewModel.SaveAllAsync();

        Assert.Equal(["recipe", "system"], saved);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(0, viewModel.UnsavedChangeCount);
        Assert.False(viewModel.CanSaveAll);
        Assert.Equal("保存全部", viewModel.SaveAllButtonText);
        Assert.Equal("暂无未保存修改", viewModel.SaveAllToolTip);
        Assert.False(viewModel.SaveAllCommand.CanExecute());
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            action();
        }
    }
}
