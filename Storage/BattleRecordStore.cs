using Microsoft.Data.Sqlite;

/// <summary>한 사용자가 한 클래스로 거둔 배틀 결과 누계입니다.</summary>
public sealed record BattleClassRecord(string ClassId, int Wins, int Losses, int Draws)
{
    public int Total => Wins + Losses + Draws;
}

/// <summary>
/// Discord 사용자별·클래스별 배틀 전적을 저장합니다. 캐릭터 등록과 같이 길드 ID를 키에 넣지 않아 어느 길드의 배틀이든 한 전적에 쌓입니다.
/// 다른 캐릭터로 다시 등록하면 사용자가 확인한 뒤 <see cref="DeleteAllAsync"/>로 이전 전적을 지웁니다.
/// </summary>
public sealed class BattleRecordStore
{
    public enum Result { Win, Loss, Draw }

    private readonly string m_DatabasePath;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public BattleRecordStore(string? databasePath = null)
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
                CREATE TABLE IF NOT EXISTS battle_records (
                    discord_user_id INTEGER NOT NULL,
                    class_id TEXT NOT NULL,
                    wins INTEGER NOT NULL DEFAULT 0,
                    losses INTEGER NOT NULL DEFAULT 0,
                    draws INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (discord_user_id, class_id)
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>한 판의 결과를 사용자·클래스 누계에 더합니다.</summary>
    public async Task RecordAsync(ulong discordUserId, string classId, Result result, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO battle_records (discord_user_id, class_id, wins, losses, draws, updated_at_utc)
                VALUES ($discordUserId, $classId, $win, $loss, $draw, $updatedAtUtc)
                ON CONFLICT(discord_user_id, class_id) DO UPDATE SET
                    wins = wins + excluded.wins,
                    losses = losses + excluded.losses,
                    draws = draws + excluded.draws,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$discordUserId", checked((long)discordUserId));
            command.Parameters.AddWithValue("$classId", classId);
            command.Parameters.AddWithValue("$win", result == Result.Win ? 1 : 0);
            command.Parameters.AddWithValue("$loss", result == Result.Loss ? 1 : 0);
            command.Parameters.AddWithValue("$draw", result == Result.Draw ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>사용자의 클래스별 전적. 판수가 많은 클래스부터, 같으면 클래스 ID 순입니다.</summary>
    public async Task<IReadOnlyList<BattleClassRecord>> LoadAsync(ulong discordUserId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT class_id, wins, losses, draws
                FROM battle_records
                WHERE discord_user_id = $discordUserId
                ORDER BY wins + losses + draws DESC, class_id;
                """;
            command.Parameters.AddWithValue("$discordUserId", checked((long)discordUserId));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var records = new List<BattleClassRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                records.Add(new BattleClassRecord(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            return records;
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>사용자의 모든 클래스 전적을 지웁니다. 지운 클래스 수를 돌려줍니다.</summary>
    public async Task<int> DeleteAllAsync(ulong discordUserId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM battle_records WHERE discord_user_id = $discordUserId;";
            command.Parameters.AddWithValue("$discordUserId", checked((long)discordUserId));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = m_DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());
}
