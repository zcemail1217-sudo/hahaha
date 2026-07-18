using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

namespace VisionStation.Vision.Halcon.TestHost;

internal sealed class HalconBenchmarkRunner
{
    private const int BenchmarkSchemaVersion = 1;
    private readonly FileTemplateModelStore _store;
    private readonly TemplateModelOwner _owner;
    private readonly string _runtimeRoot;
    private readonly string _runtimeVersion;

    public HalconBenchmarkRunner(
        FileTemplateModelStore store,
        TemplateModelOwner owner,
        string runtimeRoot,
        string runtimeVersion)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _runtimeRoot = string.IsNullOrWhiteSpace(runtimeRoot)
            ? throw new ArgumentException("HALCON runtime root is required.", nameof(runtimeRoot))
            : runtimeRoot;
        _runtimeVersion = string.IsNullOrWhiteSpace(runtimeVersion)
            ? throw new ArgumentException("HALCON runtime version is required.", nameof(runtimeVersion))
            : runtimeVersion;
    }

    public async Task<HalconBenchmarkRunResult> RunAsync(
        int iterations,
        CancellationToken cancellationToken)
    {
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        BenchmarkLearningResult learning = await LearnAsync(cancellationToken).ConfigureAwait(false);
        if (!learning.Success)
        {
            return HalconBenchmarkRunResult.Failed(
                learning.Code!,
                learning.Stage!,
                learning.TechnicalSummary!);
        }

        ImageFrame singleTarget = SyntheticSmokeProduct.CreateTemplateFrame();
        BenchmarkOperationResult warmup = await MatchWithFreshRuntimeAsync(
            singleTarget,
            learning.Parameters!,
            TemplateMatchCardinality.Single,
            expectedCount: 1,
            cancellationToken).ConfigureAwait(false);
        if (!warmup.Success)
        {
            return HalconBenchmarkRunResult.Failed(
                warmup.Code!,
                warmup.Stage!,
                "HALCON benchmark warm-up failed: " + warmup.TechnicalSummary);
        }

        BenchmarkGroupResult coldLoad = await MeasureColdLoadAsync(
            iterations,
            singleTarget,
            learning.Parameters!,
            cancellationToken).ConfigureAwait(false);
        if (coldLoad.Report is null)
        {
            return coldLoad.ToFailedRunResult("coldLoad");
        }

        TemplateMatchingRuntime warmRuntime = CreateRuntime();
        try
        {
            BenchmarkOperationResult warmRuntimeWarmup = await MatchAsync(
                warmRuntime.Service,
                singleTarget,
                learning.Parameters!,
                TemplateMatchCardinality.Single,
                expectedCount: 1,
                cancellationToken).ConfigureAwait(false);
            if (!warmRuntimeWarmup.Success)
            {
                return HalconBenchmarkRunResult.Failed(
                    warmRuntimeWarmup.Code!,
                    warmRuntimeWarmup.Stage!,
                    "HALCON warm-runtime model-cache initialization failed: " +
                    warmRuntimeWarmup.TechnicalSummary);
            }

            BenchmarkGroupResult warmSingle = await MeasureWarmGroupAsync(
                warmRuntime.Service,
                iterations,
                singleTarget,
                learning.Parameters!,
                TemplateMatchCardinality.Single,
                expectedCount: 1,
                cancellationToken).ConfigureAwait(false);
            if (warmSingle.Report is null)
            {
                return warmSingle.ToFailedRunResult("warmSingle");
            }

            BenchmarkGroupResult targets1 = await MeasureWarmGroupAsync(
                warmRuntime.Service,
                iterations,
                CreateTargetFrame(1),
                CreateExactCountParameters(learning.Parameters!, 1),
                TemplateMatchCardinality.ExactCount,
                expectedCount: 1,
                cancellationToken).ConfigureAwait(false);
            if (targets1.Report is null)
            {
                return targets1.ToFailedRunResult("targets1");
            }

            BenchmarkGroupResult targets3 = await MeasureWarmGroupAsync(
                warmRuntime.Service,
                iterations,
                CreateTargetFrame(3),
                CreateExactCountParameters(learning.Parameters!, 3),
                TemplateMatchCardinality.ExactCount,
                expectedCount: 3,
                cancellationToken).ConfigureAwait(false);
            if (targets3.Report is null)
            {
                return targets3.ToFailedRunResult("targets3");
            }

            BenchmarkGroupResult targets5 = await MeasureWarmGroupAsync(
                warmRuntime.Service,
                iterations,
                CreateTargetFrame(5),
                CreateExactCountParameters(learning.Parameters!, 5),
                TemplateMatchCardinality.ExactCount,
                expectedCount: 5,
                cancellationToken).ConfigureAwait(false);
            if (targets5.Report is null)
            {
                return targets5.ToFailedRunResult("targets5");
            }

            HalconBenchmarkFingerprint fingerprint;
            try
            {
                fingerprint = CreateFingerprint();
            }
            catch (InvalidDataException exception)
            {
                return HalconBenchmarkRunResult.Failed(
                    "BENCHMARK_FINGERPRINT_INVALID",
                    "benchmark-fingerprint",
                    $"HALCON NuGet fingerprint could not be resolved; " +
                    $"ExceptionType={exception.GetType().Name}; {exception.Message}");
            }

            var document = new HalconBenchmarkDocument(
                BenchmarkSchemaVersion,
                DateTimeOffset.UtcNow,
                iterations,
                fingerprint,
                coldLoad.Report,
                warmSingle.Report,
                targets1.Report,
                targets3.Report,
                targets5.Report);
            return HalconBenchmarkRunResult.Completed(document);
        }
        finally
        {
            await warmRuntime.Service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<BenchmarkLearningResult> LearnAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        TemplateMatchingRuntime runtime = CreateRuntime();
        TemplateLearningResult learning;
        try
        {
            learning = await runtime.Service.LearnAsync(
                new TemplateLearningRequest(
                    _owner,
                    SyntheticSmokeProduct.CreateTemplateFrame(),
                    SyntheticSmokeProduct.TemplateRoi,
                    SearchRoi: null,
                    parameters),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.Service.DisposeAsync().ConfigureAwait(false);
        }

        if (!learning.Success)
        {
            return BenchmarkLearningResult.Failed(
                learning.Diagnostic?.Code ?? TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                NormalizeStage(learning.Diagnostic?.FailureStage),
                learning.Diagnostic?.TechnicalDetails ?? "HALCON benchmark model learning failed.");
        }

        HalconTemplateModelState? state;
        try
        {
            state = TemplateModelParameterCodec.ReadHalcon(learning.Parameters);
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return BenchmarkLearningResult.Failed(
                exception.Code,
                NormalizeStage(exception.FailureStage),
                exception.TechnicalDetails ?? "HALCON benchmark model metadata is invalid.");
        }

        if (state is null)
        {
            return BenchmarkLearningResult.Failed(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "model",
                "HALCON benchmark learning did not persist model metadata.");
        }

        return BenchmarkLearningResult.Passed(
            new Dictionary<string, string>(
                learning.Parameters,
                StringComparer.OrdinalIgnoreCase));
    }

    private async Task<BenchmarkGroupResult> MeasureColdLoadAsync(
        int iterations,
        ImageFrame frame,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ProcessResourceSnapshot before = ProcessResourceSnapshot.Capture();
        var durations = new List<double>(iterations);
        var operatorFailures = 0;
        BenchmarkOperationResult? lastFailure = null;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateMatchingRuntime runtime = CreateRuntime();
            try
            {
                long started = Stopwatch.GetTimestamp();
                BenchmarkOperationResult result = await MatchAsync(
                    runtime.Service,
                    frame,
                    parameters,
                    TemplateMatchCardinality.Single,
                    expectedCount: 1,
                    cancellationToken).ConfigureAwait(false);
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (result.Success)
                {
                    durations.Add(elapsedMs);
                }
                else
                {
                    operatorFailures++;
                    lastFailure = result;
                }
            }
            finally
            {
                await runtime.Service.DisposeAsync().ConfigureAwait(false);
            }
        }

        return CompleteGroup(
            durations,
            operatorFailures,
            lastFailure,
            before,
            ProcessResourceSnapshot.Capture());
    }

    private async Task<BenchmarkGroupResult> MeasureWarmGroupAsync(
        ITemplateMatchingService service,
        int iterations,
        ImageFrame frame,
        IReadOnlyDictionary<string, string> parameters,
        TemplateMatchCardinality cardinality,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        return await MeasureGroupAsync(
            iterations,
            () => MatchAsync(
                service,
                frame,
                parameters,
                cardinality,
                expectedCount,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BenchmarkOperationResult> MatchAsync(
        ITemplateMatchingService service,
        ImageFrame frame,
        IReadOnlyDictionary<string, string> parameters,
        TemplateMatchCardinality cardinality,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        TemplateMatchBatchResult result = await service.MatchAsync(
            new TemplateMatchingRequest(
                _owner,
                frame,
                SearchRoi: null,
                parameters,
                cardinality,
                expectedCount),
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(result, expectedCount);
    }

    private static async Task<BenchmarkGroupResult> MeasureGroupAsync(
        int iterations,
        Func<Task<BenchmarkOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        ProcessResourceSnapshot before = ProcessResourceSnapshot.Capture();
        var durations = new List<double>(iterations);
        var operatorFailures = 0;
        BenchmarkOperationResult? lastFailure = null;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            BenchmarkOperationResult result = await operation().ConfigureAwait(false);
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.Success)
            {
                durations.Add(elapsedMs);
            }
            else
            {
                operatorFailures++;
                lastFailure = result;
            }
        }

        ProcessResourceSnapshot after = ProcessResourceSnapshot.Capture();
        return CompleteGroup(
            durations,
            operatorFailures,
            lastFailure,
            before,
            after);
    }

    private static BenchmarkGroupResult CompleteGroup(
        IReadOnlyCollection<double> durations,
        int operatorFailures,
        BenchmarkOperationResult? lastFailure,
        ProcessResourceSnapshot before,
        ProcessResourceSnapshot after)
    {
        if (durations.Count == 0)
        {
            return BenchmarkGroupResult.Failed(
                operatorFailures,
                lastFailure?.Code ?? TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                lastFailure?.Stage ?? "match",
                lastFailure?.TechnicalSummary ?? "HALCON benchmark group produced no valid samples.");
        }

        return BenchmarkGroupResult.Completed(
            HalconBenchmarkReport.Create(
                durations,
                before.WorkingSetBytes,
                after.WorkingSetBytes,
                before.PrivateBytes,
                after.PrivateBytes,
                before.Handles,
                after.Handles,
                operatorFailures),
            lastFailure);
    }

    private async Task<BenchmarkOperationResult> MatchWithFreshRuntimeAsync(
        ImageFrame frame,
        IReadOnlyDictionary<string, string> parameters,
        TemplateMatchCardinality cardinality,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        TemplateMatchingRuntime runtime = CreateRuntime();
        try
        {
            TemplateMatchBatchResult result = await runtime.Service.MatchAsync(
                new TemplateMatchingRequest(
                    _owner,
                    frame,
                    SearchRoi: null,
                    parameters,
                    cardinality,
                    expectedCount),
                cancellationToken).ConfigureAwait(false);
            return ToOperationResult(result, expectedCount);
        }
        finally
        {
            await runtime.Service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static BenchmarkOperationResult ToOperationResult(
        TemplateMatchBatchResult result,
        int expectedCount)
    {
        if (result.HasMatch &&
            result.Outcome == InspectionOutcome.Ok &&
            result.Matches.Count == expectedCount)
        {
            return BenchmarkOperationResult.Passed();
        }

        return BenchmarkOperationResult.Failed(
            result.Diagnostic?.Code ?? TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
            NormalizeStage(result.Diagnostic?.FailureStage),
            result.Diagnostic?.TechnicalDetails ??
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected {expectedCount} accepted target(s), but received {result.Matches.Count}."));
    }

    private static Dictionary<string, string> CreateExactCountParameters(
        IReadOnlyDictionary<string, string> learnedParameters,
        int expectedCount)
    {
        HalconTemplateModelState? state = TemplateModelParameterCodec.ReadHalcon(learnedParameters);
        if (state is null)
        {
            throw new InvalidOperationException(
                "HALCON benchmark cannot create exact-count parameters without model metadata.");
        }

        Dictionary<string, string> parameters =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        TemplateModelParameterCodec.WriteHalcon(parameters, state);
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = expectedCount.ToString(
            CultureInfo.InvariantCulture);
        return parameters;
    }

    private TemplateMatchingRuntime CreateRuntime()
    {
        return HalconTemplateMatchingFactory.Create(
            _store,
            new HalconRuntimeConfiguration { RuntimeRoot = _runtimeRoot },
            SilentDiagnosticSink.Instance);
    }

    private HalconBenchmarkFingerprint CreateFingerprint()
    {
        return new HalconBenchmarkFingerprint(
            Environment.MachineName,
            Environment.ProcessorCount,
            ReadProcessorIdentifier(),
            ReadTotalAvailableMemoryBytes(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            _runtimeVersion,
            ReadHalconNuGetVersion());
    }

    private static string ReadProcessorIdentifier()
    {
        string? identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(identifier)
            ? "unknown"
            : identifier.Trim();
    }

    private static long ReadTotalAvailableMemoryBytes()
    {
        long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Max(0, availableMemory);
    }

    private static string ReadHalconNuGetVersion()
    {
        var dependencyFiles = new List<string>();
        if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string configuredFiles &&
            !string.IsNullOrWhiteSpace(configuredFiles))
        {
            dependencyFiles.AddRange(configuredFiles.Split(
                Path.PathSeparator,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        dependencyFiles.Add(Path.Combine(
            AppContext.BaseDirectory,
            "VisionStation.Vision.Halcon.TestHost.deps.json"));
        return HalconBenchmarkFingerprintReader.ReadRequiredHalconNuGetVersion(
            dependencyFiles);
    }

    private static ImageFrame CreateTargetFrame(int targetCount)
    {
        if (targetCount is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCount));
        }

        ImageFrame tile = SyntheticSmokeProduct.CreateTemplateFrame();
        int columns = Math.Min(3, targetCount);
        int rows = (targetCount + columns - 1) / columns;
        int width = columns * tile.Width;
        int height = rows * tile.Height;
        var pixels = new byte[width * height];
        Array.Fill(pixels, tile.Pixels[0]);
        for (int target = 0; target < targetCount; target++)
        {
            int column = target % columns;
            int row = target / columns;
            for (int sourceRow = 0; sourceRow < tile.Height; sourceRow++)
            {
                Buffer.BlockCopy(
                    tile.Pixels,
                    sourceRow * tile.Stride,
                    pixels,
                    (row * tile.Height + sourceRow) * width + column * tile.Width,
                    tile.Width);
            }
        }

        return new ImageFrame(
            $"halcon-benchmark-{targetCount}-targets",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "HALCON TestHost benchmark");
    }

    private static string NormalizeStage(string? stage)
    {
        return stage switch
        {
            TemplateMatchingFailureStages.Configuration => "configuration",
            TemplateMatchingFailureStages.Runtime => "runtime",
            TemplateMatchingFailureStages.Model => "model",
            TemplateMatchingFailureStages.Match => "match",
            _ => "benchmark"
        };
    }

    private sealed record BenchmarkLearningResult(
        IReadOnlyDictionary<string, string>? Parameters,
        string? Code,
        string? Stage,
        string? TechnicalSummary)
    {
        public bool Success => Parameters is not null;

        public static BenchmarkLearningResult Passed(IReadOnlyDictionary<string, string> parameters)
        {
            return new BenchmarkLearningResult(parameters, null, null, null);
        }

        public static BenchmarkLearningResult Failed(string code, string stage, string summary)
        {
            return new BenchmarkLearningResult(null, code, stage, summary);
        }
    }

    private sealed record BenchmarkOperationResult(
        bool Success,
        string? Code,
        string? Stage,
        string? TechnicalSummary)
    {
        public static BenchmarkOperationResult Passed()
        {
            return new BenchmarkOperationResult(true, null, null, null);
        }

        public static BenchmarkOperationResult Failed(string code, string stage, string summary)
        {
            return new BenchmarkOperationResult(false, code, stage, summary);
        }
    }

    private sealed record BenchmarkGroupResult(
        HalconBenchmarkReport? Report,
        int OperatorFailures,
        string? Code,
        string? Stage,
        string? TechnicalSummary)
    {
        public static BenchmarkGroupResult Completed(
            HalconBenchmarkReport report,
            BenchmarkOperationResult? lastFailure)
        {
            return new BenchmarkGroupResult(
                report,
                report.OperatorFailures,
                lastFailure?.Code,
                lastFailure?.Stage,
                lastFailure?.TechnicalSummary);
        }

        public static BenchmarkGroupResult Failed(
            int operatorFailures,
            string code,
            string stage,
            string summary)
        {
            return new BenchmarkGroupResult(null, operatorFailures, code, stage, summary);
        }

        public HalconBenchmarkRunResult ToFailedRunResult(string group)
        {
            return HalconBenchmarkRunResult.Failed(
                Code ?? TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                Stage ?? "benchmark",
                $"HALCON benchmark group '{group}' produced no valid samples; " +
                $"operatorFailures={OperatorFailures}; {TechnicalSummary}");
        }
    }

    private readonly record struct ProcessResourceSnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        int Handles)
    {
        public static ProcessResourceSnapshot Capture()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new ProcessResourceSnapshot(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.HandleCount);
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

internal sealed record HalconBenchmarkRunResult(
    HalconBenchmarkDocument? Document,
    string? Code,
    string? Stage,
    string? TechnicalSummary)
{
    public static HalconBenchmarkRunResult Completed(HalconBenchmarkDocument document)
    {
        return new HalconBenchmarkRunResult(document, null, null, null);
    }

    public static HalconBenchmarkRunResult Failed(string code, string stage, string summary)
    {
        return new HalconBenchmarkRunResult(null, code, stage, summary);
    }
}
