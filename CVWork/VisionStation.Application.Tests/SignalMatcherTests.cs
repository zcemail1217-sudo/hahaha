using VisionStation.Application.Inspection;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class SignalMatcherTests
{
    [Theory]
    [InlineData("1", "true", "Equals", true)]
    [InlineData("NG", "false", "Equals", true)]
    [InlineData("Ready:OK", "ok", "Contains", true)]
    [InlineData("SN=ABC123", "SN=[A-Z]+\\d+", "Regex", true)]
    [InlineData("12.5", "10", "GreaterThan", true)]
    [InlineData("8", "10", "LessThan", true)]
    [InlineData("OK", "NG", "NotEquals", true)]
    [InlineData("OK", "NG", "notEqual", true)]
    [InlineData("anything", "", "Equals", true)]
    [InlineData("8", "10", "GreaterThan", false)]
    public void MatchesSignal_EvaluatesSupportedModes(
        string actual,
        string expected,
        string matchMode,
        bool isMatch)
    {
        Assert.Equal(isMatch, SignalMatcher.MatchesSignal(actual, expected, matchMode));
    }

    [Theory]
    [InlineData("Equals", "等于")]
    [InlineData("Regex", "匹配正则")]
    [InlineData("GreaterThan", "大于")]
    [InlineData("CustomMode", "CustomMode")]
    public void DescribeMatchMode_ReturnsOperatorText(string matchMode, string expected)
    {
        Assert.Equal(expected, SignalMatcher.DescribeMatchMode(matchMode));
    }
}
