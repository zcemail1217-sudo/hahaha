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
    }
}
