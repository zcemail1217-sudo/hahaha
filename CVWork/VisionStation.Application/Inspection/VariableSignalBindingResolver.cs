using VisionStation.Domain;
using VisionStation.Domain.Utilities;

namespace VisionStation.Application.Inspection;

internal static class VariableSignalBindingResolver
{
    public static VariableSignalBinding? Resolve(Recipe recipe, string variableKey)
    {
        if (string.IsNullOrWhiteSpace(variableKey))
        {
            return null;
        }

        var variable = recipe.Variables.FirstOrDefault(item =>
            item.Enabled &&
            (string.Equals(item.Key, variableKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Name, variableKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Id, variableKey, StringComparison.OrdinalIgnoreCase)));
        if (variable is null)
        {
            return null;
        }

        var source = variable.Source?.Trim() ?? string.Empty;
        var parts = source.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return RuntimeValue(variable);
        }

        var sourceType = parts[0];
        if (SignalSourceMapper.IsTcpSourceName(sourceType))
        {
            var channelKey = parts.Length > 1 ? parts[1] : "tcp-main";
            var payload = parts.Length > 2 ? string.Join(':', parts.Skip(2)) : variable.Expression;
            return new VariableSignalBinding("tcp", channelKey, variable.Key, channelKey, payload, variable.Key);
        }

        if (SignalSourceMapper.IsSerialSourceName(sourceType))
        {
            var channelKey = parts.Length > 1 ? parts[1] : "serial-main";
            var payload = parts.Length > 2 ? string.Join(':', parts.Skip(2)) : variable.Expression;
            return new VariableSignalBinding("serial", channelKey, variable.Key, channelKey, payload, variable.Key);
        }

        if (string.Equals(sourceType, "PLC", StringComparison.OrdinalIgnoreCase))
        {
            var deviceKey = parts.Length > 2 ? parts[1] : "plc-main";
            var address = parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : variable.Target;
            return new VariableSignalBinding(
                "device",
                deviceKey,
                ParameterParser.FirstNonEmpty(address, variable.Key),
                string.Empty,
                string.Empty,
                variable.Key);
        }

        if (SignalSourceMapper.IsDigitalIoSourceName(sourceType))
        {
            var deviceKey = parts.Length > 2 ? parts[1] : "io-main";
            var address = parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : variable.Target;
            return new VariableSignalBinding(
                "digitalIo",
                deviceKey,
                ParameterParser.FirstNonEmpty(address, variable.Key),
                string.Empty,
                string.Empty,
                variable.Key);
        }

        return RuntimeValue(variable);
    }

    private static VariableSignalBinding RuntimeValue(RecipeVariableDefinition variable)
    {
        return new VariableSignalBinding("runtimeValue", string.Empty, variable.Key, string.Empty, string.Empty, variable.Key);
    }
}
