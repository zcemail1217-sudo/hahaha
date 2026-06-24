using System.Text.Json;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class JsonRecipeRepository : IRecipeRepository
{
    private const string DefaultRecipeId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly RuntimePaths _paths;

    public JsonRecipeRepository(RuntimePaths paths)
    {
        _paths = paths;
    }

    public async Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var recipeId = await GetCurrentRecipeIdAsync(cancellationToken);
        var recipe = await GetAsync(recipeId, cancellationToken);
        if (recipe is not null)
        {
            return recipe;
        }

        recipe = await GetAsync(DefaultRecipeId, cancellationToken);
        if (recipe is not null)
        {
            if (!string.Equals(recipeId, recipe.Id, StringComparison.OrdinalIgnoreCase))
            {
                await SetCurrentRecipeAsync(recipe.Id, cancellationToken);
            }

            return recipe;
        }

        recipe = await GetFirstExistingRecipeAsync(cancellationToken);
        if (recipe is not null)
        {
            await SetCurrentRecipeAsync(recipe.Id, cancellationToken);
            return recipe;
        }

        recipe = DefaultRecipeFactory.Create();
        await SaveAsync(recipe, cancellationToken);
        await SetCurrentRecipeAsync(recipe.Id, cancellationToken);
        return recipe;
    }

    public async Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.CurrentRecipePath))
        {
            return DefaultRecipeId;
        }

        var recipeId = await File.ReadAllTextAsync(_paths.CurrentRecipePath, cancellationToken);
        return string.IsNullOrWhiteSpace(recipeId) ? DefaultRecipeId : recipeId.Trim();
    }

    public async Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            throw new ArgumentException("Recipe id is required.", nameof(recipeId));
        }

        var recipe = await GetAsync(recipeId, cancellationToken);
        if (recipe is null)
        {
            throw new InvalidOperationException($"Recipe '{recipeId}' was not found.");
        }

        await File.WriteAllTextAsync(_paths.CurrentRecipePath, recipe.Id, cancellationToken);
    }

    public async Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(recipeId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var recipe = await JsonSerializer.DeserializeAsync<Recipe>(stream, JsonOptions, cancellationToken);
        return recipe?.WithNormalizedFlows();
    }

    public async Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default)
    {
        var recipes = new List<Recipe>();
        foreach (var file in Directory.EnumerateFiles(_paths.RecipeDirectory, "*.recipe.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            var recipe = await JsonSerializer.DeserializeAsync<Recipe>(stream, JsonOptions, cancellationToken);
            if (recipe is not null)
            {
                recipes.Add(recipe);
            }
        }

        if (recipes.Count == 0)
        {
            recipes.Add(await GetCurrentAsync(cancellationToken));
        }

        return recipes.OrderBy(recipe => recipe.Name).ToArray();
    }

    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        var path = GetPath(recipe.Id);
        var normalized = recipe.WithNormalizedFlows() with { UpdatedAt = DateTimeOffset.Now };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);

        if (!File.Exists(_paths.CurrentRecipePath))
        {
            await File.WriteAllTextAsync(_paths.CurrentRecipePath, normalized.Id, cancellationToken);
        }
    }

    public async Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            throw new ArgumentException("Recipe id is required.", nameof(recipeId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var currentRecipeId = await GetCurrentRecipeIdAsync(cancellationToken);
        var path = GetPath(recipeId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (!string.Equals(currentRecipeId, recipeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fallbackRecipe = await GetFirstExistingRecipeAsync(cancellationToken);
        if (fallbackRecipe is not null)
        {
            await File.WriteAllTextAsync(_paths.CurrentRecipePath, fallbackRecipe.Id, cancellationToken);
        }
        else if (File.Exists(_paths.CurrentRecipePath))
        {
            File.Delete(_paths.CurrentRecipePath);
        }
    }

    private string GetPath(string recipeId)
    {
        var safeName = string.Join("_", recipeId.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(_paths.RecipeDirectory, $"{safeName}.recipe.json");
    }

    private async Task<Recipe?> GetFirstExistingRecipeAsync(CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(_paths.RecipeDirectory, "*.recipe.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(file);
            var recipe = await JsonSerializer.DeserializeAsync<Recipe>(stream, JsonOptions, cancellationToken);
            if (recipe is not null)
            {
                return recipe.WithNormalizedFlows();
            }
        }

        return null;
    }
}
