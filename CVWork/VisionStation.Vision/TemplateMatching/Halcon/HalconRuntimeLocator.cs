using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal enum HalconRuntimeSource
{
    Process,
    Environment,
    DeviceConfiguration,
    Registry64
}

internal sealed record HalconRuntimeCandidateRejection(
    HalconRuntimeSource Source,
    string? RuntimeRoot,
    TemplateMatchingDiagnostic Diagnostic);

internal sealed record HalconRegistryInstallEntry(
    string? DisplayName,
    string? InstallLocation);

internal sealed record HalconPortableExecutableInfo(
    bool IsAmd64,
    bool IsPe32Plus);

internal sealed record HalconRuntimeLocation
{
    public HalconRuntimeLocation(
        string runtimeRoot,
        string nativeLibraryPath,
        string architecture,
        HalconRuntimeSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        RuntimeRoot = runtimeRoot;
        NativeLibraryPath = nativeLibraryPath;
        Architecture = architecture;
        Source = source;
    }

    public string RuntimeRoot { get; }

    public string NativeLibraryPath { get; }

    public string Architecture { get; }

    public HalconRuntimeSource Source { get; }
}

internal sealed record HalconRuntimeLocationResult
{
    private HalconRuntimeLocationResult(
        HalconRuntimeLocation? location,
        TemplateMatchingDiagnostic? diagnostic,
        IReadOnlyList<HalconRuntimeCandidateRejection> rejections)
    {
        if ((location is null) == (diagnostic is null))
        {
            throw new ArgumentException(
                "A HALCON runtime location result must contain exactly one location or diagnostic.");
        }

        ArgumentNullException.ThrowIfNull(rejections);
        Location = location;
        Diagnostic = diagnostic;
        Rejections = Array.AsReadOnly(rejections.ToArray());
    }

    public bool Success => Location is not null;

    public HalconRuntimeLocation? Location { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }

    public IReadOnlyList<HalconRuntimeCandidateRejection> Rejections { get; }

    public static HalconRuntimeLocationResult Found(
        HalconRuntimeLocation location,
        IReadOnlyList<HalconRuntimeCandidateRejection>? rejections = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new HalconRuntimeLocationResult(
            location,
            null,
            rejections ?? Array.Empty<HalconRuntimeCandidateRejection>());
    }

    public static HalconRuntimeLocationResult Failed(
        TemplateMatchingDiagnostic diagnostic,
        IReadOnlyList<HalconRuntimeCandidateRejection>? rejections = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new HalconRuntimeLocationResult(
            null,
            diagnostic,
            rejections ?? Array.Empty<HalconRuntimeCandidateRejection>());
    }
}

internal interface IHalconRuntimeLocator
{
    HalconRuntimeLocationResult Locate(HalconRuntimeConfiguration configuration);
}

internal interface IHalconProcessEnvironment
{
    bool IsWindows { get; }

    bool Is64BitProcess { get; }

    string? GetEnvironmentVariable(string name);
}

internal interface IHalconRegistryInstallReader
{
    IReadOnlyList<HalconRegistryInstallEntry> ReadRegistry64UninstallEntries();
}

internal interface IHalconRegistryUninstallSessionFactory
{
    IHalconRegistryUninstallSession? OpenRegistry64Uninstall();
}

internal interface IHalconRegistryUninstallSession : IDisposable
{
    IReadOnlyList<string> GetSubKeyNames();

    HalconRegistryInstallEntry? ReadEntry(string subKeyName);
}

internal interface IHalconRuntimeFileInspector
{
    bool FileExists(string path);

    HalconPortableExecutableInfo InspectPortableExecutable(string path);

    string GetFileVersion(string path);
}

internal sealed class HalconRuntimeLocator : IHalconRuntimeLocator
{
    public const string ExpectedArchitecture = "x64-win64";
    public const string ExpectedNativeVersion = "26.05.0.0";
    public const string ExpectedManagedPackageVersion = "26050.0.0";
    public const string ExpectedManagedAssemblyVersion = "26050.0.0.0";
    public const string RegistryDisplayName = "MVTec HALCON 26.05 Progress";

    private const string HalconRootVariable = "HALCONROOT";
    private const string HalconArchitectureVariable = "HALCONARCH";

    private readonly IHalconProcessEnvironment _environment;
    private readonly IHalconRegistryInstallReader _registry;
    private readonly IHalconRuntimeFileInspector _files;

    public HalconRuntimeLocator()
        : this(
            new HalconProcessEnvironment(),
            new HalconRegistryInstallReader(),
            new HalconRuntimeFileInspector())
    {
    }

    internal HalconRuntimeLocator(
        IHalconProcessEnvironment environment,
        IHalconRegistryInstallReader registry,
        IHalconRuntimeFileInspector files)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public HalconRuntimeLocationResult Locate(HalconRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rejections = new List<HalconRuntimeCandidateRejection>();
        if (!_environment.IsWindows || !_environment.Is64BitProcess)
        {
            AddRejection(
                rejections,
                HalconRuntimeSource.Process,
                null,
                TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                $"HALCON 26.05 requires a Windows x64 process; " +
                $"IsWindows={_environment.IsWindows}; Is64BitProcess={_environment.Is64BitProcess}.");
            return CreateFailure(rejections);
        }

        var evaluatedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environmentRoot = _environment.GetEnvironmentVariable(HalconRootVariable);
        var environmentArchitecture = _environment.GetEnvironmentVariable(HalconArchitectureVariable);
        if (string.IsNullOrWhiteSpace(environmentRoot) ||
            string.IsNullOrWhiteSpace(environmentArchitecture))
        {
            AddRejection(
                rejections,
                HalconRuntimeSource.Environment,
                NormalizeForDisplay(environmentRoot),
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                "HALCONROOT and HALCONARCH must both be present for the environment candidate.");
        }
        else if (!string.Equals(
                     environmentArchitecture,
                     ExpectedArchitecture,
                     StringComparison.Ordinal))
        {
            AddRejection(
                rejections,
                HalconRuntimeSource.Environment,
                NormalizeForDisplay(environmentRoot),
                TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                $"HALCONARCH must equal '{ExpectedArchitecture}' exactly.");
        }
        else
        {
            var environmentLocation = TryLocateCandidate(
                HalconRuntimeSource.Environment,
                environmentRoot,
                stripOneOuterQuotePair: false,
                evaluatedRoots,
                rejections);
            if (environmentLocation is not null)
            {
                return HalconRuntimeLocationResult.Found(environmentLocation, rejections);
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.RuntimeRoot))
        {
            AddRejection(
                rejections,
                HalconRuntimeSource.DeviceConfiguration,
                null,
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                "The configured HALCON runtime root is empty.");
        }
        else
        {
            var configuredLocation = TryLocateCandidate(
                HalconRuntimeSource.DeviceConfiguration,
                configuration.RuntimeRoot,
                stripOneOuterQuotePair: false,
                evaluatedRoots,
                rejections);
            if (configuredLocation is not null)
            {
                return HalconRuntimeLocationResult.Found(configuredLocation, rejections);
            }
        }

        IReadOnlyList<HalconRegistryInstallEntry> registryEntries;
        try
        {
            registryEntries = _registry.ReadRegistry64UninstallEntries();
        }
        catch (Exception exception)
        {
            AddRejection(
                rejections,
                HalconRuntimeSource.Registry64,
                null,
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                $"Registry64 uninstall discovery failed: {exception.GetType().Name}: {exception.Message}");
            return CreateFailure(rejections);
        }

        var matchingEntries = registryEntries
            .Where(entry => string.Equals(
                entry.DisplayName?.Trim(),
                RegistryDisplayName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(
                entry => StripOneOuterQuotePair(entry.InstallLocation),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                entry => StripOneOuterQuotePair(entry.InstallLocation),
                StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in matchingEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.InstallLocation))
            {
                AddRejection(
                    rejections,
                    HalconRuntimeSource.Registry64,
                    null,
                    TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                    "The matching Registry64 uninstall entry has no InstallLocation.");
                continue;
            }

            var registryLocation = TryLocateCandidate(
                HalconRuntimeSource.Registry64,
                entry.InstallLocation,
                stripOneOuterQuotePair: true,
                evaluatedRoots,
                rejections);
            if (registryLocation is not null)
            {
                return HalconRuntimeLocationResult.Found(registryLocation, rejections);
            }
        }

        return CreateFailure(rejections);
    }

    private HalconRuntimeLocation? TryLocateCandidate(
        HalconRuntimeSource source,
        string candidateRoot,
        bool stripOneOuterQuotePair,
        HashSet<string> evaluatedRoots,
        List<HalconRuntimeCandidateRejection> rejections)
    {
        if (!TryNormalizeRoot(
                candidateRoot,
                stripOneOuterQuotePair,
                out var normalizedRoot,
                out var normalizationFailure))
        {
            AddRejection(
                rejections,
                source,
                NormalizeForDisplay(candidateRoot),
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                normalizationFailure);
            return null;
        }

        if (!evaluatedRoots.Add(normalizedRoot))
        {
            return null;
        }

        var nativeLibraryPath = Path.Combine(
            normalizedRoot,
            "bin",
            ExpectedArchitecture,
            "halcon.dll");
        bool nativeLibraryExists;
        try
        {
            nativeLibraryExists = _files.FileExists(nativeLibraryPath);
        }
        catch (Exception exception)
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                $"HALCON native library existence check failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return null;
        }

        if (!nativeLibraryExists)
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                $"Required native library was not found at '{nativeLibraryPath}'.");
            return null;
        }

        HalconPortableExecutableInfo portableExecutable;
        try
        {
            portableExecutable = _files.InspectPortableExecutable(nativeLibraryPath);
        }
        catch (Exception exception)
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                $"HALCON native PE inspection failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }

        if (!portableExecutable.IsAmd64 || !portableExecutable.IsPe32Plus)
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                $"HALCON native library must be AMD64 PE32+; " +
                $"IsAmd64={portableExecutable.IsAmd64}; IsPe32Plus={portableExecutable.IsPe32Plus}.");
            return null;
        }

        string actualVersion;
        try
        {
            actualVersion = _files.GetFileVersion(nativeLibraryPath);
        }
        catch (Exception exception)
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                $"HALCON native file version inspection failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return null;
        }

        if (!string.Equals(actualVersion, ExpectedNativeVersion, StringComparison.Ordinal))
        {
            AddRejection(
                rejections,
                source,
                normalizedRoot,
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                $"HALCON native file version must equal '{ExpectedNativeVersion}' exactly; " +
                $"actual='{actualVersion}'.");
            return null;
        }

        return new HalconRuntimeLocation(
            normalizedRoot,
            Path.GetFullPath(nativeLibraryPath),
            ExpectedArchitecture,
            source);
    }

    private static HalconRuntimeLocationResult CreateFailure(
        IReadOnlyList<HalconRuntimeCandidateRejection> rejections)
    {
        var diagnosticCode = rejections
                                 .Select(rejection => rejection.Diagnostic.Code)
                                 .FirstOrDefault(code =>
                                     string.Equals(
                                         code,
                                         TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                                         StringComparison.Ordinal) ||
                                     string.Equals(
                                         code,
                                         TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                                         StringComparison.Ordinal))
                             ?? TemplateMatchingDiagnosticCodes.RuntimeNotFound;
        var diagnostic = TemplateMatchingDiagnostics.Create(
            diagnosticCode,
            $"HALCON runtime location rejected {rejections.Count} candidate(s).");
        return HalconRuntimeLocationResult.Failed(diagnostic, rejections);
    }

    private static void AddRejection(
        ICollection<HalconRuntimeCandidateRejection> rejections,
        HalconRuntimeSource source,
        string? runtimeRoot,
        string diagnosticCode,
        string technicalDetails)
    {
        rejections.Add(
            new HalconRuntimeCandidateRejection(
                source,
                runtimeRoot,
                TemplateMatchingDiagnostics.Create(diagnosticCode, technicalDetails)));
    }

    private static bool TryNormalizeRoot(
        string value,
        bool stripOneOuterQuotePair,
        out string normalizedRoot,
        out string failure)
    {
        var candidate = stripOneOuterQuotePair
            ? StripOneOuterQuotePair(value)
            : value.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            normalizedRoot = string.Empty;
            failure = "The HALCON runtime root is empty after normalization.";
            return false;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            normalizedRoot = string.Empty;
            failure = "The HALCON runtime root must be a fully qualified path.";
            return false;
        }

        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedRoot = string.Empty;
            failure = $"The HALCON runtime root is invalid: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static string StripOneOuterQuotePair(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        return candidate;
    }

    private static string? NormalizeForDisplay(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal sealed class HalconProcessEnvironment : IHalconProcessEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public bool Is64BitProcess => Environment.Is64BitProcess;

    public string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
    }
}

internal sealed class HalconRegistryInstallReader : IHalconRegistryInstallReader
{
    private readonly IHalconRegistryUninstallSessionFactory _sessionFactory;

    public HalconRegistryInstallReader()
        : this(new WindowsHalconRegistryUninstallSessionFactory())
    {
    }

    internal HalconRegistryInstallReader(
        IHalconRegistryUninstallSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public IReadOnlyList<HalconRegistryInstallEntry> ReadRegistry64UninstallEntries()
    {
        using var uninstall = _sessionFactory.OpenRegistry64Uninstall();
        if (uninstall is null)
        {
            return Array.Empty<HalconRegistryInstallEntry>();
        }

        var entries = new List<HalconRegistryInstallEntry>();
        foreach (var subKeyName in uninstall.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var entry = uninstall.ReadEntry(subKeyName);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception exception) when (IsRecoverableEntryFailure(exception))
            {
                // Uninstall contains entries owned by unrelated products. A restricted or
                // damaged third-party key must not hide a later valid HALCON installation.
            }
        }

        return entries;
    }

    private static bool IsRecoverableEntryFailure(Exception exception)
    {
        return exception is UnauthorizedAccessException or SecurityException or IOException or ArgumentException;
    }
}

internal sealed class WindowsHalconRegistryUninstallSessionFactory :
    IHalconRegistryUninstallSessionFactory
{
    private const string UninstallRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IHalconRegistryUninstallSession? OpenRegistry64Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        try
        {
            var uninstall = localMachine.OpenSubKey(UninstallRegistryPath, writable: false);
            if (uninstall is null)
            {
                localMachine.Dispose();
                return null;
            }

            return new WindowsHalconRegistryUninstallSession(localMachine, uninstall);
        }
        catch
        {
            localMachine.Dispose();
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsHalconRegistryUninstallSession : IHalconRegistryUninstallSession
{
    private readonly RegistryKey _localMachine;
    private readonly RegistryKey _uninstall;

    internal WindowsHalconRegistryUninstallSession(
        RegistryKey localMachine,
        RegistryKey uninstall)
    {
        _localMachine = localMachine ?? throw new ArgumentNullException(nameof(localMachine));
        _uninstall = uninstall ?? throw new ArgumentNullException(nameof(uninstall));
    }

    public IReadOnlyList<string> GetSubKeyNames()
    {
        return _uninstall.GetSubKeyNames();
    }

    public HalconRegistryInstallEntry? ReadEntry(string subKeyName)
    {
        using var product = _uninstall.OpenSubKey(subKeyName, writable: false);
        return product is null
            ? null
            : new HalconRegistryInstallEntry(
                product.GetValue("DisplayName") as string,
                product.GetValue("InstallLocation") as string);
    }

    public void Dispose()
    {
        _uninstall.Dispose();
        _localMachine.Dispose();
    }
}

internal sealed class HalconRuntimeFileInspector : IHalconRuntimeFileInspector
{
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public HalconPortableExecutableInfo InspectPortableExecutable(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = reader.PEHeaders;
        return new HalconPortableExecutableInfo(
            headers.CoffHeader.Machine == Machine.Amd64,
            headers.PEHeader?.Magic == PEMagic.PE32Plus);
    }

    public string GetFileVersion(string path)
    {
        return FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
    }
}
