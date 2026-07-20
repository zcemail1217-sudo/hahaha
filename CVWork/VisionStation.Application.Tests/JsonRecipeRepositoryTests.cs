using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class JsonRecipeRepositoryTests
{
    [Fact]
    public async Task Canceled_overwrite_preserves_existing_recipe_and_leaves_no_temporary_file()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "VisionStation.JsonRecipeRepositoryTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(root);
            var repository = new JsonRecipeRepository(paths);
            var existing = new Recipe
            {
                Id = "atomic-recipe",
                Name = "Existing Recipe",
                ProductCode = "EXISTING"
            };
            await repository.SaveAsync(existing);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                repository.SaveAsync(
                    existing with
                    {
                        Name = "Canceled Replacement",
                        ProductCode = "REPLACEMENT"
                    },
                    canceled.Token));

            var reloaded = await repository.GetAsync(existing.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(existing.Name, reloaded.Name);
            Assert.Equal(existing.ProductCode, reloaded.ProductCode);
            Assert.Empty(Directory.EnumerateFiles(paths.RecipeDirectory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
