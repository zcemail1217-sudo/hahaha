using VisionStation.Domain;

namespace VisionStation.Application;

public sealed class AlarmService : IAlarmService, IDisposable
{
    private readonly IAlarmEventRepository _repository;
    private readonly IAppLogService _log;
    private readonly CancellationTokenSource _backgroundCancellation = new();
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AlarmEvent> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlarmEvent> _recent = new();

    public AlarmService(IAlarmEventRepository repository, IAppLogService log)
    {
        _repository = repository;
        _log = log;
        _ = Task.Run(
            () => LoadInitialCacheAsync(_backgroundCancellation.Token),
            _backgroundCancellation.Token);
    }

    public event EventHandler<AlarmEvent>? AlarmRaised;

    public event EventHandler<AlarmEvent>? AlarmChanged;

    public AlarmEvent Raise(
        AlarmSeverity severity,
        string source,
        string message,
        string details = "",
        string? alarmId = null)
    {
        if (IsSuppressedOperationalAlarm(severity, source, message, alarmId))
        {
            var clearedAt = DateTimeOffset.Now;
            var suppressed = new AlarmEvent(
                string.IsNullOrWhiteSpace(alarmId) ? Guid.NewGuid().ToString("N") : alarmId,
                severity,
                source,
                message,
                clearedAt,
                true,
                clearedAt,
                clearedAt,
                details);

            lock (_syncRoot)
            {
                _active.Remove(suppressed.Id);
                AddRecent(suppressed);
            }

            _log.Info("Alarm", $"Suppressed stale operational alarm from {source}: {message}");
            PersistLater(suppressed);
            AlarmChanged?.Invoke(this, suppressed);
            return suppressed;
        }

        var alarm = new AlarmEvent(
            string.IsNullOrWhiteSpace(alarmId) ? Guid.NewGuid().ToString("N") : alarmId,
            severity,
            source,
            message,
            DateTimeOffset.Now,
            false,
            null,
            null,
            details);

        lock (_syncRoot)
        {
            if (severity != AlarmSeverity.Info)
            {
                _active[alarm.Id] = alarm;
            }

            AddRecent(alarm);
        }

        WriteLog(alarm);
        PersistLater(alarm);
        AlarmRaised?.Invoke(this, alarm);
        return alarm;
    }

    public void Acknowledge(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId))
        {
            return;
        }

        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            if (_active.TryGetValue(alarmId, out var alarm) && !alarm.Acknowledged)
            {
                changed = alarm with
                {
                    Acknowledged = true,
                    AcknowledgedAt = DateTimeOffset.Now
                };
                _active[alarmId] = changed;
                ReplaceRecent(changed);
            }
        }

        if (changed is null)
        {
            return;
        }

        _log.Info("Alarm", $"Acknowledged {changed.Severity} alarm from {changed.Source}: {changed.Message}");
        PersistLater(changed);
        AlarmChanged?.Invoke(this, changed);
    }

    public void Clear(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId))
        {
            return;
        }

        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            if (_active.TryGetValue(alarmId, out var alarm))
            {
                changed = alarm with
                {
                    ClearedAt = DateTimeOffset.Now,
                    Acknowledged = alarm.Acknowledged || alarm.Severity is AlarmSeverity.Info or AlarmSeverity.Warning,
                    AcknowledgedAt = alarm.AcknowledgedAt ?? DateTimeOffset.Now
                };
                _active.Remove(alarmId);
                ReplaceRecent(changed);
            }
        }

        if (changed is null)
        {
            return;
        }

        _log.Info("Alarm", $"Cleared {changed.Severity} alarm from {changed.Source}: {changed.Message}");
        PersistLater(changed);
        AlarmChanged?.Invoke(this, changed);
    }

    public IReadOnlyList<AlarmEvent> Active()
    {
        lock (_syncRoot)
        {
            return _active.Values
                .OrderByDescending(alarm => alarm.Severity)
                .ThenByDescending(alarm => alarm.Timestamp)
                .ToArray();
        }
    }

    public IReadOnlyList<AlarmEvent> Recent(int count)
    {
        lock (_syncRoot)
        {
            return _recent
                .OrderByDescending(alarm => alarm.Timestamp)
                .Take(count)
                .ToArray();
        }
    }

    public void Dispose()
    {
        _backgroundCancellation.Cancel();
        _backgroundCancellation.Dispose();
    }

    private async Task LoadInitialCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recent = await _repository.RecentAsync(200, cancellationToken);
            var active = await _repository.ActiveAsync(cancellationToken);
            var clearedAt = DateTimeOffset.Now;
            var startupClearedAlarms = active
                .Select(alarm => alarm with
                {
                    Acknowledged = true,
                    AcknowledgedAt = alarm.AcknowledgedAt ?? clearedAt,
                    ClearedAt = clearedAt
                })
                .ToArray();

            lock (_syncRoot)
            {
                _recent.Clear();
                _recent.AddRange(recent);
                _active.Clear();

                foreach (var alarm in startupClearedAlarms)
                {
                    ReplaceRecent(alarm);
                }
            }

            foreach (var alarm in startupClearedAlarms)
            {
                _log.Info("Alarm", $"Cleared startup alarm from {alarm.Source}: {alarm.Message}");
                PersistLater(alarm);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Alarm", $"Alarm cache load failed: {ex.Message}");
        }
    }

    private void AddRecent(AlarmEvent alarm)
    {
        _recent.RemoveAll(item => string.Equals(item.Id, alarm.Id, StringComparison.OrdinalIgnoreCase));
        _recent.Add(alarm);
        _recent.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
        if (_recent.Count > 300)
        {
            _recent.RemoveRange(300, _recent.Count - 300);
        }
    }

    private void ReplaceRecent(AlarmEvent alarm)
    {
        AddRecent(alarm);
    }

    private void PersistLater(AlarmEvent alarm)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _repository.UpsertAsync(alarm, _backgroundCancellation.Token);
            }
            catch (OperationCanceledException) when (_backgroundCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _log.Error("Alarm", $"Alarm persistence failed: {ex.Message}");
            }
        }, _backgroundCancellation.Token);
    }

    private void WriteLog(AlarmEvent alarm)
    {
        var message = $"{alarm.Severity} alarm from {alarm.Source}: {alarm.Message}";
        switch (alarm.Severity)
        {
            case AlarmSeverity.Info:
                _log.Info("Alarm", message);
                break;
            case AlarmSeverity.Warning:
                _log.Warning("Alarm", message);
                break;
            case AlarmSeverity.Error:
                _log.Error("Alarm", message);
                break;
            case AlarmSeverity.Critical:
                _log.Critical("Alarm", message);
                break;
            default:
                _log.Info("Alarm", message);
                break;
        }
    }

    private static bool IsSuppressedOperationalAlarm(
        AlarmSeverity severity,
        string source,
        string message,
        string? alarmId)
    {
        if (severity is not AlarmSeverity.Error and not AlarmSeverity.Critical)
        {
            return false;
        }

        var isAxisAlarm =
            string.Equals(alarmId, "device:Googol axis card", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Googol axis card", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("固高轴卡", StringComparison.OrdinalIgnoreCase);
        if (!isAxisAlarm)
        {
            return false;
        }

        return message.Contains("zero position failed", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("moving or not ready", StringComparison.OrdinalIgnoreCase);
    }
}
