using VisionStation.Domain;

namespace VisionStation.Application.Recipes;

/// <summary>
/// Coordinates recipe JSON mutations with their external template-model resources.
/// </summary>
/// <remarks>
/// Use this service instead of composing repository and model-store calls in a UI layer.
/// Duplication prepares exact generations before creating JSON; deletion commits JSON first,
/// then retires runtime handles and removes owner-verified resources.
/// </remarks>
public interface IRecipeTemplateLifecycleService
{
    /// <summary>
    /// Creates an independent recipe graph and copies every complete HALCON generation,
    /// including inactive references retained by an OpenCV tool.
    /// </summary>
    Task<Recipe> DuplicateAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the selected recipe revision before retiring and deleting its model resources.
    /// Resource cleanup failures are reported as orphan diagnostics and do not restore JSON.
    /// </summary>
    Task DeleteAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default);
}
