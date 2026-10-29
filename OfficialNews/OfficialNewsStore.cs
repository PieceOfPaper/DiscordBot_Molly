using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Molly.OfficialNews;

/// <summary>
/// 서버·알림 종류(게시판 넷과 점검)별 구독. 해제해도 행은 남기고 Enabled만 끈다.
/// Generation은 해제 뒤 재등록·채널 교체 때마다 늘어, 같은 채널로 다시 등록해도 이전 구독으로 만든 대기 작업이 살아나지 않게 한다.
/// </summary>
public sealed record NewsSubscription(ulong GuildId, string Board, ulong ChannelId, bool Enabled, DateTimeOffset UpdatedAt, long Generation = 0);

public enum NewsReceiveStatus { Created, Duplicate }

public sealed record NewsReceiveResult(NewsReceiveStatus Status, int DeliveryCount);

/// <summary>발송 worker가 가져간 작업 하나. Board는 알림 종류, ChannelId·Generation은 작업을 만들 때의 구독이다.</summary>
public sealed record NewsDelivery(long Id, ulong GuildId, string Board, ulong ChannelId, int Attempts, NewsEvent Event, long Generation = 0);

/// <summary>한 번의 목록 확인 결과 저장. MaintenanceEvents는 이번에 새로 만든 점검 알림 이벤트다.</summary>
public sealed record NewsCheckRecord(int Deliveries, IReadOnlyList<NewsEvent> MaintenanceEvents);

public sealed record NewsDeliverySummary(int Waiting, int Failed, DateTimeOffset? LastSentAt);

/// <summary>
/// 게시판 수집 상태. FloorPostId는 처음 기준을 잡을 때 목록에 있던 가장 작은 글 ID로, 이보다 작은 글은 목록 위로 다시 올라와도 새 글로 보지 않는다.
/// LastError는 마지막 확인이 실패했을 때만 남는다.
/// </summary>
public sealed record NewsBoardState(string Board, long FloorPostId, DateTimeOffset BaselinedAt, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessAt, string? LastError);

public static class NewsDeliveryStatus
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>
/// 공식 소식 구독(official_news_subscriptions)·발견한 글과 점검 변화(official_news_events)·발송 작업(official_news_deliveries)·
/// 게시판 수집 상태(official_news_board_states)·이미 본 글(official_news_seen_posts)·공지 글의 제목·말머리 비교 기준(official_news_post_states).
/// 새 글·점검 변화 기록, 그 순간 활성 구독의 발송 작업 생성, 본 글 표시, 비교 기준 갱신은 한 트랜잭션이고,
/// event_id·(event_id, guild_id) 고유 제약으로 같은 이벤트를 다시 보내지 않는다.
/// 구독·발송 작업의 board 열은 알림 종류다(게시판 ID 넷과 점검 `maintenance`). 점검 이벤트의 board는 원본 게시판(notice)이다.
/// </summary>
public sealed class OfficialNewsStore
{
    private readonly string m_DatabasePath;

    public OfficialNewsStore(string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS official_news_subscriptions (
                    guild_id INTEGER NOT NULL,
                    board TEXT NOT NULL,
                    channel_id INTEGER NOT NULL,
                    enabled INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (guild_id, board)
                );
                CREATE TABLE IF NOT EXISTS official_news_events (
                    event_id TEXT PRIMARY KEY,
                    board TEXT NOT NULL,
                    post_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    url TEXT NOT NULL,
                    category TEXT NULL,
                    thumbnail_url TEXT NULL,
                    detected_at TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS official_news_deliveries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL REFERENCES official_news_events(event_id),
                    guild_id INTEGER NOT NULL,
                    board TEXT NOT NULL,
                    channel_id INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    lease_until_utc TEXT NULL,
                    sent_message_id INTEGER NULL,
                    last_error_code TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    UNIQUE (event_id, guild_id)
                );
                CREATE INDEX IF NOT EXISTS ix_official_news_deliveries_due ON official_news_deliveries (status, next_attempt_at_utc);
                CREATE INDEX IF NOT EXISTS ix_official_news_deliveries_guild ON official_news_deliveries (guild_id, board, status);
                CREATE TABLE IF NOT EXISTS official_news_board_states (
                    board TEXT PRIMARY KEY,
                    floor_post_id INTEGER NOT NULL,
                    baselined_at_utc TEXT NOT NULL,
                    last_checked_at_utc TEXT NULL,
                    last_success_at_utc TEXT NULL,
                    last_error TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS official_news_seen_posts (
                    board TEXT NOT NULL,
                    post_id INTEGER NOT NULL,
                    seen_at_utc TEXT NOT NULL,
                    PRIMARY KEY (board, post_id)
                );
                CREATE TABLE IF NOT EXISTS official_news_post_states (
                    board TEXT NOT NULL,
                    post_id INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    category TEXT NULL,
                    maintenance INTEGER NOT NULL,
                    revision INTEGER NOT NULL,
                    first_seen_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL,
                    PRIMARY KEY (board, post_id)
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        // 점검 알림 도입 전 DB: 기존 행은 그대로 두고 빈 열만 더한다. 기존 구독·작업의 세대는 0으로 서로 맞는다.
        await AddColumnIfMissingAsync(connection, transaction, "official_news_subscriptions", "generation", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "official_news_deliveries", "subscription_generation", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "official_news_events", "kind", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "official_news_events", "previous_title", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "official_news_events", "previous_category", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "official_news_events", "revision", "INTEGER NULL", ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition, CancellationToken ct)
    {
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column;";
            info.Parameters.AddWithValue("$column", column);
            if (await info.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 점검 알림 분리 전 공지 구독으로 만든 점검 글 작업 중 아직 보내지 않은 것을 skipped(`maintenance_split`)로 바꾼다.
    /// 근거는 새 글을 기록할 때 저장한 말머리가 `점검`인 것뿐이다. 말머리가 없거나 다른 글은 일반 공지로 두고, 보낸 기록은 바꾸지 않는다.
    /// 점검 구독으로 옮기거나 소급하지 않는다. 시작할 때 worker보다 먼저 호출하며 다시 호출해도 같은 결과다.
    /// </summary>
    public async Task<int> SkipLegacyMaintenanceNoticeDeliveriesAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE official_news_deliveries SET status = 'skipped', lease_until_utc = NULL, last_error_code = 'maintenance_split', updated_at_utc = $now
            WHERE board = 'notice' AND status IN ('pending', 'sending')
              AND event_id IN (SELECT event_id FROM official_news_events WHERE board = 'notice' AND kind IS NULL AND category = '점검');
            """;
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── 구독 ──────────────────────────────────────────────

    /// <summary>서버·게시판의 수신 채널을 지정(교체)하고 이전 설정을 돌려준다.</summary>
    public async Task<NewsSubscription?> SetSubscriptionAsync(ulong guildId, string board, ulong channelId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var previous = await ReadSubscriptionAsync(connection, transaction, guildId, board, ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO official_news_subscriptions (guild_id, board, channel_id, enabled, created_at_utc, updated_at_utc)
                VALUES ($guildId, $board, $channelId, 1, $now, $now)
                ON CONFLICT(guild_id, board) DO UPDATE SET
                    generation = CASE WHEN enabled = 1 AND channel_id = excluded.channel_id THEN generation ELSE generation + 1 END,
                    channel_id = excluded.channel_id, enabled = 1, updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$board", board);
            command.Parameters.AddWithValue("$channelId", checked((long)channelId));
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return previous;
    }

    /// <summary>구독을 끄고 해제 전 설정을 돌려준다. 켜진 구독이 없었으면 null.</summary>
    public async Task<NewsSubscription?> DisableSubscriptionAsync(ulong guildId, string board, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var previous = await ReadSubscriptionAsync(connection, transaction, guildId, board, ct).ConfigureAwait(false);
        if (previous is not { Enabled: true }) return null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE official_news_subscriptions SET enabled = 0, updated_at_utc = $now WHERE guild_id = $guildId AND board = $board;";
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$board", board);
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return previous;
    }

    public async Task<NewsSubscription?> GetSubscriptionAsync(ulong guildId, string board, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadSubscriptionAsync(connection, null, guildId, board, ct).ConfigureAwait(false);
    }

    /// <summary>발송 직전 확인: 작업을 만든 구독(채널·세대)이 지금도 켜진 구독인지. 해제 후 같은 채널로 재등록했으면 세대가 달라 false.</summary>
    public async Task<bool> IsSubscriptionCurrentAsync(ulong guildId, string board, ulong channelId, long generation = 0, CancellationToken ct = default)
    {
        var subscription = await GetSubscriptionAsync(guildId, board, ct).ConfigureAwait(false);
        return subscription is { Enabled: true } && subscription.ChannelId == channelId && subscription.Generation == generation;
    }

    // ── 수집 상태 ─────────────────────────────────────────

    public async Task<NewsBoardState?> GetBoardStateAsync(string board, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT floor_post_id, baselined_at_utc, last_checked_at_utc, last_success_at_utc, last_error FROM official_news_board_states WHERE board = $board;";
        command.Parameters.AddWithValue("$board", board);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new NewsBoardState(board, reader.GetInt64(0), Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// <summary>처음 확인한 목록의 글을 본 것으로만 기록하고 발송 작업은 만들지 않는다. 이미 기준이 있으면 아무것도 바꾸지 않고 false.</summary>
    public Task<bool> BaselineBoardAsync(string board, IReadOnlyCollection<long> postIds, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        BaselineBoardAsync(board, postIds, [], nowUtc, ct);

    /// <summary>첫 확인 기준과 함께 글별 제목·말머리 비교 기준(trackedCards)을 같은 트랜잭션에 만든다. 점검 알림도 만들지 않는다.</summary>
    public async Task<bool> BaselineBoardAsync(string board, IReadOnlyCollection<long> postIds, IReadOnlyList<NewsListCard> trackedCards, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        if (postIds.Count == 0) throw new ArgumentException("기준으로 삼을 글이 없습니다.", nameof(postIds));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO official_news_board_states (board, floor_post_id, baselined_at_utc, last_checked_at_utc, last_success_at_utc)
                VALUES ($board, $floor, $now, $now, $now) ON CONFLICT(board) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$board", board);
            insert.Parameters.AddWithValue("$floor", postIds.Min());
            insert.Parameters.AddWithValue("$now", Format(nowUtc));
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }
        await MarkSeenAsync(connection, transaction, board, postIds, nowUtc, ct).ConfigureAwait(false);
        foreach (var card in trackedCards)
        {
            var result = MaintenanceRules.Evaluate(null, card, isNew: false);
            await WritePostStateAsync(connection, transaction, board, result.Next, nowUtc, ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>주어진 글 중 이미 본 글 ID.</summary>
    public async Task<IReadOnlySet<long>> GetSeenPostIdsAsync(string board, IEnumerable<long> postIds, CancellationToken ct = default)
    {
        var seen = new HashSet<long>();
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        foreach (var postId in postIds)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM official_news_seen_posts WHERE board = $board AND post_id = $postId;";
            command.Parameters.AddWithValue("$board", board);
            command.Parameters.AddWithValue("$postId", postId);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) seen.Add(postId);
        }
        return seen;
    }

    /// <summary>
    /// 새로 발견한 글들을 기록하고 각 글마다 그 순간 켜진 구독의 발송 작업을 만들며, 본 글로 표시하고 확인 성공을 남긴다. 모두 한 트랜잭션이다.
    /// 만든 발송 작업 수를 돌려준다.
    /// </summary>
    public async Task<int> RecordDiscoveredAsync(string board, IReadOnlyList<NewsEvent> discovered, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        (await RecordCheckAsync(board, discovered, discovered.Select(n => long.Parse(n.PostId, CultureInfo.InvariantCulture)).ToList(), [], nowUtc, ct).ConfigureAwait(false)).Deliveries;

    /// <summary>
    /// 한 번의 목록 확인 결과를 한 트랜잭션으로 저장한다.
    /// <list type="number">
    /// <item>새 글 알림(discovered)을 기록하고 그 게시판 구독의 발송 작업을 만든다.</item>
    /// <item>newPostIds(새 글 판정을 통과한 모든 글, 점검 글 포함)를 본 글로 표시한다.</item>
    /// <item>observedCards(이번에 목록에서 본 글, 중복 없음)를 저장된 제목·말머리와 비교해 비교 기준을 갱신하고, 점검 변화는 점검 구독의 발송 작업으로 만든다.</item>
    /// <item>확인 성공을 남긴다. 구독이 없어도 비교 기준은 갱신한다.</item>
    /// </list>
    /// 중간에 실패하면 모두 되돌려 마지막 정상 기준을 유지한다.
    /// </summary>
    public async Task<NewsCheckRecord> RecordCheckAsync(string board, IReadOnlyList<NewsEvent> discovered, IReadOnlyCollection<long> newPostIds,
        IReadOnlyList<NewsListCard> observedCards, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var deliveries = 0;
        foreach (var news in discovered)
        {
            if (news.Board != board) throw new ArgumentException("다른 게시판의 글이 섞였습니다.", nameof(discovered));
            deliveries += (await ReceiveCoreAsync(connection, transaction, news, board, nowUtc, ct).ConfigureAwait(false)).DeliveryCount;
        }

        var maintenanceEvents = new List<NewsEvent>();
        var newIds = newPostIds.ToHashSet();
        foreach (var card in observedCards.DistinctBy(c => c.PostId))
        {
            var previous = await ReadPostStateAsync(connection, transaction, board, card.PostId, ct).ConfigureAwait(false);
            var result = MaintenanceRules.Evaluate(previous, card, newIds.Contains(card.PostId));
            if (result.Changed)
                await WritePostStateAsync(connection, transaction, board, result.Next, nowUtc, ct).ConfigureAwait(false);
            else
                await TouchPostStateAsync(connection, transaction, board, card.PostId, nowUtc, ct).ConfigureAwait(false);
            if (result.Kind is null) continue;
            var change = MaintenanceRules.ToEvent(previous, result, card, nowUtc);
            var received = await ReceiveCoreAsync(connection, transaction, change, NewsBoards.Maintenance.Id, nowUtc, ct).ConfigureAwait(false);
            if (received.Status == NewsReceiveStatus.Created) maintenanceEvents.Add(change);
            deliveries += received.DeliveryCount;
        }

        await MarkSeenAsync(connection, transaction, board, newIds, nowUtc, ct).ConfigureAwait(false);
        await UpdateCheckAsync(connection, transaction, board, nowUtc, null, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new NewsCheckRecord(deliveries, maintenanceEvents);
    }

    /// <summary>글의 제목·말머리 비교 기준. 없으면 null.</summary>
    public async Task<NewsPostState?> GetPostStateAsync(string board, long postId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadPostStateAsync(connection, null, board, postId, ct).ConfigureAwait(false);
    }

    private static async Task<NewsPostState?> ReadPostStateAsync(SqliteConnection connection, SqliteTransaction? transaction, string board, long postId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT title, category, maintenance, revision FROM official_news_post_states WHERE board = $board AND post_id = $postId;";
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$postId", postId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new NewsPostState(postId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt32(3));
    }

    private static async Task WritePostStateAsync(SqliteConnection connection, SqliteTransaction transaction, string board, NewsPostState state, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO official_news_post_states (board, post_id, title, category, maintenance, revision, first_seen_at_utc, last_seen_at_utc)
            VALUES ($board, $postId, $title, $category, $maintenance, $revision, $now, $now)
            ON CONFLICT(board, post_id) DO UPDATE SET title = excluded.title, category = excluded.category,
                maintenance = MAX(maintenance, excluded.maintenance), revision = excluded.revision, last_seen_at_utc = excluded.last_seen_at_utc;
            """;
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$postId", state.PostId);
        command.Parameters.AddWithValue("$title", state.Title);
        command.Parameters.AddWithValue("$category", (object?)state.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$maintenance", state.Maintenance ? 1 : 0);
        command.Parameters.AddWithValue("$revision", state.Revision);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task TouchPostStateAsync(SqliteConnection connection, SqliteTransaction transaction, string board, long postId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE official_news_post_states SET last_seen_at_utc = $now WHERE board = $board AND post_id = $postId;";
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$postId", postId);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>확인 실패를 남긴다. 기준·본 글은 바꾸지 않으므로 다음 확인에서 놓친 글을 다시 찾는다.</summary>
    public async Task RecordCheckFailedAsync(string board, string error, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await UpdateCheckAsync(connection, null, board, nowUtc, error, ct).ConfigureAwait(false);
    }

    private static async Task MarkSeenAsync(SqliteConnection connection, SqliteTransaction transaction, string board, IReadOnlyCollection<long> postIds, DateTimeOffset nowUtc, CancellationToken ct)
    {
        foreach (var postId in postIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO official_news_seen_posts (board, post_id, seen_at_utc) VALUES ($board, $postId, $now) ON CONFLICT(board, post_id) DO NOTHING;";
            command.Parameters.AddWithValue("$board", board);
            command.Parameters.AddWithValue("$postId", postId);
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task UpdateCheckAsync(SqliteConnection connection, SqliteTransaction? transaction, string board, DateTimeOffset nowUtc, string? error, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE official_news_board_states SET last_checked_at_utc = $now, last_error = $error,
                last_success_at_utc = CASE WHEN $error IS NULL THEN $now ELSE last_success_at_utc END
            WHERE board = $board;
            """;
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── 새 글 ─────────────────────────────────────────────

    /// <summary>
    /// 글을 저장하고 그 순간 켜진 해당 게시판 구독마다 발송 작업을 만든다. 이미 기록한 eventId면 최초 내용을 그대로 두고 작업을 만들지 않는다.
    /// </summary>
    public async Task<NewsReceiveResult> ReceiveAsync(NewsEvent news, DateTimeOffset receivedAtUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await ReceiveCoreAsync(connection, transaction, news, news.Kind is null ? news.Board : NewsBoards.Maintenance.Id, receivedAtUtc, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <param name="alert">발송 작업을 만들 구독의 알림 종류. 새 글은 그 게시판, 점검 변화는 `maintenance`다.</param>
    private static async Task<NewsReceiveResult> ReceiveCoreAsync(SqliteConnection connection, SqliteTransaction transaction, NewsEvent news, string alert,
        DateTimeOffset receivedAtUtc, CancellationToken ct)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO official_news_events (event_id, board, post_id, title, url, category, thumbnail_url, detected_at, received_at_utc,
                    kind, previous_title, previous_category, revision)
                VALUES ($eventId, $board, $postId, $title, $url, $category, $thumbnailUrl, $detectedAt, $receivedAt,
                    $kind, $previousTitle, $previousCategory, $revision)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$eventId", news.EventId);
            insert.Parameters.AddWithValue("$board", news.Board);
            insert.Parameters.AddWithValue("$postId", news.PostId);
            insert.Parameters.AddWithValue("$title", news.Title);
            insert.Parameters.AddWithValue("$url", news.Url);
            insert.Parameters.AddWithValue("$category", (object?)news.Category ?? DBNull.Value);
            insert.Parameters.AddWithValue("$thumbnailUrl", (object?)news.ThumbnailUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("$detectedAt", news.DetectedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$receivedAt", Format(receivedAtUtc));
            insert.Parameters.AddWithValue("$kind", (object?)news.Kind ?? DBNull.Value);
            insert.Parameters.AddWithValue("$previousTitle", (object?)news.PreviousTitle ?? DBNull.Value);
            insert.Parameters.AddWithValue("$previousCategory", (object?)news.PreviousCategory ?? DBNull.Value);
            insert.Parameters.AddWithValue("$revision", (object?)news.Revision ?? DBNull.Value);
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                return new(NewsReceiveStatus.Duplicate, 0);
        }

        int deliveries;
        await using (var fanOut = connection.CreateCommand())
        {
            fanOut.Transaction = transaction;
            fanOut.CommandText = """
                INSERT INTO official_news_deliveries (event_id, guild_id, board, channel_id, subscription_generation, status, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
                SELECT $eventId, guild_id, board, channel_id, generation, 'pending', 0, $now, $now, $now
                FROM official_news_subscriptions WHERE board = $alert AND enabled = 1
                ON CONFLICT(event_id, guild_id) DO NOTHING;
                """;
            fanOut.Parameters.AddWithValue("$eventId", news.EventId);
            fanOut.Parameters.AddWithValue("$alert", alert);
            fanOut.Parameters.AddWithValue("$now", Format(receivedAtUtc));
            deliveries = await fanOut.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return new(NewsReceiveStatus.Created, deliveries);
    }

    public async Task<NewsEvent?> GetEventAsync(string eventId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {EventColumns} FROM official_news_events WHERE event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEvent(reader, 0) : null;
    }

    // ── 발송 작업 ─────────────────────────────────────────

    /// <summary>
    /// 이전 실행이 처리 중(sending)으로 남긴 작업을 다시 대기로 돌린다. DB 하나를 봇 프로세스 하나가 쓰는 전제에서 시작 시 한 번 호출한다.
    /// Discord 전송 직후 상태 기록 전에 종료된 작업은 이때 다시 보내므로 메시지가 한 번 더 나갈 수 있다(완전한 exactly-once 아님).
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE official_news_deliveries SET status = 'pending', lease_until_utc = NULL, next_attempt_at_utc = $now, updated_at_utc = $now WHERE status = 'sending';";
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>보낼 때가 된 작업과 lease가 끝난 처리 중 작업을 원자적으로 가져가 sending으로 바꾼다.</summary>
    public async Task<IReadOnlyList<NewsDelivery>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var claimed = new List<NewsDelivery>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT e.event_id, e.board, e.post_id, e.title, e.url, e.category, e.thumbnail_url, e.detected_at,
                       e.kind, e.previous_title, e.previous_category, e.revision,
                       d.id, d.guild_id, d.board, d.channel_id, d.attempts, d.subscription_generation
                FROM official_news_deliveries d JOIN official_news_events e ON e.event_id = d.event_id
                WHERE (d.status = 'pending' AND d.next_attempt_at_utc <= $now)
                   OR (d.status = 'sending' AND d.lease_until_utc <= $now)
                ORDER BY d.id LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$now", Format(nowUtc));
            select.Parameters.AddWithValue("$limit", limit);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                const int d = EventColumnCount;
                claimed.Add(new NewsDelivery(reader.GetInt64(d), checked((ulong)reader.GetInt64(d + 1)), reader.GetString(d + 2),
                    checked((ulong)reader.GetInt64(d + 3)), reader.GetInt32(d + 4), ReadEvent(reader, 0), reader.GetInt64(d + 5)));
            }
        }
        foreach (var delivery in claimed)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE official_news_deliveries SET status = 'sending', lease_until_utc = $lease, updated_at_utc = $now WHERE id = $id;";
            update.Parameters.AddWithValue("$id", delivery.Id);
            update.Parameters.AddWithValue("$lease", Format(nowUtc + lease));
            update.Parameters.AddWithValue("$now", Format(nowUtc));
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    public Task MarkSentAsync(long id, ulong messageId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        UpdateAsync(id, NewsDeliveryStatus.Sent, 1, nowUtc, nowUtc, null, checked((long)messageId), ct);

    /// <summary>구독이 해제·교체돼 보내지 않은 작업. 시도 횟수는 늘리지 않는다.</summary>
    public Task MarkSkippedAsync(long id, string reason, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        UpdateAsync(id, NewsDeliveryStatus.Skipped, 0, nowUtc, nowUtc, reason, null, ct);

    public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptUtc, string errorCode, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        UpdateAsync(id, NewsDeliveryStatus.Pending, 1, nextAttemptUtc, nowUtc, errorCode, null, ct);

    /// <summary>보내지 못한 채 대기로 돌린다(Discord 연결 끊김 등). 시도 횟수는 늘리지 않는다.</summary>
    public Task ReleaseAsync(long id, string reason, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        UpdateAsync(id, NewsDeliveryStatus.Pending, 0, nowUtc, nowUtc, reason, null, ct);

    /// <summary>영구 실패(채널 삭제·권한 상실·재시도 한도 초과). 다시 시도하지 않는다.</summary>
    public Task MarkFailedAsync(long id, string errorCode, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        UpdateAsync(id, NewsDeliveryStatus.Failed, 1, nowUtc, nowUtc, errorCode, null, ct);

    /// <summary>/…알림 작업:확인에서 보여줄 서버·게시판의 발송 현황.</summary>
    public async Task<NewsDeliverySummary> GetDeliverySummaryAsync(ulong guildId, string board, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status IN ('pending', 'sending') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0),
                MAX(CASE WHEN status = 'sent' THEN updated_at_utc END)
            FROM official_news_deliveries WHERE guild_id = $guildId AND board = $board;
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$board", board);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new NewsDeliverySummary(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : Parse(reader.GetString(2)));
    }

    /// <summary>
    /// /몰리상태용: 서버·게시판의 지정 채널로 보낸 마지막 성공 시각과 마지막 실패(영구 실패 또는 재시도 대기 중인 실패)의 시각·오류 코드.
    /// 발송 작업 표를 그대로 읽으며 따로 저장하지 않는다. 실패 시각은 작업을 마지막으로 갱신한 시각이다.
    /// </summary>
    public async Task<(DateTimeOffset? LastSentAt, DateTimeOffset? LastFailureAt, string? LastErrorCode)> GetDeliveryHealthAsync(
        ulong guildId, string board, ulong channelId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT MAX(updated_at_utc) FROM official_news_deliveries
                 WHERE guild_id = $guildId AND board = $board AND channel_id = $channelId AND status = 'sent'),
                f.updated_at_utc, f.last_error_code
            FROM (SELECT 1) LEFT JOIN (
                SELECT updated_at_utc, last_error_code FROM official_news_deliveries
                WHERE guild_id = $guildId AND board = $board AND channel_id = $channelId
                  AND (status = 'failed' OR (status IN ('pending', 'sending') AND attempts > 0 AND last_error_code IS NOT NULL AND last_error_code <> 'discord_not_ready'))
                ORDER BY updated_at_utc DESC LIMIT 1) f;
            """;
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$channelId", checked((long)channelId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.IsDBNull(0) ? null : Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>테스트·운영 확인용: 이벤트의 서버별 발송 상태.</summary>
    public async Task<IReadOnlyList<(ulong GuildId, ulong ChannelId, string Status, int Attempts, ulong? MessageId, string? ErrorCode)>> GetDeliveriesAsync(
        string eventId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT guild_id, channel_id, status, attempts, sent_message_id, last_error_code FROM official_news_deliveries WHERE event_id = $eventId ORDER BY guild_id;";
        command.Parameters.AddWithValue("$eventId", eventId);
        var list = new List<(ulong, ulong, string, int, ulong?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add((checked((ulong)reader.GetInt64(0)), checked((ulong)reader.GetInt64(1)), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : checked((ulong)reader.GetInt64(4)), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return list;
    }

    private async Task UpdateAsync(long id, string status, int attemptIncrement, DateTimeOffset nextAttemptUtc, DateTimeOffset nowUtc,
        string? errorCode, long? messageId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 처리 중(sending)인 작업만 결과를 기록한다. 다른 상태로 이미 바뀐 작업을 덮어쓰지 않는다.
        command.CommandText = """
            UPDATE official_news_deliveries SET status = $status, attempts = attempts + $increment, lease_until_utc = NULL,
                next_attempt_at_utc = $next, last_error_code = $error, sent_message_id = COALESCE($messageId, sent_message_id), updated_at_utc = $now
            WHERE id = $id AND status = 'sending';
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$increment", attemptIncrement);
        command.Parameters.AddWithValue("$next", Format(nextAttemptUtc));
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<NewsSubscription?> ReadSubscriptionAsync(SqliteConnection connection, SqliteTransaction? transaction, ulong guildId, string board, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT channel_id, enabled, updated_at_utc, generation FROM official_news_subscriptions WHERE guild_id = $guildId AND board = $board;";
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        command.Parameters.AddWithValue("$board", board);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new NewsSubscription(guildId, board, checked((ulong)reader.GetInt64(0)), reader.GetInt64(1) != 0, Parse(reader.GetString(2)), reader.GetInt64(3));
    }

    private const string EventColumns = "event_id, board, post_id, title, url, category, thumbnail_url, detected_at, kind, previous_title, previous_category, revision";
    private const int EventColumnCount = 12;

    private static NewsEvent ReadEvent(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5), reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
        Parse(reader.GetString(offset + 7)),
        reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8), reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
        reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10), reader.IsDBNull(offset + 11) ? null : reader.GetInt32(offset + 11));

    // 모든 UTC 시각을 같은 왕복 형식으로 저장해 문자열 비교가 시간 순서와 같게 한다.
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // 수집기와 발송 worker·구독 명령이 동시에 쓰기 트랜잭션을 열 수 있어 잠금을 잠시 기다린다.
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
