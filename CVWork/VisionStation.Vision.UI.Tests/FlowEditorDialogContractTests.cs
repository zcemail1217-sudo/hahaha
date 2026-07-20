using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Prism.Commands;
using VisionStation.Client.ViewModels;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowEditorDialogContractTests
{
    private const string RecipeEditingBinding = "{Binding IsRecipeEditingEnabled}";

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

    [Fact]
    public void Recipe_editor_freezes_editing_without_disabling_run_controls()
    {
        var editability = typeof(RecipeManagementViewModel).GetProperty(
            "IsRecipeEditingEnabled");
        Assert.NotNull(editability);
        Assert.Equal(typeof(bool), editability.PropertyType);
        Assert.Null(editability.SetMethod);

        var document = XDocument.Load(GetRecipeManagementViewPath());
        var recipeSelector = FindControl(
            document,
            "DataGrid",
            "ItemsSource",
            "{Binding Recipes}");
        Assert.True(IsRecipeEditingBound(recipeSelector));

        var editableDataGrids = document
            .Descendants()
            .Where(element => element.Name.LocalName == "DataGrid")
            .Where(element => !string.Equals(
                GetAttribute(element, "IsReadOnly"),
                "True",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(editableDataGrids);
        Assert.All(editableDataGrids, element =>
            Assert.True(
                IsRecipeEditingBound(element),
                $"Editable DataGrid {GetAttribute(element, "ItemsSource")} is not frozen."));

        var writableTextBoxes = document
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBox")
            .Where(element => !string.Equals(
                GetAttribute(element, "IsReadOnly"),
                "True",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(writableTextBoxes);
        Assert.All(writableTextBoxes, element =>
            Assert.True(
                IsRecipeEditingBound(element),
                $"Writable TextBox {GetAttribute(element, "Text")} is not frozen."));

        Assert.True(IsRecipeEditingBound(FindControl(
            document,
            "ItemsControl",
            "ItemsSource",
            "{Binding RuntimeProcessToolboxItems}")));
        Assert.True(IsRecipeEditingBound(FindControl(
            document,
            "ListBox",
            "ItemsSource",
            "{Binding ProcessSteps}")));

        foreach (var command in new[]
                 {
                     "{Binding TestRunRecipeCommand}",
                     "{Binding PauseTestRunCommand}",
                     "{Binding ResetTestRunCommand}"
                 })
        {
            var runControl = FindControl(document, "Button", "Command", command);
            Assert.False(IsRecipeEditingBound(runControl));
        }
    }

    private static string GetRecipeManagementViewPath(
        [CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("Unable to resolve test directory.");
        return Path.GetFullPath(Path.Combine(
            testDirectory,
            "..",
            "VisionStation.Client",
            "Views",
            "RecipeManagementView.xaml"));
    }

    private static XElement FindControl(
        XDocument document,
        string elementName,
        string attributeName,
        string attributeValue) =>
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == elementName &&
            string.Equals(
                GetAttribute(element, attributeName),
                attributeValue,
                StringComparison.Ordinal));

    private static bool IsRecipeEditingBound(XElement element) =>
        element
            .AncestorsAndSelf()
            .Any(candidate => string.Equals(
                GetAttribute(candidate, "IsEnabled"),
                RecipeEditingBinding,
                StringComparison.Ordinal));

    private static string? GetAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)?
            .Value;
}
