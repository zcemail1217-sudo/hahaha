using VisionStation.Vision.Halcon.TestHost;
using System.Text.Json;
using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class HalconBenchmarkCommandTests
{
    [Fact]
    public void BenchmarkReport_ComputesPercentilesRangeAndResourceDeltas()
    {
        HalconBenchmarkReport report = HalconBenchmarkReport.Create(
            durationsMs: [10d, 11d, 12d, 13d, 30d],
            workingSetBeforeBytes: 1000,
            workingSetAfterBytes: 1200,
            privateBytesBefore: 2000,
            privateBytesAfter: 2250,
            handlesBefore: 50,
            handlesAfter: 51,
            operatorFailures: 0);

        Assert.Equal(5, report.ValidSamples);
        Assert.Equal(0, report.OperatorFailures);
        Assert.Equal(12d, report.MedianMs);
        Assert.Equal(30d, report.P95Ms);
        Assert.Equal(20d, report.RangeMs);
        Assert.Equal(200, report.WorkingSetDeltaBytes);
        Assert.Equal(250, report.PrivateBytesDelta);
        Assert.Equal(1, report.HandleDelta);
    }

    [Fact]
    public void BenchmarkReport_EvenSamplesUseMiddlePairAndNearestRankP95()
    {
        HalconBenchmarkReport report = HalconBenchmarkReport.Create(
            durationsMs: [4d, 1d, 3d, 2d],
            workingSetBeforeBytes: 1200,
            workingSetAfterBytes: 1000,
            privateBytesBefore: 3000,
            privateBytesAfter: 2700,
            handlesBefore: 60,
            handlesAfter: 57,
            operatorFailures: 2);

        Assert.Equal(2.5d, report.MedianMs);
        Assert.Equal(4d, report.P95Ms);
        Assert.Equal(3d, report.RangeMs);
        Assert.Equal(-200, report.WorkingSetDeltaBytes);
        Assert.Equal(-300, report.PrivateBytesDelta);
        Assert.Equal(-3, report.HandleDelta);
        Assert.Equal(2, report.OperatorFailures);
    }

    [Fact]
    public void BenchmarkDocument_RequiresEveryGroupToHaveAllValidSamplesAndNoFailures()
    {
        HalconBenchmarkReport complete = CreateReport(validSamples: 2, operatorFailures: 0);
        HalconBenchmarkReport incomplete = CreateReport(validSamples: 1, operatorFailures: 1);
        HalconBenchmarkFingerprint fingerprint = new(
            "factory-pc",
            16,
            "AMD64 Family 25 Model 80",
            32L * 1024 * 1024 * 1024,
            "Windows",
            "X64",
            "X64",
            ".NET 8",
            "26.05.0.0",
            "26050.0.0");

        HalconBenchmarkDocument successful = new(
            1,
            DateTimeOffset.UnixEpoch,
            2,
            fingerprint,
            complete,
            complete,
            complete,
            complete,
            complete);
        HalconBenchmarkDocument failed = successful with { Targets5 = incomplete };

        Assert.True(successful.IsSuccessful);
        Assert.False(failed.IsSuccessful);
    }

    [Fact]
    public void BenchmarkDocument_JsonHasMachineSoftwareFingerprintsAndNoLicenseDetails()
    {
        HalconBenchmarkReport group = CreateReport(validSamples: 1, operatorFailures: 0);
        HalconBenchmarkDocument document = new(
            1,
            DateTimeOffset.UnixEpoch,
            1,
            new HalconBenchmarkFingerprint(
                "factory-pc",
                16,
                "AMD64 Family 25 Model 80",
                32L * 1024 * 1024 * 1024,
                "Windows",
                "X64",
                "X64",
                ".NET 8",
                "26.05.0.0",
                "26050.0.0"),
            group,
            group,
            group,
            group,
            group);

        string json = JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"machineName\":\"factory-pc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"processorIdentifier\":\"AMD64 Family 25 Model 80\"", json, StringComparison.Ordinal);
        Assert.Contains("\"totalAvailableMemoryBytes\":34359738368", json, StringComparison.Ordinal);
        Assert.Contains("\"halconRuntimeVersion\":\"26.05.0.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"halconNuGetVersion\":\"26050.0.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coldLoad\"", json, StringComparison.Ordinal);
        Assert.Contains("\"warmSingle\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targets1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targets3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targets5\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("license", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuGetFingerprint_ReadsHalconPackageVersionFromDependencyManifest()
    {
        const string dependencyManifest = """
            {
              "libraries": {
                "VisionStation.Vision/1.0.0": {},
                "MVTec.HalconDotNet/26050.0.0": {}
              }
            }
            """;

        string version = HalconBenchmarkFingerprintReader.ReadHalconNuGetVersion(
            dependencyManifest);

        Assert.Equal("26050.0.0", version);
    }

    [Fact]
    public void NuGetFingerprint_MissingHalconPackageFailsClosed()
    {
        const string dependencyManifest = """
            { "libraries": { "VisionStation.Vision/1.0.0": {} } }
            """;

        Assert.Throws<InvalidDataException>(() =>
            HalconBenchmarkFingerprintReader.ReadHalconNuGetVersion(dependencyManifest));
    }

    [Fact]
    public void NuGetFingerprint_NoReadableDependencyManifestFailsClosed()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            HalconBenchmarkFingerprintReader.ReadRequiredHalconNuGetVersion(
                Array.Empty<string>()));

        Assert.Contains("MVTec.HalconDotNet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NuGetFingerprint_EmptyDependencyManifestFailsAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() =>
            HalconBenchmarkFingerprintReader.ReadHalconNuGetVersion("   "));
    }

    [Fact]
    public async Task BenchmarkOutputWriter_CreatesDirectoryAndReplacesOnlyAfterCompleteJson()
    {
        string ownedRoot = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-HalconBenchmarkTests",
            Guid.NewGuid().ToString("N"));
        string outputPath = Path.Combine(ownedRoot, "nested", "benchmark.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, "old-content");
            HalconBenchmarkReport group = CreateReport(validSamples: 1, operatorFailures: 0);
            HalconBenchmarkDocument document = new(
                1,
                DateTimeOffset.UnixEpoch,
                1,
                new HalconBenchmarkFingerprint(
                    "factory-pc",
                    16,
                    "unknown",
                    1024,
                    "Windows",
                    "X64",
                    "X64",
                    ".NET 8",
                    "26.05.0.0",
                    "26050.0.0"),
                group,
                group,
                group,
                group,
                group);

            await HalconBenchmarkOutputWriter.WriteAsync(
                document,
                outputPath,
                CancellationToken.None);

            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(outputPath)!,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BenchmarkOutputWriter_CommitFailurePreservesOldFileAndRemovesTemporaryFile()
    {
        string ownedRoot = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-HalconBenchmarkTests",
            Guid.NewGuid().ToString("N"));
        string outputPath = Path.Combine(ownedRoot, "benchmark.json");
        try
        {
            Directory.CreateDirectory(ownedRoot);
            await File.WriteAllTextAsync(outputPath, "old-content");
            HalconBenchmarkDocument document = CreateDocument();
            await using (var outputLock = new FileStream(
                             outputPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.None))
            {
                Exception? exception = await Record.ExceptionAsync(() =>
                    HalconBenchmarkOutputWriter.WriteAsync(
                        document,
                        outputPath,
                        CancellationToken.None));
                Assert.True(
                    exception is IOException or UnauthorizedAccessException,
                    $"Expected an atomic commit I/O failure, but received {exception?.GetType().Name ?? "no exception"}.");
            }

            Assert.Equal("old-content", await File.ReadAllTextAsync(outputPath));
            Assert.Empty(Directory.GetFiles(ownedRoot, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BenchmarkReport_InvalidDurationFailsClosed(double invalidDuration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HalconBenchmarkReport.Create(
            durationsMs: [invalidDuration],
            workingSetBeforeBytes: 1,
            workingSetAfterBytes: 1,
            privateBytesBefore: 1,
            privateBytesAfter: 1,
            handlesBefore: 1,
            handlesAfter: 1,
            operatorFailures: 0));
    }

    [Fact]
    public void BenchmarkCommand_ParsesRequiredOptions()
    {
        bool parsed = HalconTestHostCommandLine.TryParse(
        [
            HalconTestHostCommands.Benchmark,
            "--root",
            @"C:\HALCON",
            "--iterations",
            "50",
            "--output",
            @"artifacts\halcon-benchmark.json"
        ],
            out HalconTestHostCommand? command,
            out HalconTestHostReport? failure);

        Assert.True(parsed, failure?.TechnicalSummary);
        Assert.NotNull(command);
        Assert.Equal(HalconTestHostCommands.Benchmark, command!.Name);
        Assert.Equal(50, command.Iterations);
        Assert.Equal(@"artifacts\halcon-benchmark.json", command.OutputPath);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("1001")]
    public void BenchmarkCommand_InvalidIterationsFailClosed(string iterations)
    {
        bool parsed = HalconTestHostCommandLine.TryParse(
        [
            HalconTestHostCommands.Benchmark,
            "--root",
            @"C:\HALCON",
            "--iterations",
            iterations,
            "--output",
            @"artifacts\halcon-benchmark.json"
        ],
            out HalconTestHostCommand? command,
            out HalconTestHostReport? failure);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Equal("COMMAND_INVALID", failure!.Code);
        Assert.Equal("arguments", failure.Stage);
    }

    [Fact]
    public void BenchmarkCommand_NonJsonOutputFailsClosed()
    {
        bool parsed = HalconTestHostCommandLine.TryParse(
        [
            HalconTestHostCommands.Benchmark,
            "--root",
            @"C:\HALCON",
            "--iterations",
            "50",
            "--output",
            @"artifacts\halcon-benchmark.txt"
        ],
            out HalconTestHostCommand? command,
            out HalconTestHostReport? failure);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Equal("COMMAND_INVALID", failure!.Code);
        Assert.Contains(".json", failure.TechnicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--iterations", "50")]
    [InlineData("--output", "benchmark.json")]
    public void BenchmarkCommand_MissingRequiredOptionFailsClosed(
        string option,
        string value)
    {
        bool parsed = HalconTestHostCommandLine.TryParse(
        [
            HalconTestHostCommands.Benchmark,
            "--root",
            @"C:\HALCON",
            option,
            value
        ],
            out HalconTestHostCommand? command,
            out HalconTestHostReport? failure);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Equal("COMMAND_INVALID", failure!.Code);
    }

    private static HalconBenchmarkReport CreateReport(
        int validSamples,
        int operatorFailures)
    {
        return HalconBenchmarkReport.Create(
            Enumerable.Range(1, validSamples).Select(value => (double)value).ToArray(),
            workingSetBeforeBytes: 100,
            workingSetAfterBytes: 110,
            privateBytesBefore: 200,
            privateBytesAfter: 220,
            handlesBefore: 10,
            handlesAfter: 11,
            operatorFailures: operatorFailures);
    }

    private static HalconBenchmarkDocument CreateDocument()
    {
        HalconBenchmarkReport group = CreateReport(validSamples: 1, operatorFailures: 0);
        return new HalconBenchmarkDocument(
            1,
            DateTimeOffset.UnixEpoch,
            1,
            new HalconBenchmarkFingerprint(
                "factory-pc",
                16,
                "unknown",
                1024,
                "Windows",
                "X64",
                "X64",
                ".NET 8",
                "26.05.0.0",
                "26050.0.0"),
            group,
            group,
            group,
            group,
            group);
    }
}
