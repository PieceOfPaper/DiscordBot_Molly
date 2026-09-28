using Microsoft.Data.Sqlite;

/// <summary>
/// 디스코드 서버별 기준 마비노기 모바일 서버(/기본서버지정). 랭킹·캐릭터등록에서 서버를 고르지 않으면 이 값을 쓰고,
/// 지정하지 않은 디스코드 서버는 <see cref="FallbackServer"/>를 쓴다.
/// </summary>
public sealed class GuildDefaultServerStore
{
    public const MobiServer FallbackServer = MobiServer.칼릭스;

    private readonly string m_DatabasePath;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public GuildDefaultServerStore(string? dataDirectory = null, string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? (dataDirectory is null ? MollyDataPaths.DatabasePath : Path.Combine(dataDirectory, "database", "molly.sqlite"));
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
                CREATE TABLE IF NOT EXISTS guild_default_servers (
                    guild_id INTEGER PRIMARY KEY,
                    server_id INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    public async Task SaveAsync(ulong guildId, MobiServer server, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(server)) throw new ArgumentOutOfRangeException(nameof(server), server, "알 수 없는 서버입니다.");
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO guild_default_servers (guild_id, server_id) VALUES ($guildId, $serverId)
                ON CONFLICT(guild_id) DO UPDATE SET server_id = excluded.server_id;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$serverId", (int)server);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>지정한 기본 서버. 지정하지 않았거나 저장값이 알 수 없는 서버면 null.</summary>
    public async Task<MobiServer?> LoadAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT server_id FROM guild_default_servers WHERE guild_id = $guildId;";
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is null) return null;
            var server = (MobiServer)Convert.ToInt32(value);
            return Enum.IsDefined(server) ? server : null;
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>
    /// 명령에서 쓸 서버를 정한다. 사용자가 고른 서버가 우선이고, 없으면(0) 이 디스코드 서버의 기본 서버, 그것도 없으면 칼릭스.
    /// DM처럼 디스코드 서버가 없으면 기본 서버를 찾지 않는다.
    /// </summary>
    public async Task<MobiServer> ResolveAsync(ulong? guildId, MobiServer chosen, CancellationToken ct = default)
    {
        if (Enum.IsDefined(chosen)) return chosen;
        if (guildId is { } id && await LoadAsync(id, ct).ConfigureAwait(false) is { } saved) return saved;
        return FallbackServer;
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
}
