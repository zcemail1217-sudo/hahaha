using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class JsonRecipeRepository : IRecipeRepository
{
    private const string CatalogLockFileName = ".recipe-catalog.lock";
    private const string DefaultRecipeId = "default";
    private const string StorageIncarnationPropertyName = "_storageIncarnation";
    private const string LegacyRevisionPrefix = "legacy-";
    private const string VersionOneRevisionPrefix = "v1-";
    private static readonly TimeSpan CatalogLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly ConcurrentDictionary<string, CatalogCoordinator> Coordinators =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly RuntimePaths _paths;
    private readonly CatalogCoordinator _coordinator;

    public JsonRecipeRepository(RuntimePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        var recipeDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_paths.RecipeDirectory));
        _coordinator = Coordinators.GetOrAdd(
            recipeDirectory,
            static directory => new CatalogCoordinator(directory));
    }

    public async Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await _coordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var pointerRecipeId = await ReadCurrentRecipeIdNoLockAsync(cancellationToken).ConfigureAwait(false);
        var recipe = pointerRecipeId is null
            ? null
            : await TryReadCurrentTargetNoLockAsync(
                pointerRecipeId,
                cancellationToken).ConfigureAwait(false);
        if (recipe is not null)
        {
            if (!string.Equals(pointerRecipeId, recipe.Id, StringComparison.Ordinal))
            {
                await WriteTextAtomicallyAsync(
                    _paths.CurrentRecipePath,
                    recipe.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            return recipe;
        }

        recipe = await ReadRecipeNoLockAsync(
            GetPath(DefaultRecipeId),
            cancellationToken,
            DefaultRecipeId).ConfigureAwait(false);
        if (recipe is null)
        {
            recipe = await GetFirstExistingRecipeNoLockAsync(
                excludedPath: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (recipe is null)
        {
            recipe = await CreateRecipeNoLockAsync(
                DefaultRecipeFactory.Create(),
                cancellationToken).ConfigureAwait(false);
        }

        await WriteTextAtomicallyAsync(
            _paths.CurrentRecipePath,
            recipe.Id,
            cancellationToken).ConfigureAwait(false);
        return recipe;
    }

    public async Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current.Id;
    }

    public async Task SetCurrentRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecipeId(recipeId, nameof(recipeId));
        await using var scope = await _coordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var recipe = await ReadRecipeNoLockAsync(
            GetPath(recipeId),
            cancellationToken,
            recipeId).ConfigureAwait(false);
        if (recipe is null)
        {
            throw new InvalidOperationException($"Recipe '{recipeId}' was not found.");
        }

        await WriteTextAtomicallyAsync(
            _paths.CurrentRecipePath,
            recipe.Id,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Recipe?> GetAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecipeId(recipeId, nameof(recipeId));
        return await ReadRecipeNoLockAsync(
            GetPath(recipeId),
            cancellationToken,
            recipeId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Recipe>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = await _coordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var recipes = new List<Recipe>();
        foreach (var file in EnumerateRecipeFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = await ReadRecipeNoLockAsync(file, cancellationToken).ConfigureAwait(false);
            if (recipe is not null)
            {
                recipes.Add(recipe);
            }
        }

        if (recipes.Count == 0)
        {
            var created = await CreateRecipeNoLockAsync(
                DefaultRecipeFactory.Create(),
                cancellationToken).ConfigureAwait(false);
            recipes.Add(created);
        }

        return recipes.OrderBy(recipe => recipe.Name).ToArray();
    }

    public async Task<Recipe> CreateAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        await using var mutation = await BeginMutationAsync(
            recipe.Id,
            cancellationToken).ConfigureAwait(false);
        return await mutation.CreateAsync(recipe, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Recipe> SaveAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ValidateRecipeId(recipe.Id, nameof(recipe));
        if (!IsCanonicalRevision(recipe.StorageRevision))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipe.Id}' does not have a valid storage revision and cannot be updated. Reload it first.");
        }

        await using var mutation = await BeginMutationAsync(
            recipe.Id,
            cancellationToken).ConfigureAwait(false);
        return await mutation.UpdateAsync(recipe, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await BeginMutationAsync(recipeId, cancellationToken).ConfigureAwait(false);
        var recipe = await mutation.GetAsync(cancellationToken).ConfigureAwait(false);
        if (recipe is null)
        {
            return;
        }

        await mutation.DeleteAsync(recipe, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecipeMutationSession> BeginMutationAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecipeId(recipeId, nameof(recipeId));
        var scope = await _coordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return new JsonRecipeMutationSession(this, recipeId, GetPath(recipeId), scope);
    }

    private async Task<Recipe> CreateRecipeNoLockAsync(
        Recipe recipe,
        CancellationToken cancellationToken)
    {
        var path = GetPath(recipe.Id);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Recipe '{recipe.Id}' already exists.");
        }

        Recipe created;
        try
        {
            created = await PublishRecipeNoLockAsync(
                recipe,
                path,
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            throw new InvalidOperationException($"Recipe '{recipe.Id}' already exists.", exception);
        }

        await TryInitializeCurrentRecipeNoLockAsync(created.Id).ConfigureAwait(false);
        return created;
    }

    private async Task<Recipe> UpdateRecipeNoLockAsync(
        Recipe recipe,
        string path,
        CancellationToken cancellationToken)
    {
        var current = await ReadRecipeNoLockAsync(
            path,
            cancellationToken,
            recipe.Id).ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                $"Recipe '{recipe.Id}' does not exist and cannot be updated.");
        }

        if (!string.Equals(
                current.StorageRevision,
                recipe.StorageRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipe.Id}' changed after it was loaded and was not saved.");
        }

        var updated = await PublishRecipeNoLockAsync(
            recipe,
            path,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
        await TryInitializeCurrentRecipeNoLockAsync(updated.Id).ConfigureAwait(false);
        return updated;
    }

    private async Task DeleteRecipeNoLockAsync(
        Recipe expected,
        string path,
        CancellationToken cancellationToken)
    {
        var current = await ReadRecipeNoLockAsync(
            path,
            cancellationToken,
            expected.Id).ConfigureAwait(false);
        if (current is null ||
            !string.Equals(
                current.StorageRevision,
                expected.StorageRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recipe '{expected.Id}' no longer matches the revision selected for deletion.");
        }

        var currentRecipeId = await ReadCurrentRecipeIdNoLockAsync(cancellationToken).ConfigureAwait(false);
        var deletesCurrentRecipe = currentRecipeId is not null &&
            RecipeIdMapsToPath(currentRecipeId, path);
        Recipe? fallback = null;
        if (deletesCurrentRecipe)
        {
            fallback = await GetFirstExistingRecipeNoLockAsync(
                path,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The recipe JSON is authoritative and is the lifecycle commit point. The
        // current pointer is derived state and is repaired only after this succeeds.
        File.Delete(path);
        if (deletesCurrentRecipe)
        {
            await TryRepairCurrentAfterDeleteNoLockAsync(fallback).ConfigureAwait(false);
        }
    }

    private async Task<Recipe> PublishRecipeNoLockAsync(
        Recipe recipe,
        string path,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var incarnation = Guid.NewGuid().ToString("N");
        var normalized = recipe.WithNormalizedFlows() with
        {
            StorageRevision = string.Empty,
            UpdatedAt = DateTimeOffset.Now
        };
        var root = JsonSerializer.SerializeToNode(normalized, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException($"Recipe '{recipe.Id}' could not be serialized.");
        root.Add(StorageIncarnationPropertyName, incarnation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
        var revision = $"{VersionOneRevisionPrefix}{incarnation}-{ComputeHash(bytes)}";
        await WriteBytesAtomicallyAsync(path, bytes, overwrite, cancellationToken).ConfigureAwait(false);
        return normalized with { StorageRevision = revision };
    }

    private async Task<Recipe?> ReadRecipeNoLockAsync(
        string path,
        CancellationToken cancellationToken,
        string? expectedRecipeId = null,
        bool allowCanonicalAlias = false)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var incarnation = ReadStorageIncarnation(bytes, path);
            var recipe = JsonSerializer.Deserialize<Recipe>(bytes, JsonOptions);
            if (recipe is null)
            {
                throw new InvalidOperationException(
                    $"Recipe file '{path}' does not contain a recipe object.");
            }

            string ownedPath;
            try
            {
                ownedPath = GetPath(recipe.Id);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"Recipe file '{path}' contains an invalid recipe id.",
                    exception);
            }

            if (!PathsEqual(ownedPath, path))
            {
                throw new InvalidOperationException(
                    $"Recipe file '{path}' is owned by recipe '{recipe.Id}', which maps to a different storage path.");
            }

            if (expectedRecipeId is not null &&
                !string.Equals(recipe.Id, expectedRecipeId, StringComparison.OrdinalIgnoreCase) &&
                (!allowCanonicalAlias || !RecipeIdMapsToPath(expectedRecipeId, path)))
            {
                throw new InvalidOperationException(
                    $"Recipe id '{expectedRecipeId}' resolves to storage owned by recipe '{recipe.Id}'.");
            }

            var hash = ComputeHash(bytes);
            var revision = incarnation is null
                ? $"{LegacyRevisionPrefix}{hash}"
                : $"{VersionOneRevisionPrefix}{incarnation}-{hash}";
            return recipe.WithNormalizedFlows() with { StorageRevision = revision };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<string?> ReadCurrentRecipeIdNoLockAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var recipeId = await File.ReadAllTextAsync(
                _paths.CurrentRecipePath,
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(recipeId) ? null : recipeId;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<Recipe?> TryReadCurrentTargetNoLockAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = GetPath(recipeId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await ReadRecipeNoLockAsync(
            path,
            cancellationToken,
            recipeId,
            allowCanonicalAlias: true).ConfigureAwait(false);
    }

    private async Task<Recipe?> GetFirstExistingRecipeNoLockAsync(
        string? excludedPath,
        CancellationToken cancellationToken)
    {
        foreach (var file in EnumerateRecipeFiles())
        {
            if (excludedPath is not null &&
                PathsEqual(file, excludedPath))
            {
                continue;
            }

            var recipe = await ReadRecipeNoLockAsync(file, cancellationToken).ConfigureAwait(false);
            if (recipe is not null)
            {
                return recipe;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateRecipeFiles()
    {
        return Directory.EnumerateFiles(_paths.RecipeDirectory, "*.recipe.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private async Task TryInitializeCurrentRecipeNoLockAsync(string recipeId)
    {
        if (File.Exists(_paths.CurrentRecipePath))
        {
            return;
        }

        await TryWriteCurrentRecipeNoLockAsync(recipeId).ConfigureAwait(false);
    }

    private async Task TryWriteCurrentRecipeNoLockAsync(string recipeId)
    {
        try
        {
            await WriteTextAtomicallyAsync(
                _paths.CurrentRecipePath,
                recipeId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // current.recipe is derived catalog state and can be repaired on the next read.
        }
        catch (UnauthorizedAccessException)
        {
            // The recipe JSON is already committed; do not report a false failed save.
        }
    }

    private async Task TryRepairCurrentAfterDeleteNoLockAsync(Recipe? fallback)
    {
        try
        {
            if (fallback is not null)
            {
                await WriteTextAtomicallyAsync(
                    _paths.CurrentRecipePath,
                    fallback.Id,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (File.Exists(_paths.CurrentRecipePath))
            {
                File.Delete(_paths.CurrentRecipePath);
            }
        }
        catch (IOException)
        {
            // current.recipe is derived state; GetCurrentAsync repairs a stale pointer.
        }
        catch (UnauthorizedAccessException)
        {
            // The authoritative JSON deletion has committed and must not be reported as failed.
        }
    }

    private static Task WriteTextAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        return WriteBytesAtomicallyAsync(
            path,
            Encoding.UTF8.GetBytes(value),
            overwrite: true,
            cancellationToken);
    }

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagingPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.staging");
        try
        {
            await using (var stream = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private string GetPath(string recipeId)
    {
        ValidateRecipeId(recipeId, nameof(recipeId));
        var safeName = string.Join(
            "_",
            recipeId.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new ArgumentException("Recipe id does not contain a valid file name.", nameof(recipeId));
        }

        return Path.Combine(_paths.RecipeDirectory, $"{safeName}.recipe.json");
    }

    private static void ValidateRecipeId(string recipeId, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(recipeId) ||
            !string.Equals(recipeId, recipeId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Recipe id is required and must be trimmed.", parameterName);
        }
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string? ReadStorageIncarnation(
        ReadOnlyMemory<byte> bytes,
        string path)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Recipe file '{path}' must contain a JSON object.");
        }

        string? incarnation = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    StorageIncarnationPropertyName,
                    StringComparison.Ordinal))
            {
                if (string.Equals(
                        property.Name,
                        StorageIncarnationPropertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Recipe file '{path}' contains a non-canonical storage incarnation field.");
                }

                continue;
            }

            if (incarnation is not null || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Recipe file '{path}' contains an invalid storage incarnation.");
            }

            incarnation = property.Value.GetString();
            if (incarnation is null ||
                !Guid.TryParseExact(incarnation, "N", out var parsed) ||
                !string.Equals(incarnation, parsed.ToString("N"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recipe file '{path}' contains an invalid storage incarnation.");
            }
        }

        return incarnation;
    }

    private bool RecipeIdMapsToPath(string recipeId, string path)
    {
        try
        {
            return PathsEqual(GetPath(recipeId), path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            PathComparison);
    }

    private static bool IsCanonicalRevision(string revision)
    {
        if (string.IsNullOrEmpty(revision))
        {
            return false;
        }

        if (revision.StartsWith(LegacyRevisionPrefix, StringComparison.Ordinal))
        {
            return revision.Length == LegacyRevisionPrefix.Length + 64 &&
                IsLowercaseHex(revision.AsSpan(LegacyRevisionPrefix.Length));
        }

        if (!revision.StartsWith(VersionOneRevisionPrefix, StringComparison.Ordinal) ||
            revision.Length != VersionOneRevisionPrefix.Length + 32 + 1 + 64)
        {
            return false;
        }

        var incarnation = revision.AsSpan(VersionOneRevisionPrefix.Length, 32);
        if (!Guid.TryParseExact(incarnation, "N", out var parsed) ||
            !incarnation.SequenceEqual(parsed.ToString("N")))
        {
            return false;
        }

        return revision[VersionOneRevisionPrefix.Length + 32] == '-' &&
            IsLowercaseHex(revision.AsSpan(VersionOneRevisionPrefix.Length + 33));
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class JsonRecipeMutationSession : RecipeMutationSession
    {
        private readonly JsonRecipeRepository _repository;
        private readonly string _path;
        private readonly CatalogScope _scope;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private bool _disposed;

        public JsonRecipeMutationSession(
            JsonRecipeRepository repository,
            string recipeId,
            string path,
            CatalogScope scope)
        {
            _repository = repository;
            RecipeId = recipeId;
            _path = path;
            _scope = scope;
        }

        public override string RecipeId { get; }

        public override async Task<Recipe?> GetAsync(
            CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await _repository.ReadRecipeNoLockAsync(
                    _path,
                    cancellationToken,
                    RecipeId).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public override async Task<Recipe> CreateAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(recipe);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!string.Equals(recipe.Id, RecipeId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_repository.GetPath(recipe.Id), _path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The recipe id does not match this mutation session.",
                        nameof(recipe));
                }

                return await _repository.CreateRecipeNoLockAsync(
                    recipe,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public override async Task<Recipe> UpdateAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(recipe);
            if (!IsCanonicalRevision(recipe.StorageRevision))
            {
                throw new ArgumentException(
                    "A canonical recipe storage revision is required.",
                    nameof(recipe));
            }

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!string.Equals(recipe.Id, RecipeId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_repository.GetPath(recipe.Id), _path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The recipe id does not match this mutation session.",
                        nameof(recipe));
                }

                return await _repository.UpdateRecipeNoLockAsync(
                    recipe,
                    _path,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public override async Task DeleteAsync(
            Recipe expected,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            if (!IsCanonicalRevision(expected.StorageRevision))
            {
                throw new ArgumentException(
                    "A canonical recipe storage revision is required.",
                    nameof(expected));
            }

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!string.Equals(expected.Id, RecipeId, StringComparison.OrdinalIgnoreCase) ||
                    !_repository.RecipeIdMapsToPath(expected.Id, _path))
                {
                    throw new ArgumentException(
                        "The recipe id does not match this mutation session.",
                        nameof(expected));
                }

                await _repository.DeleteRecipeNoLockAsync(
                    expected,
                    _path,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private sealed class CatalogCoordinator
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _lockFilePath;
        public CatalogCoordinator(string recipeDirectory)
        {
            _lockFilePath = Path.Combine(recipeDirectory, CatalogLockFileName);
        }

        public async Task<CatalogScope> AcquireAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
                return new CatalogScope(_gate, fileLock);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(
                        _lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                }
                catch (IOException exception) when (IsSharingOrLockViolation(exception))
                {
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= CatalogLockTimeout)
                    {
                        throw new TimeoutException(
                            $"Timed out waiting for the recipe catalog lock after {CatalogLockTimeout}.",
                            exception);
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsSharingOrLockViolation(IOException exception)
        {
            var win32Error = exception.HResult & 0xFFFF;
            return win32Error is 32 or 33;
        }
    }

    private sealed class CatalogScope : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private FileStream? _fileLock;

        public CatalogScope(SemaphoreSlim gate, FileStream fileLock)
        {
            _gate = gate;
            _fileLock = fileLock;
        }

        public ValueTask DisposeAsync()
        {
            var fileLock = Interlocked.Exchange(ref _fileLock, null);
            if (fileLock is null)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                fileLock.Dispose();
            }
            finally
            {
                _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
