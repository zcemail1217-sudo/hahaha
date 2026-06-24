using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionDebugToolbarXamlTests
{
    [Fact]
    public void VisionDebugView_UsesDedicatedToolbarButtonStyles()
    {
        var xaml = File.ReadAllText(GetVisionDebugViewPath());

        Assert.Contains("VisionToolbarButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("VisionPrimaryToolbarButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("VisionIconToolButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource VisionPrimaryToolbarButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource VisionToolbarButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource VisionIconToolButtonStyle}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_GroupsImageSurfaceZoomButtons()
    {
        var xaml = File.ReadAllText(GetThemePath());

        Assert.Contains("ImageSurfaceToolbarShell", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ImageSurfaceToolbarShell}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ImageSurfaceToolButtonStyle", xaml, StringComparison.Ordinal);
    }

    private static string GetVisionDebugViewPath([CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(Path.Combine(testProjectPath, "..", "VisionStation.Vision.UI", "Views", "VisionDebugView.xaml"));
    }

    private static string GetThemePath([CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(Path.Combine(testProjectPath, "..", "VisionStation.Client", "Styles", "Theme.xaml"));
    }
}
