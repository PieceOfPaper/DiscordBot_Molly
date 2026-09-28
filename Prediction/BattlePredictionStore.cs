using System.Globalization;
using Microsoft.Data.Sqlite;
using Molly.Currency;

namespace Molly.Prediction;

public enum PredictionStatus { Open, Locked, Resolved, Cancelled }

/// <summary>예측이 무효가 된 이유. 무효면 증표는 오가지 않는다.</summary>
public enum PredictionCancelReason
{
    /// <summary>무승부.</summary>
    Draw,
    /// <summary>/배틀종료로 강제 종료.</summary>
    Stopped,
    /// <summary>배틀 진행 중 오류.</summary>
    Error,
    /// <summary>봇 재시작으로 배틀이 끊김.</summary>
    Restart,
    /// <summary>참여 인원이 최소 인원보다 적음.</summary>
    TooFewPredictors,
    /// <summary>한쪽 진영에만 걸림.</summary>
    OneSided,
    /// <summary>정산 중 잔액이 모자란 사람이 있음(정상적으로는 발생하지 않음).</summary>
    SettlementFailed,
}

public enum PredictionBetStatus
{
    /// <summary>걸었다(처음 또는 추가).</summary>
    Placed,
    /// <summary>예측이 없거나 이미 마감되었다.</summary>
    Closed,
    /// <summary>이미 반대 진영에 걸었다.</summary>
    OtherSide,
    /// <summary>버튼 금액이 이 예측 규칙에 없다.</summary>
    InvalidAmount,
    /// <summary>누적 상한을 넘는다.</summary>
    OverLimit,
}

/// <summary>
/// 베팅 결과. <paramref name="Side"/>·<paramref name="SideName"/>은 이번에 걸었거나 이미 걸어 둔 진영,
/// <paramref name="Total"/>은 처리 후(거부면 현재) 누적액, <paramref name="Limit"/>은 이 예측의 누적 상한이다.
/// </summary>
public sealed record PredictionBetResult(PredictionBetStatus Status, PredictionSide Side = default, string SideName = "",
    long Amount = 0, long Total = 0, long Limit = 0, long Balance = 0);

/// <summary>마감 결과. 진영별 누적액과 참여 인원, 이대로 성립하는지.</summary>
public sealed record PredictionLockResult(long ChallengerTotal, long OpponentTotal, int PredictorCount, PredictionCancelReason? InvalidReason)
{
    public bool IsValid => InvalidReason is null;
}

/// <summary>정산된 참여 한 건. 무효면 <paramref name="Payout"/>은 null.</summary>
public sealed record PredictionSettledEntry(ulong UserId, PredictionSide Side, long Amount, long? Payout);

/// <summary>정산 또는 무효 처리 결과. 이미 처리된 예측을 다시 요청하면 저장된 결과를 그대로 돌려준다.</summary>
public sealed record PredictionSettlement(PredictionStatus Status, PredictionCancelReason? CancelReason, PredictionSide? WinningSide,
    string ChallengerName, string OpponentName, IReadOnlyList<PredictionSettledEntry> Entries)
{
    public long Pool => Entries.Sum(x => x.Amount);
}

/// <summary>
/// 배틀 예측(battle_predictions)과 참여(battle_prediction_entries). 증표는 걸 때가 아니라 정산할 때 한 트랜잭션에서 차감·배당한다.
/// 한 서버에 배틀은 하나뿐이고 증표 사용처는 예측뿐이라 베팅부터 정산 사이에 잔액이 줄지 않는다.
/// 그래도 베팅 상한은 같은 서버의 정산되지 않은 다른 예측에 건 금액을 빼고 계산한다.
/// </summary>
public sealed class BattlePredictionStore
{
    private readonly string m_DatabasePath;

    public BattlePredictionStore(string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    /// <summary>원장 참조 ID. 차감과 배당이 같은 참조 ID를 쓰고, 사유가 달라 따로 한 번씩만 반영된다.</summary>
    public static string ReferenceId(ulong predictionId) => "battle-prediction:" + predictionId.ToString(CultureInfo.InvariantCulture);

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
                CREATE TABLE IF NOT EXISTS battle_predictions (
                    prediction_id INTEGER PRIMARY KEY,
                    guild_id INTEGER NOT NULL,
                    challenger_user_id INTEGER NOT NULL,
                    opponent_user_id INTEGER NOT NULL,
                    challenger_name TEXT NOT NULL,
                    opponent_name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    cancel_reason TEXT NULL,
                    winning_side TEXT NULL,
                    rules_snapshot TEXT NOT NULL,
                    opened_at_utc TEXT NOT NULL,
                    locked_at_utc TEXT NULL,
                    resolved_at_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_battle_predictions_status ON battle_predictions (status);
                CREATE TABLE IF NOT EXISTS battle_prediction_entries (
                    prediction_id INTEGER NOT NULL,
                    guild_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL,
                    selected_side TEXT NOT NULL,
                    amount INTEGER NOT NULL CHECK (amount > 0),
                    payout_amount INTEGER NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    settled_at_utc TEXT NULL,
                    PRIMARY KEY (prediction_id, user_id)
                );
                CREATE INDEX IF NOT EXISTS ix_battle_prediction_entries_user ON battle_prediction_entries (guild_id, user_id);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>예측을 연다. 예측 ID는 배틀 스레드 ID다.</summary>
    public async Task OpenAsync(ulong predictionId, ulong guildId, ulong challengerUserId, ulong opponentUserId,
        string challengerName, string opponentName, PredictionRules rules, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO battle_predictions (prediction_id, guild_id, challenger_user_id, opponent_user_id, challenger_name, opponent_name,
                status, rules_snapshot, opened_at_utc)
            VALUES ($id, $guildId, $challenger, $opponent, $challengerName, $opponentName, 'open', $rules, $now);
            """;
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$challenger", checked((long)challengerUserId));
        command.Parameters.AddWithValue("$opponent", checked((long)opponentUserId));
        command.Parameters.AddWithValue("$challengerName", challengerName);
        command.Parameters.AddWithValue("$opponentName", opponentName);
        command.Parameters.AddWithValue("$rules", rules.ToJson());
        command.Parameters.AddWithValue("$now", Utc(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 버튼 한 번의 베팅. 배틀 참가자도 어느 쪽이든 걸 수 있다. 상태·진영·금액·누적 상한 판정과 기록을 한 트랜잭션에서 처리한다.
    /// 같은 진영에 다시 걸면 누적액에 더한다. 증표는 여기서 차감하지 않는다.
    /// </summary>
    public async Task<PredictionBetResult> PlaceBetAsync(ulong guildId, ulong predictionId, ulong userId, PredictionSide side, long amount,
        DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var prediction = await ReadPredictionAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
        if (prediction is null || prediction.GuildId != guildId || prediction.Status != PredictionStatus.Open)
            return new(PredictionBetStatus.Closed);
        if (!prediction.Rules.BetAmounts.Contains(amount))
            return new(PredictionBetStatus.InvalidAmount);

        var existing = await ReadEntryAsync(connection, transaction, predictionId, userId, ct).ConfigureAwait(false);
        if (existing is { } entry && entry.Side != side)
            return new(PredictionBetStatus.OtherSide, entry.Side, prediction.NameOf(entry.Side), 0, entry.Amount);

        var balance = await MollyTokenStore.GetBalanceAsync(connection, transaction, guildId, userId, ct).ConfigureAwait(false);
        var pendingElsewhere = await PendingElsewhereAsync(connection, transaction, guildId, userId, predictionId, ct).ConfigureAwait(false);
        var limit = prediction.Rules.LimitFor(balance - pendingElsewhere);
        var current = existing?.Amount ?? 0;
        var total = checked(current + amount);
        if (total > limit)
            return new(PredictionBetStatus.OverLimit, side, prediction.NameOf(side), amount, current, limit, balance);

        await using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO battle_prediction_entries (prediction_id, guild_id, user_id, selected_side, amount, created_at_utc, updated_at_utc)
                VALUES ($id, $guildId, $userId, $side, $amount, $now, $now)
                ON CONFLICT(prediction_id, user_id) DO UPDATE SET amount = amount + excluded.amount, updated_at_utc = excluded.updated_at_utc;
                """;
            write.Parameters.AddWithValue("$id", checked((long)predictionId));
            write.Parameters.AddWithValue("$guildId", checked((long)guildId));
            write.Parameters.AddWithValue("$userId", checked((long)userId));
            write.Parameters.AddWithValue("$side", SideKey(side));
            write.Parameters.AddWithValue("$amount", amount);
            write.Parameters.AddWithValue("$now", Utc(now));
            await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(PredictionBetStatus.Placed, side, prediction.NameOf(side), amount, total, limit, balance);
    }

    /// <summary>
    /// 예측을 마감한다(open → locked). 이후 베팅은 거부된다. 이미 마감·정산되었어도 진영별 누적액을 돌려준다.
    /// 예측이 없으면 null.
    /// </summary>
    public async Task<PredictionLockResult?> LockAsync(ulong predictionId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var prediction = await ReadPredictionAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
        if (prediction is null) return null;
        if (prediction.Status == PredictionStatus.Open)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE battle_predictions SET status = 'locked', locked_at_utc = $now WHERE prediction_id = $id AND status = 'open';";
            update.Parameters.AddWithValue("$id", checked((long)predictionId));
            update.Parameters.AddWithValue("$now", Utc(now));
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var entries = await ReadEntriesAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(entries.Where(x => x.Side == PredictionSide.Challenger).Sum(x => x.Amount),
            entries.Where(x => x.Side == PredictionSide.Opponent).Sum(x => x.Amount),
            entries.Count, InvalidReason(prediction.Rules, entries));
    }

    /// <summary>
    /// 배틀 결과로 정산한다. <paramref name="winner"/>가 null이면 무승부(무효).
    /// 인원 미달·한쪽 쏠림이면 무효로 처리한다. 성립하면 모든 참여자의 차감과 승리 진영의 배당을 한 트랜잭션에서 반영한다.
    /// 누군가 잔액이 모자라면 모두 되돌리고 무효로 처리한다. 이미 정산·무효 처리된 예측은 저장된 결과를 그대로 돌려준다.
    /// 예측이 없으면 null.
    /// </summary>
    public async Task<PredictionSettlement?> ResolveAsync(ulong predictionId, PredictionSide? winner, DateTimeOffset now, CancellationToken ct = default)
    {
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var prediction = await ReadPredictionAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
            if (prediction is null) return null;
            if (prediction.Status is PredictionStatus.Resolved or PredictionStatus.Cancelled)
                return await ReadSettlementAsync(connection, transaction, prediction, ct).ConfigureAwait(false);

            var entries = await ReadEntriesAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
            if ((winner is null ? PredictionCancelReason.Draw : InvalidReason(prediction.Rules, entries)) is { } reason)
            {
                await MarkCancelledAsync(connection, transaction, predictionId, reason, now, ct).ConfigureAwait(false);
                var cancelled = await ReadSettlementAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return cancelled;
            }

            var payouts = PredictionPayout.Calculate(entries, winner!.Value);
            var reference = ReferenceId(predictionId);
            var insufficient = false;
            // 배당을 먼저 반영해 승리 진영은 잔액과 관계없이 차감이 성립하게 한다.
            foreach (var (userId, payout) in payouts.Where(x => x.Value > 0))
                await MollyTokenStore.ApplyAsync(connection, transaction, prediction.GuildId,
                    new TokenChange(userId, payout, TokenReasons.PredictionPayout, reference), now, ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var bet = await MollyTokenStore.ApplyAsync(connection, transaction, prediction.GuildId,
                    new TokenChange(entry.UserId, -entry.Amount, TokenReasons.PredictionBet, reference), now, ct).ConfigureAwait(false);
                if (bet.Status == TokenChangeStatus.InsufficientBalance) { insufficient = true; break; }
            }

            if (!insufficient)
            {
                await using (var settle = connection.CreateCommand())
                {
                    settle.Transaction = transaction;
                    settle.CommandText = """
                        UPDATE battle_prediction_entries SET payout_amount = $payout, settled_at_utc = $now WHERE prediction_id = $id AND user_id = $userId;
                        """;
                    var payout = settle.Parameters.Add("$payout", SqliteType.Integer);
                    var user = settle.Parameters.Add("$userId", SqliteType.Integer);
                    settle.Parameters.AddWithValue("$now", Utc(now));
                    settle.Parameters.AddWithValue("$id", checked((long)predictionId));
                    foreach (var entry in entries)
                    {
                        payout.Value = payouts.GetValueOrDefault(entry.UserId);
                        user.Value = checked((long)entry.UserId);
                        await settle.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE battle_predictions SET status = 'resolved', winning_side = $side, resolved_at_utc = $now,
                            locked_at_utc = COALESCE(locked_at_utc, $now) WHERE prediction_id = $id;
                        """;
                    update.Parameters.AddWithValue("$side", SideKey(winner.Value));
                    update.Parameters.AddWithValue("$now", Utc(now));
                    update.Parameters.AddWithValue("$id", checked((long)predictionId));
                    await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                var resolved = await ReadSettlementAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return resolved;
            }
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        return await CancelAsync(predictionId, PredictionCancelReason.SettlementFailed, now, ct).ConfigureAwait(false);
    }

    /// <summary>예측을 무효로 처리한다. 증표는 움직이지 않는다. 이미 정산·무효 처리되었으면 저장된 결과를 돌려준다. 예측이 없으면 null.</summary>
    public async Task<PredictionSettlement?> CancelAsync(ulong predictionId, PredictionCancelReason reason, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await MarkCancelledAsync(connection, transaction, predictionId, reason, now, ct).ConfigureAwait(false);
        var settlement = await ReadSettlementAsync(connection, transaction, predictionId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return settlement;
    }

    /// <summary>봇 시작 시 정산되지 않은 예측을 모두 재시작 무효로 처리한다. 처리한 예측 수를 돌려준다.</summary>
    public async Task<int> CancelUnsettledAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE battle_predictions SET status = 'cancelled', cancel_reason = $reason, resolved_at_utc = $now
            WHERE status IN ('open', 'locked');
            """;
        command.Parameters.AddWithValue("$reason", PredictionCancelReason.Restart.ToString());
        command.Parameters.AddWithValue("$now", Utc(now));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>예측 상태. 예측이 없으면 null.</summary>
    public async Task<PredictionStatus?> GetStatusAsync(ulong predictionId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return (await ReadPredictionAsync(connection, null, predictionId, ct).ConfigureAwait(false))?.Status;
    }

    private sealed record PredictionRow(ulong PredictionId, ulong GuildId, ulong ChallengerUserId, ulong OpponentUserId,
        string ChallengerName, string OpponentName, PredictionStatus Status, PredictionCancelReason? CancelReason, PredictionSide? WinningSide, PredictionRules Rules)
    {
        public string NameOf(PredictionSide side) => side == PredictionSide.Challenger ? ChallengerName : OpponentName;
    }

    private static PredictionCancelReason? InvalidReason(PredictionRules rules, IReadOnlyList<PredictionStake> entries)
    {
        if (entries.Count < rules.MinPredictorCount) return PredictionCancelReason.TooFewPredictors;
        if (entries.All(x => x.Side == entries[0].Side)) return PredictionCancelReason.OneSided;
        return null;
    }

    private static async Task MarkCancelledAsync(SqliteConnection connection, SqliteTransaction transaction, ulong predictionId,
        PredictionCancelReason reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE battle_predictions SET status = 'cancelled', cancel_reason = $reason, resolved_at_utc = $now
            WHERE prediction_id = $id AND status IN ('open', 'locked');
            """;
        command.Parameters.AddWithValue("$reason", reason.ToString());
        command.Parameters.AddWithValue("$now", Utc(now));
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<PredictionSettlement?> ReadSettlementAsync(SqliteConnection connection, SqliteTransaction transaction, ulong predictionId, CancellationToken ct)
        => await ReadPredictionAsync(connection, transaction, predictionId, ct).ConfigureAwait(false) is { } prediction
            ? await ReadSettlementAsync(connection, transaction, prediction, ct).ConfigureAwait(false)
            : null;

    private static async Task<PredictionSettlement> ReadSettlementAsync(SqliteConnection connection, SqliteTransaction transaction, PredictionRow prediction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT user_id, selected_side, amount, payout_amount FROM battle_prediction_entries WHERE prediction_id = $id ORDER BY rowid;";
        command.Parameters.AddWithValue("$id", checked((long)prediction.PredictionId));
        var list = new List<PredictionSettledEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new((ulong)reader.GetInt64(0), ParseSide(reader.GetString(1)), reader.GetInt64(2),
                prediction.Status == PredictionStatus.Resolved && !reader.IsDBNull(3) ? reader.GetInt64(3) : null));
        return new(prediction.Status, prediction.CancelReason, prediction.WinningSide, prediction.ChallengerName, prediction.OpponentName, list);
    }

    private static async Task<PredictionRow?> ReadPredictionAsync(SqliteConnection connection, SqliteTransaction? transaction, ulong predictionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT guild_id, challenger_user_id, opponent_user_id, challenger_name, opponent_name, status, cancel_reason, winning_side, rules_snapshot
            FROM battle_predictions WHERE prediction_id = $id;
            """;
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new(predictionId, (ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            Enum.Parse<PredictionStatus>(reader.GetString(5), ignoreCase: true),
            reader.IsDBNull(6) ? null : Enum.Parse<PredictionCancelReason>(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseSide(reader.GetString(7)),
            PredictionRules.FromJson(reader.GetString(8)));
    }

    private static async Task<PredictionStake?> ReadEntryAsync(SqliteConnection connection, SqliteTransaction transaction, ulong predictionId, ulong userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT selected_side, amount, rowid FROM battle_prediction_entries WHERE prediction_id = $id AND user_id = $userId;";
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        command.Parameters.AddWithValue("$userId", checked((long)userId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new(userId, ParseSide(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    // rowid는 처음 건 순서다. 같은 진영에 추가해도(ON CONFLICT UPDATE) 바뀌지 않는다.
    private static async Task<IReadOnlyList<PredictionStake>> ReadEntriesAsync(SqliteConnection connection, SqliteTransaction transaction, ulong predictionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT user_id, selected_side, amount, rowid FROM battle_prediction_entries WHERE prediction_id = $id ORDER BY rowid;";
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        var list = new List<PredictionStake>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new((ulong)reader.GetInt64(0), ParseSide(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3)));
        return list;
    }

    private static async Task<long> PendingElsewhereAsync(SqliteConnection connection, SqliteTransaction transaction, ulong guildId, ulong userId, ulong predictionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(e.amount), 0) FROM battle_prediction_entries e
            JOIN battle_predictions p ON p.prediction_id = e.prediction_id
            WHERE e.guild_id = $guildId AND e.user_id = $userId AND e.prediction_id <> $id AND p.status IN ('open', 'locked');
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$userId", checked((long)userId));
        command.Parameters.AddWithValue("$id", checked((long)predictionId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static string SideKey(PredictionSide side) => side == PredictionSide.Challenger ? "A" : "B";

    private static PredictionSide ParseSide(string key) => key switch
    {
        "A" => PredictionSide.Challenger,
        "B" => PredictionSide.Opponent,
        _ => throw new InvalidDataException("알 수 없는 예측 진영입니다: " + key),
    };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
