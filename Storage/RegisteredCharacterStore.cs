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
        MollySqlite.EnsureProvider();
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

            return Read(reader, discordUserId, 0);
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>등록된 모든 캐릭터. 등록은 길드와 무관하므로 길드별 현황은 호출한 쪽에서 멤버 여부로 거른다.</summary>
    public async Task<IReadOnlyList<RegisteredCharacter>> LoadAllAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT discord_user_id, server_id, character_name, registered_at_utc, class_id, combat_power, life_power, charm_power, last_synced_at_utc
                FROM registered_characters
                ORDER BY discord_user_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var characters = new List<RegisteredCharacter>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                characters.Add(Read(reader, (ulong)reader.GetInt64(0), 1));
            return characters;
        }
        finally { m_Gate.Release(); }
    }

    private static RegisteredCharacter Read(SqliteDataReader reader, ulong discordUserId, int offset) => new(
        discordUserId,
        (MobiServer)reader.GetInt32(offset),
        reader.GetString(offset + 1),
        DateTimeOffset.Parse(reader.GetString(offset + 2), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetInt32(offset + 6),
        reader.IsDBNull(offset + 7) ? null : DateTimeOffset.Parse(reader.GetString(offset + 7), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = m_DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());
}
