using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Application.Inspection;

internal static class VariableResolver
{
    public static Dictionary<string, string> CreateInitialRuntimeValues(
        Recipe recipe,
        InspectionRequest request,
        DeviceConfiguration configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SetRuntimeValue(values, "RecipeId", recipe.Id);
        SetRuntimeValue(values, "RecipeName", recipe.Name);
        SetRuntimeValue(values, "ProductCode", recipe.ProductCode);
        SetRuntimeValue(values, "BatchId", request.BatchId);
        SetRuntimeValue(values, "OperatorName", request.OperatorName);
        SetRuntimeValue(values, "TriggeredByPlc", request.TriggeredByPlc.ToString());

        var systemParameters = configuration.SystemSettings.Parameters;
        SetRuntimeValue(values, "MachineName", systemParameters.MachineName);
        SetRuntimeValue(values, "InspectionTimeoutMs", systemParameters.InspectionTimeoutMs.ToString(CultureInfo.InvariantCulture));
        SetRuntimeValue(values, "ImageRetentionDays", systemParameters.ImageRetentionDays.ToString(CultureInfo.InvariantCulture));
        SetRuntimeValue(values, "SaveOriginalImage", systemParameters.SaveOriginalImage.ToString());
        SetRuntimeValue(values, "SaveResultImage", systemParameters.SaveResultImage.ToString());

        foreach (var parameter in systemParameters.Items.Where(parameter => parameter.Enabled))
        {
            SetRuntimeValue(values, parameter.Key, parameter.Value);
            SetRuntimeValue(values, parameter.Name, parameter.Value);
        }

        foreach (var parameter in recipe.ProductParameters)
        {
            SetRuntimeValue(values, parameter.Id, parameter.Value);
            SetRuntimeValue(values, parameter.Name, parameter.Value);
        }

        foreach (var variable in recipe.Variables.Where(variable => variable.Enabled))
        {
            var value = FirstNonEmpty(variable.CurrentValue, variable.DefaultValue);
            SetRuntimeValue(values, variable.Key, value);
            SetRuntimeValue(values, variable.Name, value);
        }

        foreach (var pair in request.RuntimeVariables)
        {
            SetRuntimeValue(values, pair.Key, pair.Value);
        }

        foreach (var variable in recipe.Variables.Where(variable =>
                     variable.Enabled &&
                     variable.Required &&
                     IsInputDirection(variable.Direction)))
        {
            var hasValue = values.TryGetValue(variable.Key, out var value) && !string.IsNullOrWhiteSpace(value);
            if (!hasValue)
            {
                throw new InvalidOperationException($"Required runtime variable '{variable.Key}' has no value.");
            }
        }

        return values;
    }

    public static Recipe ApplyVariableBindings(Recipe recipe, IReadOnlyDictionary<string, string> values)
    {
        var flows = recipe.EffectiveFlows
            .Select(flow => flow with
            {
                Name = ResolveVariableTokens(flow.Name, values),
                Description = ResolveVariableTokens(flow.Description, values),
                Rois = flow.Rois.Select(roi => roi with { Name = ResolveVariableTokens(roi.Name, values) }).ToArray(),
                Tools = flow.Tools.Select(tool => ResolveToolVariables(tool, values)).ToArray()
            })
            .ToArray();
        var activeFlow = flows.FirstOrDefault(flow => string.Equals(flow.Id, recipe.CurrentFlowId, StringComparison.OrdinalIgnoreCase))
                         ?? flows.FirstOrDefault();

        return recipe with
        {
            Name = ResolveVariableTokens(recipe.Name, values),
            ProductCode = ResolveVariableTokens(recipe.ProductCode, values),
            Description = ResolveVariableTokens(recipe.Description, values),
            ProductParameters = recipe.ProductParameters
                .Select(parameter => parameter with
                {
                    Name = ResolveVariableTokens(parameter.Name, values),
                    Value = ResolveVariableTokens(parameter.Value, values),
                    Unit = ResolveVariableTokens(parameter.Unit, values),
                    Description = ResolveVariableTokens(parameter.Description, values)
                })
                .ToArray(),
            Variables = recipe.Variables
                .Select(variable => variable with
                {
                    Name = ResolveVariableTokens(variable.Name, values),
                    DefaultValue = ResolveVariableTokens(variable.DefaultValue, values),
                    CurrentValue = ResolveVariableTokens(variable.CurrentValue, values),
                    Unit = ResolveVariableTokens(variable.Unit, values),
                    Source = ResolveVariableTokens(variable.Source, values),
                    Target = ResolveVariableTokens(variable.Target, values),
                    Expression = ResolveVariableTokens(variable.Expression, values),
                    Description = ResolveVariableTokens(variable.Description, values)
                })
                .ToArray(),
            Flows = flows,
            Rois = activeFlow?.Rois ?? recipe.Rois,
            Tools = activeFlow?.Tools ?? recipe.Tools,
            ProcessSteps = recipe.ProcessSteps.Select(step => ResolveProcessStepVariables(step, values)).ToArray(),
            VisionResults = recipe.VisionResults
                .Select(result => result with
                {
                    Name = ResolveVariableTokens(result.Name, values),
                    FlowId = ResolveVariableTokens(result.FlowId, values),
                    SourceToolId = ResolveVariableTokens(result.SourceToolId, values),
                    SourceKey = ResolveVariableTokens(result.SourceKey, values),
                    DataType = ResolveVariableTokens(result.DataType, values),
                    Unit = ResolveVariableTokens(result.Unit, values),
                    ExternalAlias = ResolveVariableTokens(result.ExternalAlias, values),
                    PlcAddress = ResolveVariableTokens(result.PlcAddress, values),
                    Description = ResolveVariableTokens(result.Description, values)
                })
                .ToArray(),
            PlcSignals = recipe.PlcSignals
                .Select(signal => signal with
                {
                    Name = ResolveVariableTokens(signal.Name, values),
                    Address = ResolveVariableTokens(signal.Address, values),
                    Direction = ResolveVariableTokens(signal.Direction, values),
                    TriggerValue = ResolveVariableTokens(signal.TriggerValue, values),
                    Description = ResolveVariableTokens(signal.Description, values)
                })
                .ToArray(),
            SignalMappings = recipe.SignalMappings
                .Select(signal => signal with
                {
                    Key = ResolveVariableTokens(signal.Key, values),
                    Name = ResolveVariableTokens(signal.Name, values),
                    DataType = ResolveVariableTokens(signal.DataType, values),
                    SourceType = ResolveVariableTokens(signal.SourceType, values),
                    DeviceKey = ResolveVariableTokens(signal.DeviceKey, values),
                    Address = ResolveVariableTokens(signal.Address, values),
                    ChannelKey = ResolveVariableTokens(signal.ChannelKey, values),
                    RequestText = ResolveVariableTokens(signal.RequestText, values),
                    Description = ResolveVariableTokens(signal.Description, values)
                })
                .ToArray()
        };
    }

    public static ProcessStepDefinition ResolveProcessStepVariables(
        ProcessStepDefinition step,
        IReadOnlyDictionary<string, string> values)
    {
        return step with
        {
            Name = ResolveVariableTokens(step.Name, values),
            DeviceKey = ResolveVariableTokens(step.DeviceKey, values),
            AxisKey = ResolveVariableTokens(step.AxisKey, values),
            AxisTargets = step.AxisTargets
                .Select(target => target with { AxisKey = ResolveVariableTokens(target.AxisKey, values) })
                .ToArray(),
            FlowId = ResolveVariableTokens(step.FlowId, values),
            SignalId = ResolveVariableTokens(step.SignalId, values),
            ResultKey = ResolveVariableTokens(step.ResultKey, values),
            OutputTarget = ResolveVariableTokens(step.OutputTarget, values),
            CommandName = ResolveVariableTokens(step.CommandName, values),
            Parameters = ResolveParameterVariables(step.Parameters, values),
            Description = ResolveVariableTokens(step.Description, values)
        };
    }

    public static Dictionary<string, string> ResolveParameterVariables(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> values)
    {
        return parameters.ToDictionary(
            pair => ResolveVariableTokens(pair.Key, values),
            pair => ResolveVariableTokens(pair.Value, values),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string ResolveVariableTokens(string? text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text) || values.Count == 0)
        {
            return text ?? string.Empty;
        }

        var resolved = text;
        foreach (var pair in values
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                     .OrderByDescending(pair => pair.Key.Length))
        {
            resolved = resolved.Replace($"${{{pair.Key}}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            resolved = resolved.Replace($"{{{{{pair.Key}}}}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    public static void EvaluateExpressionVariables(
        Recipe recipe,
        IDictionary<string, string> runtimeValues)
    {
        var expressionVariables = recipe.Variables
            .Where(variable => variable.Enabled)
            .Select(variable => (Variable: variable, Expression: ResolveExpression(variable)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Expression))
            .ToArray();
        if (expressionVariables.Length == 0)
        {
            return;
        }

        for (var pass = 0; pass < 4; pass++)
        {
            foreach (var (variable, expression) in expressionVariables)
            {
                var resolved = ResolveVariableTokens(expression, (IReadOnlyDictionary<string, string>)runtimeValues);
                var value = ExpressionEvaluator.Evaluate(resolved);
                SetRuntimeValue(runtimeValues, variable.Key, value);
                SetRuntimeValue(runtimeValues, variable.Name, value);
                if (!string.IsNullOrWhiteSpace(variable.Target))
                {
                    SetRuntimeValue(runtimeValues, variable.Target, value);
                }
            }
        }
    }

    private static VisionToolDefinition ResolveToolVariables(
        VisionToolDefinition tool,
        IReadOnlyDictionary<string, string> values)
    {
        return tool with
        {
            Name = ResolveVariableTokens(tool.Name, values),
            Parameters = ResolveParameterVariables(tool.Parameters, values)
        };
    }

    private static string ResolveExpression(RecipeVariableDefinition variable)
    {
        const string expressionPrefix = "Expression:";
        if (variable.Source.StartsWith(expressionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return variable.Source[expressionPrefix.Length..].Trim();
        }

        return !string.IsNullOrWhiteSpace(variable.Expression)
            ? variable.Expression.Trim()
            : string.Empty;
    }

    private static bool IsInputDirection(string? direction)
    {
        return string.Equals(direction, "Input", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(direction, "InOut", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRuntimeValue(IDictionary<string, string> values, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        values[key.Trim()] = value ?? string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
