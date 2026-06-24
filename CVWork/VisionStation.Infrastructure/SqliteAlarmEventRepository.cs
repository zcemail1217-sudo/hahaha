using Microsoft.Data.Sqlite;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class SqliteAlarmEventRepository : IAlarmEventRepository
{
    private readonly RuntimePaths _paths;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteAlarmEventRepository(RuntimePaths paths)
    {
        _paths = paths;
    }

    public async Task UpsertAsync(AlarmEvent alarm, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alarm_events
            (id, severity, source, message, details, timestamp, acknowledged, acknowledged_at, cleared_at)
            VALUES
            ($id, $severity, $source, $message, $details, $timestamp, $acknowledged, $acknowledgedAt, $clearedAt)
            ON CONFLICT(id) DO UPDATE SET
                severity = excluded.severity,
                source = excluded.source,
                message = excluded.message,
                details = excluded.details,
                timestamp = excluded.timestamp,
                acknowledged = excluded.acknowledged,
                acknowledged_at = excluded.acknowledged_at,
                cleared_at = excluded.cleared_at;
            """;
        AddAlarmParameters(command, alarm);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlarmEvent>> RecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var alarms = new List<AlarmEvent>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, severity, source, message, details, timestamp, acknowledged, acknowledged_at, cleared_at
            FROM alarm_events
            ORDER BY timestamp DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alarms.Add(ReadAlarm(reader));
        }

        return alarms;
    }

    public async Task<IReadOnlyList<AlarmEvent>> ActiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var alarms = new List<AlarmEvent>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, severity, source, message, details, timestamp, acknowledged, acknowledged_at, cleared_at
            FROM alarm_events
            WHERE cleared_at IS NULL
            ORDER BY
                CASE severity
                    WHEN 'Critical' THEN 0
                    WHEN 'Error' THEN 1
                    WHEN 'Warning' THEN 2
                    ELSE 3
                END,
                timestamp DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alarms.Add(ReadAlarm(reader));
        }

        return alarms;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS alarm_events
                (
                    id TEXT PRIMARY KEY,
                    severity TEXT NOT NULL,
                    source TEXT NOT NULL,
                    message TEXT NOT NULL,
                    details TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    acknowledged INTEGER NOT NULL,
                    acknowledged_at TEXT NULL,
                    cleared_at TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_alarm_events_timestamp ON alarm_events(timestamp DESC);
                CREATE INDEX IF NOT EXISTS ix_alarm_events_active ON alarm_events(cleared_at, timestamp DESC);
                CREATE INDEX IF NOT EXISTS ix_alarm_events_severity ON alarm_events(severity);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static void AddAlarmParameters(SqliteCommand command, AlarmEvent alarm)
    {
        command.Parameters.AddWithValue("$id", alarm.Id);
        command.Parameters.AddWithValue("$severity", alarm.Severity.ToString());
        command.Parameters.AddWithValue("$source", alarm.Source);
        command.Parameters.AddWithValue("$message", alarm.Message);
        command.Parameters.AddWithValue("$details", alarm.Details);
        command.Parameters.AddWithValue("$timestamp", alarm.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$acknowledged", alarm.Acknowledged ? 1 : 0);
        command.Parameters.AddWithValue("$acknowledgedAt", alarm.AcknowledgedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$clearedAt", alarm.ClearedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static AlarmEvent ReadAlarm(SqliteDataReader reader)
    {
        return new AlarmEvent(
            reader.GetString(0),
            Enum.Parse<AlarmSeverity>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetInt32(6) != 0,
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            reader.GetString(4));
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_paths.DatabasePath}");
    }
}
