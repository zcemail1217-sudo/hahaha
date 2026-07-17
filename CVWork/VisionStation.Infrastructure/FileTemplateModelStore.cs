using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStation.Vision;

namespace VisionStation.Infrastructure;

public sealed class FileTemplateModelStore : ITemplateModelStore
{
    private const string ModelStagingSuffix = ".staging.shm";
    private const string MetadataStagingSuffix = ".staging.json";

    private static readonly JsonSerializerOptions MetadataWriteOptions = new()
    {
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, StoreCoordinator> Coordinators =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Guid _storeId = Guid.NewGuid();
    private readonly TemplateModelPathGuard _pathGuard;
    private readonly StoreCoordinator _coordinator;

    public FileTemplateModelStore(RuntimePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _pathGuard = new TemplateModelPathGuard(paths.TemplateResourceDirectory);
        _coordinator = Coordinators.GetOrAdd(
            _pathGuard.RootDirectory,
            static _ => new StoreCoordinator());
    }

    public Task<TemplateModelWriteSession> BeginWriteAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownerDirectory = _pathGuard.GetOwnerDirectory(owner, requireExisting: false);
        var session = _coordinator.RegisterSession(
            ownerDirectory,
            () => CreateWriteSession(owner, ownerDirectory, cancellationToken));
        return Task.FromResult<TemplateModelWriteSession>(session);
    }

    private StoreWriteSession CreateWriteSession(
        TemplateModelOwner owner,
        string expectedOwnerDirectory,
        CancellationToken cancellationToken)
    {
        var ownerDirectory = _pathGuard.CreateOwnerDirectory(owner);
        if (!string.Equals(ownerDirectory, expectedOwnerDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The controlled owner directory changed during session creation.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var generation = CreateGeneration();
            var token = Guid.NewGuid().ToString("N");
            var modelStagingPath = Path.Combine(
                ownerDirectory,
                $".{generation}.{token}{ModelStagingSuffix}");
            var metadataStagingPath = Path.Combine(
                ownerDirectory,
                $".{generation}.{token}{MetadataStagingSuffix}");
            var modelPath = Path.Combine(ownerDirectory, $"model-{generation}.shm");
            var metadataPath = Path.Combine(ownerDirectory, $"model-{generation}.json");
            if (File.Exists(modelStagingPath) ||
                File.Exists(metadataStagingPath) ||
                File.Exists(modelPath) ||
                File.Exists(metadataPath))
            {
                continue;
            }

            return new StoreWriteSession(
                _storeId,
                owner,
                ownerDirectory,
                modelStagingPath,
                metadataStagingPath,
                generation,
                () => _coordinator.ReleaseSession(
                    ownerDirectory,
                    () => _pathGuard.DeleteEmptyOwnerHierarchy(owner)));
        }
    }

    public async Task<TemplateModelReference> CommitAsync(
        TemplateModelWriteSession session,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controlledSession = GetControlledSession(session);
        await controlledSession.BeginCommitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitCoreAsync(
                controlledSession,
                metadataJson,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            controlledSession.EndCommit();
        }
    }

    private async Task<TemplateModelReference> CommitCoreAsync(
        StoreWriteSession controlledSession,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken)
    {
        _pathGuard.ValidateStoreIssuedStagingPath(
            controlledSession.Owner,
            controlledSession.OwnerDirectory,
            controlledSession.StagingModelPath,
            ModelStagingSuffix,
            requireExisting: true);
        if (metadataJson.IsEmpty)
        {
            throw MetadataInvalid("Template model metadata is empty.");
        }

        var modelChecksum = await ComputeFileHashAsync(
            controlledSession.StagingModelPath,
            cancellationToken).ConfigureAwait(false);
        var modelFileName = $"model-{controlledSession.Generation}.shm";
        var header = ParseMetadata(metadataJson);
        ValidateHeaderForCommit(
            header,
            controlledSession.Owner,
            controlledSession.Generation,
            modelFileName,
            modelChecksum);

        _pathGuard.ValidateStoreIssuedStagingPath(
            controlledSession.Owner,
            controlledSession.OwnerDirectory,
            controlledSession.MetadataStagingPath,
            MetadataStagingSuffix,
            requireExisting: false);
        await WriteMetadataStagingAsync(
            controlledSession.MetadataStagingPath,
            metadataJson,
            cancellationToken).ConfigureAwait(false);
        _pathGuard.ValidateStoreIssuedStagingPath(
            controlledSession.Owner,
            controlledSession.OwnerDirectory,
            controlledSession.MetadataStagingPath,
            MetadataStagingSuffix,
            requireExisting: true);
        var metadataChecksum = ComputeHash(metadataJson.Span);

        var modelRelativePath = _pathGuard.GetRelativeModelPath(
            controlledSession.Owner,
            controlledSession.Generation);
        var metadataRelativePath = _pathGuard.GetRelativeMetadataPath(
            controlledSession.Owner,
            controlledSession.Generation);
        var reference = new TemplateModelReference(
            modelRelativePath,
            metadataRelativePath,
            header.ModelFormat,
            modelChecksum,
            metadataChecksum,
            controlledSession.Generation,
            header.ModelVersion,
            header.NativeRuntimeVersion,
            header.GenerationParameterFingerprint);

        var finalModelPath = _pathGuard.ResolveModelPath(
            controlledSession.Owner,
            modelRelativePath,
            controlledSession.Generation,
            requireExisting: false);
        var finalMetadataPath = _pathGuard.ResolveMetadataPath(
            controlledSession.Owner,
            metadataRelativePath,
            controlledSession.Generation,
            requireExisting: false);
        if (File.Exists(finalModelPath) || File.Exists(finalMetadataPath))
        {
            throw new IOException("A template model generation with the same identifier already exists.");
        }

        // No cancellation is observed after the first irreversible move. A crash can leave an
        // unreferenced generation, but the caller never receives a reference to a partial pair.
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(controlledSession.StagingModelPath, finalModelPath, overwrite: false);
        File.Move(controlledSession.MetadataStagingPath, finalMetadataPath, overwrite: false);

        _ = await ResolveAsync(
            controlledSession.Owner,
            reference,
            CancellationToken.None).ConfigureAwait(false);
        controlledSession.MarkCommitted();
        return reference;
    }

    public async Task<ResolvedTemplateModel> ResolveAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);

        var modelPath = _pathGuard.ResolveModelPath(
            owner,
            reference.ModelPath,
            reference.Generation,
            requireExisting: true);
        var metadataPath = _pathGuard.ResolveMetadataPath(
            owner,
            reference.MetadataPath,
            reference.Generation,
            requireExisting: true);

        var metadataJson = await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(ComputeHash(metadataJson), reference.MetadataChecksum))
        {
            throw ChecksumMismatch("Template model metadata checksum does not match its reference.");
        }

        var modelChecksum = await ComputeFileHashAsync(modelPath, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(modelChecksum, reference.ModelChecksum))
        {
            throw ChecksumMismatch("Template model checksum does not match its reference.");
        }

        var header = ParseMetadata(metadataJson);
        ValidateHeaderForReference(
            header,
            owner,
            reference,
            Path.GetFileName(modelPath),
            modelChecksum);
        _pathGuard.ValidateExistingFile(modelPath);
        _pathGuard.ValidateExistingFile(metadataPath);
        return new ResolvedTemplateModel(Path.GetFullPath(modelPath), metadataJson.ToArray());
    }

    public async Task<TemplateModelReference> CopyGenerationAsync(
        TemplateModelOwner sourceOwner,
        TemplateModelReference sourceReference,
        TemplateModelOwner targetOwner,
        CancellationToken cancellationToken)
    {
        var source = await ResolveAsync(
            sourceOwner,
            sourceReference,
            cancellationToken).ConfigureAwait(false);
        await using var session = await BeginWriteAsync(targetOwner, cancellationToken).ConfigureAwait(false);

        await CopyModelAsync(
            source.ModelPath,
            session.StagingModelPath,
            cancellationToken).ConfigureAwait(false);
        var modelChecksum = await ComputeFileHashAsync(
            session.StagingModelPath,
            cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(modelChecksum, sourceReference.ModelChecksum))
        {
            throw ChecksumMismatch(
                "The source template model changed between validation and generation copy.");
        }

        var metadata = JsonNode.Parse(Encoding.UTF8.GetString(source.MetadataJson.Span)) as JsonObject
                       ?? throw MetadataInvalid("Template model metadata root must be an object.");
        metadata["owner"] = new JsonObject
        {
            ["recipeId"] = targetOwner.RecipeId,
            ["flowId"] = targetOwner.FlowId,
            ["toolId"] = targetOwner.ToolId
        };
        metadata["generation"] = session.Generation;
        metadata["modelFileName"] = $"model-{session.Generation}.shm";
        metadata["modelChecksum"] = modelChecksum;
        var rewrittenMetadata = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataWriteOptions);

        return await CommitAsync(session, rewrittenMetadata, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteOwnerResourcesAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerDirectory = _pathGuard.GetOwnerDirectory(owner, requireExisting: false);
        _coordinator.BeginDelete(ownerDirectory);
        try
        {
            try
            {
                ownerDirectory = _pathGuard.GetOwnerDirectory(owner, requireExisting: true);
            }
            catch (TemplateModelStoreException exception)
                when (exception.Code == TemplateMatchingDiagnosticCodes.ModelNotFound)
            {
                return;
            }

            var entries = Directory.EnumerateFileSystemEntries(ownerDirectory).ToArray();
            var unknown = entries
                .Where(path => !IsCommittedGenerationFile(Path.GetFileName(path)))
                .ToArray();
            if (unknown.Length > 0)
            {
                throw MetadataInvalid(
                    $"The template owner directory contains {unknown.Length} unverified orphan file(s).");
            }

            var metadataFiles = entries
                .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var verifiedPairs = new List<(string ModelPath, string MetadataPath)>();
            var referencedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadataPath in metadataFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pathGuard.ValidateExistingFile(metadataPath);
                var metadataJson = await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
                var header = ParseMetadata(metadataJson);
                ValidateOwner(header.Owner, owner);
                var metadataGeneration = header.Generation;
                var metadataRelativePath = _pathGuard.GetRelativeMetadataPath(owner, metadataGeneration);
                var modelRelativePath = _pathGuard.GetRelativeModelPath(owner, metadataGeneration);
                if (!string.Equals(
                        Path.GetFileName(metadataPath),
                        Path.GetFileName(metadataRelativePath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        header.ModelFileName,
                        Path.GetFileName(modelRelativePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw MetadataInvalid("Template generation filenames do not match their metadata.");
                }

                var reference = new TemplateModelReference(
                    modelRelativePath,
                    metadataRelativePath,
                    header.ModelFormat,
                    header.ModelChecksum,
                    ComputeHash(metadataJson),
                    header.Generation,
                    header.ModelVersion,
                    header.NativeRuntimeVersion,
                    header.GenerationParameterFingerprint);
                var resolved = await ResolveAsync(owner, reference, cancellationToken).ConfigureAwait(false);
                referencedModels.Add(Path.GetFullPath(resolved.ModelPath));
                verifiedPairs.Add((resolved.ModelPath, Path.GetFullPath(metadataPath)));
            }

            var modelFiles = entries
                .Where(path => path.EndsWith(".shm", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .ToArray();
            if (modelFiles.Length != referencedModels.Count ||
                modelFiles.Any(path => !referencedModels.Contains(path)))
            {
                throw MetadataInvalid("The template owner directory contains an unreferenced model generation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pair in verifiedPairs)
            {
                _pathGuard.ValidateExistingFile(pair.ModelPath);
                _pathGuard.ValidateExistingFile(pair.MetadataPath);
                File.Delete(pair.MetadataPath);
                File.Delete(pair.ModelPath);
            }

            _pathGuard.DeleteEmptyOwnerHierarchy(owner);
        }
        finally
        {
            _coordinator.EndDelete(ownerDirectory);
        }
    }

    private StoreWriteSession GetControlledSession(TemplateModelWriteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not StoreWriteSession controlledSession || controlledSession.StoreId != _storeId)
        {
            throw new ArgumentException("The template model write session was not issued by this store.", nameof(session));
        }

        return controlledSession;
    }

    private static void ValidateReference(TemplateModelReference reference)
    {
        TemplateModelPathGuard.ValidateGeneration(reference.Generation);
        if (string.IsNullOrWhiteSpace(reference.ModelPath) ||
            string.IsNullOrWhiteSpace(reference.MetadataPath))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelPathInvalid,
                "Template model reference paths cannot be empty.");
        }

        ValidateRequired(reference.ModelFormat, nameof(reference.ModelFormat));
        ValidateRequired(reference.ModelVersion, nameof(reference.ModelVersion));
        ValidateRequired(reference.RuntimeVersion, nameof(reference.RuntimeVersion));
        if (!IsSha256(reference.ModelChecksum) || !IsSha256(reference.MetadataChecksum))
        {
            throw ChecksumMismatch("Template model reference checksums are invalid.");
        }

        ValidateSha256(
            reference.GenerationParameterFingerprint,
            nameof(reference.GenerationParameterFingerprint));
    }

    private static MetadataHeader ParseMetadata(ReadOnlyMemory<byte> metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw MetadataInvalid("Template model metadata root must be an object.");
            }

            var schemaVersionElement = GetUniqueProperty(root, "schemaVersion");
            if (!schemaVersionElement.TryGetInt32(out var schemaVersion) || schemaVersion <= 0)
            {
                throw MetadataInvalid("Template model metadata schema version is invalid.");
            }

            var engine = GetRequiredString(root, "engine");
            var ownerElement = GetUniqueProperty(root, "owner");
            if (ownerElement.ValueKind != JsonValueKind.Object)
            {
                throw MetadataInvalid("Template model metadata owner must be an object.");
            }

            var owner = new TemplateModelOwner(
                GetRequiredString(ownerElement, "recipeId"),
                GetRequiredString(ownerElement, "flowId"),
                GetRequiredString(ownerElement, "toolId"));
            var modelChecksum = GetRequiredString(root, "modelChecksum");
            var fingerprint = GetRequiredString(root, "generationParameterFingerprint");
            ValidateSha256(modelChecksum, "modelChecksum");
            ValidateSha256(fingerprint, "generationParameterFingerprint");

            var header = new MetadataHeader(
                schemaVersion,
                engine,
                GetRequiredString(root, "modelFormat"),
                GetRequiredString(root, "modelVersion"),
                GetRequiredString(root, "nativeRuntimeVersion"),
                owner,
                GetRequiredString(root, "generation"),
                GetRequiredString(root, "modelFileName"),
                modelChecksum.ToLowerInvariant(),
                fingerprint.ToLowerInvariant());
            if (header.SchemaVersion != 1 ||
                !string.Equals(header.Engine, "Halcon", StringComparison.Ordinal))
            {
                throw MetadataInvalid("Template model metadata schema or engine is unsupported.");
            }

            return header;
        }
        catch (TemplateModelStoreException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw MetadataInvalid("Template model metadata is not valid JSON.", exception);
        }
    }

    private static void ValidateHeaderForCommit(
        MetadataHeader header,
        TemplateModelOwner owner,
        string generation,
        string modelFileName,
        string modelChecksum)
    {
        ValidateOwner(header.Owner, owner);
        if (!string.Equals(header.Generation, generation, StringComparison.Ordinal) ||
            !string.Equals(header.ModelFileName, modelFileName, StringComparison.Ordinal) ||
            !FixedTimeEquals(header.ModelChecksum, modelChecksum))
        {
            throw MetadataInvalid("Template model metadata does not match its write session.");
        }

        ValidateModelFileName(header.ModelFileName);
    }

    private static void ValidateHeaderForReference(
        MetadataHeader header,
        TemplateModelOwner owner,
        TemplateModelReference reference,
        string modelFileName,
        string actualModelChecksum)
    {
        ValidateOwner(header.Owner, owner);
        if (!string.Equals(header.ModelFormat, reference.ModelFormat, StringComparison.Ordinal) ||
            !string.Equals(header.Generation, reference.Generation, StringComparison.Ordinal) ||
            !string.Equals(header.ModelFileName, modelFileName, StringComparison.OrdinalIgnoreCase) ||
            !FixedTimeEquals(header.ModelChecksum, reference.ModelChecksum) ||
            !FixedTimeEquals(header.ModelChecksum, actualModelChecksum) ||
            !FixedTimeEquals(
                header.GenerationParameterFingerprint,
                reference.GenerationParameterFingerprint))
        {
            throw MetadataInvalid("Template model metadata does not match its reference.");
        }

        if (!string.Equals(header.ModelVersion, reference.ModelVersion, StringComparison.Ordinal))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelVersionMismatch,
                "Template model metadata version does not match its reference.");
        }

        if (!string.Equals(header.NativeRuntimeVersion, reference.RuntimeVersion, StringComparison.Ordinal))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                "Template model runtime version does not match its reference.");
        }

        ValidateModelFileName(header.ModelFileName);
    }

    private static void ValidateOwner(TemplateModelOwner actual, TemplateModelOwner expected)
    {
        if (!string.Equals(actual.RecipeId, expected.RecipeId, StringComparison.Ordinal) ||
            !string.Equals(actual.FlowId, expected.FlowId, StringComparison.Ordinal) ||
            !string.Equals(actual.ToolId, expected.ToolId, StringComparison.Ordinal))
        {
            throw MetadataInvalid("Template model metadata owner does not match the requested owner.");
        }
    }

    private static void ValidateModelFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains(':') ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw MetadataInvalid("Template model metadata contains an unsafe model filename.");
        }
    }

    private static JsonElement GetUniqueProperty(JsonElement element, string propertyName)
    {
        JsonElement match = default;
        string? actualName = null;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            match = property.Value;
            actualName = property.Name;
        }

        if (count != 1 || !string.Equals(actualName, propertyName, StringComparison.Ordinal))
        {
            throw MetadataInvalid($"Template model metadata property '{propertyName}' is missing or ambiguous.");
        }

        return match;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var property = GetUniqueProperty(element, propertyName);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw MetadataInvalid($"Template model metadata property '{propertyName}' is invalid.");
        }

        return property.GetString()!;
    }

    private static async Task WriteMetadataStagingAsync(
        string path,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(metadataJson, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CopyModelAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(checksum).ToLowerInvariant();
    }

    private static string ComputeHash(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
    }

    private static void ValidateRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw MetadataInvalid($"Template model reference field '{field}' is empty.");
        }
    }

    private static void ValidateSha256(string? value, string field)
    {
        if (!IsSha256(value))
        {
            throw MetadataInvalid($"Template model checksum field '{field}' is invalid.");
        }
    }

    private static bool IsSha256(string? value)
    {
        return value is not null &&
               value.Length == 64 &&
               value.All(Uri.IsHexDigit);
    }

    private static bool IsCommittedGenerationFile(string fileName)
    {
        return fileName.StartsWith("model-", StringComparison.OrdinalIgnoreCase) &&
               (fileName.EndsWith(".shm", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateGeneration()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff");
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{timestamp}-{random}";
    }

    private static TemplateModelStoreException MetadataInvalid(
        string detail,
        Exception? innerException = null)
    {
        return new TemplateModelStoreException(
            TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
            detail,
            innerException);
    }

    private static TemplateModelStoreException ChecksumMismatch(string detail)
    {
        return new TemplateModelStoreException(
            TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
            detail);
    }

    private sealed record MetadataHeader(
        int SchemaVersion,
        string Engine,
        string ModelFormat,
        string ModelVersion,
        string NativeRuntimeVersion,
        TemplateModelOwner Owner,
        string Generation,
        string ModelFileName,
        string ModelChecksum,
        string GenerationParameterFingerprint);

    private sealed class StoreCoordinator
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, OwnerCoordinationState> _owners =
            new(StringComparer.OrdinalIgnoreCase);

        public T RegisterSession<T>(string ownerDirectory, Func<T> createSession)
        {
            lock (_gate)
            {
                if (!_owners.TryGetValue(ownerDirectory, out var state))
                {
                    state = new OwnerCoordinationState();
                    _owners.Add(ownerDirectory, state);
                }

                if (state.DeleteInProgress)
                {
                    throw OwnerBusy("Template model resources are being deleted.");
                }

                try
                {
                    var session = createSession();
                    state.ActiveSessionCount++;
                    return session;
                }
                catch
                {
                    if (state.ActiveSessionCount == 0)
                    {
                        _owners.Remove(ownerDirectory);
                    }

                    throw;
                }
            }
        }

        public void ReleaseSession(string ownerDirectory, Action cleanupEmptyHierarchy)
        {
            lock (_gate)
            {
                if (!_owners.TryGetValue(ownerDirectory, out var state) ||
                    state.ActiveSessionCount == 0)
                {
                    return;
                }

                state.ActiveSessionCount--;
                if (state.ActiveSessionCount == 0 && !state.DeleteInProgress)
                {
                    _owners.Remove(ownerDirectory);
                    cleanupEmptyHierarchy();
                }
            }
        }

        public void BeginDelete(string ownerDirectory)
        {
            lock (_gate)
            {
                if (!_owners.TryGetValue(ownerDirectory, out var state))
                {
                    state = new OwnerCoordinationState();
                    _owners.Add(ownerDirectory, state);
                }

                if (state.DeleteInProgress || state.ActiveSessionCount > 0)
                {
                    throw OwnerBusy("Template model resources have active write sessions.");
                }

                state.DeleteInProgress = true;
            }
        }

        public void EndDelete(string ownerDirectory)
        {
            lock (_gate)
            {
                if (!_owners.TryGetValue(ownerDirectory, out var state))
                {
                    return;
                }

                state.DeleteInProgress = false;
                if (state.ActiveSessionCount == 0)
                {
                    _owners.Remove(ownerDirectory);
                }
            }
        }

        private static TemplateModelStoreException OwnerBusy(string details)
        {
            return new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                details);
        }

        private sealed class OwnerCoordinationState
        {
            public int ActiveSessionCount { get; set; }

            public bool DeleteInProgress { get; set; }
        }
    }

    private sealed class StoreWriteSession : TemplateModelWriteSession
    {
        private readonly Action _releaseSession;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private bool _commitStarted;
        private bool _committed;
        private bool _disposed;

        public StoreWriteSession(
            Guid storeId,
            TemplateModelOwner owner,
            string ownerDirectory,
            string modelStagingPath,
            string metadataStagingPath,
            string generation,
            Action releaseSession)
        {
            StoreId = storeId;
            Owner = owner;
            OwnerDirectory = ownerDirectory;
            StagingModelPath = modelStagingPath;
            MetadataStagingPath = metadataStagingPath;
            Generation = generation;
            _releaseSession = releaseSession;
        }

        public Guid StoreId { get; }

        public TemplateModelOwner Owner { get; }

        public string OwnerDirectory { get; }

        public override string StagingModelPath { get; }

        public string MetadataStagingPath { get; }

        public override string Generation { get; }

        public async Task BeginCommitAsync(CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_disposed || _commitStarted)
            {
                _operationGate.Release();
                throw new InvalidOperationException("Template model write sessions can be committed only once.");
            }

            _commitStarted = true;
        }

        public void EndCommit()
        {
            _operationGate.Release();
        }

        public void MarkCommitted()
        {
            _committed = true;
        }

        public override async ValueTask DisposeAsync()
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_committed)
                {
                    DeleteIfExists(StagingModelPath);
                    DeleteIfExists(MetadataStagingPath);
                }

                _releaseSession();
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
