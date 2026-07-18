using System.Diagnostics;
using System.Text.Json;
using VisionStation.Vision.Halcon.TestHost;
using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class HalconRuntimeProbeProcessTests
{
    private const string ApprovedRuntimeVersion = "26.05.0.0";
    private const string HalconArchitecture = "x64-win64";
    private const string CancellationAcceptedCode = "OPERATION_CANCELLED";

    [Fact]
    public async Task InvalidCommand_EmitsExactlyOneStableJsonObject()
    {
        TestHostProcessResult execution = await RunTestHostAsync([]);

        HalconTestHostReport report = AssertSingleJsonReport(execution);
        Assert.Equal(HalconTestHostExitCodes.InvalidArguments, execution.ExitCode);
        Assert.False(report.Success);
        Assert.Equal("COMMAND_INVALID", report.Code);
        Assert.Equal("arguments", report.Stage);
        Assert.Null(report.RuntimeVersion);
        AssertExpectedEmptyStandardError(execution);
    }

    [Fact]
    public async Task Probe_MissingNativeDll_ReportsRuntimeNotFoundFromFreshX64Process()
    {
        using TemporaryDirectory runtimeRoot = TemporaryDirectory.Create();
        Directory.CreateDirectory(
            Path.Combine(runtimeRoot.FullPath, "bin", HalconArchitecture));

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Probe,
            "--root",
            runtimeRoot.FullPath,
            "--expected-version",
            ApprovedRuntimeVersion
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.RuntimeNotFound,
            "runtime-preflight");
        Assert.Null(report.RuntimeVersion);
        Assert.Contains("Source=Environment", report.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains("halcon.dll", report.TechnicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_NonAmd64PeImage_ReportsRuntimeArchitectureMismatch()
    {
        using TemporaryDirectory runtimeRoot = TemporaryDirectory.Create();
        string managedAnyCpuAssembly = typeof(FactAttribute).Assembly.Location;
        CopyAsNativeRuntime(runtimeRoot.FullPath, managedAnyCpuAssembly);

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Probe,
            "--root",
            runtimeRoot.FullPath,
            "--expected-version",
            ApprovedRuntimeVersion
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
            "runtime-preflight");
        Assert.Null(report.RuntimeVersion);
        Assert.Contains("Source=Environment", report.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains("AMD64", report.TechnicalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ProductionNativeVersionMismatch_ReportsActualPeVersion()
    {
        using TemporaryDirectory runtimeRoot = TemporaryDirectory.Create();
        string amd64SystemImage = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        Assert.True(
            File.Exists(amd64SystemImage),
            $"Expected an AMD64 Windows system image at '{amd64SystemImage}'.");
        CopyAsNativeRuntime(runtimeRoot.FullPath, amd64SystemImage);

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Probe,
            "--root",
            runtimeRoot.FullPath,
            "--expected-version",
            ApprovedRuntimeVersion
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
            "runtime-preflight");
        Assert.False(string.IsNullOrWhiteSpace(report.RuntimeVersion));
        Assert.NotEqual(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("Source=Environment", report.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains(ApprovedRuntimeVersion, report.TechnicalSummary, StringComparison.Ordinal);
    }

    [HalconIntegrationFact]
    public async Task Probe_ApprovedRuntimeAndLicense_SucceedsInFreshProcess()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Probe,
            "--root",
            runtimeRoot,
            "--expected-version",
            ApprovedRuntimeVersion
        ]);

        HalconTestHostReport report = AssertExpectedSuccess(execution, HalconTestHostCommands.Probe);
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
    }

    [HalconIntegrationFact]
    public async Task Probe_SecondRuntimeRootAfterResolverBinding_IsRejected()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();
        string nativePath = GetRequiredNativePath(runtimeRoot);
        using TemporaryDirectory secondRuntimeRoot = TemporaryDirectory.Create();
        CopyAsNativeRuntime(secondRuntimeRoot.FullPath, nativePath);

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Probe,
            "--root",
            runtimeRoot,
            "--expected-version",
            ApprovedRuntimeVersion,
            "--second-root",
            secondRuntimeRoot.FullPath
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
            "configuration");
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("different runtime root", report.TechnicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [HalconIntegrationFact]
    public async Task LicenseSmoke_ApprovedMachineExecutesRealLicensedOperator()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.LicenseSmoke,
            "--root",
            runtimeRoot
        ]);

        HalconTestHostReport report = AssertExpectedSuccess(
            execution,
            HalconTestHostCommands.LicenseSmoke);
        Assert.Equal("OK", report.Code);
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("licensed", report.TechnicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [HalconIntegrationFact]
    public async Task ModelRoundtrip_CorruptedShapeModel_IsRejectedByChecksumInFreshProcess()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();
        using TemporaryDirectory workingDirectory = TemporaryDirectory.Create();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.ModelRoundtrip,
            "--root",
            runtimeRoot,
            "--working-directory",
            workingDirectory.FullPath,
            "--corrupt-model",
            "true"
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
            "model");
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("checksum", report.TechnicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [HalconIntegrationFact]
    public async Task Timeout_HalconError9400_MapsToStableMatchTimeoutDiagnostic()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Timeout,
            "--root",
            runtimeRoot,
            "--milliseconds",
            "100"
        ]);

        HalconTestHostReport report = AssertExpectedFailure(
            execution,
            TemplateMatchingDiagnosticCodes.MatchTimeout,
            "match");
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("ErrorCode=9400", report.TechnicalSummary, StringComparison.Ordinal);
    }

    [HalconIntegrationFact]
    public async Task Timeout_LongOperatorBudget_CompletesNormally()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Timeout,
            "--root",
            runtimeRoot,
            "--milliseconds",
            "5000"
        ]);

        HalconTestHostReport report = AssertExpectedSuccess(
            execution,
            HalconTestHostCommands.Timeout);
        Assert.Equal("OK", report.Code);
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("operatorTimeoutMs=5000", report.TechnicalSummary, StringComparison.Ordinal);
    }

    [HalconIntegrationFact]
    public async Task Cancellation_DuringNativeCall_WaitsForSafeReturnThenThrowsWithoutSyntheticDiagnostic()
    {
        string runtimeRoot = GetRequiredRuntimeRoot();

        TestHostProcessResult execution = await RunTestHostAsync(
        [
            HalconTestHostCommands.Timeout,
            "--root",
            runtimeRoot,
            "--milliseconds",
            "5000",
            "--cancel-after-milliseconds",
            "150"
        ]);

        HalconTestHostReport report = AssertExpectedSuccess(execution, "cancel");
        Assert.Equal(CancellationAcceptedCode, report.Code);
        Assert.Equal(ApprovedRuntimeVersion, report.RuntimeVersion);
        Assert.Contains("OperationCanceledException", report.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains("nativeEntered=true", report.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains("nativeReturned=true", report.TechnicalSummary, StringComparison.Ordinal);
        long elapsedMilliseconds = ReadSummaryMilliseconds(
            report.TechnicalSummary,
            "elapsedMs");
        long postCancelWaitMilliseconds = ReadSummaryMilliseconds(
            report.TechnicalSummary,
            "postCancelWaitMs");
        Assert.True(
            elapsedMilliseconds > 150,
            $"Expected elapsedMs > cancelAfterMs, but summary was: {report.TechnicalSummary}");
        Assert.True(
            postCancelWaitMilliseconds >= 50,
            $"Expected postCancelWaitMs >= 50, but summary was: {report.TechnicalSummary}");
        Assert.DoesNotContain("MATCH_CANCELLED", execution.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("MATCH_CANCELLED", execution.StandardError, StringComparison.Ordinal);
    }

    private static async Task<TestHostProcessResult> RunTestHostAsync(
        IReadOnlyList<string> arguments)
    {
        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "VisionStation.Vision.Halcon.TestHost.exe");
        Assert.True(
            File.Exists(executablePath),
            $"Expected the x64 TestHost apphost at '{executablePath}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start HALCON TestHost '{executablePath}'.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new TimeoutException(
                $"HALCON TestHost process {process.Id} did not exit within 90 seconds.");
        }

        return new TestHostProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static HalconTestHostReport AssertExpectedFailure(
        TestHostProcessResult execution,
        string expectedCode,
        string expectedStage)
    {
        HalconTestHostReport report = AssertSingleJsonReport(execution);
        Assert.Equal(HalconTestHostExitCodes.CommandFailed, execution.ExitCode);
        Assert.False(report.Success);
        Assert.Equal(expectedCode, report.Code);
        Assert.Equal(expectedStage, report.Stage);
        AssertExpectedEmptyStandardError(execution);
        return report;
    }

    private static HalconTestHostReport AssertExpectedSuccess(
        TestHostProcessResult execution,
        string expectedStage)
    {
        HalconTestHostReport report = AssertSingleJsonReport(execution);
        Assert.Equal(HalconTestHostExitCodes.Success, execution.ExitCode);
        Assert.True(report.Success, Describe(execution, report));
        Assert.Equal(expectedStage, report.Stage);
        AssertExpectedEmptyStandardError(execution);
        return report;
    }

    private static HalconTestHostReport AssertSingleJsonReport(TestHostProcessResult execution)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(execution.StandardOutput),
            $"TestHost emitted no JSON. ExitCode={execution.ExitCode}; stderr={execution.StandardError}");
        Assert.Equal(execution.StandardOutput.Trim(), execution.StandardOutput);

        using JsonDocument document = JsonDocument.Parse(execution.StandardOutput);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        string[] propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "code",
            "runtimeVersion",
            "stage",
            "success",
            "technicalSummary"
        ],
            propertyNames);

        HalconTestHostReport? report = JsonSerializer.Deserialize<HalconTestHostReport>(
            execution.StandardOutput);
        Assert.NotNull(report);
        Assert.False(string.IsNullOrWhiteSpace(report!.Code));
        Assert.False(string.IsNullOrWhiteSpace(report.Stage));
        Assert.False(string.IsNullOrWhiteSpace(report.TechnicalSummary));
        return report;
    }

    private static void AssertExpectedEmptyStandardError(TestHostProcessResult execution)
    {
        Assert.True(
            string.IsNullOrEmpty(execution.StandardError),
            $"Expected no stack trace for a handled TestHost result, but stderr was: {execution.StandardError}");
    }

    private static string GetRequiredRuntimeRoot()
    {
        string runtimeRoot = SyntheticHalconProductFactory
            .CreateRuntimeConfiguration()
            .RuntimeRoot;
        Assert.False(
            string.IsNullOrWhiteSpace(runtimeRoot),
            "HALCON integration is enabled, but VISIONSTATION_HALCON_ROOT/HALCONROOT is empty and the approved runtime is not installed.");
        string fullPath = Path.GetFullPath(runtimeRoot);
        Assert.True(
            Directory.Exists(fullPath),
            $"HALCON integration is enabled, but runtime root '{fullPath}' does not exist.");
        return fullPath;
    }

    private static string GetRequiredNativePath(string runtimeRoot)
    {
        string nativePath = Path.Combine(
            runtimeRoot,
            "bin",
            HalconArchitecture,
            "halcon.dll");
        Assert.True(
            File.Exists(nativePath),
            $"HALCON integration is enabled, but native library '{nativePath}' does not exist.");
        return nativePath;
    }

    private static void CopyAsNativeRuntime(string runtimeRoot, string sourceImage)
    {
        string nativeDirectory = Path.Combine(runtimeRoot, "bin", HalconArchitecture);
        Directory.CreateDirectory(nativeDirectory);
        File.Copy(sourceImage, Path.Combine(nativeDirectory, "halcon.dll"), overwrite: false);
    }

    private static string Describe(
        TestHostProcessResult execution,
        HalconTestHostReport report)
    {
        return $"TestHost failed. ExitCode={execution.ExitCode}; Code={report.Code}; " +
               $"Stage={report.Stage}; RuntimeVersion={report.RuntimeVersion ?? "<null>"}; " +
               $"Technical={report.TechnicalSummary}; stderr={execution.StandardError}";
    }

    private static long ReadSummaryMilliseconds(string summary, string key)
    {
        string prefix = key + "=";
        string? field = summary
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(field),
            $"Expected '{key}' in TestHost summary: {summary}");
        string rawValue = field![prefix.Length..].TrimEnd('.');
        Assert.True(
            long.TryParse(rawValue, out long value),
            $"Expected integer '{key}', but summary was: {summary}");
        return value;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch
        {
            // Preserve the timeout as the primary test failure.
        }
    }

    private sealed record TestHostProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        private const string OwnedParentName = "VisionStation-HalconProcessTests";
        private bool _disposed;

        private TemporaryDirectory(string fullPath)
        {
            FullPath = fullPath;
        }

        public string FullPath { get; }

        public static TemporaryDirectory Create()
        {
            string parent = Path.Combine(Path.GetTempPath(), OwnedParentName);
            string fullPath = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fullPath);
            return new TemporaryDirectory(fullPath);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            string ownedParent = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), OwnedParentName));
            string fullPath = Path.GetFullPath(FullPath);
            string requiredPrefix = ownedParent.TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete non-owned process test directory '{fullPath}'.");
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
