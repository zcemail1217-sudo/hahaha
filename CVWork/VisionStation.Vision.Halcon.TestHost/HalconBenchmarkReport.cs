using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStation.Vision.Halcon.TestHost;

public sealed record HalconBenchmarkFingerprint(
    string MachineName,
    int ProcessorCount,
    string ProcessorIdentifier,
    long TotalAvailableMemoryBytes,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string DotnetDescription,
    string HalconRuntimeVersion,
    string HalconNuGetVersion);

public static class HalconBenchmarkFingerprintReader
{
    private const string HalconPackagePrefix = "MVTec.HalconDotNet/";

    public static string ReadHalconNuGetVersion(string dependencyManifestJson)
    {
        if (string.IsNullOrWhiteSpace(dependencyManifestJson))
        {
            throw new InvalidDataException(
                "The .NET dependency manifest is empty and cannot identify MVTec.HalconDotNet.");
        }

        using JsonDocument document = JsonDocument.Parse(dependencyManifestJson);
        if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries) ||
            libraries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The .NET dependency manifest does not contain a 'libraries' object.");
        }

        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            if (!library.Name.StartsWith(HalconPackagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string version = library.Name[HalconPackagePrefix.Length..];
            if (!string.IsNullOrWhiteSpace(version) && !version.Contains('/', StringComparison.Ordinal))
            {
                return version;
            }
        }

        throw new InvalidDataException(
            "The .NET dependency manifest does not identify MVTec.HalconDotNet.");
    }

    public static string ReadRequiredHalconNuGetVersion(
        IEnumerable<string> dependencyManifestPaths)
    {
        ArgumentNullException.ThrowIfNull(dependencyManifestPaths);
        foreach (string dependencyFile in dependencyManifestPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(dependencyFile))
                {
                    continue;
                }

                return ReadHalconNuGetVersion(File.ReadAllText(dependencyFile));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                continue;
            }
        }

        throw new InvalidDataException(
            "No readable .NET dependency manifest identifies MVTec.HalconDotNet.");
    }
}

public static class HalconBenchmarkOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        HalconBenchmarkDocument document,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        string fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(
                Path.GetExtension(fullOutputPath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "HALCON benchmark output must use the '.json' extension.",
                nameof(outputPath));
        }

        string outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException(
                "HALCON benchmark output directory is invalid.",
                nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        catch
        {
            TryDeleteOwnedTemporaryFile(temporaryPath, outputDirectory);
            throw;
        }
    }

    private static void TryDeleteOwnedTemporaryFile(
        string temporaryPath,
        string outputDirectory)
    {
        try
        {
            string fullDirectory = Path.GetFullPath(outputDirectory);
            string fullTemporaryPath = Path.GetFullPath(temporaryPath);
            string requiredPrefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            if (fullTemporaryPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(fullTemporaryPath), ".tmp", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullTemporaryPath))
            {
                File.Delete(fullTemporaryPath);
            }
        }
        catch
        {
            // A uniquely named incomplete temp file is safer than touching the requested output.
        }
    }
}

public sealed record HalconBenchmarkDocument(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    int Iterations,
    HalconBenchmarkFingerprint Fingerprint,
    HalconBenchmarkReport ColdLoad,
    HalconBenchmarkReport WarmSingle,
    HalconBenchmarkReport Targets1,
    HalconBenchmarkReport Targets3,
    HalconBenchmarkReport Targets5)
{
    public bool IsSuccessful =>
        IsSuccessfulGroup(ColdLoad) &&
        IsSuccessfulGroup(WarmSingle) &&
        IsSuccessfulGroup(Targets1) &&
        IsSuccessfulGroup(Targets3) &&
        IsSuccessfulGroup(Targets5);

    private bool IsSuccessfulGroup(HalconBenchmarkReport report)
    {
        return report.ValidSamples == Iterations && report.OperatorFailures == 0;
    }
}

public sealed record HalconBenchmarkReport(
    int ValidSamples,
    int OperatorFailures,
    double MinimumMs,
    double MaximumMs,
    double MedianMs,
    double P95Ms,
    double RangeMs,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long WorkingSetDeltaBytes,
    long PrivateBytesBefore,
    long PrivateBytesAfter,
    long PrivateBytesDelta,
    int HandlesBefore,
    int HandlesAfter,
    int HandleDelta)
{
    public static HalconBenchmarkReport Create(
        IReadOnlyCollection<double> durationsMs,
        long workingSetBeforeBytes,
        long workingSetAfterBytes,
        long privateBytesBefore,
        long privateBytesAfter,
        int handlesBefore,
        int handlesAfter,
        int operatorFailures)
    {
        ArgumentNullException.ThrowIfNull(durationsMs);
        if (durationsMs.Count == 0)
        {
            throw new ArgumentException(
                "At least one valid benchmark duration is required.",
                nameof(durationsMs));
        }

        if (workingSetBeforeBytes < 0 ||
            workingSetAfterBytes < 0 ||
            privateBytesBefore < 0 ||
            privateBytesAfter < 0 ||
            handlesBefore < 0 ||
            handlesAfter < 0 ||
            operatorFailures < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingSetBeforeBytes),
                "Benchmark resource counters and failure counts cannot be negative.");
        }

        double[] ordered = durationsMs.OrderBy(value => value).ToArray();
        if (ordered.Any(value => !double.IsFinite(value) || value <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationsMs),
                "Benchmark durations must be finite and greater than zero.");
        }

        int middle = ordered.Length / 2;
        double median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
        int p95Index = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95d) - 1);
        double minimum = ordered[0];
        double maximum = ordered[^1];

        return new HalconBenchmarkReport(
            ordered.Length,
            operatorFailures,
            minimum,
            maximum,
            median,
            ordered[p95Index],
            maximum - minimum,
            workingSetBeforeBytes,
            workingSetAfterBytes,
            workingSetAfterBytes - workingSetBeforeBytes,
            privateBytesBefore,
            privateBytesAfter,
            privateBytesAfter - privateBytesBefore,
            handlesBefore,
            handlesAfter,
            handlesAfter - handlesBefore);
    }
}
