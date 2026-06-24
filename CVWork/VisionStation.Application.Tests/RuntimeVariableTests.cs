using VisionStation.Domain;
using VisionStation.Domain.Utilities;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class RuntimeVariableTests
{
    [Fact]
    public void ParameterParser_ReadsValuesWithDefaultsAndInvariantNumbers()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["int"] = "42",
            ["double"] = "3.5",
            ["bool"] = "1",
            ["text"] = "  value  "
        };

        Assert.Equal(42, ParameterParser.GetInt(values, "INT", -1));
        Assert.Equal(3.5, ParameterParser.GetDouble(values, "double", -1));
        Assert.True(ParameterParser.GetBool(values, "bool"));
        Assert.Equal("value", ParameterParser.GetString(values, "text"));
        Assert.Equal("fallback", ParameterParser.GetString(values, "missing", "fallback"));
    }

    [Fact]
    public void TypedRuntimeVariables_PreservesCaseInsensitiveKeysAndTypedReads()
    {
        var variables = new TypedRuntimeVariables();

        variables.Set("MeasuredWidth", "12.25");
        variables.Set("Accepted", "true");

        Assert.Equal(12.25, variables.Get("measuredwidth").AsDouble());
        Assert.True(variables.Get("ACCEPTED").AsBool());
        Assert.Equal("12.25", variables.ToDictionary()["MeasuredWidth"]);
    }
}
