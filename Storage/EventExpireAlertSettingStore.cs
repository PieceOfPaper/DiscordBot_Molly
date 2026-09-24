using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class EventExpireAlertSettingStore
{
    private readonly string m_DatabasePath;
    private readonly string m_LegacyDirectory;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public EventExpireAlertSettingStore(string? dataDirectory = null, string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        var root = dataDirectory ?? MollyDataPaths.RootDirectory;
        m_DatabasePath = databasePath ?? (dataDirectory is null ? MollyDataPaths.DatabasePath : Path.Combine(root, "database", "molly.sqlite"));
        m_LegacyDirectory = Path.Combine(root, "eventexpirealertsetting");
    }

    public string DatabasePath => m_DatabasePath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS event_expire_alert_settings (
                    guild_id INTEGER PRIMARY KEY,
                    enabled INTEGER NOT NULL,
                    channel_id INTEGER NOT NULL,
                    hours_before INTEGER NOT NULL,
                    last_alert_at_kst TEXT NULL
                );
                """, ct).ConfigureAwait(false);
            await MigrateLegacyFilesAsync(connection, ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    public async Task SaveAsync(ulong guildId, EventExpireAlertSetting setting, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO event_expire_alert_settings (guild_id, enabled, channel_id, hours_before, last_alert_at_kst)
                VALUES ($guildId, $enabled, $channelId, $hoursBefore, $lastAlertAtKst)
                ON CONFLICT(guild_id) DO UPDATE SET
                    enabled = excluded.enabled, channel_id = excluded.channel_id,
                    hours_before = excluded.hours_before, last_alert_at_kst = excluded.last_alert_at_kst;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$enabled", setting.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$channelId", checked((long)setting.ChannelId));
            command.Parameters.AddWithValue("$hoursBefore", setting.HoursBefore);
            command.Parameters.AddWithValue("$lastAlertAtKst", setting.LastAlertAtKst?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<EventExpireAlertSetting?> LoadAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT enabled, channel_id, hours_before, last_alert_at_kst FROM event_expire_alert_settings WHERE guild_id = $guildId;";
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return new EventExpireAlertSetting
            {
                Enabled = reader.GetInt64(0) != 0,
                ChannelId = checked((ulong)reader.GetInt64(1)),
                HoursBefore = reader.GetInt32(2),
                LastAlertAtKst = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }
        finally { m_Gate.Release(); }
    }

    public async Task<List<ulong>> GetAllGuildIdsAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT guild_id FROM event_expire_alert_settings ORDER BY guild_id;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<ulong>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(checked((ulong)reader.GetInt64(0)));
            return result;
        }
        finally { m_Gate.Release(); }
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task MigrateLegacyFilesAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!Directory.Exists(m_LegacyDirectory)) return;
        var files = Directory.GetFiles(m_LegacyDirectory, "*.json");
        if (files.Length == 0) return;
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var rows = new List<(string Path, ulong GuildId, EventExpireAlertSetting Setting)>();
        foreach (var path in files)
        {
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var guildId))
                continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var setting = JsonSerializer.Deserialize<EventExpireAlertSetting>(json, serializerOptions)
                              ?? throw new InvalidDataException("JSON 값이 비었습니다.");
                rows.Add((path, guildId, setting));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                Console.WriteLine($"[SQLite] 기존 알림 설정 이전 보류 ({path}): {ex.Message}");
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO event_expire_alert_settings (guild_id, enabled, channel_id, hours_before, last_alert_at_kst)
                VALUES ($guildId, $enabled, $channelId, $hoursBefore, $lastAlertAtKst)
                ON CONFLICT(guild_id) DO UPDATE SET enabled = excluded.enabled, channel_id = excluded.channel_id,
                    hours_before = excluded.hours_before, last_alert_at_kst = excluded.last_alert_at_kst;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)row.GuildId));
            command.Parameters.AddWithValue("$enabled", row.Setting.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$channelId", checked((long)row.Setting.ChannelId));
            command.Parameters.AddWithValue("$hoursBefore", row.Setting.HoursBefore);
            command.Parameters.AddWithValue("$lastAlertAtKst", row.Setting.LastAlertAtKst?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            try { File.Delete(row.Path); }
            catch (IOException ex) { Console.WriteLine($"[SQLite] 이전 완료 JSON 삭제 보류 ({row.Path}): {ex.Message}"); }
        }
        Console.WriteLine($"[SQLite] 기존 이벤트 알림 설정 {rows.Count}개를 이전했습니다.");
    }
}
