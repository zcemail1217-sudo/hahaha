using System.Reflection;
using System.Runtime.CompilerServices;
using Prism.Commands;
using VisionStation.Client.ViewModels;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowEditorDialogContractTests
{
    [Fact]
    public void Dialog_service_uses_lazy_workspace_and_recipe_vm_has_no_debug_vm_dependency()
    {
        var dialogConstructor = Assert.Single(
            typeof(WpfFlowEditorDialogService).GetConstructors());
        Assert.Equal(
            typeof(Lazy<VisionDebugViewModel>),
            Assert.Single(dialogConstructor.GetParameters()).ParameterType);

        var throwingLazy = new Lazy<VisionDebugViewModel>(
            () => throw new InvalidOperationException("must stay lazy"));
        _ = new WpfFlowEditorDialogService(throwingLazy);
        Assert.False(throwingLazy.IsValueCreated);

        var show = typeof(IFlowEditorDialogService).GetMethod("ShowEditorAsync")!;
        var recipeId = show.GetParameters()[0];
        Assert.True(recipeId.HasDefaultValue);
        Assert.Null(recipeId.DefaultValue);
        Assert.NotNull(typeof(VisionDebugViewModel).GetMethod(
            nameof(VisionDebugViewModel.EnsureInitializedAsync)));

        var recipeConstructor = Assert.Single(
            typeof(RecipeManagementViewModel).GetConstructors());
        Assert.DoesNotContain(
            recipeConstructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(VisionDebugViewModel));

        Assert.Equal(
            typeof(AsyncDelegateCommand),
            typeof(RecipeManagementViewModel)
                .GetProperty(nameof(RecipeManagementViewModel.OpenFlowEditorCommand))!
                .PropertyType);
        Assert.Equal(
            typeof(AsyncDelegateCommand<object>),
            typeof(VisionDebugViewModel)
                .GetProperty(nameof(VisionDebugViewModel.OpenFlowEditorCommand))!
                .PropertyType);

        var uninitializedDebugViewModel =
            (VisionDebugViewModel)RuntimeHelpers.GetUninitializedObject(
                typeof(VisionDebugViewModel));
        var createOptions = typeof(VisionDebugViewModel).GetMethod(
            "CreateVisionFlowContextOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = Assert.IsAssignableFrom<IReadOnlyList<FlowConnectionOptionItem>>(
            createOptions.Invoke(
                uninitializedDebugViewModel,
                [new VisionFlowItem("flow", "Flow", string.Empty, DateTimeOffset.UtcNow)]));

        Assert.IsType<AsyncDelegateCommand>(
            Assert.Single(options, option => option.Header == "编辑流程").Command);
    }
}
