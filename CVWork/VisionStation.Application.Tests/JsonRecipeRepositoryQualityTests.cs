using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class JsonRecipeRepositoryQualityTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(
        Path.GetTempPath(),
        $"visionstation-recipe-quality-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAndUpdateReturnRecipeWithPrivateIncarnationContentRevision()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        const string forgedRevision = "v1-00000000000000000000000000000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var created = await repository.CreateAsync(
            new Recipe
            {
                Id = "single-truth",
                Name = "A",
                StorageRevision = forgedRevision
            });
        var firstBytes = await File.ReadAllBytesAsync(GetRecipePath(paths, created.Id));
        using var firstDocument = JsonDocument.Parse(firstBytes);
        var firstIncarnation = firstDocument.RootElement
            .GetProperty("_storageIncarnation")
            .GetString();

        Assert.False(firstDocument.RootElement.TryGetProperty("storageRevision", out _));
        Assert.NotEqual(forgedRevision, created.StorageRevision);
        Assert.True(Guid.TryParseExact(firstIncarnation, "N", out _));
        Assert.Equal(
            $"v1-{firstIncarnation}-{ComputeHash(firstBytes)}",
            created.StorageRevision);

        var updated = await repository.SaveAsync(created with { Name = "B" });
        var secondBytes = await File.ReadAllBytesAsync(GetRecipePath(paths, updated.Id));
        using var secondDocument = JsonDocument.Parse(secondBytes);
        var secondIncarnation = secondDocument.RootElement
            .GetProperty("_storageIncarnation")
            .GetString();

        Assert.NotEqual(firstIncarnation, secondIncarnation);
        Assert.Equal(
            $"v1-{secondIncarnation}-{ComputeHash(secondBytes)}",
            updated.StorageRevision);
        Assert.Equal(updated.StorageRevision, (await repository.GetAsync(updated.Id))!.StorageRevision);
    }

    [Fact]
    public async Task ExternalContentEditRetainingIncarnationRejectsStaleSaveAndDelete()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var created = await repository.CreateAsync(new Recipe { Id = "external-edit", Name = "A" });
        var recipePath = GetRecipePath(paths, created.Id);

        await RewriteRecipeAsync(recipePath, root => root["name"] = "B");

        var externallyEdited = await repository.GetAsync(created.Id);
        Assert.NotEqual(created.StorageRevision, externallyEdited!.StorageRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(created with { Name = "stale-save" }));
        await using var mutation = await repository.BeginMutationAsync(created.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.DeleteAsync(created));
        Assert.True(File.Exists(recipePath));
    }

    [Fact]
    public async Task SuccessfulUpdatesFromAToBToAStillProduceDistinctRevisions()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);

        var firstA = await repository.CreateAsync(new Recipe { Id = "a-b-a", Name = "A" });
        var b = await repository.SaveAsync(firstA with { Name = "B" });
        var secondA = await repository.SaveAsync(b with { Name = "A" });

        Assert.NotEqual(firstA.StorageRevision, b.StorageRevision);
        Assert.NotEqual(b.StorageRevision, secondA.StorageRevision);
        Assert.NotEqual(firstA.StorageRevision, secondA.StorageRevision);
    }

    [Fact]
    public async Task LegacyJsonUsesStableDigestThenMigratesOnFirstUpdate()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var recipePath = GetRecipePath(paths, "legacy");
        var legacyBytes = Encoding.UTF8.GetBytes("{\"id\":\"legacy\",\"name\":\"legacy\"}");
        await File.WriteAllBytesAsync(recipePath, legacyBytes);
        var repository = new JsonRecipeRepository(paths);

        var first = await repository.GetAsync("legacy");
        var second = await new JsonRecipeRepository(paths).GetAsync("legacy");

        Assert.Equal($"legacy-{ComputeHash(legacyBytes)}", first!.StorageRevision);
        Assert.Equal(first.StorageRevision, second!.StorageRevision);

        var migrated = await repository.SaveAsync(first with { Name = "migrated" });
        var migratedBytes = await File.ReadAllBytesAsync(recipePath);
        using var document = JsonDocument.Parse(migratedBytes);

        Assert.StartsWith("v1-", migrated.StorageRevision, StringComparison.Ordinal);
        Assert.True(document.RootElement.TryGetProperty("_storageIncarnation", out _));
        Assert.False(document.RootElement.TryGetProperty("storageRevision", out _));
    }

    [Fact]
    public async Task InvalidPersistedIncarnationFailsClosed()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var recipePath = GetRecipePath(paths, "invalid-incarnation");
        await File.WriteAllTextAsync(
            recipePath,
            "{\"id\":\"invalid-incarnation\",\"name\":\"invalid\",\"_storageIncarnation\":\"NOT-A-GUID\"}");
        var repository = new JsonRecipeRepository(paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetAsync("invalid-incarnation"));
    }

    [Fact]
    public async Task MutationRejectsNonCanonicalRevisionFormat()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var created = await repository.CreateAsync(new Recipe { Id = "strict-revision", Name = "strict" });
        await using var mutation = await repository.BeginMutationAsync(created.Id);
        var nonCanonical = created with
        {
            StorageRevision = created.StorageRevision.ToUpperInvariant()
        };

        await Assert.ThrowsAsync<ArgumentException>(() => mutation.DeleteAsync(nonCanonical));
    }

    [Fact]
    public async Task LockedCurrentPointerFailsClosedWithoutSwitchingProduct()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "default", Name = "default" });
        _ = await repository.CreateAsync(new Recipe { Id = "product-b", Name = "product-b" });
        await repository.SetCurrentRecipeAsync("product-b");

        await using (var blocker = new FileStream(
                         paths.CurrentRecipePath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => repository.GetCurrentAsync());
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal("product-b", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task FallbackIsNotReturnedWhenCurrentPointerRepairFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "fallback", Name = "fallback" });
        await File.WriteAllTextAsync(paths.CurrentRecipePath, ":");

        await using (var blocker = new FileStream(
                         paths.CurrentRecipePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => repository.GetCurrentAsync());
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal(":", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task CurrentPointerDirectoryFailsClosedWithoutBeingModified()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "fallback", Name = "fallback" });
        File.Delete(paths.CurrentRecipePath);
        Directory.CreateDirectory(paths.CurrentRecipePath);

        await Assert.ThrowsAnyAsync<Exception>(() => repository.GetCurrentAsync());

        Assert.True(Directory.Exists(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task ListFailsClosedWhenPersistedRecipeIdDoesNotOwnItsPath()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var created = await repository.CreateAsync(new Recipe { Id = "list-owner", Name = "list-owner" });
        await RewriteRecipeAsync(GetRecipePath(paths, created.Id), root => root["id"] = "other-owner");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ListAsync());
    }

    [Fact]
    public async Task CurrentTargetIdentityTamperingFailsClosedWithoutRepairingPointer()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var created = await repository.CreateAsync(new Recipe { Id = "current-owner", Name = "current-owner" });
        await repository.SetCurrentRecipeAsync(created.Id);
        await RewriteRecipeAsync(GetRecipePath(paths, created.Id), root => root["id"] = "other-owner");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetCurrentAsync());

        Assert.Equal(created.Id, await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task FallbackIdentityTamperingFailsClosedWithoutRepairingPointer()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var created = await repository.CreateAsync(new Recipe { Id = "fallback-owner", Name = "fallback-owner" });
        await File.WriteAllTextAsync(paths.CurrentRecipePath, "missing-owner");
        await RewriteRecipeAsync(GetRecipePath(paths, created.Id), root => root["id"] = "other-owner");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetCurrentAsync());

        Assert.Equal("missing-owner", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private static string GetRecipePath(RuntimePaths paths, string recipeId)
    {
        var safeName = string.Join(
            "_",
            recipeId.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(paths.RecipeDirectory, $"{safeName}.recipe.json");
    }

    private static async Task RewriteRecipeAsync(
        string path,
        Action<JsonObject> rewrite)
    {
        var root = JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        rewrite(root);
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                root,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
