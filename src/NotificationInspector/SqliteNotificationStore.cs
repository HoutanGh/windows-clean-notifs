using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WindowsCleanNotifs.NotificationInspector;

public sealed class SqliteNotificationStore : INotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly string _connectionString;

    public SqliteNotificationStore(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            PRAGMA journal_mode = WAL;
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS sources (
                app_id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 0,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS notifications (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                app_id TEXT NOT NULL,
                windows_notification_id INTEGER NOT NULL,
                creation_time TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                raw_text_json TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                UNIQUE (app_id, windows_notification_id, creation_time),
                FOREIGN KEY (app_id) REFERENCES sources(app_id) ON DELETE RESTRICT
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE INDEX IF NOT EXISTS idx_notifications_captured_at
            ON notifications(captured_at);
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            PRAGMA user_version = 1;
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredNotificationSource>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT app_id, display_name, enabled, first_seen_at, last_seen_at
            FROM sources
            ORDER BY enabled DESC, display_name COLLATE NOCASE, app_id;
            """;

        var sources = new List<StoredNotificationSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(ReadSource(reader));
        }

        return sources;
    }

    public async Task<StoredNotificationSource?> GetSourceAsync(string appId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetSourceAsync(connection, appId, cancellationToken);
    }

    public async Task<SourceUpsertResult> UpsertSourceAsync(
        NotificationSource source,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var existing = await GetSourceAsync(connection, source.AppId, cancellationToken);
        if (existing is null)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sources (app_id, display_name, enabled, first_seen_at, last_seen_at)
                VALUES ($app_id, $display_name, 0, $first_seen_at, $last_seen_at);
                """;
            insert.Parameters.AddWithValue("$app_id", source.AppId);
            insert.Parameters.AddWithValue("$display_name", source.AppDisplayName);
            insert.Parameters.AddWithValue("$first_seen_at", FormatDateTime(seenAt));
            insert.Parameters.AddWithValue("$last_seen_at", FormatDateTime(seenAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            var inserted = await GetSourceAsync(connection, source.AppId, cancellationToken)
                ?? throw new DataException($"Inserted source {source.AppId} could not be read back.");
            return new SourceUpsertResult(Inserted: true, inserted);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE sources
            SET display_name = $display_name,
                last_seen_at = $last_seen_at
            WHERE app_id = $app_id;
            """;
        update.Parameters.AddWithValue("$app_id", source.AppId);
        update.Parameters.AddWithValue("$display_name", source.AppDisplayName);
        update.Parameters.AddWithValue("$last_seen_at", FormatDateTime(seenAt));
        await update.ExecuteNonQueryAsync(cancellationToken);

        var updated = await GetSourceAsync(connection, source.AppId, cancellationToken)
            ?? throw new DataException($"Updated source {source.AppId} could not be read back.");
        return new SourceUpsertResult(Inserted: false, updated);
    }

    public async Task<bool> SetSourceEnabledAsync(
        string appId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sources
            SET enabled = $enabled
            WHERE app_id = $app_id;
            """;
        command.Parameters.AddWithValue("$app_id", appId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<NotificationInsertResult> TryInsertNotificationAsync(
        CapturedNotification notification,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var source = await GetSourceAsync(connection, notification.AppId, cancellationToken);
        if (source is null)
        {
            return new NotificationInsertResult(NotificationInsertStatus.SourceMissing, null);
        }

        if (!source.Enabled)
        {
            return new NotificationInsertResult(NotificationInsertStatus.SourceDisabled, null);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO notifications (
                app_id,
                windows_notification_id,
                creation_time,
                title,
                body,
                raw_text_json,
                captured_at
            )
            VALUES (
                $app_id,
                $windows_notification_id,
                $creation_time,
                $title,
                $body,
                $raw_text_json,
                $captured_at
            );
            """;
        AddNotificationParameters(insert, notification, capturedAt);

        var changed = await insert.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            return new NotificationInsertResult(NotificationInsertStatus.Duplicate, null);
        }

        var stored = await GetNotificationAsync(connection, notification.Identity, cancellationToken)
            ?? throw new DataException($"Inserted notification {notification.Identity} could not be read back.");
        return new NotificationInsertResult(NotificationInsertStatus.Stored, stored);
    }

    public async Task<IReadOnlyList<StoredNotificationRecord>> ListNotificationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_id, windows_notification_id, creation_time, title, body, raw_text_json, captured_at
            FROM notifications
            ORDER BY captured_at DESC, id DESC;
            """;

        var notifications = new List<StoredNotificationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            notifications.Add(ReadNotification(reader));
        }

        return notifications;
    }

    public async Task<int> DeleteNotificationsOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM notifications
            WHERE captured_at < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", FormatDateTime(cutoff));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StoredNotificationSource?> GetSourceAsync(
        SqliteConnection connection,
        string appId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT app_id, display_name, enabled, first_seen_at, last_seen_at
            FROM sources
            WHERE app_id = $app_id;
            """;
        command.Parameters.AddWithValue("$app_id", appId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSource(reader);
    }

    private static async Task<StoredNotificationRecord?> GetNotificationAsync(
        SqliteConnection connection,
        NotificationIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_id, windows_notification_id, creation_time, title, body, raw_text_json, captured_at
            FROM notifications
            WHERE app_id = $app_id
              AND windows_notification_id = $windows_notification_id
              AND creation_time = $creation_time;
            """;
        command.Parameters.AddWithValue("$app_id", identity.AppId);
        command.Parameters.AddWithValue("$windows_notification_id", (long)identity.WindowsNotificationId);
        command.Parameters.AddWithValue("$creation_time", FormatDateTime(identity.CreationTime));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadNotification(reader);
    }

    private static void AddNotificationParameters(
        SqliteCommand command,
        CapturedNotification notification,
        DateTimeOffset capturedAt)
    {
        command.Parameters.AddWithValue("$app_id", notification.AppId);
        command.Parameters.AddWithValue("$windows_notification_id", (long)notification.WindowsNotificationId);
        command.Parameters.AddWithValue("$creation_time", FormatDateTime(notification.CreationTime));
        command.Parameters.AddWithValue("$title", notification.Title);
        command.Parameters.AddWithValue("$body", notification.Body);
        command.Parameters.AddWithValue("$raw_text_json", JsonSerializer.Serialize(notification.RawTextElements, JsonOptions));
        command.Parameters.AddWithValue("$captured_at", FormatDateTime(capturedAt));
    }

    private static StoredNotificationSource ReadSource(SqliteDataReader reader)
    {
        return new StoredNotificationSource(
            AppId: reader.GetString(0),
            AppDisplayName: reader.GetString(1),
            Enabled: reader.GetInt64(2) != 0,
            FirstSeenAt: ParseDateTime(reader.GetString(3)),
            LastSeenAt: ParseDateTime(reader.GetString(4)));
    }

    private static StoredNotificationRecord ReadNotification(SqliteDataReader reader)
    {
        var rawText = JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions)
            ?? Array.Empty<string>();

        return new StoredNotificationRecord(
            Id: reader.GetInt64(0),
            AppId: reader.GetString(1),
            WindowsNotificationId: checked((uint)reader.GetInt64(2)),
            CreationTime: ParseDateTime(reader.GetString(3)),
            Title: reader.GetString(4),
            Body: reader.GetString(5),
            RawTextElements: rawText,
            CapturedAt: ParseDateTime(reader.GetString(7)));
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
