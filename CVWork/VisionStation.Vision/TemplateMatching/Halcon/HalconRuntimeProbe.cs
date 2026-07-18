using System.Reflection;
using HalconDotNet;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal interface IHalconRuntimeProbe
{
    /// <summary>
    /// Ensures the configured runtime and Matching license are ready. The first expected result,
    /// including a stable failure diagnostic, is cached. Caller cancellation cancels only that
    /// caller's wait after probing starts; it never abandons an in-flight native operation.
    /// Unexpected exceptions remain retryable by a later call.
    /// </summary>
    Task<HalconRuntimeProbeResult> EnsureReadyAsync(CancellationToken cancellationToken);
}

internal interface IHalconRuntimeNativeApi
{
    string ManagedPackageVersion { get; }

    string ManagedAssemblyVersion { get; }

    void DisableLicenseTermination();

    string GetSystemFileVersion();

    void VerifyMatchingLicense();
}

internal sealed record HalconRuntimeDescriptor(
    string RuntimeRoot,
    HalconRuntimeSource Source,
    string Architecture,
    string ManagedPackageVersion,
    string ManagedAssemblyVersion,
    string NativeFileVersion,
    string SystemFileVersion);

internal sealed record HalconRuntimeProbeResult
{
    private HalconRuntimeProbeResult(
        HalconRuntimeDescriptor? descriptor,
        TemplateMatchingDiagnostic? diagnostic,
        IReadOnlyList<HalconRuntimeCandidateRejection> rejections)
    {
        if ((descriptor is null) == (diagnostic is null))
        {
            throw new ArgumentException("A runtime probe result must contain either a descriptor or one diagnostic.");
        }

        Descriptor = descriptor;
        Diagnostic = diagnostic;
        Rejections = Array.AsReadOnly(rejections.ToArray());
    }

    public bool IsReady => Descriptor is not null;

    public HalconRuntimeDescriptor? Descriptor { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }

    public IReadOnlyList<HalconRuntimeCandidateRejection> Rejections { get; }

    public static HalconRuntimeProbeResult Ready(
        HalconRuntimeDescriptor descriptor,
        IReadOnlyList<HalconRuntimeCandidateRejection>? rejections = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new HalconRuntimeProbeResult(
            descriptor,
            null,
            rejections ?? Array.Empty<HalconRuntimeCandidateRejection>());
    }

    public static HalconRuntimeProbeResult Failed(
        TemplateMatchingDiagnostic diagnostic,
        IReadOnlyList<HalconRuntimeCandidateRejection>? rejections = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new HalconRuntimeProbeResult(
            null,
            diagnostic,
            rejections ?? Array.Empty<HalconRuntimeCandidateRejection>());
    }
}

internal sealed class HalconRuntimeProbe : IHalconRuntimeProbe
{
    private static readonly Lazy<IHalconOperationScheduler> DefaultOperationScheduler = new(
        static () => new HalconOperationScheduler(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _syncRoot = new();
    private readonly HalconRuntimeConfiguration _configuration;
    private readonly IHalconRuntimeLocator _locator;
    private readonly IHalconNativeLibraryBootstrapper _bootstrapper;
    private readonly IHalconRuntimeNativeApi _nativeApi;
    private readonly IHalconOperationScheduler _operationScheduler;
    private Task<HalconRuntimeProbeResult>? _sharedProbe;

    internal HalconRuntimeProbe(HalconRuntimeConfiguration configuration)
        : this(
            configuration,
            new HalconRuntimeLocator(),
            new HalconNativeLibraryBootstrapper(),
            new HalconRuntimeNativeApi(),
            DefaultOperationScheduler.Value)
    {
    }

    internal HalconRuntimeProbe(
        HalconRuntimeConfiguration configuration,
        IHalconRuntimeLocator locator,
        IHalconNativeLibraryBootstrapper bootstrapper,
        IHalconRuntimeNativeApi nativeApi)
        : this(
            configuration,
            locator,
            bootstrapper,
            nativeApi,
            DefaultOperationScheduler.Value)
    {
    }

    internal HalconRuntimeProbe(
        HalconRuntimeConfiguration configuration,
        IHalconRuntimeLocator locator,
        IHalconNativeLibraryBootstrapper bootstrapper,
        IHalconRuntimeNativeApi nativeApi,
        IHalconOperationScheduler operationScheduler)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _operationScheduler = operationScheduler
            ?? throw new ArgumentNullException(nameof(operationScheduler));
    }

    public Task<HalconRuntimeProbeResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sharedProbe = GetOrStartProbe();
        return cancellationToken.CanBeCanceled
            ? sharedProbe.WaitAsync(cancellationToken)
            : sharedProbe;
    }

    private Task<HalconRuntimeProbeResult> GetOrStartProbe()
    {
        lock (_syncRoot)
        {
            if (_sharedProbe is not null &&
                !_sharedProbe.IsFaulted &&
                !_sharedProbe.IsCanceled)
            {
                return _sharedProbe;
            }

            _sharedProbe = Task.Run(ProbeCore);
            return _sharedProbe;
        }
    }

    private HalconRuntimeProbeResult ProbeCore()
    {
        var locationResult = _locator.Locate(_configuration);
        if (!locationResult.Success)
        {
            return HalconRuntimeProbeResult.Failed(
                locationResult.Diagnostic
                ?? TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                    "HALCON runtime locator returned no location or diagnostic."),
                locationResult.Rejections);
        }

        var location = locationResult.Location
            ?? throw new InvalidOperationException("A successful HALCON runtime location must contain a location.");
        var rejections = locationResult.Rejections;

        var versionFailure = ValidateManagedVersions();
        if (versionFailure is not null)
        {
            return HalconRuntimeProbeResult.Failed(versionFailure, rejections);
        }

        var bootstrap = _bootstrapper.EnsureBound(location);
        if (!bootstrap.Success)
        {
            return HalconRuntimeProbeResult.Failed(
                bootstrap.Diagnostic
                ?? TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                    "HALCON native bootstrap returned no binding or diagnostic."),
                rejections);
        }

        var operation = "disable-license-termination";
        try
        {
            return _operationScheduler.RunAsync(
                    () =>
                    {
                        _nativeApi.DisableLicenseTermination();
                        operation = "get-system-file-version";
                        string systemVersion = _nativeApi.GetSystemFileVersion();
                        if (!string.Equals(
                                systemVersion,
                                HalconRuntimeLocator.ExpectedNativeVersion,
                                StringComparison.Ordinal))
                        {
                            return HalconRuntimeProbeResult.Failed(
                                TemplateMatchingDiagnostics.Create(
                                    TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                                    $"HALCON system file version mismatch; expected={HalconRuntimeLocator.ExpectedNativeVersion}; actual={NormalizeVersion(systemVersion)}."),
                                rejections);
                        }

                        operation = "matching-license-smoke";
                        _nativeApi.VerifyMatchingLicense();
                        return HalconRuntimeProbeResult.Ready(
                            new HalconRuntimeDescriptor(
                                location.RuntimeRoot,
                                location.Source,
                                location.Architecture,
                                _nativeApi.ManagedPackageVersion,
                                _nativeApi.ManagedAssemblyVersion,
                                HalconRuntimeLocator.ExpectedNativeVersion,
                                systemVersion),
                            rejections);
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            HalconExceptionClassifier.TryClassify(exception, operation, out var diagnostic))
        {
            return HalconRuntimeProbeResult.Failed(diagnostic!, rejections);
        }
    }

    private TemplateMatchingDiagnostic? ValidateManagedVersions()
    {
        if (!string.Equals(
                _nativeApi.ManagedPackageVersion,
                HalconRuntimeLocator.ExpectedManagedPackageVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                _nativeApi.ManagedAssemblyVersion,
                HalconRuntimeLocator.ExpectedManagedAssemblyVersion,
                StringComparison.Ordinal))
        {
            return TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                $"HALCON managed version mismatch; expectedPackage={HalconRuntimeLocator.ExpectedManagedPackageVersion}; " +
                $"actualPackage={NormalizeVersion(_nativeApi.ManagedPackageVersion)}; " +
                $"expectedAssembly={HalconRuntimeLocator.ExpectedManagedAssemblyVersion}; " +
                $"actualAssembly={NormalizeVersion(_nativeApi.ManagedAssemblyVersion)}.");
        }

        return null;
    }

    private static string NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<missing>" : value.Trim();
    }
}

internal sealed class HalconRuntimeNativeApi : IHalconRuntimeNativeApi
{
    private static readonly Assembly HalconAssembly = typeof(HSystem).Assembly;
    private readonly IHalconOperatorBackend _operators;

    internal HalconRuntimeNativeApi()
        : this(new HalconDotNetOperatorBackend())
    {
    }

    internal HalconRuntimeNativeApi(IHalconOperatorBackend operators)
    {
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    public string ManagedPackageVersion
    {
        get
        {
            var informationalVersion = HalconAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return informationalVersion?.Split('+', 2)[0] ?? string.Empty;
        }
    }

    public string ManagedAssemblyVersion => HalconAssembly.GetName().Version?.ToString() ?? string.Empty;

    public void DisableLicenseTermination()
    {
        HalconAPI.DoLicenseError(false);
    }

    public string GetSystemFileVersion()
    {
        using var version = HSystem.GetSystemInfo("file_version");
        return version.S;
    }

    public void VerifyMatchingLicense()
    {
        _operators.VerifyMatchingLicense();
    }
}
