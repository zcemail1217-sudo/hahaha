using VisionStation.Application.Inspection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VariableResolverTests
{
    [Fact]
    public void CreateInitialRuntimeValues_MergesRecipeSystemAndRequestValues()
    {
        var recipe = new Recipe
        {
            Id = "recipe-1",
            Name = "Recipe",
            ProductCode = "P-100",
            ProductParameters =
            [
                new ProductParameterDefinition { Id = "param-width", Name = "Width", Value = "50" }
            ],
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "TargetWidth",
                    Name = "Target Width",
                    DefaultValue = "45",
                    CurrentValue = "50",
                    Required = true
                }
            ]
        };
        var request = new InspectionRequest
        {
            BatchId = "batch-1",
            OperatorName = "operator",
            RuntimeVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TargetWidth"] = "52"
            }
        };
        var configuration = new DeviceConfiguration
        {
            SystemSettings = new SystemSettingsConfiguration
            {
                Parameters = new RuntimeParameterSettings
                {
                    MachineName = "station-1",
                    Items =
                    [
                        new SystemParameterDefinition { Key = "Fixture", Name = "Fixture Name", Value = "FX-1" }
                    ]
                }
            }
        };

        var values = VariableResolver.CreateInitialRuntimeValues(recipe, request, configuration);

        Assert.Equal("recipe-1", values["RecipeId"]);
        Assert.Equal("station-1", values["MachineName"]);
        Assert.Equal("FX-1", values["Fixture"]);
        Assert.Equal("50", values["Width"]);
        Assert.Equal("52", values["TargetWidth"]);
    }

    [Fact]
    public void ApplyVariableBindings_ReplacesRecipeAndStepTokens()
    {
        var recipe = new Recipe
        {
            Name = "Recipe ${Product}",
            ProductCode = "{{Product}}",
            CurrentFlowId = "main",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "main",
                    Name = "Flow ${Product}",
                    Tools =
                    [
                        new VisionToolDefinition
                        {
                            Id = "tool",
                            Name = "Tool ${Product}",
                            Kind = VisionToolKind.Judge,
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["threshold"] = "${Limit}"
                            }
                        }
                    ]
                }
            ],
            ProcessSteps =
            [
                new ProcessStepDefinition
                {
                    Name = "Wait ${Product}",
                    StepType = ProcessStepType.WaitPlcSignal,
                    SignalId = "${Signal}",
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["expected"] = "{{Expected}}"
                    }
                }
            ]
        };

        var resolved = VariableResolver.ApplyVariableBindings(
            recipe,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Product"] = "A",
                ["Limit"] = "10",
                ["Signal"] = "M100",
                ["Expected"] = "1"
            });

        Assert.Equal("Recipe A", resolved.Name);
        Assert.Equal("A", resolved.ProductCode);
        Assert.Equal("Flow A", resolved.GetActiveFlow().Name);
        Assert.Equal("10", resolved.GetActiveFlow().Tools[0].Parameters["threshold"]);
        Assert.Equal("M100", resolved.ProcessSteps[0].SignalId);
        Assert.Equal("1", resolved.ProcessSteps[0].Parameters["expected"]);
    }

    [Fact]
    public void EvaluateExpressionVariables_AllowsCalculatedVariablesToDependOnPriorResults()
    {
        var recipe = new Recipe
        {
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "A",
                    Name = "A Name",
                    Source = "Expression:${Input}+2"
                },
                new RecipeVariableDefinition
                {
                    Key = "B",
                    Name = "B Name",
                    Expression = "${A}*3",
                    Target = "RuntimeValues.B"
                }
            ]
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Input"] = "4"
        };

        VariableResolver.EvaluateExpressionVariables(recipe, values);

        Assert.Equal("6", values["A"]);
        Assert.Equal("18", values["B"]);
        Assert.Equal("18", values["RuntimeValues.B"]);
    }
}
