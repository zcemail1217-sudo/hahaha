using VisionStation.Application.Inspection;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ExpressionEvaluatorTests
{
    [Theory]
    [InlineData("1+2*3", "7")]
    [InlineData("(1+2)*3", "9")]
    [InlineData("10/4", "2.5")]
    [InlineData("-2+5", "3")]
    [InlineData("1.2e2+3", "123")]
    public void Evaluate_ArithmeticExpression_ReturnsInvariantResult(string expression, string expected)
    {
        var actual = ExpressionEvaluator.Evaluate(expression);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1+")]
    [InlineData("1<2")]
    public void Evaluate_UnsupportedExpression_ReturnsOriginalText(string expression)
    {
        var actual = ExpressionEvaluator.Evaluate(expression);

        Assert.Equal(expression, actual);
    }
}
