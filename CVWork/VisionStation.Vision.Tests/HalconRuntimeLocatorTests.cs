using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconRuntimeLocatorTests
{
    private readonly FakeHalconProcessEnvironment _environment = new();
    private readonly FakeHalconRegistryInstallReader _registry = new();
    private readonly FakeHalconRuntimeFileInspector _files = new();

    [Fact]
    public void ContractConstantsStayPinnedToApprovedHalconRelease()
    {
        Assert.Equal("x64-win64", HalconRuntimeLocator.ExpectedArchitecture);
        Assert.Equal("26.05.0.0", HalconRuntimeLocator.ExpectedNativeVersion);
        Assert.Equal("26050.0.0", HalconRuntimeLocator.ExpectedManagedPackageVersion);
        Assert.Equal("26050.0.0.0", HalconRuntimeLocator.ExpectedManagedAssemblyVersion);
    }

    [Fact]
    public void LocateUsesCompleteEnvironmentBeforeConfigurationAndRegistry()
    {
        var environmentRoot = Root("environment");
        var configuredRoot = Root("configured");
        var registryRoot = Root("registry");
        _environment.Variables["HALCONROOT"] = environmentRoot;
        _environment.Variables["HALCONARCH"] = HalconRuntimeLocator.ExpectedArchitecture;
        _files.AddValid(environmentRoot);
        _files.AddValid(configuredRoot);
        _files.AddValid(registryRoot);
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            registryRoot));
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = configuredRoot });

        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);
        Assert.Equal(HalconRuntimeSource.Environment, result.Location!.Source);
        Assert.Equal(Path.GetFullPath(environmentRoot), result.Location.RuntimeRoot);
        Assert.Equal(NativePath(environmentRoot), result.Location.NativeLibraryPath);
        Assert.Equal(HalconRuntimeLocator.ExpectedArchitecture, result.Location.Architecture);
        Assert.Equal(["HALCONROOT", "HALCONARCH"], _environment.ReadVariables);
        Assert.Empty(_registry.ReadViews);
        Assert.Equal([NativePath(environmentRoot)], _files.ExistenceChecks);
    }

    [Theory]
    [InlineData(null, "x64-win64")]
    [InlineData("C:\\halcon-tests\\environment", null)]
    [InlineData("C:\\halcon-tests\\environment", " X64-WIN64 ")]
    public void IncompleteOrInexactEnvironmentIsRejectedBeforeConfigurationFallback(
        string? environmentRoot,
        string? environmentArchitecture)
    {
        var configuredRoot = Root("configured-fallback");
        _environment.Variables["HALCONROOT"] = environmentRoot;
        _environment.Variables["HALCONARCH"] = environmentArchitecture;
        _files.AddValid(configuredRoot);
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = configuredRoot });

        Assert.True(result.Success);
        Assert.Equal(HalconRuntimeSource.DeviceConfiguration, result.Location!.Source);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(HalconRuntimeSource.Environment, rejection.Source);
        Assert.Equal(
            environmentArchitecture is not null && environmentRoot is not null
                ? TemplateMatchingDiagnosticCodes.RuntimeArchMismatch
                : TemplateMatchingDiagnosticCodes.RuntimeNotFound,
            rejection.Diagnostic.Code);
    }

    [Fact]
    public void InvalidConfigurationFallsBackToSortedMatchingRegistry64EntryAndStripsOneQuotePair()
    {
        var invalidConfiguredRoot = Root("missing-configured");
        var laterRoot = Root("registry-z");
        var selectedRoot = Root("registry-a");
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            $"  \"{laterRoot}\"  "));
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            "MVTec HALCON 25.11 Progress",
            Root("wrong-product")));
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            $"\"{selectedRoot}\""));
        _files.AddValid(selectedRoot);
        _files.AddValid(laterRoot);
        var locator = CreateLocator();

        var result = locator.Locate(
            new HalconRuntimeConfiguration { RuntimeRoot = invalidConfiguredRoot });

        Assert.True(result.Success);
        Assert.Equal(HalconRuntimeSource.Registry64, result.Location!.Source);
        Assert.Equal(Path.GetFullPath(selectedRoot), result.Location.RuntimeRoot);
        Assert.Equal(["Registry64"], _registry.ReadViews);
        Assert.Equal(
            [NativePath(invalidConfiguredRoot), NativePath(selectedRoot)],
            _files.ExistenceChecks);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.Source == HalconRuntimeSource.DeviceConfiguration &&
                         rejection.Diagnostic.Code == TemplateMatchingDiagnosticCodes.RuntimeNotFound);
    }

    [Fact]
    public void RegistryReaderSkipsUnreadableUnrelatedEntryAndStillFindsHalcon()
    {
        var registryRoot = Root("registry-after-unreadable-entry");
        _files.AddValid(registryRoot);
        var session = new FakeHalconRegistryUninstallSession
        {
            SubKeyNames = ["00-unreadable-third-party", "01-halcon"]
        };
        session.Failures["00-unreadable-third-party"] =
            new UnauthorizedAccessException("injected restricted uninstall entry");
        session.Entries["01-halcon"] = new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            registryRoot);
        var reader = new HalconRegistryInstallReader(
            new StubHalconRegistryUninstallSessionFactory(session));
        var locator = new HalconRuntimeLocator(_environment, reader, _files);

        var result = locator.Locate(new HalconRuntimeConfiguration());

        Assert.True(result.Success);
        Assert.Equal(HalconRuntimeSource.Registry64, result.Location!.Source);
        Assert.Equal(Path.GetFullPath(registryRoot), result.Location.RuntimeRoot);
        Assert.Equal(["00-unreadable-third-party", "01-halcon"], session.ReadSubKeys);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public void DuplicateRootsAreInspectedOnceUsingFirstSourcePrecedence()
    {
        var duplicateRoot = Root("duplicate");
        var validRegistryRoot = Root("registry-valid");
        _environment.Variables["HALCONROOT"] = duplicateRoot;
        _environment.Variables["HALCONARCH"] = HalconRuntimeLocator.ExpectedArchitecture;
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            duplicateRoot.ToUpperInvariant()));
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            validRegistryRoot));
        _files.AddValid(validRegistryRoot);
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = duplicateRoot });

        Assert.True(result.Success);
        Assert.Equal(HalconRuntimeSource.Registry64, result.Location!.Source);
        Assert.Equal(
            1,
            _files.ExistenceChecks.Count(path =>
                string.Equals(path, NativePath(duplicateRoot), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NoCandidateReturnsRuntimeNotFoundWithImmutableRejectionHistory()
    {
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration());

        Assert.False(result.Success);
        Assert.Null(result.Location);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeNotFound, result.Diagnostic!.Code);
        Assert.NotEmpty(result.Rejections);
        var exposed = Assert.IsAssignableFrom<IList<HalconRuntimeCandidateRejection>>(
            result.Rejections);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnsupportedProcessPlatformFailsAsArchitectureMismatchWithoutTouchingNativeBoundaries(
        bool isWindows,
        bool is64BitProcess)
    {
        _environment.IsWindows = isWindows;
        _environment.Is64BitProcess = is64BitProcess;
        var locator = CreateLocator();

        var result = locator.Locate(
            new HalconRuntimeConfiguration { RuntimeRoot = Root("configured") });

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeArchMismatch, result.Diagnostic!.Code);
        Assert.Equal(HalconRuntimeSource.Process, Assert.Single(result.Rejections).Source);
        Assert.Empty(_files.ExistenceChecks);
        Assert.Empty(_registry.ReadViews);
    }

    [Fact]
    public void MissingNativeLibraryMapsToRuntimeNotFound()
    {
        var root = Root("missing-dll");
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = root });

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeNotFound, result.Diagnostic!.Code);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.RuntimeRoot == Path.GetFullPath(root) &&
                         rejection.Diagnostic.Code == TemplateMatchingDiagnosticCodes.RuntimeNotFound);
    }

    [Theory]
    [InlineData("Environment")]
    [InlineData("DeviceConfiguration")]
    [InlineData("Registry64")]
    public void RelativeRuntimeRootIsRejectedInsteadOfResolvedFromWorkingDirectory(
        string sourceName)
    {
        var source = Enum.Parse<HalconRuntimeSource>(sourceName);
        const string relativeRoot = @"relative\halcon-26.05";
        var configuration = new HalconRuntimeConfiguration();
        if (source == HalconRuntimeSource.Environment)
        {
            _environment.Variables["HALCONROOT"] = relativeRoot;
            _environment.Variables["HALCONARCH"] = HalconRuntimeLocator.ExpectedArchitecture;
        }
        else if (source == HalconRuntimeSource.DeviceConfiguration)
        {
            configuration = configuration with { RuntimeRoot = relativeRoot };
        }
        else
        {
            _registry.Entries.Add(new HalconRegistryInstallEntry(
                HalconRuntimeLocator.RegistryDisplayName,
                relativeRoot));
        }

        var result = CreateLocator().Locate(configuration);

        Assert.False(result.Success);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.Source == source &&
                         rejection.RuntimeRoot == relativeRoot &&
                         rejection.Diagnostic.Code == TemplateMatchingDiagnosticCodes.RuntimeNotFound);
        Assert.Empty(_files.ExistenceChecks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NonAmd64OrNonPe32PlusNativeLibraryMapsToArchitectureMismatch(
        bool isAmd64,
        bool isPe32Plus)
    {
        var root = Root($"architecture-{isAmd64}-{isPe32Plus}");
        _files.Add(root, isAmd64, isPe32Plus, HalconRuntimeLocator.ExpectedNativeVersion);
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = root });

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeArchMismatch, result.Diagnostic!.Code);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
            result.Rejections.Last().Diagnostic.Code);
    }

    [Theory]
    [InlineData("26.5.0.0")]
    [InlineData("26.05.0.1")]
    [InlineData("")]
    public void NativeFileVersionMustMatchExactApprovedText(string actualVersion)
    {
        var root = Root($"version-{Guid.NewGuid():N}");
        _files.Add(root, isAmd64: true, isPe32Plus: true, actualVersion);
        var locator = CreateLocator();

        var result = locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = root });

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch, result.Diagnostic!.Code);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
            result.Rejections.Last().Diagnostic.Code);
    }

    [Fact]
    public void InspectorFailureIsRejectedAndRegistryFallbackContinues()
    {
        var brokenRoot = Root("broken-inspection");
        var fallbackRoot = Root("inspection-fallback");
        _files.AddValid(brokenRoot);
        _files.PortableExecutableFailures[NativePath(brokenRoot)] =
            new IOException("injected PE read failure");
        _files.AddValid(fallbackRoot);
        _registry.Entries.Add(new HalconRegistryInstallEntry(
            HalconRuntimeLocator.RegistryDisplayName,
            fallbackRoot));
        var locator = CreateLocator();

        var result = locator.Locate(
            new HalconRuntimeConfiguration { RuntimeRoot = brokenRoot });

        Assert.True(result.Success);
        Assert.Equal(HalconRuntimeSource.Registry64, result.Location!.Source);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.RuntimeRoot == Path.GetFullPath(brokenRoot) &&
                         rejection.Diagnostic.Code == TemplateMatchingDiagnosticCodes.RuntimeArchMismatch &&
                         rejection.Diagnostic.TechnicalDetails!.Contains(
                             "injected PE read failure",
                             StringComparison.Ordinal));
    }

    private HalconRuntimeLocator CreateLocator()
    {
        return new HalconRuntimeLocator(_environment, _registry, _files);
    }

    private static string Root(string name)
    {
        return Path.Combine("C:\\halcon-tests", name);
    }

    private static string NativePath(string root)
    {
        return Path.GetFullPath(
            Path.Combine(root, "bin", HalconRuntimeLocator.ExpectedArchitecture, "halcon.dll"));
    }
}

internal sealed class FakeHalconProcessEnvironment : IHalconProcessEnvironment
{
    public bool IsWindows { get; set; } = true;

    public bool Is64BitProcess { get; set; } = true;

    public Dictionary<string, string?> Variables { get; } = new(StringComparer.Ordinal);

    public List<string> ReadVariables { get; } = [];

    public string? GetEnvironmentVariable(string name)
    {
        ReadVariables.Add(name);
        return Variables.GetValueOrDefault(name);
    }
}

internal sealed class FakeHalconRegistryInstallReader : IHalconRegistryInstallReader
{
    public List<HalconRegistryInstallEntry> Entries { get; } = [];

    public List<string> ReadViews { get; } = [];

    public IReadOnlyList<HalconRegistryInstallEntry> ReadRegistry64UninstallEntries()
    {
        ReadViews.Add("Registry64");
        return Entries.ToArray();
    }
}

internal sealed class FakeHalconRuntimeFileInspector : IHalconRuntimeFileInspector
{
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HalconPortableExecutableInfo> _portableExecutables =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    public List<string> ExistenceChecks { get; } = [];

    public Dictionary<string, Exception> PortableExecutableFailures { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void AddValid(string root)
    {
        Add(
            root,
            isAmd64: true,
            isPe32Plus: true,
            HalconRuntimeLocator.ExpectedNativeVersion);
    }

    public void Add(string root, bool isAmd64, bool isPe32Plus, string version)
    {
        var path = Path.GetFullPath(
            Path.Combine(root, "bin", HalconRuntimeLocator.ExpectedArchitecture, "halcon.dll"));
        _existing.Add(path);
        _portableExecutables[path] = new HalconPortableExecutableInfo(isAmd64, isPe32Plus);
        _versions[path] = version;
    }

    public bool FileExists(string path)
    {
        var fullPath = Path.GetFullPath(path);
        ExistenceChecks.Add(fullPath);
        return _existing.Contains(fullPath);
    }

    public HalconPortableExecutableInfo InspectPortableExecutable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (PortableExecutableFailures.TryGetValue(fullPath, out var failure))
        {
            throw failure;
        }

        return _portableExecutables[fullPath];
    }

    public string GetFileVersion(string path)
    {
        return _versions[Path.GetFullPath(path)];
    }
}

internal sealed class StubHalconRegistryUninstallSessionFactory(
    IHalconRegistryUninstallSession session) : IHalconRegistryUninstallSessionFactory
{
    public IHalconRegistryUninstallSession? OpenRegistry64Uninstall()
    {
        return session;
    }
}

internal sealed class FakeHalconRegistryUninstallSession : IHalconRegistryUninstallSession
{
    public IReadOnlyList<string> SubKeyNames { get; init; } = Array.Empty<string>();

    public Dictionary<string, HalconRegistryInstallEntry> Entries { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Exception> Failures { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> ReadSubKeys { get; } = [];

    public bool IsDisposed { get; private set; }

    public IReadOnlyList<string> GetSubKeyNames()
    {
        return SubKeyNames;
    }

    public HalconRegistryInstallEntry? ReadEntry(string subKeyName)
    {
        ReadSubKeys.Add(subKeyName);
        if (Failures.TryGetValue(subKeyName, out var failure))
        {
            throw failure;
        }

        return Entries.GetValueOrDefault(subKeyName);
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
