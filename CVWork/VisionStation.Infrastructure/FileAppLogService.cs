using System.Text;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class FileAppLogService : IAppLogService
{
    private readonly object _syncRoot = new();
    private readonly Queue<AppLogEntry> _recent = new();
    private readonly RuntimePaths _paths;
    private readonly AppLoggingSettingsConfiguration _settings;
    private string _currentLogDate = string.Empty;
    private string _logFile = string.Empty;
    private DateTimeOffset _lastRetentionSweep = DateTimeOffset.MinValue;

    public FileAppLogService(RuntimePaths paths)
        : this(paths, new AppLoggingSettingsConfiguration())
    {
    }

    public FileAppLogService(RuntimePaths paths, AppLoggingSettingsConfiguration settings)
    {
        _paths = paths;
        _settings = settings;
        EnsureCurrentLogFile(DateTimeOffset.Now);
        SweepRetention(DateTimeOffset.Now);
    }

    public event EventHandler<AppLogEntry>? LogWritten;

    public void Info(string source, string message)
    {
        Write("INFO", source, message);
    }

    public void Warning(string source, string message)
    {
        Write("WARN", source, message);
    }

    public void Error(string source, string message)
    {
        Write("ERROR", source, message);
    }

    public void Critical(string source, string message)
    {
        Write("CRITICAL", source, message);
    }

    public IReadOnlyList<AppLogEntry> Recent(int count)
    {
        lock (_syncRoot)
        {
            return _recent.Reverse().Take(count).ToArray();
        }
    }

    private void Write(string level, string source, string message)
    {
        var entry = new AppLogEntry(
            DateTimeOffset.Now,
            Normalize(level),
            Normalize(source),
            message?.TrimEnd() ?? string.Empty);
        lock (_syncRoot)
        {
            EnsureCurrentLogFile(entry.Timestamp);
            _recent.Enqueue(entry);
            while (_recent.Count > Math.Max(1, _settings.MaxRecentEntries))
            {
                _recent.Dequeue();
            }

            try
            {
                File.AppendAllText(_logFile, FormatEntry(entry), Encoding.UTF8);
                SweepRetention(entry.Timestamp);
            }
            catch
            {
                // Logging must never take down production flow.
            }
        }

        LogWritten?.Invoke(this, entry);
    }

    private void EnsureCurrentLogFile(DateTimeOffset timestamp)
    {
        var logDate = timestamp.ToString("yyyyMMdd");
        if (string.Equals(logDate, _currentLogDate, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(_paths.LogDirectory);
        _currentLogDate = logDate;
        _logFile = Path.Combine(_paths.LogDirectory, $"{logDate}.log");
        if (!File.Exists(_logFile))
        {
            File.WriteAllText(_logFile, string.Empty, Encoding.UTF8);
        }
    }

    private void SweepRetention(DateTimeOffset now)
    {
        if (_settings.RetentionDays <= 0 ||
            now - _lastRetentionSweep < TimeSpan.FromHours(12))
        {
            return;
        }

        _lastRetentionSweep = now;
        var cutoff = now.Date.AddDays(-_settings.RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_paths.LogDirectory, "*.log"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) &&
                    date < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private string FormatEntry(AppLogEntry entry)
    {
        var builder = new StringBuilder();
        builder
            .Append(entry.Timestamp.ToString("O"))
            .Append(" [")
            .Append(entry.Level)
            .Append("] ");
        if (_settings.IncludeThreadId)
        {
            builder
                .Append("[T")
                .Append(Environment.CurrentManagedThreadId)
                .Append("] ");
        }

        builder
            .Append(entry.Source)
            .Append(' ');

        var normalizedMessage = (entry.Message ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedMessage.Split('\n');
        builder.Append(lines.Length > 0 ? lines[0] : string.Empty).AppendLine();
        foreach (var line in lines.Skip(1))
        {
            builder.Append("    ").AppendLine(line);
        }

        return builder.ToString();
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }
}
