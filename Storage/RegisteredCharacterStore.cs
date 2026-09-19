using Microsoft.Data.Sqlite;

public sealed record RegisteredCharacter(
    ulong DiscordUserId,
    MobiServer Server,
    string CharacterName,
    DateTimeOffset RegisteredAtUtc);

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
                INSERT INTO registered_characters (discord_user_id, server_id, character_name, registered_at_utc)
                VALUES ($discordUserId, $serverId, $characterName, $registeredAtUtc)
                ON CONFLICT(discord_user_id) DO UPDATE SET
                    server_id = excluded.server_id,
                    character_name = excluded.character_name,
                    registered_at_utc = excluded.registered_at_utc;
                """;
            command.Parameters.AddWithValue("$discordUserId", checked((long)character.DiscordUserId));
            command.Parameters.AddWithValue("$serverId", (int)character.Server);
            command.Parameters.AddWithValue("$characterName", character.CharacterName);
            command.Parameters.AddWithValue("$registeredAtUtc", character.RegisteredAtUtc.ToString("O"));
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
                SELECT server_id, character_name, registered_at_utc
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
                DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind));
        }
        finally { m_Gate.Release(); }
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = m_DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());
}
