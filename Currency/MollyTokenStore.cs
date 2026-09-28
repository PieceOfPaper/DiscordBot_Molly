using Microsoft.Data.Sqlite;

namespace Molly.Currency;

/// <summary>증표 잔액을 바꾼 사유. 원장의 reason_type에 그대로 저장한다. 목록에 없는 사유는 거부한다.</summary>
public static class TokenReasons
{
    public const string AttendanceReward = "attendance_reward";
    public const string PredictionBet = "prediction_bet";
    public const string PredictionPayout = "prediction_payout";
    public const string PredictionRefund = "prediction_refund";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AttendanceReward, PredictionBet, PredictionPayout, PredictionRefund,
    };
}

/// <summary>
/// 증표 잔액 변경 한 건. 지급은 양수, 차감은 음수다.
/// 같은 서버·사용자·사유·참조 ID의 변경은 한 번만 반영되므로 재시도해도 중복 지급·차감되지 않는다.
/// </summary>
public sealed record TokenChange(ulong UserId, long Amount, string ReasonType, string ReferenceId);

public enum TokenChangeStatus
{
    /// <summary>이번 요청으로 반영했다.</summary>
    Applied,
    /// <summary>같은 변경이 이미 반영되어 있어 아무것도 바꾸지 않았다.</summary>
    AlreadyApplied,
    /// <summary>잔액이 모자라 차감하지 않았다.</summary>
    InsufficientBalance,
}

/// <summary>변경 결과와 처리 후 잔액. 반영하지 않았다면 현재 잔액이다.</summary>
public sealed record TokenChangeResult(TokenChangeStatus Status, long Balance)
{
    public bool Succeeded => Status is TokenChangeStatus.Applied or TokenChangeStatus.AlreadyApplied;
}

/// <summary>원장 한 줄.</summary>
public sealed record TokenTransaction(long TransactionId, ulong GuildId, ulong UserId, long Amount, long BalanceAfter,
    string ReasonType, string ReferenceId, DateTimeOffset CreatedAt);

/// <summary>
/// 마물 퇴치 증표의 서버별 잔액(currency_accounts)과 거래 원장(currency_transactions).
/// 잔액 변경은 항상 원장 기록과 같은 트랜잭션에서 처리한다. 출석·예측처럼 자기 기록과 증표 변경을 함께 묶어야 하는 기능은
/// 같은 DB(molly.sqlite)의 연결·트랜잭션으로 <see cref="ApplyAsync(SqliteConnection, SqliteTransaction, ulong, TokenChange, DateTimeOffset, CancellationToken)"/>를 호출한다.
/// </summary>
public sealed class MollyTokenStore
{
    private readonly string m_DatabasePath;

    public MollyTokenStore(string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>이 서버에서의 보유량. 한 번도 받은 적 없으면 0.</summary>
    public async Task<long> GetBalanceAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await GetBalanceAsync(connection, null, guildId, userId, ct).ConfigureAwait(false);
    }

    /// <summary>변경 한 건을 자체 트랜잭션으로 반영한다.</summary>
    public async Task<TokenChangeResult> ApplyAsync(ulong guildId, TokenChange change, CancellationToken ct = default)
        => (await ApplyAllAsync(guildId, [change], ct).ConfigureAwait(false))[0];

    /// <summary>
    /// 여러 건을 한 트랜잭션으로 반영한다(예측 정산·환불). 한 건이라도 잔액이 모자라면 모두 되돌리고,
    /// 그 건은 <see cref="TokenChangeStatus.InsufficientBalance"/>, 나머지는 반영 전 잔액으로 돌려준다.
    /// </summary>
    public async Task<IReadOnlyList<TokenChangeResult>> ApplyAllAsync(ulong guildId, IReadOnlyList<TokenChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return [];
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var results = new TokenChangeResult[changes.Count];
        for (var i = 0; i < changes.Count; i++)
        {
            results[i] = await ApplyAsync(connection, transaction, guildId, changes[i], now, ct).ConfigureAwait(false);
            if (results[i].Status != TokenChangeStatus.InsufficientBalance) continue;

            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            var failed = results[i];
            for (var j = 0; j < changes.Count; j++)
                results[j] = j == i ? failed : new(TokenChangeStatus.InsufficientBalance,
                    await GetBalanceAsync(connection, null, guildId, changes[j].UserId, ct).ConfigureAwait(false));
            return results;
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>한 사용자의 최근 원장. 최신순.</summary>
    public async Task<IReadOnlyList<TokenTransaction>> GetTransactionsAsync(ulong guildId, ulong userId, int limit = 20, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transaction_id, amount, balance_after, reason_type, reference_id, created_at_utc
            FROM currency_transactions WHERE guild_id = $guildId AND user_id = $userId
            ORDER BY transaction_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$userId", checked((long)userId));
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var list = new List<TokenTransaction>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new(reader.GetInt64(0), guildId, userId, reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        return list;
    }

    /// <summary>증표 표를 만든다. 다른 기능의 저장소가 같은 트랜잭션에서 증표를 다룰 때도 초기화 시 호출할 수 있다.</summary>
    public static async Task EnsureSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS currency_accounts (
                guild_id INTEGER NOT NULL,
                user_id INTEGER NOT NULL,
                balance INTEGER NOT NULL CHECK (balance >= 0),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS currency_transactions (
                transaction_id INTEGER PRIMARY KEY AUTOINCREMENT,
                guild_id INTEGER NOT NULL,
                user_id INTEGER NOT NULL,
                amount INTEGER NOT NULL CHECK (amount <> 0),
                balance_after INTEGER NOT NULL CHECK (balance_after >= 0),
                reason_type TEXT NOT NULL,
                reference_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE (guild_id, user_id, reason_type, reference_id)
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 호출한 쪽의 트랜잭션 안에서 변경 한 건을 반영한다. 커밋·롤백은 호출한 쪽이 한다.
    /// 잔액이 모자라면 아무것도 쓰지 않고 <see cref="TokenChangeStatus.InsufficientBalance"/>를 돌려준다.
    /// 같은 사유·참조 ID로 금액만 다른 변경이 다시 들어오면 잘못된 호출이므로 예외를 던진다.
    /// </summary>
    public static async Task<TokenChangeResult> ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, ulong guildId,
        TokenChange change, DateTimeOffset now, CancellationToken ct)
    {
        Validate(change);
        var guild = checked((long)guildId);
        var user = checked((long)change.UserId);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT amount FROM currency_transactions
                WHERE guild_id = $guildId AND user_id = $userId AND reason_type = $reason AND reference_id = $reference;
                """;
            existing.Parameters.AddWithValue("$guildId", guild);
            existing.Parameters.AddWithValue("$userId", user);
            existing.Parameters.AddWithValue("$reason", change.ReasonType);
            existing.Parameters.AddWithValue("$reference", change.ReferenceId);
            if (await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) is { } amount)
            {
                if (Convert.ToInt64(amount) != change.Amount)
                    throw new InvalidOperationException($"이미 처리한 증표 변경({change.ReasonType}/{change.ReferenceId})과 금액이 다릅니다.");
                return new(TokenChangeStatus.AlreadyApplied, await GetBalanceAsync(connection, transaction, guildId, change.UserId, ct).ConfigureAwait(false));
            }
        }

        var balance = await GetBalanceAsync(connection, transaction, guildId, change.UserId, ct).ConfigureAwait(false);
        var after = checked(balance + change.Amount);
        if (after < 0) return new(TokenChangeStatus.InsufficientBalance, balance);

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO currency_accounts (guild_id, user_id, balance, created_at_utc, updated_at_utc)
            VALUES ($guildId, $userId, $balance, $now, $now)
            ON CONFLICT(guild_id, user_id) DO UPDATE SET balance = excluded.balance, updated_at_utc = excluded.updated_at_utc;
            INSERT INTO currency_transactions (guild_id, user_id, amount, balance_after, reason_type, reference_id, created_at_utc)
            VALUES ($guildId, $userId, $amount, $balance, $reason, $reference, $now);
            """;
        write.Parameters.AddWithValue("$guildId", guild);
        write.Parameters.AddWithValue("$userId", user);
        write.Parameters.AddWithValue("$balance", after);
        write.Parameters.AddWithValue("$amount", change.Amount);
        write.Parameters.AddWithValue("$reason", change.ReasonType);
        write.Parameters.AddWithValue("$reference", change.ReferenceId);
        write.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return new(TokenChangeStatus.Applied, after);
    }

    private static void Validate(TokenChange change)
    {
        if (change.Amount == 0) throw new ArgumentException("증표 변경량은 0일 수 없습니다.", nameof(change));
        if (!TokenReasons.All.Contains(change.ReasonType)) throw new ArgumentException($"알 수 없는 증표 변경 사유입니다: {change.ReasonType}", nameof(change));
        if (string.IsNullOrWhiteSpace(change.ReferenceId)) throw new ArgumentException("증표 변경의 참조 ID가 비어 있습니다.", nameof(change));
    }

    /// <summary>호출한 쪽의 연결·트랜잭션에서 잔액을 읽는다(예측 베팅 상한 판정).</summary>
    public static async Task<long> GetBalanceAsync(SqliteConnection connection, SqliteTransaction? transaction, ulong guildId, ulong userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT balance FROM currency_accounts WHERE guild_id = $guildId AND user_id = $userId;";
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$userId", checked((long)userId));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is { } value ? Convert.ToInt64(value) : 0;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
