using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class SqliteInspectionRecordRepository : IInspectionRecordRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RuntimePaths _paths;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteInspectionRecordRepository(RuntimePaths paths)
    {
        _paths = paths;
    }

    public async Task AddAsync(InspectionResult result, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO inspection_records
            (id, timestamp, recipe_id, recipe_name, batch_id, outcome, cycle_time_ms, barcode, message, original_image_path, result_image_path, tool_results_json, result_data_json)
            VALUES
            ($id, $timestamp, $recipeId, $recipeName, $batchId, $outcome, $cycleTimeMs, $barcode, $message, $originalImagePath, $resultImagePath, $toolResultsJson, $resultDataJson);
            """;
        command.Parameters.AddWithValue("$id", result.Id);
        command.Parameters.AddWithValue("$timestamp", result.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$recipeId", result.RecipeId);
        command.Parameters.AddWithValue("$recipeName", result.RecipeName);
        command.Parameters.AddWithValue("$batchId", result.BatchId);
        command.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
        command.Parameters.AddWithValue("$cycleTimeMs", result.CycleTime.TotalMilliseconds);
        command.Parameters.AddWithValue("$barcode", result.Barcode);
        command.Parameters.AddWithValue("$message", result.Message);
        command.Parameters.AddWithValue("$originalImagePath", result.OriginalImagePath);
        command.Parameters.AddWithValue("$resultImagePath", result.ResultImagePath);
        command.Parameters.AddWithValue("$toolResultsJson", JsonSerializer.Serialize(result.ToolResults, JsonOptions));
        command.Parameters.AddWithValue("$resultDataJson", JsonSerializer.Serialize(result.ResultData, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InspectionResult>> RecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var records = new List<InspectionResult>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, recipe_id, recipe_name, batch_id, outcome, cycle_time_ms, barcode, message, original_image_path, result_image_path, tool_results_json, result_data_json
            FROM inspection_records
            ORDER BY timestamp DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var toolResultsJson = reader.GetString(11);
            var toolResults = JsonSerializer.Deserialize<IReadOnlyList<ToolResult>>(toolResultsJson, JsonOptions) ?? Array.Empty<ToolResult>();
            var resultDataJson = reader.IsDBNull(12) ? "{}" : reader.GetString(12);
            var resultData = JsonSerializer.Deserialize<Dictionary<string, string>>(resultDataJson, JsonOptions)
                             ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            records.Add(new InspectionResult
            {
                Id = reader.GetString(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                RecipeId = reader.GetString(2),
                RecipeName = reader.GetString(3),
                BatchId = reader.GetString(4),
                Outcome = Enum.Parse<InspectionOutcome>(reader.GetString(5)),
                CycleTime = TimeSpan.FromMilliseconds(reader.GetDouble(6)),
                Barcode = reader.GetString(7),
                Message = reader.GetString(8),
                OriginalImagePath = reader.GetString(9),
                ResultImagePath = reader.GetString(10),
                ToolResults = toolResults,
                ResultData = resultData
            });
        }

        return records;
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
                CREATE TABLE IF NOT EXISTS inspection_records
                (
                    id TEXT PRIMARY KEY,
                    timestamp TEXT NOT NULL,
                    recipe_id TEXT NOT NULL,
                    recipe_name TEXT NOT NULL,
                    batch_id TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    cycle_time_ms REAL NOT NULL,
                    barcode TEXT NOT NULL,
                    message TEXT NOT NULL,
                    original_image_path TEXT NOT NULL,
                    result_image_path TEXT NOT NULL,
                    tool_results_json TEXT NOT NULL,
                    result_data_json TEXT NOT NULL DEFAULT '{}'
                );

                CREATE INDEX IF NOT EXISTS ix_inspection_records_timestamp ON inspection_records(timestamp DESC);
                CREATE INDEX IF NOT EXISTS ix_inspection_records_recipe ON inspection_records(recipe_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await EnsureColumnAsync(connection, "inspection_records", "result_data_json", "TEXT NOT NULL DEFAULT '{}'", cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_paths.DatabasePath}");
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.CloseAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
