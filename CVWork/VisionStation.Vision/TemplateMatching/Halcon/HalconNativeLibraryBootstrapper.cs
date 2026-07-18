using System.Reflection;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace VisionStation.Vision;

internal interface IHalconNativeLibraryBootstrapper
{
    /// <summary>
    /// Binds the HALCON managed assembly to one validated native runtime root for the process.
    /// Repeating the same root is idempotent; a different root is rejected because a registered
    /// DllImport resolver and its loaded module cannot be safely replaced in-process.
    /// </summary>
    HalconNativeLibraryBootstrapResult EnsureBound(HalconRuntimeLocation location);
}

internal sealed record HalconNativeLibraryBootstrapResult
{
    private HalconNativeLibraryBootstrapResult(
        bool success,
        TemplateMatchingDiagnostic? diagnostic)
    {
        if (success == (diagnostic is not null))
        {
            throw new ArgumentException("A bootstrap result must contain either success or one diagnostic.");
        }

        Success = success;
        Diagnostic = diagnostic;
    }

    public bool Success { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }

    public static HalconNativeLibraryBootstrapResult Bound()
    {
        return new HalconNativeLibraryBootstrapResult(true, null);
    }

    public static HalconNativeLibraryBootstrapResult Failed(TemplateMatchingDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new HalconNativeLibraryBootstrapResult(false, diagnostic);
    }
}

internal interface IHalconProcessEnvironmentMutator
{
    string? GetProcessVariable(string name);

    void SetProcessVariable(string name, string? value);
}

internal interface IHalconNativeLibraryApi
{
    IntPtr LoadLibrary(string absolutePath, uint flags);

    int GetLastError();

    void RegisterResolver(Assembly assembly, DllImportResolver resolver);

    void FreeLibrary(IntPtr moduleHandle);
}

internal sealed class HalconNativeBindingState
{
    internal static HalconNativeBindingState Process { get; } = new();

    internal object SyncRoot { get; } = new();

    internal string? RuntimeRoot { get; set; }

    internal IntPtr ModuleHandle { get; set; }
}

internal sealed class HalconNativeLibraryBootstrapper : IHalconNativeLibraryBootstrapper
{
    internal const uint LoadLibrarySearchDllLoadDir = 0x00000100;

    private readonly IHalconNativeLibraryApi _nativeLibrary;
    private readonly IHalconProcessEnvironmentMutator _environment;
    private readonly HalconNativeBindingState _state;

    internal HalconNativeLibraryBootstrapper()
        : this(
            new WindowsHalconNativeLibraryApi(),
            new ProcessHalconEnvironmentMutator(),
            HalconNativeBindingState.Process)
    {
    }

    internal HalconNativeLibraryBootstrapper(
        IHalconNativeLibraryApi nativeLibrary,
        IHalconProcessEnvironmentMutator environment,
        HalconNativeBindingState state)
    {
        _nativeLibrary = nativeLibrary ?? throw new ArgumentNullException(nameof(nativeLibrary));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public HalconNativeLibraryBootstrapResult EnsureBound(HalconRuntimeLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!TryValidateLocation(location, out var requestedRoot, out var validationFailure))
        {
            return validationFailure!;
        }

        lock (_state.SyncRoot)
        {
            if (_state.ModuleHandle != IntPtr.Zero)
            {
                return string.Equals(_state.RuntimeRoot, requestedRoot, StringComparison.OrdinalIgnoreCase)
                    ? HalconNativeLibraryBootstrapResult.Bound()
                    : Failure(
                        TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                        "HALCON native binding is already fixed to a different runtime root.");
            }

            string? previousRoot;
            string? previousArchitecture;
            try
            {
                previousRoot = _environment.GetProcessVariable("HALCONROOT");
                previousArchitecture = _environment.GetProcessVariable("HALCONARCH");
            }
            catch (Exception exception)
            {
                return Failure(
                    TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                    $"HALCON process environment could not be read; type={exception.GetType().Name}.");
            }

            var rootMayHaveChanged = false;
            var architectureMayHaveChanged = false;
            var moduleHandle = IntPtr.Zero;
            var committed = false;
            var stage = "configure-process-environment";
            try
            {
                rootMayHaveChanged = true;
                _environment.SetProcessVariable("HALCONROOT", requestedRoot);
                architectureMayHaveChanged = true;
                _environment.SetProcessVariable(
                    "HALCONARCH",
                    HalconRuntimeLocator.ExpectedArchitecture);

                stage = "load-native-library";
                moduleHandle = _nativeLibrary.LoadLibrary(
                    Path.GetFullPath(location.NativeLibraryPath.Trim()),
                    LoadLibrarySearchDllLoadDir);
                if (moduleHandle == IntPtr.Zero)
                {
                    var error = _nativeLibrary.GetLastError();
                    return Failure(
                        MapLoaderError(error),
                        $"LoadLibraryExW failed; win32Error={error}.");
                }

                stage = "register-dll-import-resolver";
                _nativeLibrary.RegisterResolver(
                    typeof(HSystem).Assembly,
                    (libraryName, _, _) => string.Equals(libraryName, "halcon", StringComparison.Ordinal)
                        ? moduleHandle
                        : IntPtr.Zero);

                _state.RuntimeRoot = requestedRoot;
                _state.ModuleHandle = moduleHandle;
                committed = true;
                return HalconNativeLibraryBootstrapResult.Bound();
            }
            catch (Exception exception)
            {
                return Failure(
                    MapBootstrapException(stage, exception),
                    $"HALCON native bootstrap failed; stage={stage}; type={exception.GetType().Name}.");
            }
            finally
            {
                if (!committed)
                {
                    TryFreeLibrary(moduleHandle);
                    TryRestoreEnvironment(
                        previousRoot,
                        previousArchitecture,
                        rootMayHaveChanged,
                        architectureMayHaveChanged);
                }
            }
        }
    }

    private static bool TryValidateLocation(
        HalconRuntimeLocation location,
        out string requestedRoot,
        out HalconNativeLibraryBootstrapResult? failure)
    {
        requestedRoot = string.Empty;
        failure = null;
        if (!string.Equals(
                location.Architecture,
                HalconRuntimeLocator.ExpectedArchitecture,
                StringComparison.Ordinal))
        {
            failure = Failure(
                TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
                $"HALCON architecture must equal '{HalconRuntimeLocator.ExpectedArchitecture}' exactly.");
            return false;
        }

        var root = location.RuntimeRoot.Trim();
        var nativeLibraryPath = location.NativeLibraryPath.Trim();
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(nativeLibraryPath))
        {
            failure = Failure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                "HALCON runtime root and native library path must be absolute.");
            return false;
        }

        try
        {
            requestedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedNativePath = Path.GetFullPath(nativeLibraryPath);
            var expectedNativePath = Path.GetFullPath(
                Path.Combine(
                    requestedRoot,
                    "bin",
                    HalconRuntimeLocator.ExpectedArchitecture,
                    "halcon.dll"));
            if (!string.Equals(
                    normalizedNativePath,
                    expectedNativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                failure = Failure(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON native library path is outside the validated runtime layout.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = Failure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"HALCON runtime path normalization failed; type={exception.GetType().Name}.");
            return false;
        }
    }

    private void TryFreeLibrary(IntPtr moduleHandle)
    {
        if (moduleHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _nativeLibrary.FreeLibrary(moduleHandle);
        }
        catch
        {
            // Preserve the stable primary diagnostic. The module was never published
            // through the resolver or binding state, so no caller can safely use it.
        }
    }

    private void TryRestoreEnvironment(
        string? root,
        string? architecture,
        bool rootMayHaveChanged,
        bool architectureMayHaveChanged)
    {
        if (architectureMayHaveChanged)
        {
            TrySetProcessVariable("HALCONARCH", architecture);
        }

        if (rootMayHaveChanged)
        {
            TrySetProcessVariable("HALCONROOT", root);
        }
    }

    private void TrySetProcessVariable(string name, string? value)
    {
        try
        {
            _environment.SetProcessVariable(name, value);
        }
        catch
        {
            // Best-effort rollback must not replace the diagnostic for the operation
            // that caused binding to fail.
        }
    }

    private static string MapLoaderError(int error)
    {
        return error switch
        {
            193 => TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
            127 => TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
            _ => TemplateMatchingDiagnosticCodes.RuntimeNotFound
        };
    }

    private static string MapBootstrapException(string stage, Exception exception)
    {
        if (string.Equals(stage, "register-dll-import-resolver", StringComparison.Ordinal))
        {
            return TemplateMatchingDiagnosticCodes.ConfigInvalidParameter;
        }

        return exception switch
        {
            BadImageFormatException => TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
            EntryPointNotFoundException => TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
            _ => TemplateMatchingDiagnosticCodes.RuntimeNotFound
        };
    }

    private static HalconNativeLibraryBootstrapResult Failure(string code, string technicalDetails)
    {
        return HalconNativeLibraryBootstrapResult.Failed(
            TemplateMatchingDiagnostics.Create(code, technicalDetails));
    }
}

internal sealed class ProcessHalconEnvironmentMutator : IHalconProcessEnvironmentMutator
{
    public string? GetProcessVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
    }

    public void SetProcessVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
    }
}

internal sealed class WindowsHalconNativeLibraryApi : IHalconNativeLibraryApi
{
    public IntPtr LoadLibrary(string absolutePath, uint flags)
    {
        return NativeMethods.LoadLibraryEx(absolutePath, IntPtr.Zero, flags);
    }

    public int GetLastError()
    {
        return Marshal.GetLastWin32Error();
    }

    public void RegisterResolver(Assembly assembly, DllImportResolver resolver)
    {
        NativeLibrary.SetDllImportResolver(assembly, resolver);
    }

    public void FreeLibrary(IntPtr moduleHandle)
    {
        _ = NativeMethods.FreeLibrary(moduleHandle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr moduleHandle);
    }
}
