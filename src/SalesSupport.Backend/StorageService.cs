using Microsoft.Data.Sqlite;

namespace SalesSupport.Backend;

public sealed record InteractionRecord(
    string Id,
    string InteractionKind,
    string? CustomerRef,
    string Language,
    DateTime StartedAt,
    DateTime EndedAt,
    string TranscriptJson,
    string PictureJson,
    string SummaryJson,
    string? AsksJson = null);

/// <summary>
/// Interaction storage (D17): transcript + picture + summary, never audio. Every row
/// carries interaction_kind so future channels are rows, not migrations (D30). Retention
/// is enforced by purging on startup and on every save.
/// </summary>
public sealed class StorageService
{
    private readonly string _connectionString;
    private readonly int _retentionDays;

    public StorageService(string databasePath, int retentionDays)
    {
        _retentionDays = retentionDays;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = $"Data Source={databasePath}";

        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS interactions (
              id TEXT PRIMARY KEY,
              interaction_kind TEXT NOT NULL,
              customer_ref TEXT,
              language TEXT NOT NULL,
              started_at TEXT NOT NULL,
              ended_at TEXT NOT NULL,
              transcript TEXT NOT NULL,
              picture TEXT NOT NULL,
              summary TEXT NOT NULL,
              asks TEXT);
            """);
        // Databases created before the written-exchange column (D34) get it added in place.
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(interactions)";
            var hasAsks = false;
            using var reader = columns.ExecuteReader();
            while (reader.Read()) if (reader.GetString(1) == "asks") hasAsks = true;
            if (!hasAsks) Execute(connection, "ALTER TABLE interactions ADD COLUMN asks TEXT");
        }
        PurgeExpired();
    }

    public void Save(InteractionRecord record)
    {
        using var connection = Open();
        Execute(connection,
            "INSERT OR REPLACE INTO interactions (id, interaction_kind, customer_ref, language, started_at, ended_at, transcript, picture, summary, asks) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            record.Id, record.InteractionKind, record.CustomerRef, record.Language,
            record.StartedAt.ToString("O"), record.EndedAt.ToString("O"),
            record.TranscriptJson, record.PictureJson, record.SummaryJson, record.AsksJson);
        PurgeExpired();
    }

    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM interactions";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays).ToString("O");
        using var connection = Open();
        Execute(connection, "DELETE FROM interactions WHERE ended_at < $1", cutoff);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params object?[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            command.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
