using Microsoft.Data.Sqlite;

public sealed record RegisteredCharacter(
    ulong DiscordUserId,
    MobiServer Server,
    string CharacterName,
    DateTimeOffset RegisteredAtUtc,
    string? ClassId = null,
    int? CombatPower = null,
    int? LifePower = null,
    int? CharmPower = null,
    DateTimeOffset? LastSyncedAtUtc = null);

/// <summary>
/// Discord 사용자와 게임 캐릭터의 전역 연결을 저장합니다.
/// 길드 ID를 키에 포함하지 않아 어느 길드에서든 같은 등록 정보를 사용합니다.
/// </summary>
public sealed class RegisteredCharacterStore
{
    private readonly string m_DatabasePath;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public RegisteredCharacterStore(string? databasePath = null)
    {
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS registered_characters (
                    discord_user_id INTEGER PRIMARY KEY,
                    server_id INTEGER NOT NULL,
                    character_name TEXT NOT NULL,
                    registered_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            foreach (var column in new[] { "class_id TEXT", "combat_power INTEGER", "life_power INTEGER", "charm_power INTEGER", "last_synced_at_utc TEXT" })
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = $"ALTER TABLE registered_characters ADD COLUMN {column};";
                try { await migration.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
            }
        }
        finally { m_Gate.Release(); }
    }

    public async Task SaveAsync(RegisteredCharacter character, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO registered_characters (discord_user_id, server_id, character_name, registered_at_utc, class_id, combat_power, life_power, charm_power, last_synced_at_utc)
                VALUES ($discordUserId, $serverId, $characterName, $registeredAtUtc, $classId, $combatPower, $lifePower, $charmPower, $lastSyncedAtUtc)
                ON CONFLICT(discord_user_id) DO UPDATE SET
                    server_id = excluded.server_id,
                    character_name = excluded.character_name,
                    registered_at_utc = excluded.registered_at_utc,
                    class_id = excluded.class_id,
                    combat_power = excluded.combat_power,
                    life_power = excluded.life_power,
                    charm_power = excluded.charm_power,
                    last_synced_at_utc = excluded.last_synced_at_utc;
                """;
            command.Parameters.AddWithValue("$discordUserId", checked((long)character.DiscordUserId));
            command.Parameters.AddWithValue("$serverId", (int)character.Server);
            command.Parameters.AddWithValue("$characterName", character.CharacterName);
            command.Parameters.AddWithValue("$registeredAtUtc", character.RegisteredAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$classId", (object?)character.ClassId ?? DBNull.Value);
            command.Parameters.AddWithValue("$combatPower", (object?)character.CombatPower ?? DBNull.Value);
            command.Parameters.AddWithValue("$lifePower", (object?)character.LifePower ?? DBNull.Value);
            command.Parameters.AddWithValue("$charmPower", (object?)character.CharmPower ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSyncedAtUtc", character.LastSyncedAtUtc?.ToString("O") ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<RegisteredCharacter?> LoadAsync(ulong discordUserId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT server_id, character_name, registered_at_utc, class_id, combat_power, life_power, charm_power, last_synced_at_utc
                FROM registered_characters
                WHERE discord_user_id = $discordUserId;
                """;
            command.Parameters.AddWithValue("$discordUserId", checked((long)discordUserId));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            return new RegisteredCharacter(
                discordUserId,
                (MobiServer)reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind));
        }
        finally { m_Gate.Release(); }
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = m_DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());
}
