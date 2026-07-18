using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateLocateToolDialogXamlTests
{
    private static readonly string[] CoreHalconBindings =
    [
        "HalconAngleStartDeg",
        "HalconAngleExtentDeg",
        "HalconScaleMin",
        "HalconScaleMax",
        "HalconCandidateMinScore",
        "HalconOuterCoverageMin",
        "HalconInnerCoverageMin"
    ];

    private static readonly string[] AdvancedHalconBindings =
    [
        "HalconEdgeTolerancePx",
        "HalconPolarityAgreementMin",
        "HalconCandidateMaxOverlap",
        "HalconMaxOverlap",
        "HalconGreediness",
        "HalconSubPixel",
        "HalconNumLevels",
        "HalconOperatorTimeoutMs",
        "HalconCandidateLimit"
    ];

    [Fact]
    public void ParameterPanel_ProvidesEngineSelectorAndThreeNamedPresets()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var engineSelector = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ComboBox" &&
                IsBindingTo(element.Attribute("ItemsSource"), "EngineOptions"));

        Assert.True(
            IsBindingTo(engineSelector.Attribute("SelectedItem"), "SelectedEngine") ||
            IsBindingTo(engineSelector.Attribute("SelectedValue"), "SelectedEngine"),
            "The engine selector must write the selected engine back to SelectedEngine.");
        Assert.Contains("SelectedPreset", GetBindingPaths(document));
        AssertPresetLabel(document, "Strict", "严格");
        AssertPresetLabel(document, "Balanced", "均衡");
        AssertPresetLabel(document, "HighRecall", "高召回");
    }

    [Fact]
    public void EngineSelector_RemainsVisibleAcrossAllEditorTabs()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var engineSelector = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ComboBox" &&
                IsBindingTo(element.Attribute("ItemsSource"), "EngineOptions"));

        var conditionalTabAncestor = engineSelector
            .Ancestors()
            .FirstOrDefault(element =>
                TryGetBindingPath(element.Attribute("Visibility")) is { } path &&
                path.EndsWith("Tab", StringComparison.Ordinal));

        Assert.Null(conditionalTabAncestor);
    }

    [Fact]
    public void HalconTemplateTab_HidesLegacyOpenCvBasicParameters()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var minScoreEditor = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "TextBox" &&
                IsBindingTo(element.Attribute("Text"), "MinScore"));

        var halconVisibilityBoundary = minScoreEditor
            .Ancestors()
            .Take(2)
            .FirstOrDefault(element =>
                element.Descendants().Any(trigger =>
                    trigger.Name.LocalName == "DataTrigger" &&
                    IsBindingTo(trigger.Attribute("Binding"), "IsHalconEngine") &&
                    string.Equals(trigger.Attribute("Value")?.Value, "True", StringComparison.Ordinal)) &&
                element.Descendants().Any(setter =>
                    setter.Name.LocalName == "Setter" &&
                    string.Equals(setter.Attribute("Property")?.Value, "Visibility", StringComparison.Ordinal) &&
                    string.Equals(setter.Attribute("Value")?.Value, "Collapsed", StringComparison.Ordinal)));

        Assert.NotNull(halconVisibilityBoundary);
    }

    [Fact]
    public void PresetEditor_ShowsCustomAfterManualEditsClearSelection()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");

        Assert.Contains(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute =>
                attribute.Value.Contains("{Binding SelectedPreset", StringComparison.Ordinal) &&
                attribute.Value.Contains("TargetNullValue=自定义", StringComparison.Ordinal));
    }

    [Fact]
    public void ParameterPanel_BindsAllCoreHalconParameters()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var bindingPaths = GetBindingPaths(document);

        foreach (var bindingPath in CoreHalconBindings)
        {
            Assert.Contains(bindingPath, bindingPaths);
        }
    }

    [Fact]
    public void ParameterPanel_UsesRealExpanderForAdvancedHalconParameters()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var expander = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Expander" &&
                IsBindingTo(element.Attribute("IsExpanded"), "IsAdvancedParametersExpanded"));
        var advancedBindingPaths = GetBindingPaths(expander);

        foreach (var bindingPath in AdvancedHalconBindings)
        {
            Assert.Contains(bindingPath, advancedBindingPaths);
        }
    }

    [Fact]
    public void ExpectedCountEditor_IsVisibleOnlyForHalconMultiTargetTools()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var expectedCountEditor = Assert.Single(
            document.Descendants(),
            element => element
                .Attributes()
                .Any(attribute => IsBindingTo(attribute, "HalconExpectedCount")));
        var conditionalAncestors = expectedCountEditor
            .AncestorsAndSelf()
            .Take(8)
            .ToArray();

        Assert.True(
            conditionalAncestors.Any(
                element => IsBindingTo(element.Attribute("Visibility"), "IsMultiTargetTool")),
            "HalconExpectedCount must be contained by an IsMultiTargetTool visibility boundary.");
        Assert.True(
            conditionalAncestors.Any(
                element => IsBindingTo(element.Attribute("Visibility"), "IsHalconEngine")),
            "HalconExpectedCount must be contained by an IsHalconEngine visibility boundary.");
    }

    [Theory]
    [InlineData("TemplateLocateToolDialog.xaml", "MultiTargetResultPoints")]
    [InlineData("FlowEditorView.xaml", "SelectedToolMatchPoints")]
    public void MatchResultTable_ExposesScaleColumn(string fileName, string itemsSourceBinding)
    {
        var document = LoadView(fileName);
        var resultGrid = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "DataGrid" &&
                IsBindingTo(element.Attribute("ItemsSource"), itemsSourceBinding));
        var scaleColumn = Assert.Single(
            resultGrid.Descendants(),
            element =>
                element.Name.LocalName == "DataGridTextColumn" &&
                IsBindingTo(element.Attribute("Binding"), "Scale"));
        var header = scaleColumn.Attribute("Header")?.Value;

        Assert.True(
            string.Equals(header, "Scale", StringComparison.Ordinal) ||
            string.Equals(header, "尺度", StringComparison.Ordinal),
            "The Scale result column must have an operator-visible header.");
    }

    [Fact]
    public void AdvancedPanel_DoesNotUseLegacyMoreCommandOrPlaceholderPrompt()
    {
        var xaml = File.ReadAllText(GetViewPath("TemplateLocateToolDialog.xaml"));
        var viewModelSource = File.ReadAllText(GetViewModelPath());

        Assert.DoesNotContain("MoreCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("更多模板参数暂未开放", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogProvidesDistinctSaveAndCancelPathsWithAwaitedLifecycle()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();

        Assert.Contains(buttons, button =>
            IsBindingTo(button.Attribute("Command"), "CancelCommand") &&
            string.Equals(button.Attribute("Content")?.Value, "取消", StringComparison.Ordinal));
        Assert.Contains(buttons, button =>
            IsBindingTo(button.Attribute("Command"), "CloseCommand") &&
            string.Equals(button.Attribute("Content")?.Value, "保存并关闭", StringComparison.Ordinal));
        Assert.Contains(buttons, button =>
            IsBindingTo(button.Attribute("Command"), "CancelCommand") &&
            button.Attribute("Style")?.Value.Contains("CloseCaptionButtonStyle", StringComparison.Ordinal) == true);

        var serviceSource = File.ReadAllText(GetDialogServicePath());
        Assert.Contains("dialog.Loaded += async", serviceSource, StringComparison.Ordinal);
        Assert.Contains("await viewModel.InitializeAsync()", serviceSource, StringComparison.Ordinal);
        Assert.Contains("await viewModel.CancelAndRetireAsync()", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyOverlayLeavesTitleBarCancellationReachableAndSaveDrainsBeforeApply()
    {
        var document = LoadView("TemplateLocateToolDialog.xaml");
        var overlay = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "SimpleLoadingOverlay");

        Assert.Equal("1", overlay.Attributes().Single(attribute => attribute.Name.LocalName == "Grid.Row").Value);
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "Button"),
            button =>
                IsBindingTo(button.Attribute("Command"), "CancelCommand") &&
                button.Ancestors().Any(ancestor =>
                    ancestor.Attributes().Any(attribute =>
                        attribute.Name.LocalName == "Grid.Row" && attribute.Value == "0")));

        var serviceSource = File.ReadAllText(GetDialogServicePath());
        var drainIndex = serviceSource.IndexOf(
            "await viewModel.CancelAndDrainAsync()",
            StringComparison.Ordinal);
        var applyIndex = drainIndex < 0
            ? -1
            : serviceSource.IndexOf("viewModel.ApplyTo(tool)", drainIndex, StringComparison.Ordinal);
        Assert.True(drainIndex >= 0, "Accepted close must drain active operations.");
        Assert.True(applyIndex > drainIndex, "Accepted close must apply only after active operations drain.");
        Assert.Contains("cancelCloseRequested = true", serviceSource, StringComparison.Ordinal);
        Assert.Contains("viewModel.CancelPendingOperations()", serviceSource, StringComparison.Ordinal);
    }

    private static void AssertPresetLabel(XDocument document, string englishLabel, string chineseLabel)
    {
        Assert.Contains(
            document.Descendants(),
            element => element
                .Attributes()
                .Any(attribute =>
                    attribute.Value.Contains(englishLabel, StringComparison.Ordinal) ||
                    attribute.Value.Contains(chineseLabel, StringComparison.Ordinal)));
    }

    private static IReadOnlyCollection<string> GetBindingPaths(XContainer container)
    {
        return container
            .Descendants()
            .SelectMany(element => element.Attributes())
            .Select(TryGetBindingPath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
    }

    private static bool IsBindingTo(XAttribute? attribute, string expectedPath)
    {
        return string.Equals(TryGetBindingPath(attribute), expectedPath, StringComparison.Ordinal);
    }

    private static string? TryGetBindingPath(XAttribute? attribute)
    {
        if (attribute is null)
        {
            return null;
        }

        const string prefix = "{Binding";
        var expression = attribute.Value.Trim();
        if (!expression.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        expression = expression[prefix.Length..].TrimStart();
        if (expression.StartsWith("Path=", StringComparison.Ordinal))
        {
            expression = expression["Path=".Length..].TrimStart();
        }

        var delimiterIndex = expression.IndexOfAny([',', '}']);
        if (delimiterIndex >= 0)
        {
            expression = expression[..delimiterIndex];
        }

        return expression.Trim();
    }

    private static XDocument LoadView(string fileName)
    {
        return XDocument.Load(GetViewPath(fileName));
    }

    private static string GetViewPath(string fileName, [CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(
            Path.Combine(testProjectPath, "..", "VisionStation.Vision.UI", "Views", fileName));
    }

    private static string GetViewModelPath([CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(
            Path.Combine(
                testProjectPath,
                "..",
                "VisionStation.Vision.UI",
                "ViewModels",
                "TemplateLocateToolDialogViewModel.cs"));
    }

    private static string GetDialogServicePath([CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(
            Path.Combine(
                testProjectPath,
                "..",
                "VisionStation.Vision.UI",
                "Services",
                "WpfToolParameterDialogService.cs"));
    }
}
