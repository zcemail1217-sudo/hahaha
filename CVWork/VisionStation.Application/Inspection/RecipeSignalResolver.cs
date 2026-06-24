using VisionStation.Domain;

namespace VisionStation.Application.Inspection;

internal static class RecipeSignalResolver
{
    public static PlcSignalDefinition? ResolvePlcSignal(Recipe recipe, string? signalKey)
    {
        if (string.IsNullOrWhiteSpace(signalKey))
        {
            return null;
        }

        return recipe.PlcSignals.FirstOrDefault(signal =>
            string.Equals(signal.Id, signalKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(signal.Name, signalKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(signal.Address, signalKey, StringComparison.OrdinalIgnoreCase));
    }

    public static SignalMappingDefinition? ResolveSignalMapping(Recipe recipe, string signalKey)
    {
        if (string.IsNullOrWhiteSpace(signalKey))
        {
            return null;
        }

        return recipe.SignalMappings.FirstOrDefault(signal =>
            signal.Enabled &&
            (string.Equals(signal.Key, signalKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(signal.Id, signalKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(signal.Name, signalKey, StringComparison.OrdinalIgnoreCase)));
    }
}
