using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowCanvasXamlTests
{
    [Theory]
    [InlineData("FlowEditorView.xaml")]
    [InlineData("VisionDebugView.xaml")]
    public void FlowNodeTemplate_BindsResultSourceSummaries(string fileName)
    {
        var xaml = File.ReadAllText(GetViewPath(fileName));

        Assert.Contains("SourceDisplayName", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowResultSourceSummaries", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowOutputPorts", xaml, StringComparison.Ordinal);
    }

    private static string GetViewPath(string fileName, [CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(Path.Combine(testProjectPath, "..", "VisionStation.Vision.UI", "Views", fileName));
    }
}
