using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Molly.Status;

/// <summary>수집 작업 하나의 마지막 결과. LastAttemptAt·LastSuccessAt은 그 시도를 시작한 시각이다.</summary>
public sealed record JobStatusRecord(string JobId, DateTimeOffset LastAttemptAt, DateTimeOffset? LastSuccessAt, bool LastSucceeded,
    string? LastErrorKind, int ConsecutiveFailures)
{
    /// <summary>
    /// 기존 저장소가 따로 남기는 마지막 성공 시각(게시판 확인·시세 수집 시각)을 합친다. 상태 기록이 실패했거나 이 기능 전의 기록이어도
    /// 더 최근 성공이 있으면 그것을 쓴다.
    /// </summary>
    public static JobStatusRecord? Merge(string jobId, JobStatusRecord? record, DateTimeOffset? knownSuccessAt)
    {
        if (knownSuccessAt is not { } success) return record;
        if (record is null) return new JobStatusRecord(jobId, success, success, true, null, 0);
        if (record.LastSuccessAt is { } recorded && recorded >= success) return record;
        if (success >= record.LastAttemptAt) return new JobStatusRecord(jobId, success, success, true, null, 0);
        return record with { LastSuccessAt = success };
    }
}

/// <summary>서버·알림 종류별 마지막 전송 결과. 성공·실패 각각 보낸 채널을 함께 남겨 현재 등록 채널과 비교한다.</summary>
public sealed record AlertDeliveryRecord(ulong GuildId, string AlertKind, DateTimeOffset? LastSuccessAt, ulong? LastSuccessChannelId,
    DateTimeOffset? LastFailureAt, ulong? LastFailureChannelId, string? LastErrorKind);

/// <summary>
/// /몰리상태용 상태 저장소. 수집 작업별 마지막 결과(molly_job_status)와 시세·이벤트 마감 알림의 서버별 마지막 전송 결과(molly_alert_delivery_status)만 둔다.
/// 성공 이력이나 원시 로그는 쌓지 않고 행마다 마지막 값만 덮어쓴다. 공식 소식 발송 결과는 기존 발송 작업 표에서 읽으므로 여기에 저장하지 않는다.
/// 더 늦게 시작한 시도의 결과만 반영해 재시도·작업 중첩으로 오래된 결과가 최신 상태를 덮어쓰지 않게 한다.
/// </summary>
public sealed class MollyStatusStore
{
    private readonly string m_DatabasePath;

    public MollyStatusStore(string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS molly_job_status (
                job_id TEXT PRIMARY KEY,
                last_attempt_at_utc TEXT NOT NULL,
                last_success_at_utc TEXT NULL,
                last_succeeded INTEGER NOT NULL,
                last_error_kind TEXT NULL,
                consecutive_failures INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS molly_alert_delivery_status (
                guild_id INTEGER NOT NULL,
                alert_kind TEXT NOT NULL,
                last_success_at_utc TEXT NULL,
                last_success_channel_id INTEGER NULL,
                last_failure_at_utc TEXT NULL,
                last_failure_channel_id INTEGER NULL,
                last_error_kind TEXT NULL,
                PRIMARY KEY (guild_id, alert_kind)
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>수집 시도 결과를 남긴다. 이미 더 늦게 시작한 시도의 결과가 있으면 아무것도 바꾸지 않는다.</summary>
    public async Task RecordJobAsync(string jobId, DateTimeOffset attemptStartedAt, bool succeeded, string? errorKind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO molly_job_status (job_id, last_attempt_at_utc, last_success_at_utc, last_succeeded, last_error_kind, consecutive_failures)
            VALUES ($job, $at, CASE WHEN $ok = 1 THEN $at END, $ok, $kind, CASE WHEN $ok = 1 THEN 0 ELSE 1 END)
            ON CONFLICT(job_id) DO UPDATE SET
                last_attempt_at_utc = excluded.last_attempt_at_utc,
                last_success_at_utc = CASE WHEN $ok = 1 THEN excluded.last_attempt_at_utc ELSE molly_job_status.last_success_at_utc END,
                last_succeeded = excluded.last_succeeded,
                last_error_kind = excluded.last_error_kind,
                consecutive_failures = CASE WHEN $ok = 1 THEN 0 ELSE molly_job_status.consecutive_failures + 1 END
            WHERE excluded.last_attempt_at_utc >= molly_job_status.last_attempt_at_utc;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$at", Format(attemptStartedAt));
        command.Parameters.AddWithValue("$ok", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$kind", succeeded ? DBNull.Value : (object?)errorKind ?? StatusErrorKinds.Unknown);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<JobStatusRecord?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_attempt_at_utc, last_success_at_utc, last_succeeded, last_error_kind, consecutive_failures FROM molly_job_status WHERE job_id = $job;";
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new JobStatusRecord(jobId, Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Parse(reader.GetString(1)),
            reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4));
    }

    /// <summary>전송 결과를 남긴다. 성공·실패는 각자 더 최근 값일 때만 바뀐다.</summary>
    public async Task RecordDeliveryAsync(ulong guildId, string alertKind, ulong channelId, DateTimeOffset at, bool succeeded, string? errorKind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = succeeded
            ? """
              INSERT INTO molly_alert_delivery_status (guild_id, alert_kind, last_success_at_utc, last_success_channel_id)
              VALUES ($guild, $kind, $at, $channel)
              ON CONFLICT(guild_id, alert_kind) DO UPDATE SET last_success_at_utc = excluded.last_success_at_utc, last_success_channel_id = excluded.last_success_channel_id
              WHERE molly_alert_delivery_status.last_success_at_utc IS NULL OR excluded.last_success_at_utc >= molly_alert_delivery_status.last_success_at_utc;
              """
            : """
              INSERT INTO molly_alert_delivery_status (guild_id, alert_kind, last_failure_at_utc, last_failure_channel_id, last_error_kind)
              VALUES ($guild, $kind, $at, $channel, $error)
              ON CONFLICT(guild_id, alert_kind) DO UPDATE SET last_failure_at_utc = excluded.last_failure_at_utc,
                  last_failure_channel_id = excluded.last_failure_channel_id, last_error_kind = excluded.last_error_kind
              WHERE molly_alert_delivery_status.last_failure_at_utc IS NULL OR excluded.last_failure_at_utc >= molly_alert_delivery_status.last_failure_at_utc;
              """;
        command.Parameters.AddWithValue("$guild", checked((long)guildId));
        command.Parameters.AddWithValue("$kind", alertKind);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$channel", checked((long)channelId));
        command.Parameters.AddWithValue("$error", (object?)errorKind ?? StatusErrorKinds.Unknown);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<AlertDeliveryRecord?> GetDeliveryAsync(ulong guildId, string alertKind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_success_at_utc, last_success_channel_id, last_failure_at_utc, last_failure_channel_id, last_error_kind
            FROM molly_alert_delivery_status WHERE guild_id = $guild AND alert_kind = $kind;
            """;
        command.Parameters.AddWithValue("$guild", checked((long)guildId));
        command.Parameters.AddWithValue("$kind", alertKind);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new AlertDeliveryRecord(guildId, alertKind,
            reader.IsDBNull(0) ? null : Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : checked((ulong)reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : checked((ulong)reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    // UTC 고정 길이 문자열이라 문자열 비교가 시간 순서와 같다.
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // 여러 수집기·전송이 동시에 기록할 수 있어 잠금을 잠시 기다린다.
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
