using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class FileTemplateModelStoreTests : IDisposable
{
    private static readonly TemplateModelOwner Owner = new("Recipe/A", "Flow:1", "Tool?1");

    private readonly string _baseDirectory;
    private readonly RuntimePaths _paths;
    private readonly FileTemplateModelStore _store;

    public FileTemplateModelStoreTests()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), $"visionstation-model-store-{Guid.NewGuid():N}");
        _paths = new RuntimePaths(_baseDirectory);
        _store = new FileTemplateModelStore(_paths);
    }

    [Fact]
    public async Task OwnerSegmentsCombineReadableSlugWithCollisionResistantHash()
    {
        var first = await StoreGenerationAsync(new TemplateModelOwner("Recipe/A", "Flow:1", "Tool?1"));
        var second = await StoreGenerationAsync(new TemplateModelOwner("Recipe_A", "Flow_1", "Tool_1"));

        Assert.NotEqual(first.ModelPath, second.ModelPath);
        Assert.Matches(@"recipe-a-[0-9a-f]{12}", first.ModelPath.Replace('\\', '/'));
        Assert.False(Path.IsPathRooted(first.ModelPath));
        Assert.Equal(ExpectedSlug("Recipe/A"), first.ModelPath.Replace('\\', '/').Split('/')[0]);
    }

    [Fact]
    public void SlugHashUsesOriginalOwnerValueRatherThanNormalizedText()
    {
        Assert.NotEqual(ExpectedSlug("Recipe/A"), ExpectedSlug("recipe/a"));
        Assert.EndsWith(Hash("Recipe/A")[..12], ExpectedSlug("Recipe/A"), StringComparison.Ordinal);
        Assert.True(ExpectedSlug(new string('A', 100)).Length <= 61);
    }

    [Theory]
    [InlineData("..\\outside\\model.shm")]
    [InlineData("C:\\outside\\model.shm")]
    [InlineData("C:outside\\model.shm")]
    [InlineData("\\\\server\\share\\model.shm")]
    [InlineData("\\\\?\\C:\\outside\\model.shm")]
    [InlineData("\\\\.\\C:\\outside\\model.shm")]
    [InlineData("recipe//tool/model.shm")]
    [InlineData("recipe/./tool/model.shm")]
    [InlineData("recipe/tool/model.shm:stream")]
    [InlineData("recipe/tool/model.shm/")]
    public async Task ResolveRejectsUntrustedRelativePaths(string path)
    {
        var reference = await StoreGenerationAsync(Owner);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference with { ModelPath = path }, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelPathInvalid, error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveRejectsEmptyReferencePathsAsPathErrors(bool emptyModelPath)
    {
        var reference = await StoreGenerationAsync(Owner);
        reference = emptyModelPath
            ? reference with { ModelPath = " " }
            : reference with { MetadataPath = string.Empty };

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelPathInvalid, error.Code);
    }

    [Fact]
    public async Task CommitReturnsRelativeReferenceOnlyAfterBothFilesResolve()
    {
        var reference = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("model-v1"));

        Assert.False(Path.IsPathRooted(reference.ModelPath));
        Assert.False(Path.IsPathRooted(reference.MetadataPath));
        Assert.Equal(reference.Generation, Path.GetFileNameWithoutExtension(reference.ModelPath)["model-".Length..]);
        Assert.True(File.Exists(ToFullPath(reference.ModelPath)));
        Assert.True(File.Exists(ToFullPath(reference.MetadataPath)));

        var resolved = await _store.ResolveAsync(Owner, reference, default);
        Assert.Equal(Path.GetFullPath(ToFullPath(reference.ModelPath)), resolved.ModelPath);
        Assert.Equal("model-v1", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(resolved.ModelPath)));
        Assert.Equal(reference.MetadataChecksum, Hash(resolved.MetadataJson.Span));
    }

    [Fact]
    public async Task ConcurrentGenerationsForSameOwnerRemainUniqueAndResolvable()
    {
        var writes = Enumerable.Range(0, 12)
            .Select(index => StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes($"model-{index}")))
            .ToArray();

        var references = await Task.WhenAll(writes);

        Assert.Equal(references.Length, references.Select(item => item.Generation).Distinct().Count());
        Assert.Equal(references.Length, references.Select(item => item.ModelPath).Distinct().Count());
        foreach (var reference in references)
        {
            _ = await _store.ResolveAsync(Owner, reference, default);
        }
    }

    [Fact]
    public async Task DeleteGenerationRemovesOnlyRequestedPairForSameOwner()
    {
        var first = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("first-generation"));
        var second = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("second-generation"));

        await _store.DeleteGenerationAsync(Owner, first, default);

        Assert.False(File.Exists(ToFullPath(first.ModelPath)));
        Assert.False(File.Exists(ToFullPath(first.MetadataPath)));
        _ = await _store.ResolveAsync(Owner, second, default);
    }

    [Fact]
    public async Task DeleteGenerationCanResumeAfterModelWasDeletedButMetadataWasTemporarilyLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("target-generation"));
        var sibling = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("sibling-generation"));
        var modelPath = ToFullPath(target.ModelPath);
        var metadataPath = ToFullPath(target.MetadataPath);

        await using (var blocker = new FileStream(
                         metadataPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                _store.DeleteGenerationAsync(Owner, target, default));
            Assert.False(File.Exists(modelPath));
            Assert.True(File.Exists(metadataPath));
        }

        await _store.DeleteGenerationAsync(Owner, target, default);

        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(metadataPath));
        _ = await _store.ResolveAsync(Owner, sibling, default);
    }

    [Fact]
    public async Task AbandonedRecipeCopyKeepsPreexistingGenerationForSameTargetOwner()
    {
        var sourceReference = await StoreGenerationAsync(
            Owner,
            Encoding.UTF8.GetBytes("source-generation"));
        var targetOwner = new TemplateModelOwner("recipe-copy", Owner.FlowId, Owner.ToolId);
        var siblingReference = await StoreGenerationAsync(
            targetOwner,
            Encoding.UTF8.GetBytes("preexisting-sibling"));
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TemplateModelParameterCodec.WriteHalcon(
            parameters,
            new HalconTemplateModelState(
                sourceReference,
                new TemplateLearnedGeometry(new Pose2D(1, 2, 3) { Scale = 1 }, 20, 30)));
        var source = new Recipe
        {
            Id = Owner.RecipeId,
            CurrentFlowId = Owner.FlowId,
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = Owner.FlowId,
                    Tools = [new VisionToolDefinition { Id = Owner.ToolId, Parameters = parameters }]
                }
            ]
        }.WithNormalizedFlows();
        var events = new List<string>();
        var manager = new TemplateModelResourceManager(
            _store,
            new RecordingRetirementSink(events),
            new RecordingLog());
        var copy = await manager.PrepareRecipeCopyAsync(source, "recipe-copy", default);
        var copiedReference = TemplateModelParameterCodec.ReadHalcon(
            copy.Recipe.EffectiveFlows[0].Tools[0].Parameters)!.Reference;

        await copy.DisposeAsync();

        Assert.False(File.Exists(ToFullPath(copiedReference.ModelPath)));
        Assert.False(File.Exists(ToFullPath(copiedReference.MetadataPath)));
        var sibling = await _store.ResolveAsync(targetOwner, siblingReference, default);
        Assert.Equal(
            "preexisting-sibling",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(sibling.ModelPath)));
    }

    [Fact]
    public async Task DisposingSessionFromOneStoreDoesNotBreakAnotherStoreSession()
    {
        var otherStore = new FileTemplateModelStore(_paths);
        await using var first = await _store.BeginWriteAsync(Owner, default);
        await using var second = await otherStore.BeginWriteAsync(Owner, default);

        await first.DisposeAsync();
        var model = Encoding.UTF8.GetBytes("second-session-model");
        await File.WriteAllBytesAsync(second.StagingModelPath, model);
        var metadata = CreateMetadata(
            Owner,
            second.Generation,
            $"model-{second.Generation}.shm",
            Hash(model));

        var reference = await otherStore.CommitAsync(second, metadata, default);

        _ = await otherStore.ResolveAsync(Owner, reference, default);
    }

    [Fact]
    public async Task DeleteRejectsActiveWriteSessionWithoutRemovingExistingGeneration()
    {
        var existing = await StoreGenerationAsync(Owner);
        await using var active = await _store.BeginWriteAsync(Owner, default);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.DeleteOwnerResourcesAsync(Owner, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
        _ = await _store.ResolveAsync(Owner, existing, default);

        await active.DisposeAsync();
        await _store.DeleteOwnerResourcesAsync(Owner, default);
        Assert.False(File.Exists(ToFullPath(existing.ModelPath)));
        Assert.False(File.Exists(ToFullPath(existing.MetadataPath)));
    }

    [Fact]
    public async Task DisposeWaitsForInFlightCommitAndDoesNotDeleteItsStagingFiles()
    {
        var session = await _store.BeginWriteAsync(Owner, default);
        var model = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(model);
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(
            Owner,
            session.Generation,
            $"model-{session.Generation}.shm",
            Hash(model));

        var commit = _store.CommitAsync(session, metadata, default);
        var dispose = session.DisposeAsync().AsTask();

        var reference = await commit;
        await dispose;
        _ = await _store.ResolveAsync(Owner, reference, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveRejectsMissingGenerationFile(bool deleteModel)
    {
        var reference = await StoreGenerationAsync(Owner);
        File.Delete(ToFullPath(deleteModel ? reference.ModelPath : reference.MetadataPath));

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelNotFound, error.Code);
    }

    [Fact]
    public async Task ResolveRejectsTamperedModelChecksum()
    {
        var reference = await StoreGenerationAsync(Owner);
        await File.AppendAllTextAsync(ToFullPath(reference.ModelPath), "tampered");

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch, error.Code);
    }

    [Fact]
    public async Task ResolveRejectsTamperedMetadataChecksumBeforeTrustingJson()
    {
        var reference = await StoreGenerationAsync(Owner);
        await File.AppendAllTextAsync(ToFullPath(reference.MetadataPath), " ");

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch, error.Code);
    }

    [Fact]
    public async Task ResolveRejectsMetadataOwnerMismatchEvenWhenReferenceChecksumIsUpdated()
    {
        var reference = await StoreGenerationAsync(Owner);
        var metadataPath = ToFullPath(reference.MetadataPath);
        var metadata = ParseObject(await File.ReadAllBytesAsync(metadataPath));
        metadata["owner"] = new Dictionary<string, string>
        {
            ["recipeId"] = "other-recipe",
            ["flowId"] = Owner.FlowId,
            ["toolId"] = Owner.ToolId
        };
        var changed = JsonSerializer.SerializeToUtf8Bytes(metadata);
        await File.WriteAllBytesAsync(metadataPath, changed);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(
                Owner,
                reference with { MetadataChecksum = Hash(changed) },
                default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveRejectsDuplicateRequiredMetadataProperty()
    {
        var reference = await StoreGenerationAsync(Owner);
        var metadataPath = ToFullPath(reference.MetadataPath);
        var original = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(metadataPath));
        var changedText = original.Insert(1, "\"generation\":\"attacker\",");
        var changed = Encoding.UTF8.GetBytes(changedText);
        await File.WriteAllBytesAsync(metadataPath, changed);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(
                Owner,
                reference with { MetadataChecksum = Hash(changed) },
                default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveRejectsReferenceVersionAndFingerprintMismatchesWithStableCodes()
    {
        var reference = await StoreGenerationAsync(Owner);

        var version = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference with { ModelVersion = "other-version" }, default));
        var runtime = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference with { RuntimeVersion = "other-runtime" }, default));
        var fingerprint = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(
                Owner,
                reference with { GenerationParameterFingerprint = new string('d', 64) },
                default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelVersionMismatch, version.Code);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch, runtime.Code);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, fingerprint.Code);
    }

    [Fact]
    public async Task ResolveRejectsMalformedReferenceChecksumBeforeOpeningModel()
    {
        var reference = await StoreGenerationAsync(Owner);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(Owner, reference with { ModelChecksum = "bad" }, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelChecksumMismatch, error.Code);
    }

    [Theory]
    [InlineData("schemaVersion", 2)]
    [InlineData("engine", "OpenCv")]
    public async Task ResolveRejectsUnsupportedMetadataEnvelope(string propertyName, object value)
    {
        var reference = await StoreGenerationAsync(Owner);
        var metadataPath = ToFullPath(reference.MetadataPath);
        var metadata = ParseObject(await File.ReadAllBytesAsync(metadataPath));
        metadata[propertyName] = value;
        var changed = JsonSerializer.SerializeToUtf8Bytes(metadata);
        await File.WriteAllBytesAsync(metadataPath, changed);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.ResolveAsync(
                Owner,
                reference with { MetadataChecksum = Hash(changed) },
                default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
    }

    [Fact]
    public async Task CommitRejectsMetadataThatDoesNotMatchSessionGeneration()
    {
        await using var session = await _store.BeginWriteAsync(Owner, default);
        var model = Encoding.UTF8.GetBytes("model");
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(
            Owner,
            "another-generation",
            $"model-{session.Generation}.shm",
            Hash(model));

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.CommitAsync(session, metadata, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
        Assert.Empty(Directory.EnumerateFiles(_paths.TemplateResourceDirectory, "model-*.shm", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UncommittedSessionDisposeDeletesOnlyItsStagingFile()
    {
        var oldReference = await StoreGenerationAsync(Owner);
        string stagingPath;
        await using (var session = await _store.BeginWriteAsync(Owner, default))
        {
            stagingPath = session.StagingModelPath;
            await File.WriteAllTextAsync(stagingPath, "uncommitted");
        }

        Assert.False(File.Exists(stagingPath));
        _ = await _store.ResolveAsync(Owner, oldReference, default);
    }

    [Fact]
    public async Task FailedStagingCleanupReleasesOwnerLeaseAndRetriesOnlyItsExactFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sibling = await StoreGenerationAsync(
            Owner,
            Encoding.UTF8.GetBytes("preexisting-sibling"));
        var failedSession = await _store.BeginWriteAsync(Owner, default);
        await File.WriteAllTextAsync(failedSession.StagingModelPath, "failed-session");
        var failedStagingPath = failedSession.StagingModelPath;

        await using (var blocker = new FileStream(
                         failedStagingPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                failedSession.DisposeAsync().AsTask());
        }

        var replacement = await _store.BeginWriteAsync(Owner, default);
        await File.WriteAllTextAsync(replacement.StagingModelPath, "replacement-session");
        var replacementStagingPath = replacement.StagingModelPath;

        await failedSession.DisposeAsync();

        Assert.False(File.Exists(failedStagingPath));
        Assert.True(File.Exists(replacementStagingPath));
        _ = await _store.ResolveAsync(Owner, sibling, default);

        await replacement.DisposeAsync();
        await _store.DeleteOwnerResourcesAsync(Owner, default);

        Assert.False(File.Exists(ToFullPath(sibling.ModelPath)));
        Assert.False(File.Exists(ToFullPath(sibling.MetadataPath)));
    }

    [Fact]
    public async Task SessionCannotBeCommittedTwice()
    {
        await using var session = await _store.BeginWriteAsync(Owner, default);
        var model = Encoding.UTF8.GetBytes("model");
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(Owner, session.Generation, $"model-{session.Generation}.shm", Hash(model));
        _ = await _store.CommitAsync(session, metadata, default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CommitAsync(session, metadata, default));
    }

    [Fact]
    public async Task StoreRejectsSessionIssuedByAnotherStoreWithoutConsumingIt()
    {
        await using var session = await _store.BeginWriteAsync(Owner, default);
        var model = Encoding.UTF8.GetBytes("model");
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(Owner, session.Generation, $"model-{session.Generation}.shm", Hash(model));
        var otherPaths = new RuntimePaths(Path.Combine(_baseDirectory, "other-store"));
        var otherStore = new FileTemplateModelStore(otherPaths);

        await Assert.ThrowsAsync<ArgumentException>(
            () => otherStore.CommitAsync(session, metadata, default));

        var reference = await _store.CommitAsync(session, metadata, default);
        _ = await _store.ResolveAsync(Owner, reference, default);
    }

    [Fact]
    public async Task StoreRejectsForgedWriteSession()
    {
        await using var session = new ForgedWriteSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CommitAsync(session, "{}"u8.ToArray(), default));
    }

    [Fact]
    public async Task PreCancelledCommitDoesNotPublishPartialGeneration()
    {
        await using var session = await _store.BeginWriteAsync(Owner, default);
        var model = Encoding.UTF8.GetBytes("model");
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(Owner, session.Generation, $"model-{session.Generation}.shm", Hash(model));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.CommitAsync(session, metadata, cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(_paths.TemplateResourceDirectory, "model-*.shm", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_paths.TemplateResourceDirectory, "model-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FailedNewCommitLeavesOldReferenceResolvable()
    {
        var oldReference = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("old-model"));
        var session = await _store.BeginWriteAsync(Owner, default);
        var newModel = Encoding.UTF8.GetBytes("new-model");
        await File.WriteAllBytesAsync(session.StagingModelPath, newModel);
        var metadata = CreateMetadata(Owner, session.Generation, $"model-{session.Generation}.shm", Hash(newModel));
        var ownerDirectory = Path.GetDirectoryName(ToFullPath(oldReference.ModelPath))!;
        var unpublishedModelPath = Path.Combine(ownerDirectory, $"model-{session.Generation}.shm");
        var blockedMetadataPath = Path.Combine(ownerDirectory, $"model-{session.Generation}.json");
        Directory.CreateDirectory(blockedMetadataPath);

        await Assert.ThrowsAsync<IOException>(() => _store.CommitAsync(session, metadata, default));
        await session.DisposeAsync();

        Assert.False(File.Exists(unpublishedModelPath));
        Assert.True(Directory.Exists(blockedMetadataPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(ownerDirectory),
            path => Path.GetFileName(path).Contains(session.Generation, StringComparison.Ordinal));
        var resolved = await _store.ResolveAsync(Owner, oldReference, default);
        Assert.Equal("old-model", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(resolved.ModelPath)));
    }

    [Fact]
    public void GenerationCleanupExceptionPreservesPrimaryIdentityAndExactTargetContext()
    {
        var primary = new OperationCanceledException("copy cancelled");
        var cleanup = new IOException("staging file is locked");
        var owner = new TemplateModelOwner("target-recipe", "target-flow", "target-tool");

        var failure = new TemplateModelGenerationCleanupException(
            "Template generation copy and rollback both failed.",
            [new TemplateModelGenerationCleanupFailure(owner, "generation-42", cleanup)],
            primary);

        Assert.Same(primary, failure.PrimaryException);
        var exactFailure = Assert.Single(failure.Failures);
        Assert.Equal(owner, exactFailure.Owner);
        Assert.Equal("generation-42", exactFailure.Generation);
        Assert.Same(cleanup, exactFailure.CleanupException);
        var exposedFailures = Assert.IsAssignableFrom<IList<TemplateModelGenerationCleanupFailure>>(
            failure.Failures);
        Assert.Throws<NotSupportedException>(() =>
            exposedFailures[0] = new TemplateModelGenerationCleanupFailure(
                owner,
                "replacement",
                new IOException("replacement")));
    }

    [Fact]
    public async Task CopyGenerationRewritesOwnershipAndGenerationButPreservesPayloadAndFingerprint()
    {
        var sourceReference = await StoreGenerationAsync(Owner, extraPayload: new { customFuturePayload = new[] { 1, 2, 3 } });
        var targetOwner = new TemplateModelOwner("recipe-copy", "flow-copy", "tool-copy");

        var targetReference = await _store.CopyGenerationAsync(Owner, sourceReference, targetOwner, default);
        var target = await _store.ResolveAsync(targetOwner, targetReference, default);
        using var document = JsonDocument.Parse(target.MetadataJson);
        var root = document.RootElement;

        Assert.NotEqual(sourceReference.Generation, targetReference.Generation);
        Assert.NotEqual(sourceReference.ModelPath, targetReference.ModelPath);
        Assert.Equal(sourceReference.GenerationParameterFingerprint, targetReference.GenerationParameterFingerprint);
        Assert.Equal(targetOwner.RecipeId, root.GetProperty("owner").GetProperty("recipeId").GetString());
        Assert.Equal(targetReference.Generation, root.GetProperty("generation").GetString());
        Assert.Equal(Path.GetFileName(targetReference.ModelPath), root.GetProperty("modelFileName").GetString());
        Assert.Equal(3, root.GetProperty("customFuturePayload").GetArrayLength());
        _ = await _store.ResolveAsync(Owner, sourceReference, default);
    }

    [Fact]
    public async Task DeleteOwnerResourcesDeletesOnlyVerifiedOwnerGenerations()
    {
        var first = await StoreGenerationAsync(Owner);
        var otherOwner = new TemplateModelOwner("other-recipe", "other-flow", "other-tool");
        var other = await StoreGenerationAsync(otherOwner);

        await _store.DeleteOwnerResourcesAsync(Owner, default);

        Assert.False(File.Exists(ToFullPath(first.ModelPath)));
        Assert.False(File.Exists(ToFullPath(first.MetadataPath)));
        _ = await _store.ResolveAsync(otherOwner, other, default);
    }

    [Fact]
    public async Task DeleteOwnerResourcesCanResumeAfterModelWasDeletedButMetadataWasTemporarilyLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("target-generation"));
        var siblingOwner = new TemplateModelOwner(Owner.RecipeId, Owner.FlowId, "SiblingTool");
        var sibling = await StoreGenerationAsync(
            siblingOwner,
            Encoding.UTF8.GetBytes("sibling-generation"));
        var modelPath = ToFullPath(target.ModelPath);
        var metadataPath = ToFullPath(target.MetadataPath);
        var externalDirectory = Path.Combine(
            Path.GetTempPath(),
            $"visionstation-owner-delete-external-{Guid.NewGuid():N}");
        var externalSentinel = Path.Combine(externalDirectory, "keep.txt");
        Directory.CreateDirectory(externalDirectory);
        await File.WriteAllTextAsync(externalSentinel, "keep");

        try
        {
            await using (var blocker = new FileStream(
                             metadataPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<IOException>(() =>
                    _store.DeleteOwnerResourcesAsync(Owner, default));

                Assert.False(File.Exists(modelPath));
                Assert.True(File.Exists(metadataPath));
                _ = await _store.ResolveAsync(siblingOwner, sibling, default);
                Assert.True(File.Exists(externalSentinel));
            }

            await _store.DeleteOwnerResourcesAsync(Owner, default);

            Assert.False(File.Exists(modelPath));
            Assert.False(File.Exists(metadataPath));
            _ = await _store.ResolveAsync(siblingOwner, sibling, default);
            Assert.True(File.Exists(externalSentinel));
        }
        finally
        {
            if (Directory.Exists(externalDirectory))
            {
                Directory.Delete(externalDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteOwnerResourcesRefusesModelOnlyGeneration()
    {
        var target = await StoreGenerationAsync(Owner, Encoding.UTF8.GetBytes("model-only-generation"));
        var modelPath = ToFullPath(target.ModelPath);
        var metadataPath = ToFullPath(target.MetadataPath);
        File.Delete(metadataPath);

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.DeleteOwnerResourcesAsync(Owner, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
        Assert.True(File.Exists(modelPath));
    }

    [Fact]
    public async Task DeleteOwnerResourcesRefusesUnknownFilesInsteadOfRecursiveDeletion()
    {
        var reference = await StoreGenerationAsync(Owner);
        var ownerDirectory = Path.GetDirectoryName(ToFullPath(reference.ModelPath))!;
        var unknownPath = Path.Combine(ownerDirectory, "operator-notes.txt");
        await File.WriteAllTextAsync(unknownPath, "do not delete");

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.DeleteOwnerResourcesAsync(Owner, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
        Assert.True(File.Exists(unknownPath));
        Assert.True(File.Exists(ToFullPath(reference.ModelPath)));
        Assert.True(File.Exists(ToFullPath(reference.MetadataPath)));
    }

    [Fact]
    public async Task DeleteOwnerResourcesRefusesMismatchedMetadataOwnerWithoutDeletingPair()
    {
        var reference = await StoreGenerationAsync(Owner);
        var metadataPath = ToFullPath(reference.MetadataPath);
        var metadata = ParseObject(await File.ReadAllBytesAsync(metadataPath));
        metadata["owner"] = new Dictionary<string, string>
        {
            ["recipeId"] = "attacker",
            ["flowId"] = Owner.FlowId,
            ["toolId"] = Owner.ToolId
        };
        await File.WriteAllBytesAsync(metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata));

        var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
            () => _store.DeleteOwnerResourcesAsync(Owner, default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ModelMetadataInvalid, error.Code);
        Assert.True(File.Exists(ToFullPath(reference.ModelPath)));
        Assert.True(File.Exists(ToFullPath(reference.MetadataPath)));
    }

    [Fact]
    public async Task BeginWriteRejectsExistingReparsePointInTemplateRootAncestry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var reparseParent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Local Settings");
        if (!Directory.Exists(reparseParent) ||
            (File.GetAttributes(reparseParent) & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        var linkedBase = Path.Combine(reparseParent, $"visionstation-reparse-test-{Guid.NewGuid():N}");
        try
        {
            var linkedPaths = new RuntimePaths(linkedBase);
            var linkedStore = new FileTemplateModelStore(linkedPaths);
            var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
                () => linkedStore.BeginWriteAsync(Owner, default));

            Assert.Equal(TemplateMatchingDiagnosticCodes.ModelPathInvalid, error.Code);
        }
        finally
        {
            if (Directory.Exists(linkedBase))
            {
                Directory.Delete(linkedBase, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private async Task<TemplateModelReference> StoreGenerationAsync(
        TemplateModelOwner owner,
        byte[]? model = null,
        object? extraPayload = null)
    {
        await using var session = await _store.BeginWriteAsync(owner, default);
        model ??= Encoding.UTF8.GetBytes($"model:{owner.RecipeId}:{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(session.StagingModelPath, model);
        var metadata = CreateMetadata(
            owner,
            session.Generation,
            $"model-{session.Generation}.shm",
            Hash(model),
            extraPayload);
        return await _store.CommitAsync(session, metadata, default);
    }

    private static byte[] CreateMetadata(
        TemplateModelOwner owner,
        string generation,
        string modelFileName,
        string modelChecksum,
        object? extraPayload = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["engine"] = "Halcon",
            ["modelFormat"] = "halcon-scaled-shape",
            ["modelVersion"] = "halcon-scaled-shape-v1",
            ["nativeRuntimeVersion"] = "26.05.0.0",
            ["owner"] = new Dictionary<string, string>
            {
                ["recipeId"] = owner.RecipeId,
                ["flowId"] = owner.FlowId,
                ["toolId"] = owner.ToolId
            },
            ["generation"] = generation,
            ["modelFileName"] = modelFileName,
            ["modelChecksum"] = modelChecksum,
            ["generationParameterFingerprint"] = Hash("generation-parameters")
        };

        if (extraPayload is not null)
        {
            using var payload = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(extraPayload));
            foreach (var property in payload.RootElement.EnumerateObject())
            {
                metadata[property.Name] = property.Value.Clone();
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(metadata);
    }

    private string ToFullPath(string relativePath)
    {
        return Path.Combine(
            _paths.TemplateResourceDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static Dictionary<string, object?> ParseObject(byte[] json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
               ?? throw new InvalidOperationException("Metadata fixture must be an object.");
    }

    private static string ExpectedSlug(string value)
    {
        var readable = System.Text.RegularExpressions.Regex
            .Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-")
            .Trim('-');
        if (readable.Length == 0)
        {
            readable = "item";
        }

        if (readable.Length > 48)
        {
            readable = readable[..48].TrimEnd('-');
        }

        return $"{readable}-{Hash(value)[..12]}";
    }

    private static string Hash(string value)
    {
        return Hash(Encoding.UTF8.GetBytes(value));
    }

    private static string Hash(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private sealed class ForgedWriteSession : TemplateModelWriteSession
    {
        public override string StagingModelPath => "forged.shm";

        public override string Generation => "forged";

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class TemplateModelParameterCodecTests
{
    private static readonly HalconTemplateModelState State = new(
        new TemplateModelReference(
            "recipe/flow/tool/model-generation.shm",
            "recipe/flow/tool/model-generation.json",
            "halcon-scaled-shape",
            new string('a', 64),
            new string('b', 64),
            "generation",
            "halcon-scaled-shape-v1",
            "26.05.0.0",
            new string('c', 64)),
        new TemplateLearnedGeometry(
            new Pose2D(12.25, 34.5, -67.75) { Scale = 1.125 },
            321,
            654));

    [Fact]
    public void WriteAndReadHalconRoundTripsEveryNamespacedKeyInvariantly()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        TemplateModelParameterCodec.WriteHalcon(parameters, State);
        var roundTrip = TemplateModelParameterCodec.ReadHalcon(parameters);

        Assert.Equal(15, TemplateModelParameterCodec.Keys.Count);
        Assert.Equal(TemplateModelParameterCodec.Keys.OrderBy(x => x), parameters.Keys.OrderBy(x => x));
        Assert.Equal(State, roundTrip);
        Assert.Equal("12.25", parameters["halcon.standardX"]);
        Assert.Equal("1.125", parameters["halcon.standardScale"]);
        Assert.Equal("321", parameters["halcon.templateWidth"]);
    }

    [Fact]
    public void ReadHalconReturnsNullWhenNoKnownStateExists()
    {
        var parameters = new Dictionary<string, string>
        {
            ["halcon.futureExtension"] = "preserve",
            ["modelPath"] = "legacy.bin"
        };

        Assert.Null(TemplateModelParameterCodec.ReadHalcon(parameters));
    }

    [Fact]
    public void ReadHalconFailsClosedWhenAnyRequiredKeyIsMissing()
    {
        var complete = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TemplateModelParameterCodec.WriteHalcon(complete, State);

        foreach (var key in TemplateModelParameterCodec.Keys)
        {
            var partial = new Dictionary<string, string>(complete, StringComparer.OrdinalIgnoreCase);
            partial.Remove(key);

            var error = Assert.Throws<TemplateMatchingConfigurationException>(
                () => TemplateModelParameterCodec.ReadHalcon(partial));

            Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, error.Code);
        }
    }

    [Theory]
    [InlineData("halcon.standardX", "NaN")]
    [InlineData("halcon.standardScale", "0")]
    [InlineData("halcon.templateWidth", "-1")]
    [InlineData("halcon.modelChecksum", "not-a-sha256")]
    [InlineData("halcon.metadataChecksum", "")]
    [InlineData("halcon.generationParameterFingerprint", "abc")]
    public void ReadHalconRejectsInvalidValues(string key, string value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TemplateModelParameterCodec.WriteHalcon(parameters, State);
        parameters[key] = value;

        var error = Assert.Throws<TemplateMatchingConfigurationException>(
            () => TemplateModelParameterCodec.ReadHalcon(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, error.Code);
    }

    [Fact]
    public void ReadHalconRejectsWrongModelFormatAndAmbiguousCaseVariantKeys()
    {
        var wrongFormat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TemplateModelParameterCodec.WriteHalcon(wrongFormat, State);
        wrongFormat["halcon.modelFormat"] = "future-format";
        Assert.Throws<TemplateMatchingConfigurationException>(
            () => TemplateModelParameterCodec.ReadHalcon(wrongFormat));

        var ambiguous = new Dictionary<string, string>(StringComparer.Ordinal);
        TemplateModelParameterCodec.WriteHalcon(ambiguous, State);
        ambiguous["HALCON.MODELPATH"] = State.Reference.ModelPath;
        Assert.Throws<TemplateMatchingConfigurationException>(
            () => TemplateModelParameterCodec.ReadHalcon(ambiguous));
    }

    [Fact]
    public void WriteHalconValidatesBeforeMutatingAndUsesInvariantCulture()
    {
        var parameters = new Dictionary<string, string>
        {
            ["sentinel"] = "unchanged"
        };
        var invalid = State with
        {
            Geometry = State.Geometry with
            {
                StandardPose = State.Geometry.StandardPose with { Scale = 0 }
            }
        };

        Assert.Throws<TemplateMatchingConfigurationException>(
            () => TemplateModelParameterCodec.WriteHalcon(parameters, invalid));
        Assert.Equal(new Dictionary<string, string> { ["sentinel"] = "unchanged" }, parameters);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            TemplateModelParameterCodec.WriteHalcon(parameters, State);
            Assert.Equal("12.25", parameters["halcon.standardX"]);
            Assert.Equal(State, TemplateModelParameterCodec.ReadHalcon(parameters));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void RemoveHalconDeletesOnlyExactKnownKeys()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["modelPath"] = "legacy.bin",
            ["modelVersion"] = "1.0",
            ["templatePixels"] = "pixels",
            ["standardX"] = "10",
            ["templateWidth"] = "20",
            ["halcon.futureExtension"] = "preserve"
        };
        TemplateModelParameterCodec.WriteHalcon(parameters, State);

        TemplateModelParameterCodec.RemoveHalcon(parameters);
        TemplateModelParameterCodec.RemoveHalcon(parameters);

        Assert.All(TemplateModelParameterCodec.Keys, key => Assert.False(parameters.ContainsKey(key)));
        Assert.Equal("OpenCv", parameters["engine"]);
        Assert.Equal("legacy.bin", parameters["modelPath"]);
        Assert.Equal("1.0", parameters["modelVersion"]);
        Assert.Equal("pixels", parameters["templatePixels"]);
        Assert.Equal("10", parameters["standardX"]);
        Assert.Equal("20", parameters["templateWidth"]);
        Assert.Equal("preserve", parameters["halcon.futureExtension"]);
    }

    [Fact]
    public void TemplateReferencePoseCodecUsesCompleteCentralStateReader()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "Halcon"
        };
        TemplateModelParameterCodec.WriteHalcon(parameters, State);

        Assert.Equal(State.Geometry, TemplateReferencePoseCodec.ReadActive(parameters));

        parameters.Remove("halcon.modelChecksum");
        var error = Assert.Throws<TemplateMatchingConfigurationException>(
            () => TemplateReferencePoseCodec.ReadActive(parameters));
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, error.Code);
    }
}
