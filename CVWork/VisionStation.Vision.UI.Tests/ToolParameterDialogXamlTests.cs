using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ToolParameterDialogXamlTests
{
    [Fact]
    public void ResultInputDeleteButton_OverridesActionButtonMinimumWidth()
    {
        var document = XDocument.Load(GetToolParameterDialogPath());
        var deleteButton = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Button" &&
                element.Attribute("Command")?.Value.Contains("DeleteResultInputCommand", StringComparison.Ordinal) == true);

        Assert.Equal("30", deleteButton.Attribute("MinWidth")?.Value);
    }

    private static string GetToolParameterDialogPath([CallerFilePath] string sourcePath = "")
    {
        var testProjectPath = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Unable to locate test source path.");
        return Path.GetFullPath(Path.Combine(testProjectPath, "..", "VisionStation.Vision.UI", "Views", "ToolParameterDialog.xaml"));
    }
}
