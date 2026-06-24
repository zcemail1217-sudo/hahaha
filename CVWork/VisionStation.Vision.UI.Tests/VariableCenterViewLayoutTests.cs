using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VariableCenterViewLayoutTests
{
    [Fact]
    public void VariableDefinitionGrid_ShowsMonitoringColumns()
    {
        var viewPath = GetVariableCenterViewPath();
        var xaml = File.ReadAllText(viewPath);
        var columnsBlock = ExtractVariableColumnsBlock(xaml);

        var headers = Regex.Matches(columnsBlock, "Header=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(
            [
                "状态",
                "参数 Key",
                "名称",
                "参数类型",
                "当前值",
                "时间",
                "调用写法",
                "说明"
            ],
            headers);
    }

    private static string GetVariableCenterViewPath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("Unable to resolve test directory.");
        return Path.GetFullPath(Path.Combine(
            testDirectory,
            "..",
            "VisionStation.Client",
            "Views",
            "VariableCenterView.xaml"));
    }

    private static string ExtractVariableColumnsBlock(string xaml)
    {
        var match = Regex.Match(
            xaml,
            "ItemsSource=\"\\{Binding Variables\\}\".*?<DataGrid.Columns>(.*?)</DataGrid.Columns>",
            RegexOptions.Singleline);

        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException("Variable definition grid columns were not found.");
    }
}
