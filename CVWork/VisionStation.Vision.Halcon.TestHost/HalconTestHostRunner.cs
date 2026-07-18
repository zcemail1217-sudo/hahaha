using System.Diagnostics;
using System.Globalization;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

namespace VisionStation.Vision.Halcon.TestHost;

internal sealed class HalconTestHostRunner
{
    private const string ApprovedRuntimeVersion = "26.05.0.0";
    private const string HalconArchitecture = "x64-win64";
    private const string SuccessCode = "OK";
    private const int MinimumPostCancellationWaitMilliseconds = 50;
    private static readonly TimeSpan NativeEntryObservationTimeout = TimeSpan.FromSeconds(30);

    public async Task<HalconTestHostReport> ExecuteAsync(
        HalconTestHostCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        string expectedVersion = string.Equals(
            command.Name,
            HalconTestHostCommands.Probe,
            StringComparison.Ordinal)
            ? command.ExpectedVersion!
            : ApprovedRuntimeVersion;
        RuntimePreflightResult preflight = InspectRuntimeRoot(command.RuntimeRoot, expectedVersion);
        if (preflight.Failure is not null)
        {
            return preflight.Failure;
        }

        RuntimePreflightResult? secondPreflight = null;
        if (command.SecondRuntimeRoot is not null)
        {
            secondPreflight = InspectRuntimeRoot(command.SecondRuntimeRoot, expectedVersion);
            if (secondPreflight.Failure is not null)
            {
                return secondPreflight.Failure;
            }
        }

        Environment.SetEnvironmentVariable("HALCONROOT", preflight.RuntimeRoot);
        Environment.SetEnvironmentVariable("HALCONARCH", HalconArchitecture);

        return command.Name switch
        {
            HalconTestHostCommands.Benchmark => await RunBenchmarkAsync(
                preflight,
                command.Iterations!.Value,
                command.OutputPath!,
                cancellationToken).ConfigureAwait(false),
            HalconTestHostCommands.Probe => await RunProbeAsync(
                preflight,
                command.ExpectedVersion!,
                secondPreflight,
                cancellationToken).ConfigureAwait(false),
            HalconTestHostCommands.LicenseSmoke => await RunLicenseSmokeAsync(
                preflight,
                cancellationToken).ConfigureAwait(false),
            HalconTestHostCommands.ModelRoundtrip => await RunModelRoundtripAsync(
                preflight,
                command.WorkingDirectory!,
                command.CorruptModel,
                cancellationToken).ConfigureAwait(false),
            HalconTestHostCommands.Timeout => await RunTimeoutAsync(
                preflight,
                command.Milliseconds!.Value,
                command.CancelAfterMilliseconds,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported TestHost command '{command.Name}'.")
        };
    }

    private static async Task<HalconTestHostReport> RunBenchmarkAsync(
        RuntimePreflightResult preflight,
        int iterations,
        string outputPath,
        CancellationToken cancellationToken)
    {
        return await RunTemporaryLearningCommandAsync(
            preflight,
            HalconTestHostCommands.Benchmark,
            async (store, owner) =>
            {
                var runner = new HalconBenchmarkRunner(
                    store,
                    owner,
                    preflight.RuntimeRoot!,
                    preflight.RuntimeVersion!);
                HalconBenchmarkRunResult benchmark = await runner.RunAsync(
                    iterations,
                    cancellationToken).ConfigureAwait(false);
                if (benchmark.Document is null)
                {
                    return Failure(
                        benchmark.Code ?? "BENCHMARK_FAILED",
                        benchmark.Stage ?? HalconTestHostCommands.Benchmark,
                        preflight.RuntimeVersion,
                        benchmark.TechnicalSummary ?? "HALCON benchmark did not produce a report.");
                }

                try
                {
                    await HalconBenchmarkOutputWriter.WriteAsync(
                        benchmark.Document,
                        outputPath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
                {
                    return Failure(
                        "BENCHMARK_OUTPUT_INVALID",
                        "benchmark-output",
                        preflight.RuntimeVersion,
                        $"Benchmark output could not be committed atomically; " +
                        $"ExceptionType={exception.GetType().Name}.");
                }

                if (!benchmark.Document.IsSuccessful)
                {
                    return Failure(
                        "BENCHMARK_INCOMPLETE",
                        HalconTestHostCommands.Benchmark,
                        preflight.RuntimeVersion,
                        CreateBenchmarkSummary(benchmark.Document, outputPath));
                }

                return Success(
                    HalconTestHostCommands.Benchmark,
                    preflight.RuntimeVersion!,
                    CreateBenchmarkSummary(benchmark.Document, outputPath));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateBenchmarkSummary(
        HalconBenchmarkDocument document,
        string outputPath)
    {
        int operatorFailures = document.ColdLoad.OperatorFailures +
                               document.WarmSingle.OperatorFailures +
                               document.Targets1.OperatorFailures +
                               document.Targets3.OperatorFailures +
                               document.Targets5.OperatorFailures;
        return $"HALCON benchmark completed; iterations={document.Iterations}; " +
               $"coldLoadSamples={document.ColdLoad.ValidSamples}; " +
               $"warmSingleSamples={document.WarmSingle.ValidSamples}; " +
               $"targets1Samples={document.Targets1.ValidSamples}; " +
               $"targets3Samples={document.Targets3.ValidSamples}; " +
               $"targets5Samples={document.Targets5.ValidSamples}; " +
               $"operatorFailures={operatorFailures}; output={outputPath}.";
    }

    private static async Task<HalconTestHostReport> RunProbeAsync(
        RuntimePreflightResult preflight,
        string expectedVersion,
        RuntimePreflightResult? secondPreflight,
        CancellationToken cancellationToken)
    {
        return await RunTemporaryLearningCommandAsync(
            preflight,
            HalconTestHostCommands.Probe,
            async (store, owner) =>
            {
                LearningSmokeResult learning = await LearnAndDisposeAsync(
                    store,
                    owner,
                    preflight.RuntimeRoot!,
                    operatorTimeoutMilliseconds: null,
                    cancellationToken).ConfigureAwait(false);
                if (learning.Failure is not null)
                {
                    return WithRuntimeVersion(learning.Failure, preflight.RuntimeVersion);
                }

                string runtimeVersion = learning.State!.Reference.RuntimeVersion;
                if (!string.Equals(runtimeVersion, expectedVersion, StringComparison.Ordinal))
                {
                    return Failure(
                        TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                        "runtime",
                        runtimeVersion,
                        $"HALCON system runtime version does not equal expected version '{expectedVersion}'.");
                }

                if (secondPreflight is not null)
                {
                    Environment.SetEnvironmentVariable("HALCONROOT", secondPreflight.RuntimeRoot);
                    Environment.SetEnvironmentVariable("HALCONARCH", HalconArchitecture);
                    LearningSmokeResult secondLearning = await LearnAndDisposeAsync(
                        store,
                        CreateOwner("probe-second-root"),
                        secondPreflight.RuntimeRoot!,
                        operatorTimeoutMilliseconds: null,
                        cancellationToken).ConfigureAwait(false);
                    if (secondLearning.Failure is null)
                    {
                        return Failure(
                            "TESTHOST_EXPECTATION_FAILED",
                            "runtime",
                            runtimeVersion,
                            "HALCON unexpectedly rebound one process to a second runtime root.");
                    }

                    return WithRuntimeVersion(secondLearning.Failure, runtimeVersion);
                }

                return Success(
                    HalconTestHostCommands.Probe,
                    runtimeVersion,
                    "HALCON root, AMD64 image, native/managed versions, and licensed operator probe passed.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HalconTestHostReport> RunLicenseSmokeAsync(
        RuntimePreflightResult preflight,
        CancellationToken cancellationToken)
    {
        return await RunTemporaryLearningCommandAsync(
            preflight,
            HalconTestHostCommands.LicenseSmoke,
            async (store, owner) =>
            {
                LearningSmokeResult learning = await LearnAndDisposeAsync(
                    store,
                    owner,
                    preflight.RuntimeRoot!,
                    operatorTimeoutMilliseconds: null,
                    cancellationToken).ConfigureAwait(false);
                if (learning.Failure is not null)
                {
                    return WithRuntimeVersion(learning.Failure, preflight.RuntimeVersion);
                }

                return Success(
                    HalconTestHostCommands.LicenseSmoke,
                    learning.State!.Reference.RuntimeVersion,
                    "A licensed HALCON scaled-shape model was created and persisted successfully.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HalconTestHostReport> RunModelRoundtripAsync(
        RuntimePreflightResult preflight,
        string workingDirectory,
        bool corruptModel,
        CancellationToken cancellationToken)
    {
        string fullWorkingDirectory;
        try
        {
            fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            Directory.CreateDirectory(fullWorkingDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelPathInvalid,
                "model",
                preflight.RuntimeVersion,
                $"Working directory is unavailable; ExceptionType={exception.GetType().Name}.");
        }

        var store = new FileTemplateModelStore(new RuntimePaths(fullWorkingDirectory));
        var owner = CreateOwner("roundtrip");
        LearningSmokeResult learning = await LearnAndDisposeAsync(
            store,
            owner,
            preflight.RuntimeRoot!,
            operatorTimeoutMilliseconds: null,
            cancellationToken).ConfigureAwait(false);
        if (learning.Failure is not null)
        {
            return WithRuntimeVersion(learning.Failure, preflight.RuntimeVersion);
        }

        if (corruptModel)
        {
            ResolvedTemplateModel resolved = await store.ResolveAsync(
                owner,
                learning.State!.Reference,
                cancellationToken).ConfigureAwait(false);
            await using var model = new FileStream(
                resolved.ModelPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await model.WriteAsync(new byte[] { 0xA5 }, cancellationToken).ConfigureAwait(false);
            await model.FlushAsync(cancellationToken).ConfigureAwait(false);
            model.Flush(flushToDisk: true);
        }

        TemplateMatchingRuntime runtime = CreateRuntime(store, preflight.RuntimeRoot!);
        TemplateMatchBatchResult matching;
        try
        {
            matching = await runtime.Service.MatchAsync(
                new TemplateMatchingRequest(
                    owner,
                    SyntheticSmokeProduct.CreateTemplateFrame(),
                    null,
                    learning.Parameters!,
                    TemplateMatchCardinality.Single,
                    1),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.Service.DisposeAsync().ConfigureAwait(false);
        }

        if (!matching.HasMatch ||
            matching.Outcome != InspectionOutcome.Ok ||
            matching.Matches.Count != 1)
        {
            return FromDiagnostic(
                matching.Diagnostic,
                TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                "match",
                learning.State!.Reference.RuntimeVersion,
                "Fresh runtime did not reload and match exactly one persisted model.");
        }

        TemplateMatchBatchCandidate candidate = matching.Matches[0];
        return Success(
            HalconTestHostCommands.ModelRoundtrip,
            learning.State!.Reference.RuntimeVersion,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Learn/dispose/recreate/load/match passed; score={candidate.Score:F4}; " +
                $"pose=({candidate.Pose.X:F3},{candidate.Pose.Y:F3},{candidate.Pose.Angle:F3},{candidate.Pose.Scale:F4})."));
    }

    private static async Task<HalconTestHostReport> RunTimeoutAsync(
        RuntimePreflightResult preflight,
        int milliseconds,
        int? cancelAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        return await RunTemporaryLearningCommandAsync(
            preflight,
            HalconTestHostCommands.Timeout,
            async (store, owner) =>
            {
                LearningSmokeResult learning = await LearnAndDisposeAsync(
                    store,
                    owner,
                    preflight.RuntimeRoot!,
                    milliseconds,
                    cancellationToken).ConfigureAwait(false);
                if (learning.Failure is not null)
                {
                    return WithRuntimeVersion(learning.Failure, preflight.RuntimeVersion);
                }

                ImageFrame timeoutSearch = SyntheticSmokeProduct.CreateTimeoutSearchFrame();
                if (cancelAfterMilliseconds.HasValue)
                {
                    return await RunObservedCancellationAsync(
                        store,
                        owner,
                        preflight.RuntimeRoot!,
                        learning,
                        timeoutSearch,
                        cancelAfterMilliseconds.Value,
                        cancellationToken).ConfigureAwait(false);
                }

                TemplateMatchingRuntime runtime = CreateRuntime(store, preflight.RuntimeRoot!);
                TemplateMatchBatchResult matching;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    matching = await runtime.Service.MatchAsync(
                        new TemplateMatchingRequest(
                            owner,
                            timeoutSearch,
                            null,
                            learning.Parameters!,
                            TemplateMatchCardinality.Single,
                            1),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    stopwatch.Stop();
                    await runtime.Service.DisposeAsync().ConfigureAwait(false);
                }

                if (!matching.HasMatch ||
                    matching.Outcome != InspectionOutcome.Ok)
                {
                    return FromDiagnostic(
                        matching.Diagnostic,
                        TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                        "match",
                        learning.State!.Reference.RuntimeVersion,
                        $"Timeout smoke search failed after {stopwatch.ElapsedMilliseconds} ms.");
                }

                return Success(
                    HalconTestHostCommands.Timeout,
                    learning.State!.Reference.RuntimeVersion,
                    $"HALCON search completed safely with operatorTimeoutMs={milliseconds}; elapsedMs={stopwatch.ElapsedMilliseconds}.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HalconTestHostReport> RunObservedCancellationAsync(
        FileTemplateModelStore store,
        TemplateModelOwner owner,
        string runtimeRoot,
        LearningSmokeResult learning,
        ImageFrame timeoutSearch,
        int cancelAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        var observer = new RecordingHalconFindScaledShapeObserver();
        TemplateMatchingRuntime runtime = CreateRuntime(store, runtimeRoot, observer);
        using CancellationTokenSource userCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TemplateMatchBatchResult>? matchingTask = null;
        try
        {
            matchingTask = runtime.Service.MatchAsync(
                new TemplateMatchingRequest(
                    owner,
                    timeoutSearch,
                    null,
                    learning.Parameters!,
                    TemplateMatchCardinality.Single,
                    1),
                userCancellation.Token);

            Task entryTimeout = Task.Delay(NativeEntryObservationTimeout, cancellationToken);
            await Task.WhenAny(observer.Started, matchingTask, entryTimeout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!observer.NativeEntered)
            {
                userCancellation.Cancel();
                await ObserveExpectedCancellationAsync(matchingTask, userCancellation.Token)
                    .ConfigureAwait(false);
                return Failure(
                    "NATIVE_ENTRY_NOT_OBSERVED",
                    "cancel",
                    learning.State!.Reference.RuntimeVersion,
                    $"Cancellation smoke did not observe native admission; nativeEntered=false; " +
                    $"nativeReturned={ToLowerBoolean(observer.NativeReturned)}; " +
                    $"cancelAfterMs={cancelAfterMilliseconds}; elapsedMs=0; postCancelWaitMs=0.");
            }

            await Task.Delay(cancelAfterMilliseconds, cancellationToken).ConfigureAwait(false);
            if (observer.NativeReturned)
            {
                await matchingTask.ConfigureAwait(false);
                long nativeElapsedMilliseconds = observer.GetNativeElapsedMilliseconds();
                return Failure(
                    "CANCELLATION_NOT_OBSERVED",
                    "cancel",
                    learning.State!.Reference.RuntimeVersion,
                    $"HALCON returned before the requested cancellation point; nativeEntered=true; " +
                    $"nativeReturned=true; cancelAfterMs={cancelAfterMilliseconds}; " +
                    $"elapsedMs={nativeElapsedMilliseconds}; postCancelWaitMs=0.");
            }

            long cancellationTimestamp = Stopwatch.GetTimestamp();
            userCancellation.Cancel();
            var operationCanceled = false;
            try
            {
                await matchingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (userCancellation.IsCancellationRequested)
            {
                operationCanceled = true;
            }

            if (!operationCanceled)
            {
                return Failure(
                    "CANCELLATION_NOT_OBSERVED",
                    "cancel",
                    learning.State!.Reference.RuntimeVersion,
                    $"HALCON matching completed without OperationCanceledException; " +
                    $"nativeEntered={ToLowerBoolean(observer.NativeEntered)}; " +
                    $"nativeReturned={ToLowerBoolean(observer.NativeReturned)}; " +
                    $"cancelAfterMs={cancelAfterMilliseconds}; " +
                    $"elapsedMs={observer.GetNativeElapsedMilliseconds()}; postCancelWaitMs=0.");
            }

            if (!observer.NativeReturned ||
                observer.StartCount != 1 ||
                observer.CompletedCount != 1)
            {
                return Failure(
                    "CANCELLATION_NOT_DRAINED",
                    "cancel",
                    learning.State!.Reference.RuntimeVersion,
                    $"OperationCanceledException was observed without one completed native call; " +
                    $"nativeEntered={ToLowerBoolean(observer.NativeEntered)}; " +
                    $"nativeReturned={ToLowerBoolean(observer.NativeReturned)}; " +
                    $"startCount={observer.StartCount}; completedCount={observer.CompletedCount}; " +
                    $"cancelAfterMs={cancelAfterMilliseconds}; elapsedMs=0; postCancelWaitMs=0.");
            }

            long nativeElapsed = observer.GetNativeElapsedMilliseconds();
            long postCancelWait = observer.GetPostCancellationWaitMilliseconds(
                cancellationTimestamp);
            if (postCancelWait < MinimumPostCancellationWaitMilliseconds)
            {
                return Failure(
                    "CANCELLATION_NOT_DRAINED",
                    "cancel",
                    learning.State!.Reference.RuntimeVersion,
                    $"Native return followed cancellation without the required drain margin; " +
                    $"nativeEntered=true; nativeReturned=true; cancelAfterMs={cancelAfterMilliseconds}; " +
                    $"elapsedMs={nativeElapsed}; postCancelWaitMs={postCancelWait}.");
            }

            return new HalconTestHostReport(
                true,
                "OPERATION_CANCELLED",
                "cancel",
                learning.State!.Reference.RuntimeVersion,
                $"OperationCanceledException observed only after the native operator returned; " +
                $"nativeEntered=true; nativeReturned=true; cancelAfterMs={cancelAfterMilliseconds}; " +
                $"elapsedMs={nativeElapsed}; postCancelWaitMs={postCancelWait}.");
        }
        finally
        {
            if (matchingTask is { IsCompleted: false })
            {
                userCancellation.Cancel();
                await ObserveExpectedCancellationAsync(matchingTask, userCancellation.Token)
                    .ConfigureAwait(false);
            }

            await runtime.Service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveExpectedCancellationAsync(
        Task<TemplateMatchBatchResult> matchingTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await matchingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<HalconTestHostReport> RunTemporaryLearningCommandAsync(
        RuntimePreflightResult preflight,
        string commandName,
        Func<FileTemplateModelStore, TemplateModelOwner, Task<HalconTestHostReport>> action,
        CancellationToken cancellationToken)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "VisionStation.Halcon.TestHost",
            $"{commandName}-{Guid.NewGuid():N}");
        try
        {
            var store = new FileTemplateModelStore(new RuntimePaths(temporaryRoot));
            return await action(store, CreateOwner(commandName)).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteOwnedTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task<LearningSmokeResult> LearnAndDisposeAsync(
        FileTemplateModelStore store,
        TemplateModelOwner owner,
        string runtimeRoot,
        int? operatorTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        if (operatorTimeoutMilliseconds.HasValue)
        {
            parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] =
                operatorTimeoutMilliseconds.Value.ToString(CultureInfo.InvariantCulture);
            parameters[TemplateMatchingParameterCatalog.NumLevels] = "2";
        }

        TemplateMatchingRuntime runtime = CreateRuntime(store, runtimeRoot);
        TemplateLearningResult learning;
        try
        {
            learning = await runtime.Service.LearnAsync(
                new TemplateLearningRequest(
                    owner,
                    SyntheticSmokeProduct.CreateTemplateFrame(),
                    SyntheticSmokeProduct.TemplateRoi,
                    null,
                    parameters),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.Service.DisposeAsync().ConfigureAwait(false);
        }

        if (!learning.Success)
        {
            return LearningSmokeResult.Failed(
                FromDiagnostic(
                    learning.Diagnostic,
                    TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                    "runtime",
                    null,
                    "HALCON learning smoke failed."));
        }

        HalconTemplateModelState? state;
        try
        {
            state = TemplateModelParameterCodec.ReadHalcon(learning.Parameters);
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return LearningSmokeResult.Failed(
                Failure(
                    exception.Code,
                    NormalizeStage(exception.FailureStage, "model"),
                    null,
                    exception.TechnicalDetails ?? "HALCON learning returned invalid model metadata."));
        }

        if (state is null)
        {
            return LearningSmokeResult.Failed(
                Failure(
                    TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                    "model",
                    null,
                    "HALCON learning succeeded without a persisted model reference."));
        }

        return LearningSmokeResult.Passed(learning.Parameters, state);
    }

    private static TemplateMatchingRuntime CreateRuntime(
        ITemplateModelStore store,
        string runtimeRoot,
        IHalconFindScaledShapeObserver? findObserver = null)
    {
        var configuration = new HalconRuntimeConfiguration
        {
            RuntimeRoot = runtimeRoot
        };
        return findObserver is null
            ? HalconTemplateMatchingFactory.Create(
                store,
                configuration,
                SilentDiagnosticSink.Instance)
            : HalconTemplateMatchingFactory.Create(
                store,
                configuration,
                SilentDiagnosticSink.Instance,
                findObserver);
    }

    private static RuntimePreflightResult InspectRuntimeRoot(
        string runtimeRoot,
        string expectedVersion)
    {
        var files = new HalconRuntimeFileInspector();
        var locator = new HalconRuntimeLocator(
            new ExplicitRootProcessEnvironment(runtimeRoot),
            EmptyRegistryInstallReader.Instance,
            files);
        HalconRuntimeLocationResult location = locator.Locate(new HalconRuntimeConfiguration());
        if (!location.Success)
        {
            string? runtimeVersion = string.Equals(
                location.Diagnostic!.Code,
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                StringComparison.Ordinal)
                ? TryReadRuntimeVersion(runtimeRoot, files)
                : null;
            string summary = string.Join(
                " | ",
                location.Rejections.Select(rejection =>
                    $"Source={rejection.Source}; Code={rejection.Diagnostic.Code}; " +
                    (rejection.Diagnostic.TechnicalDetails ?? rejection.Diagnostic.UserMessage)));
            return RuntimePreflightResult.Failed(
                Failure(
                    location.Diagnostic.Code,
                    "runtime-preflight",
                    runtimeVersion,
                    string.IsNullOrWhiteSpace(summary)
                        ? location.Diagnostic.TechnicalDetails ?? location.Diagnostic.UserMessage
                        : summary));
        }

        HalconRuntimeLocation resolved = location.Location!;
        string resolvedVersion = files.GetFileVersion(resolved.NativeLibraryPath);
        if (!string.Equals(resolvedVersion, expectedVersion, StringComparison.Ordinal))
        {
            return RuntimePreflightResult.Failed(
                Failure(
                    TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                    "runtime-preflight",
                    resolvedVersion,
                    $"HALCON native version does not equal expected version '{expectedVersion}'."));
        }

        return RuntimePreflightResult.Passed(resolved.RuntimeRoot, resolvedVersion);
    }

    private static string? TryReadRuntimeVersion(
        string runtimeRoot,
        IHalconRuntimeFileInspector files)
    {
        try
        {
            string nativePath = Path.Combine(
                Path.GetFullPath(runtimeRoot),
                "bin",
                HalconArchitecture,
                "halcon.dll");
            return files.FileExists(nativePath)
                ? files.GetFileVersion(nativePath)
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TemplateModelOwner CreateOwner(string commandName)
    {
        string runId = Guid.NewGuid().ToString("N");
        return new TemplateModelOwner(
            $"testhost-{commandName}-{runId}",
            "smoke-flow",
            "scaled-shape-tool");
    }

    private static HalconTestHostReport FromDiagnostic(
        TemplateMatchingDiagnostic? diagnostic,
        string fallbackCode,
        string fallbackStage,
        string? runtimeVersion,
        string fallbackSummary)
    {
        return diagnostic is null
            ? Failure(fallbackCode, fallbackStage, runtimeVersion, fallbackSummary)
            : Failure(
                diagnostic.Code,
                NormalizeStage(diagnostic.FailureStage, fallbackStage),
                runtimeVersion,
                diagnostic.TechnicalDetails ?? fallbackSummary);
    }

    private static string NormalizeStage(string? stage, string fallback)
    {
        return stage switch
        {
            TemplateMatchingFailureStages.Configuration => "configuration",
            TemplateMatchingFailureStages.Runtime => "runtime",
            TemplateMatchingFailureStages.Model => "model",
            TemplateMatchingFailureStages.Match => "match",
            _ => fallback
        };
    }

    private static string ToLowerBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private static HalconTestHostReport Success(
        string stage,
        string runtimeVersion,
        string summary)
    {
        return new HalconTestHostReport(
            true,
            SuccessCode,
            stage,
            runtimeVersion,
            summary);
    }

    private static HalconTestHostReport Failure(
        string code,
        string stage,
        string? runtimeVersion,
        string summary)
    {
        return new HalconTestHostReport(
            false,
            code,
            stage,
            runtimeVersion,
            summary);
    }

    private static HalconTestHostReport WithRuntimeVersion(
        HalconTestHostReport report,
        string? runtimeVersion)
    {
        return report.RuntimeVersion is null && runtimeVersion is not null
            ? report with { RuntimeVersion = runtimeVersion }
            : report;
    }

    private static void TryDeleteOwnedTemporaryDirectory(string temporaryRoot)
    {
        try
        {
            string tempParent = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "VisionStation.Halcon.TestHost"));
            string fullTemporaryRoot = Path.GetFullPath(temporaryRoot);
            string requiredPrefix = tempParent.TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            if (!fullTemporaryRoot.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(fullTemporaryRoot))
            {
                Directory.Delete(fullTemporaryRoot, recursive: true);
            }
        }
        catch
        {
            // The smoke result is authoritative. A uniquely-owned temp directory can be reclaimed later.
        }
    }

    private sealed record RuntimePreflightResult(
        string? RuntimeRoot,
        string? RuntimeVersion,
        HalconTestHostReport? Failure)
    {
        public static RuntimePreflightResult Passed(string runtimeRoot, string runtimeVersion)
        {
            return new RuntimePreflightResult(runtimeRoot, runtimeVersion, null);
        }

        public static RuntimePreflightResult Failed(HalconTestHostReport failure)
        {
            return new RuntimePreflightResult(null, failure.RuntimeVersion, failure);
        }
    }

    private sealed record LearningSmokeResult(
        IReadOnlyDictionary<string, string>? Parameters,
        HalconTemplateModelState? State,
        HalconTestHostReport? Failure)
    {
        public static LearningSmokeResult Passed(
            IReadOnlyDictionary<string, string> parameters,
            HalconTemplateModelState state)
        {
            return new LearningSmokeResult(parameters, state, null);
        }

        public static LearningSmokeResult Failed(HalconTestHostReport failure)
        {
            return new LearningSmokeResult(null, null, failure);
        }
    }

    private sealed class ExplicitRootProcessEnvironment(string runtimeRoot) : IHalconProcessEnvironment
    {
        public bool IsWindows => OperatingSystem.IsWindows();

        public bool Is64BitProcess => Environment.Is64BitProcess;

        public string? GetEnvironmentVariable(string name)
        {
            return name switch
            {
                "HALCONROOT" => runtimeRoot,
                "HALCONARCH" => HalconArchitecture,
                _ => null
            };
        }
    }

    private sealed class EmptyRegistryInstallReader : IHalconRegistryInstallReader
    {
        public static EmptyRegistryInstallReader Instance { get; } = new();

        private EmptyRegistryInstallReader()
        {
        }

        public IReadOnlyList<HalconRegistryInstallEntry> ReadRegistry64UninstallEntries()
        {
            return Array.Empty<HalconRegistryInstallEntry>();
        }
    }

    private sealed class RecordingHalconFindScaledShapeObserver : IHalconFindScaledShapeObserver
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private long _startedTimestamp;
        private long _completedTimestamp;
        private int _startCount;
        private int _completedCount;

        public Task Started => _started.Task;

        public int StartCount => Volatile.Read(ref _startCount);

        public int CompletedCount => Volatile.Read(ref _completedCount);

        public bool NativeEntered => StartCount > 0;

        public bool NativeReturned => CompletedCount > 0;

        public void OnStarted()
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (Interlocked.Increment(ref _startCount) == 1)
            {
                Volatile.Write(ref _startedTimestamp, timestamp);
                _started.TrySetResult();
            }
        }

        public void OnCompleted()
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (Interlocked.Increment(ref _completedCount) == 1)
            {
                Volatile.Write(ref _completedTimestamp, timestamp);
            }
        }

        public long GetNativeElapsedMilliseconds()
        {
            return GetElapsedMilliseconds(
                Volatile.Read(ref _startedTimestamp),
                Volatile.Read(ref _completedTimestamp));
        }

        public long GetPostCancellationWaitMilliseconds(long cancellationTimestamp)
        {
            return GetElapsedMilliseconds(
                cancellationTimestamp,
                Volatile.Read(ref _completedTimestamp));
        }

        private static long GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp <= 0 || endTimestamp < startTimestamp)
            {
                return 0;
            }

            return (long)Math.Floor(
                Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds);
        }
    }

    private sealed class SilentDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        public static SilentDiagnosticSink Instance { get; } = new();

        private SilentDiagnosticSink()
        {
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }
    }
}
