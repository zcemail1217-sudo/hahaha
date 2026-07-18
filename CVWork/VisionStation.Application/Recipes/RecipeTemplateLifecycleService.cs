using System.Runtime.ExceptionServices;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application.Recipes;

public sealed class RecipeTemplateLifecycleService : IRecipeTemplateLifecycleService
{
    private readonly IRecipeRepository _recipes;
    private readonly ITemplateModelResourceManager _modelResources;
    private readonly IAppLogService _log;

    public RecipeTemplateLifecycleService(
        IRecipeRepository recipes,
        ITemplateModelResourceManager modelResources,
        IAppLogService log)
    {
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _modelResources = modelResources ?? throw new ArgumentNullException(nameof(modelResources));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<Recipe> DuplicateAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(newRecipeId) ||
            !string.Equals(newRecipeId, newRecipeId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty, trimmed target recipe id is required.", nameof(newRecipeId));
        }

        if (string.Equals(source.Id, newRecipeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The target recipe id must differ from the source recipe id.", nameof(newRecipeId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var mutation = await _recipes.BeginMutationAsync(
            newRecipeId,
            cancellationToken).ConfigureAwait(false);
        if (await mutation.GetAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException($"Recipe '{newRecipeId}' already exists.");
        }

        var copySource = source with
        {
            Name = $"{source.Name}-副本",
            ProductCode = $"{source.ProductCode}-COPY",
            UpdatedAt = DateTimeOffset.Now
        };
        var copy = await _modelResources.PrepareRecipeCopyAsync(
            copySource,
            newRecipeId,
            cancellationToken).ConfigureAwait(false);
        Recipe? createdRecipe = null;
        try
        {
            createdRecipe = await mutation.CreateAsync(
                copy.Recipe,
                cancellationToken).ConfigureAwait(false);
            await copy.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = new List<Exception>();
            var recipeJsonRemoved = false;
            if (createdRecipe is not null)
            {
                try
                {
                    await mutation.DeleteAsync(
                        createdRecipe,
                        CancellationToken.None).ConfigureAwait(false);
                    recipeJsonRemoved = true;
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            try
            {
                if (createdRecipe is not null && !recipeJsonRemoved)
                {
                    // If the JSON cannot be rolled back it may still reference the copied models.
                    // Preserve those models rather than creating a durable dangling reference.
                    await copy.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                // Disposal is an independent cleanup/release step. A failed preserve
                // transition must not prevent the session from releasing its reservation.
                await copy.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            if (primaryFailure is OperationCanceledException)
            {
                foreach (var cleanupFailure in cleanupFailures)
                {
                    LogCancellationOrphans(cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw new InvalidOperationException("Unreachable recipe duplication cancellation path.");
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Recipe duplication failed and one or more rollback steps also failed.",
                    [primaryFailure, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new InvalidOperationException("Unreachable recipe duplication failure path.");
        }

        await copy.DisposeAsync().ConfigureAwait(false);
        return createdRecipe;
    }

    private void LogCancellationOrphans(Exception cleanupFailure)
    {
        foreach (var exactFailure in EnumerateGenerationCleanupFailures(cleanupFailure))
        {
            var owner = exactFailure.Owner;
            try
            {
                _log.Warning(
                    "Recipe",
                    $"Recipe duplication cancellation left template generation " +
                    $"'{exactFailure.Generation}' for owner " +
                    $"'{owner.RecipeId}/{owner.FlowId}/{owner.ToolId}': " +
                    exactFailure.CleanupException.Message);
            }
            catch
            {
                // Preserve the exact cancellation object even if an application log
                // subscriber fails while orphan details are being reported.
            }
        }
    }

    private static IEnumerable<TemplateModelGenerationCleanupFailure>
        EnumerateGenerationCleanupFailures(Exception exception)
    {
        if (exception is TemplateModelGenerationCleanupException generationCleanupFailure)
        {
            foreach (var failure in generationCleanupFailure.Failures)
            {
                yield return failure;
            }

            yield break;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                foreach (var failure in EnumerateGenerationCleanupFailures(innerException))
                {
                    yield return failure;
                }
            }
        }
    }

    public async Task DeleteAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        cancellationToken.ThrowIfCancellationRequested();
        var callerSnapshot = recipe.WithNormalizedFlows();
        await using var mutation = await _recipes.BeginMutationAsync(
            callerSnapshot.Id,
            cancellationToken).ConfigureAwait(false);
        var authoritative = await mutation.GetAsync(cancellationToken).ConfigureAwait(false);
        var cleanupSnapshot = authoritative ?? callerSnapshot;
        if (authoritative is not null)
        {
            if (string.IsNullOrWhiteSpace(callerSnapshot.StorageRevision))
            {
                throw new InvalidOperationException(
                    $"Recipe '{callerSnapshot.Id}' does not have a storage revision and cannot be deleted. Reload it first.");
            }

            await mutation.DeleteAsync(
                callerSnapshot,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _modelResources.DeleteRecipeResourcesAsync(
                cleanupSnapshot,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Warning(
                "Recipe",
                $"Template model resource cleanup left an orphan after recipe '{cleanupSnapshot.Id}' was deleted: {exception.Message}");
        }
    }
}
