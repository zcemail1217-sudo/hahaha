using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionRunXamlTests
{
    [Fact]
    public void StopButton_DescribesCurrentProductionInspection()
    {
        var xaml = File.ReadAllText(GetProductionDashboardViewPath());
        var stopButton = Regex.Match(
            xaml,
            "<Button Command=\"\\{Binding StopCommand\\}\".*?</Button>",
            RegexOptions.Singleline);

        Assert.True(stopButton.Success, "Stop command button was not found.");
        Assert.Contains("ToolTip=\"停止当前生产检测\"", stopButton.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingOverlay_CoversOnlyInspectionResultsBelowCommandBar()
    {
        var xaml = File.ReadAllText(GetProductionDashboardViewPath());
        var overlay = Regex.Match(
            xaml,
            "<controls:SimpleLoadingOverlay\\b[^>]*/>",
            RegexOptions.Singleline);

        Assert.True(overlay.Success, "Production loading overlay was not found.");
        Assert.Contains("Grid.Column=\"0\"", overlay.Value, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,52,10,0\"", overlay.Value, StringComparison.Ordinal);
        Assert.Contains("Panel.ZIndex=\"20\"", overlay.Value, StringComparison.Ordinal);
        Assert.Contains("IsActive=\"{Binding IsBusy}\"", overlay.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.ColumnSpan", overlay.Value, StringComparison.Ordinal);
    }

    private static string GetProductionDashboardViewPath([CallerFilePath] string sourcePath = "")
    {
        var testDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Unable to resolve test directory.");
        return Path.GetFullPath(Path.Combine(
            testDirectory,
            "..",
            "VisionStation.Client",
            "Views",
            "ProductionDashboardView.xaml"));
    }
}
