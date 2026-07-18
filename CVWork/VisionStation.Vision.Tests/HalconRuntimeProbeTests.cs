using System.Reflection;
using System.Runtime.InteropServices;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconRuntimeProbeTests
{
    private const string RuntimeRoot = @"C:\MVTec\HALCON-26.05-Progress";
    private const string NativeLibraryPath = @"C:\MVTec\HALCON-26.05-Progress\bin\x64-win64\halcon.dll";

    [Fact]
    public void BootstrapperPreloadsAbsoluteDllAndResolvesOnlyHalcon()
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch"
        };
        var native = new FakeNativeLibraryApi();
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);
        Assert.Equal(NativeLibraryPath, native.LoadedPath);
        Assert.Equal(HalconNativeLibraryBootstrapper.NativeLibraryLoadFlags, native.LoadFlags);
        Assert.NotEqual(
            0u,
            native.LoadFlags & HalconNativeLibraryBootstrapper.LoadLibrarySearchDllLoadDir);
        Assert.NotEqual(
            0u,
            native.LoadFlags & HalconNativeLibraryBootstrapper.LoadLibrarySearchSystem32);
        Assert.Equal(RuntimeRoot, environment.HalconRoot);
        Assert.Equal(HalconRuntimeLocator.ExpectedArchitecture, environment.HalconArch);
        Assert.Equal(1, native.RegisterResolverCalls);
        Assert.NotNull(native.Resolver);
        Assert.Equal(native.ModuleHandle, native.Resolver!("halcon", typeof(HalconRuntimeProbeTests).Assembly, null));
        Assert.Equal(IntPtr.Zero, native.Resolver!("hdevenginecpp", typeof(HalconRuntimeProbeTests).Assembly, null));
        Assert.Equal(IntPtr.Zero, native.Resolver!("HALCON", typeof(HalconRuntimeProbeTests).Assembly, null));
    }

    [Fact]
    public void BootstrapperIsIdempotentForSameRootAndRejectsDifferentRoot()
    {
        var native = new FakeNativeLibraryApi();
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            new FakeProcessEnvironment(),
            new HalconNativeBindingState());

        var first = bootstrapper.EnsureBound(Location());
        var sameRoot = bootstrapper.EnsureBound(Location(RuntimeRoot + "\\"));
        var differentRoot = bootstrapper.EnsureBound(Location(@"D:\OtherHalcon"));

        Assert.True(first.Success);
        Assert.True(sameRoot.Success);
        Assert.False(differentRoot.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, differentRoot.Diagnostic?.Code);
        Assert.Equal(1, native.LoadCalls);
        Assert.Equal(1, native.RegisterResolverCalls);
    }

    [Theory]
    [InlineData(126, TemplateMatchingDiagnosticCodes.RuntimeNotFound)]
    [InlineData(193, TemplateMatchingDiagnosticCodes.RuntimeArchMismatch)]
    [InlineData(127, TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch)]
    public void BootstrapperRestoresProcessEnvironmentWhenNativeLoadFails(int nativeError, string expectedCode)
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch"
        };
        var native = new FakeNativeLibraryApi
        {
            ModuleHandle = IntPtr.Zero,
            LastError = nativeError
        };
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Diagnostic?.Code);
        Assert.Equal("previous-root", environment.HalconRoot);
        Assert.Equal("previous-arch", environment.HalconArch);
        Assert.Equal(0, native.RegisterResolverCalls);
    }

    [Fact]
    public void BootstrapperFreesLibraryAndRestoresEnvironmentWhenResolverRegistrationFails()
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch"
        };
        var native = new FakeNativeLibraryApi
        {
            RegisterException = new InvalidOperationException("resolver already registered")
        };
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal("previous-root", environment.HalconRoot);
        Assert.Equal("previous-arch", environment.HalconArch);
        Assert.Equal(1, native.FreeCalls);
    }

    [Fact]
    public void BootstrapperRestoresPartialEnvironmentWhenSecondProcessWriteThrows()
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch",
            FailOnceAfterSetting = "HALCONARCH"
        };
        var native = new FakeNativeLibraryApi();
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeNotFound, result.Diagnostic?.Code);
        Assert.Equal("previous-root", environment.HalconRoot);
        Assert.Equal("previous-arch", environment.HalconArch);
        Assert.Equal(0, native.LoadCalls);
    }

    [Fact]
    public void BootstrapperClassifiesThrownLoadFailureAndStillRestoresEnvironment()
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch"
        };
        var native = new FakeNativeLibraryApi
        {
            LoadException = new DllNotFoundException("injected loader failure")
        };
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeNotFound, result.Diagnostic?.Code);
        Assert.Equal("previous-root", environment.HalconRoot);
        Assert.Equal("previous-arch", environment.HalconArch);
    }

    [Fact]
    public void BootstrapperCleanupFailureDoesNotHideResolverRegistrationDiagnostic()
    {
        var native = new FakeNativeLibraryApi
        {
            RegisterException = new InvalidOperationException("resolver already registered"),
            FreeException = new InvalidOperationException("injected free failure")
        };
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            new FakeProcessEnvironment(),
            new HalconNativeBindingState());

        var result = bootstrapper.EnsureBound(Location());

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal(1, native.FreeCalls);
    }

    [Fact]
    public void BootstrapperRejectsNonCanonicalLocationBeforeMutatingProcessState()
    {
        var environment = new FakeProcessEnvironment
        {
            HalconRoot = "previous-root",
            HalconArch = "previous-arch"
        };
        var native = new FakeNativeLibraryApi();
        var bootstrapper = new HalconNativeLibraryBootstrapper(
            native,
            environment,
            new HalconNativeBindingState());
        var location = new HalconRuntimeLocation(
            RuntimeRoot,
            @"relative\halcon.dll",
            HalconRuntimeLocator.ExpectedArchitecture,
            HalconRuntimeSource.DeviceConfiguration);

        var result = bootstrapper.EnsureBound(location);

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Equal("previous-root", environment.HalconRoot);
        Assert.Equal("previous-arch", environment.HalconArch);
        Assert.Equal(0, native.LoadCalls);
    }

    [Fact]
    public async Task ProbeValidatesManagedAndSystemVersionsThenMatchingLicense()
    {
        var runtime = new FakeRuntimeNativeApi();
        var probe = CreateProbe(runtime);

        var result = await probe.EnsureReadyAsync(default);

        Assert.True(result.IsReady);
        Assert.Null(result.Diagnostic);
        Assert.NotNull(result.Descriptor);
        Assert.Equal(RuntimeRoot, result.Descriptor.RuntimeRoot);
        Assert.Equal(HalconRuntimeLocator.ExpectedManagedPackageVersion, result.Descriptor.ManagedPackageVersion);
        Assert.Equal(HalconRuntimeLocator.ExpectedManagedAssemblyVersion, result.Descriptor.ManagedAssemblyVersion);
        Assert.Equal(HalconRuntimeLocator.ExpectedNativeVersion, result.Descriptor.NativeFileVersion);
        Assert.Equal(HalconRuntimeLocator.ExpectedNativeVersion, result.Descriptor.SystemFileVersion);
        Assert.Equal(
            ["disable-license-termination", "system-version", "matching-license-smoke"],
            runtime.Operations);
    }

    [Fact]
    public async Task ProbeClassifiesLicenseFailureWithoutLeakingNativeMessage()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            SmokeException = new HalconDotNet.HOperatorException(2003, "SECRET-LICENSE-DETAIL")
        };
        var probe = CreateProbe(runtime);

        var result = await probe.EnsureReadyAsync(default);

        Assert.False(result.IsReady);
        Assert.Equal(TemplateMatchingDiagnosticCodes.LicenseUnavailable, result.Diagnostic?.Code);
        Assert.DoesNotContain("SECRET-LICENSE-DETAIL", result.Diagnostic?.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeRejectsManagedVersionMismatchBeforeNativeBinding()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            ManagedPackageVersion = "26050.0.1"
        };
        var bootstrapper = new StubBootstrapper(HalconNativeLibraryBootstrapResult.Bound());
        var probe = new HalconRuntimeProbe(
            new HalconRuntimeConfiguration { RuntimeRoot = RuntimeRoot },
            new StubRuntimeLocator(HalconRuntimeLocationResult.Found(Location())),
            bootstrapper,
            runtime);

        var result = await probe.EnsureReadyAsync(default);

        Assert.False(result.IsReady);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch, result.Diagnostic?.Code);
        Assert.Equal(0, bootstrapper.Calls);
        Assert.Empty(runtime.Operations);
    }

    [Fact]
    public async Task ProbeRejectsSystemVersionMismatchBeforeLicenseSmoke()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            SystemFileVersion = "26.05.0.1"
        };
        var probe = CreateProbe(runtime);

        var result = await probe.EnsureReadyAsync(default);

        Assert.False(result.IsReady);
        Assert.Equal(TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch, result.Diagnostic?.Code);
        Assert.Equal(0, runtime.SmokeCalls);
        Assert.Equal(["disable-license-termination", "system-version"], runtime.Operations);
    }

    [Fact]
    public void ProductionManagedBoundaryReportsPinnedPackageAndAssemblyVersionsWithoutNativeCalls()
    {
        var native = new HalconRuntimeNativeApi();

        Assert.Equal(HalconRuntimeLocator.ExpectedManagedPackageVersion, native.ManagedPackageVersion);
        Assert.Equal(HalconRuntimeLocator.ExpectedManagedAssemblyVersion, native.ManagedAssemblyVersion);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneProbeExecution()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var probe = CreateProbe(runtime);
        var calls = Enumerable.Range(0, 20)
            .Select(_ => probe.EnsureReadyAsync(default))
            .ToArray();

        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.True(result.IsReady));
        Assert.Equal(1, runtime.SmokeCalls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonSharedProbe()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var probe = CreateProbe(runtime);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = probe.EnsureReadyAsync(cancellation.Token);

        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        var survivingWaiter = probe.EnsureReadyAsync(default);
        runtime.Gate.SetResult();
        var result = await survivingWaiter;

        Assert.True(result.IsReady);
        Assert.Equal(1, runtime.SmokeCalls);
    }

    [Fact]
    public async Task ExpectedProbeFailureIsCached()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            SmokeException = new HalconDotNet.HOperatorException(2003, "fake-license")
        };
        var probe = CreateProbe(runtime);

        var first = await probe.EnsureReadyAsync(default);
        var second = await probe.EnsureReadyAsync(default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.LicenseUnavailable, first.Diagnostic?.Code);
        Assert.Same(first, second);
        Assert.Equal(1, runtime.SmokeCalls);
    }

    [Fact]
    public async Task UnexpectedProbeFailureIsRetriedByNextCaller()
    {
        var runtime = new FakeRuntimeNativeApi
        {
            SmokeException = new InvalidOperationException("injected unexpected failure")
        };
        var probe = CreateProbe(runtime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.EnsureReadyAsync(default));
        runtime.SmokeException = null;
        var retry = await probe.EnsureReadyAsync(default);

        Assert.True(retry.IsReady);
        Assert.Equal(2, runtime.SmokeCalls);
    }

    [Fact]
    public async Task AlreadyCanceledFirstCallerDoesNotStartProbe()
    {
        var runtime = new FakeRuntimeNativeApi();
        var probe = CreateProbe(runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.EnsureReadyAsync(cancellation.Token));

        Assert.Equal(0, runtime.SmokeCalls);
        Assert.True((await probe.EnsureReadyAsync(default)).IsReady);
    }

    [Fact]
    public async Task BoundNativePhaseRunsAsOneUncancelledSchedulerOperation()
    {
        var runtime = new FakeRuntimeNativeApi();
        var scheduler = new RecordingRuntimeScheduler();
        var probe = new HalconRuntimeProbe(
            new HalconRuntimeConfiguration { RuntimeRoot = RuntimeRoot },
            new StubRuntimeLocator(HalconRuntimeLocationResult.Found(Location())),
            new StubBootstrapper(HalconNativeLibraryBootstrapResult.Bound()),
            runtime,
            scheduler);

        HalconRuntimeProbeResult result = await probe.EnsureReadyAsync(default);

        Assert.True(result.IsReady);
        Assert.Equal(1, scheduler.RunCount);
        Assert.False(scheduler.LastCancellationToken.CanBeCanceled);
        Assert.Equal(
            ["disable-license-termination", "system-version", "matching-license-smoke"],
            runtime.Operations);
    }

    [Fact]
    public void RuntimeNativeApiDelegatesLicenseSmokeToSharedOperatorSeam()
    {
        var operators = new RecordingRuntimeOperatorBackend();
        var native = new HalconRuntimeNativeApi(operators);

        native.VerifyMatchingLicense();

        Assert.Equal(1, operators.LicenseSmokeCount);
    }

    private static HalconRuntimeProbe CreateProbe(FakeRuntimeNativeApi runtime)
    {
        return new HalconRuntimeProbe(
            new HalconRuntimeConfiguration { RuntimeRoot = RuntimeRoot },
            new StubRuntimeLocator(HalconRuntimeLocationResult.Found(Location())),
            new StubBootstrapper(HalconNativeLibraryBootstrapResult.Bound()),
            runtime);
    }

    private static HalconRuntimeLocation Location(string root = RuntimeRoot)
    {
        var normalizedRoot = root.TrimEnd('\\');
        return new HalconRuntimeLocation(
            normalizedRoot,
            Path.Combine(normalizedRoot, "bin", HalconRuntimeLocator.ExpectedArchitecture, "halcon.dll"),
            HalconRuntimeLocator.ExpectedArchitecture,
            HalconRuntimeSource.DeviceConfiguration);
    }

    private sealed class StubRuntimeLocator(HalconRuntimeLocationResult result) : IHalconRuntimeLocator
    {
        public int Calls { get; private set; }

        public HalconRuntimeLocationResult Locate(HalconRuntimeConfiguration configuration)
        {
            Calls++;
            return result;
        }
    }

    private sealed class StubBootstrapper(HalconNativeLibraryBootstrapResult result) : IHalconNativeLibraryBootstrapper
    {
        public int Calls { get; private set; }

        public HalconNativeLibraryBootstrapResult EnsureBound(HalconRuntimeLocation location)
        {
            Calls++;
            return result;
        }
    }

    private sealed class FakeProcessEnvironment : IHalconProcessEnvironmentMutator
    {
        public string? HalconRoot { get; set; }

        public string? HalconArch { get; set; }

        public string? FailOnceAfterSetting { get; set; }

        public string? GetProcessVariable(string name)
        {
            return name switch
            {
                "HALCONROOT" => HalconRoot,
                "HALCONARCH" => HalconArch,
                _ => null
            };
        }

        public void SetProcessVariable(string name, string? value)
        {
            switch (name)
            {
                case "HALCONROOT":
                    HalconRoot = value;
                    break;
                case "HALCONARCH":
                    HalconArch = value;
                    break;
            }

            if (string.Equals(FailOnceAfterSetting, name, StringComparison.Ordinal))
            {
                FailOnceAfterSetting = null;
                throw new InvalidOperationException("injected process environment write failure");
            }
        }
    }

    private sealed class FakeNativeLibraryApi : IHalconNativeLibraryApi
    {
        public IntPtr ModuleHandle { get; set; } = new(1234);

        public int LastError { get; set; }

        public string? LoadedPath { get; private set; }

        public uint LoadFlags { get; private set; }

        public int LoadCalls { get; private set; }

        public int RegisterResolverCalls { get; private set; }

        public DllImportResolver? Resolver { get; private set; }

        public Exception? RegisterException { get; set; }

        public Exception? LoadException { get; set; }

        public Exception? FreeException { get; set; }

        public int FreeCalls { get; private set; }

        public IntPtr LoadLibrary(string absolutePath, uint flags)
        {
            LoadCalls++;
            if (LoadException is not null)
            {
                throw LoadException;
            }

            LoadedPath = absolutePath;
            LoadFlags = flags;
            return ModuleHandle;
        }

        public int GetLastError()
        {
            return LastError;
        }

        public void RegisterResolver(Assembly assembly, DllImportResolver resolver)
        {
            RegisterResolverCalls++;
            if (RegisterException is not null)
            {
                throw RegisterException;
            }

            Resolver = resolver;
        }

        public void FreeLibrary(IntPtr moduleHandle)
        {
            FreeCalls++;
            if (FreeException is not null)
            {
                throw FreeException;
            }
        }
    }

    private sealed class FakeRuntimeNativeApi : IHalconRuntimeNativeApi
    {
        private int _smokeCalls;

        public string ManagedPackageVersion { get; set; } = HalconRuntimeLocator.ExpectedManagedPackageVersion;

        public string ManagedAssemblyVersion { get; set; } = HalconRuntimeLocator.ExpectedManagedAssemblyVersion;

        public string SystemFileVersion { get; set; } = HalconRuntimeLocator.ExpectedNativeVersion;

        public Exception? SmokeException { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Operations { get; } = [];

        public int SmokeCalls => Volatile.Read(ref _smokeCalls);

        public void DisableLicenseTermination()
        {
            lock (Operations)
            {
                Operations.Add("disable-license-termination");
            }
        }

        public string GetSystemFileVersion()
        {
            lock (Operations)
            {
                Operations.Add("system-version");
            }

            return SystemFileVersion;
        }

        public void VerifyMatchingLicense()
        {
            Interlocked.Increment(ref _smokeCalls);
            lock (Operations)
            {
                Operations.Add("matching-license-smoke");
            }

            Started.TrySetResult();
            Gate?.Task.GetAwaiter().GetResult();
            if (SmokeException is not null)
            {
                throw SmokeException;
            }
        }
    }

    private sealed class RecordingRuntimeScheduler : IHalconOperationScheduler
    {
        public int RunCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            RunCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRuntimeOperatorBackend : IHalconOperatorBackend
    {
        public int LicenseSmokeCount { get; private set; }

        public void CreateAndWriteShapeModel(
            HalconShapeModelCreationRequest request,
            string stagingModelPath) => throw new NotSupportedException();

        public IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath) =>
            throw new NotSupportedException();

        public void VerifyMatchingLicense()
        {
            LicenseSmokeCount++;
        }
    }
}
