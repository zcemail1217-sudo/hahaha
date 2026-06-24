using System.IO;
using VisionStation.Domain;
using VisionStation.Infrastructure;

namespace VisionStation.Client.Services;

public sealed class CrashGuardService
{
    private readonly RuntimePaths _paths;
    private readonly IAppLogService _log;
    private readonly IAlarmService _alarms;

    public CrashGuardService(RuntimePaths paths, IAppLogService log, IAlarmService alarms)
    {
        _paths = paths;
        _log = log;
        _alarms = alarms;
    }

    public void HandleUiException(Exception exception)
    {
        try
        {
            var crashPath = WriteCrashFile("ui", exception);
            _log.Critical("Crash", $"UI exception captured. Crash file: {crashPath}");
            _alarms.Raise(
                AlarmSeverity.Critical,
                "Crash",
                $"UI exception captured: {exception.Message}",
                $"{exception}{Environment.NewLine}Crash file: {crashPath}",
                "crash:ui");
        }
        catch
        {
            FallbackWriteCrash("ui-fallback", exception);
        }
    }

    public void HandleFatalException(Exception exception, bool isTerminating)
    {
        try
        {
            var crashPath = WriteCrashFile("fatal", exception);
            _log.Critical("Crash", $"Fatal exception captured. Terminating={isTerminating}. Crash file: {crashPath}");
            _alarms.Raise(
                AlarmSeverity.Critical,
                "Crash",
                $"Fatal exception captured: {exception.Message}",
                $"{exception}{Environment.NewLine}Terminating={isTerminating}{Environment.NewLine}Crash file: {crashPath}",
                "crash:fatal");
        }
        catch
        {
            FallbackWriteCrash("fatal-fallback", exception);
        }
    }

    public void HandleUnobservedTaskException(Exception exception)
    {
        try
        {
            var crashPath = WriteCrashFile("task", exception);
            _log.Error("Task", $"Unobserved task exception captured. Crash file: {crashPath}");
            _alarms.Raise(
                AlarmSeverity.Error,
                "Task",
                $"Background task exception: {exception.Message}",
                $"{exception}{Environment.NewLine}Crash file: {crashPath}",
                "crash:task");
        }
        catch
        {
            FallbackWriteCrash("task-fallback", exception);
        }
    }

    private string WriteCrashFile(string kind, Exception exception)
    {
        Directory.CreateDirectory(_paths.CrashDirectory);
        var path = Path.Combine(_paths.CrashDirectory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{kind}.log");
        File.WriteAllText(path, BuildCrashText(kind, exception));
        return path;
    }

    private static string BuildCrashText(string kind, Exception exception)
    {
        return string.Join(
            Environment.NewLine,
            $"Kind: {kind}",
            $"Timestamp: {DateTimeOffset.Now:O}",
            $"Process: {Environment.ProcessId}",
            $"Machine: {Environment.MachineName}",
            $"User: {Environment.UserName}",
            "",
            exception.ToString());
    }

    private static void FallbackWriteCrash(string kind, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                RuntimePaths.DefaultBaseDirectory,
                "Crashes");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{kind}.log");
            File.WriteAllText(path, BuildCrashText(kind, exception));
        }
        catch
        {
        }
    }
}
