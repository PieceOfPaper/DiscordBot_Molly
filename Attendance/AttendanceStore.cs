using System.Globalization;
using Microsoft.Data.Sqlite;
using Molly.Currency;

namespace Molly.Attendance;

/// <summary>서버별 출석부. 서버마다 채널 하나·메시지 하나만 둔다.</summary>
public sealed record AttendanceSetting(ulong GuildId, ulong ChannelId, ulong MessageId, DateOnly CurrentDate, bool Enabled);

/// <summary>출석 기록 한 줄. 명단은 출석한 순서대로 보여준다.</summary>
public sealed record AttendanceEntry(ulong UserId, string DisplayName, long RewardAmount, DateTimeOffset CreatedAt);

public enum AttendanceCheckInStatus { CheckedIn, AlreadyCheckedIn }

public sealed record AttendanceCheckInResult(AttendanceCheckInStatus Status, long RewardAmount, long Balance);

/// <summary>
/// 출석부 설정(attendance_settings)과 출석 기록(attendance_entries). 출석 기록과 증표 지급은 한 트랜잭션에서 함께 성공하거나 함께 실패한다.
/// </summary>
public sealed class AttendanceStore
{
    private readonly string m_DatabasePath;

    public AttendanceStore(string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await MollyTokenStore.EnsureSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS attendance_settings (
                    guild_id INTEGER PRIMARY KEY,
                    channel_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    current_attendance_date TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS attendance_entries (
                    guild_id INTEGER NOT NULL,
                    attendance_date TEXT NOT NULL,
                    user_id INTEGER NOT NULL,
                    display_name TEXT NOT NULL,
                    reward_amount INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    PRIMARY KEY (guild_id, attendance_date, user_id)
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<AttendanceSetting?> GetSettingAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT guild_id, channel_id, message_id, current_attendance_date, enabled FROM attendance_settings WHERE guild_id = $guildId;";
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSetting(reader) : null;
    }

    public async Task<IReadOnlyList<AttendanceSetting>> GetEnabledSettingsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT guild_id, channel_id, message_id, current_attendance_date, enabled FROM attendance_settings WHERE enabled = 1 ORDER BY guild_id;";
        var list = new List<AttendanceSetting>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadSetting(reader));
        return list;
    }

    /// <summary>출석부를 저장한다. 처음 등록이면 만들고, 이미 있으면 채널·메시지·날짜·사용 여부를 바꾼다.</summary>
    public async Task SaveSettingAsync(AttendanceSetting setting, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attendance_settings (guild_id, channel_id, message_id, current_attendance_date, enabled, created_at_utc, updated_at_utc)
            VALUES ($guildId, $channelId, $messageId, $date, $enabled, $now, $now)
            ON CONFLICT(guild_id) DO UPDATE SET channel_id = excluded.channel_id, message_id = excluded.message_id,
                current_attendance_date = excluded.current_attendance_date, enabled = excluded.enabled, updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)setting.GuildId));
        command.Parameters.AddWithValue("$channelId", checked((long)setting.ChannelId));
        command.Parameters.AddWithValue("$messageId", checked((long)setting.MessageId));
        command.Parameters.AddWithValue("$date", AttendanceClock.DateKey(setting.CurrentDate));
        command.Parameters.AddWithValue("$enabled", setting.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 출석 기록과 증표 지급을 한 트랜잭션으로 처리한다. 같은 서버·출석일·사용자는 DB 유일 키로 한 번만 기록되어
    /// 중복 클릭이나 동시 요청에도 한 번만 지급한다. 지급이 실패하면 출석 기록도 남지 않는다.
    /// </summary>
    public async Task<AttendanceCheckInResult> CheckInAsync(ulong guildId, DateOnly date, ulong userId, string displayName, long reward,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (reward <= 0) throw new ArgumentOutOfRangeException(nameof(reward), reward, "출석 지급량은 1 이상이어야 합니다.");
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var dateKey = AttendanceClock.DateKey(date);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO attendance_entries (guild_id, attendance_date, user_id, display_name, reward_amount, created_at_utc)
                VALUES ($guildId, $date, $userId, $name, $reward, $now)
                ON CONFLICT DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$guildId", checked((long)guildId));
            insert.Parameters.AddWithValue("$date", dateKey);
            insert.Parameters.AddWithValue("$userId", checked((long)userId));
            insert.Parameters.AddWithValue("$name", displayName);
            insert.Parameters.AddWithValue("$reward", reward);
            insert.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return new(AttendanceCheckInStatus.AlreadyCheckedIn, 0, 0);
            }
        }

        var token = await MollyTokenStore.ApplyAsync(connection, transaction, guildId,
            new TokenChange(userId, reward, TokenReasons.AttendanceReward, "attendance:" + dateKey), now, ct).ConfigureAwait(false);
        if (!token.Succeeded) throw new InvalidOperationException("출석 보상을 지급하지 못했습니다.");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(AttendanceCheckInStatus.CheckedIn, reward, token.Balance);
    }

    /// <summary>한 출석일의 명단. 출석한 순서대로.</summary>
    public async Task<IReadOnlyList<AttendanceEntry>> GetEntriesAsync(ulong guildId, DateOnly date, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, display_name, reward_amount, created_at_utc FROM attendance_entries
            WHERE guild_id = $guildId AND attendance_date = $date ORDER BY created_at_utc, rowid;
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$date", AttendanceClock.DateKey(date));
        var list = new List<AttendanceEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new((ulong)reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        return list;
    }

    private static AttendanceSetting ReadSetting(SqliteDataReader reader)
        => new((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2),
            DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetInt64(4) != 0);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
