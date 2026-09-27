using System.Globalization;
using Microsoft.Data.Sqlite;
using Molly.Market;

namespace Molly.KeywordMarket;

// 등락률(%)이 null이면 저장된 값이 없는 것이라 기본값(KeywordMarketRules.ChangeThreshold)을 씁니다.
public sealed record KeywordMonitorChannel(ulong GuildId, ulong ChannelId, DateTimeOffset RegisteredAtUtc, int? ChangePercent = null)
{
    public decimal Threshold => ChangePercent is { } percent ? percent / 100m : KeywordMarketRules.ChangeThreshold;
}

// 마지막 정각 수집에서 저장한 시세 전체(매진·매물 부족 포함).
public sealed record KeywordLatestPrices(DateTimeOffset CollectedAtUtc, IReadOnlyList<MarketPrice> Prices);

/// <summary>
/// 검색어 시세 모니터링 SQLite 저장소. 모니터링 하나(상자·패키지 등)의 길드별 알림 채널(길드당 1개)과 등락률,
/// 시간별 시세 이력, 아이템별 공용 상태(현재시세·누락 횟수), 길드·아이템별 과거시세를 보관합니다. 여러 모니터링이 같은 테이블을 monitor 열로 나눠 씁니다.
/// </summary>
public sealed class KeywordMarketStore
{
    private readonly string m_Monitor;
    private readonly string m_DatabasePath;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public KeywordMarketStore(string monitorId, string? databasePath = null)
    {
        MollySqlite.EnsureProvider();
        m_Monitor = monitorId;
        m_DatabasePath = databasePath ?? MollyDataPaths.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_DatabasePath)!);
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var hasGuildBaseline = await MollySqliteSchema.TableExistsAsync(connection, transaction, "keyword_market_guild_baseline", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS keyword_market_channels (
                    monitor TEXT NOT NULL,
                    guild_id INTEGER NOT NULL,
                    channel_id INTEGER NOT NULL,
                    registered_at_utc TEXT NOT NULL,
                    PRIMARY KEY (monitor, guild_id)
                );
                CREATE TABLE IF NOT EXISTS keyword_market_price_history (
                    monitor TEXT NOT NULL,
                    collected_at_utc TEXT NOT NULL,
                    kind_id INTEGER NOT NULL,
                    item_name TEXT NOT NULL,
                    category TEXT NOT NULL,
                    min_price INTEGER NOT NULL,
                    total_count INTEGER NOT NULL,
                    is_sold_out INTEGER NOT NULL,
                    priced_at_utc TEXT NOT NULL,
                    PRIMARY KEY (monitor, collected_at_utc, kind_id)
                );
                CREATE TABLE IF NOT EXISTS keyword_market_item_state (
                    monitor TEXT NOT NULL,
                    kind_id INTEGER NOT NULL,
                    item_name TEXT NOT NULL,
                    baseline_price INTEGER NOT NULL,
                    baseline_at_utc TEXT NOT NULL,
                    current_price INTEGER NOT NULL,
                    current_at_utc TEXT NOT NULL,
                    first_seen_at_utc TEXT NOT NULL,
                    missing_runs INTEGER NOT NULL,
                    PRIMARY KEY (monitor, kind_id)
                );
                CREATE TABLE IF NOT EXISTS keyword_market_guild_baseline (
                    monitor TEXT NOT NULL,
                    guild_id INTEGER NOT NULL,
                    kind_id INTEGER NOT NULL,
                    baseline_price INTEGER NOT NULL,
                    baseline_at_utc TEXT NOT NULL,
                    PRIMARY KEY (monitor, guild_id, kind_id)
                );
                """, ct).ConfigureAwait(false);
            // 등락률 열은 나중에 추가되어 이전 DB에는 없습니다. 값이 없으면(NULL) 기본값을 씁니다.
            await MollySqliteSchema.AddColumnIfMissingAsync(connection, transaction, "keyword_market_channels", "change_percent", "INTEGER", ct).ConfigureAwait(false);
            // 이전에는 과거시세를 모든 길드가 함께 썼습니다(keyword_market_item_state.baseline_price).
            // 길드별 표를 처음 만들 때 모든 모니터링의 등록 길드마다 복사해 이어갑니다.
            if (!hasGuildBaseline)
                await ExecuteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO keyword_market_guild_baseline (monitor, guild_id, kind_id, baseline_price, baseline_at_utc)
                    SELECT c.monitor, c.guild_id, s.kind_id, s.baseline_price, s.baseline_at_utc
                    FROM keyword_market_channels c JOIN keyword_market_item_state s ON s.monitor = c.monitor
                    WHERE s.baseline_price > 0;
                    """, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    // 이전에 등록된 채널을 돌려줍니다(없으면 null). 길드당 하나만 유지하므로 다른 채널에서 등록하면 옮겨집니다.
    // 등락률(%)은 null이면 저장하지 않고(NULL) 기본값을 따르게 합니다.
    public async Task<KeywordMonitorChannel?> SetChannelAsync(ulong guildId, ulong channelId, DateTimeOffset nowUtc, int? changePercent = null, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var previous = await ReadChannelAsync(connection, transaction, guildId, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO keyword_market_channels (monitor, guild_id, channel_id, registered_at_utc, change_percent) VALUES ($monitor, $guildId, $channelId, $at, $percent)
                ON CONFLICT(monitor, guild_id) DO UPDATE SET channel_id = excluded.channel_id, registered_at_utc = excluded.registered_at_utc,
                    change_percent = excluded.change_percent;
                """, ct,
                ("$guildId", checked((long)guildId)), ("$channelId", checked((long)channelId)), ("$at", Format(nowUtc)),
                ("$percent", (object?)changePercent ?? DBNull.Value)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return previous;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<KeywordMonitorChannel?> RemoveChannelAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var previous = await ReadChannelAsync(connection, transaction, guildId, ct).ConfigureAwait(false);
            // 다시 등록하면 과거시세를 새로 시작하도록 길드별 과거시세도 지웁니다.
            await ExecuteAsync(connection, transaction, """
                DELETE FROM keyword_market_channels WHERE monitor = $monitor AND guild_id = $guildId;
                DELETE FROM keyword_market_guild_baseline WHERE monitor = $monitor AND guild_id = $guildId;
                """, ct, ("$guildId", checked((long)guildId))).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return previous;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyList<KeywordMonitorChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection,
                $"SELECT {ChannelColumns} FROM keyword_market_channels WHERE monitor = $monitor ORDER BY guild_id;");
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<KeywordMonitorChannel>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(ReadChannel(reader));
            return result;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<KeywordMonitorChannel?> GetChannelAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            return await ReadChannelAsync(connection, transaction, guildId, ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    // 마지막 수집 시각. 시세 데이터가 한 번도 없으면 null입니다.
    public async Task<DateTimeOffset?> GetLatestCollectedAtAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection, "SELECT MAX(collected_at_utc) FROM keyword_market_price_history WHERE monitor = $monitor;");
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is string text ? Parse(text) : null;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<KeywordLatestPrices?> LoadLatestPricesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection, """
                SELECT collected_at_utc, kind_id, item_name, category, min_price, total_count, is_sold_out, priced_at_utc FROM keyword_market_price_history
                WHERE monitor = $monitor AND collected_at_utc = (SELECT MAX(collected_at_utc) FROM keyword_market_price_history WHERE monitor = $monitor)
                ORDER BY item_name;
                """);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            string? collectedAt = null;
            var prices = new List<MarketPrice>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                collectedAt = reader.GetString(0);
                prices.Add(new MarketPrice(reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6) != 0, Parse(reader.GetString(7))));
            }
            return collectedAt is null ? null : new KeywordLatestPrices(Parse(collectedAt), prices);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<long, KeywordItemState>> LoadStatesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection, """
                SELECT kind_id, item_name, current_price, current_at_utc, first_seen_at_utc, missing_runs
                FROM keyword_market_item_state WHERE monitor = $monitor;
                """);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<long, KeywordItemState>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result[reader.GetInt64(0)] = new KeywordItemState(reader.GetInt64(0), reader.GetString(1),
                    reader.GetInt64(2), Parse(reader.GetString(3)), Parse(reader.GetString(4)), reader.GetInt32(5));
            return result;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<long, KeywordBaseline>> LoadBaselinesAsync(ulong guildId, CancellationToken ct = default) =>
        (await LoadAllBaselinesAsync(ct).ConfigureAwait(false)).GetValueOrDefault(guildId) ?? new Dictionary<long, KeywordBaseline>();

    // 길드별 과거시세.
    public async Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<long, KeywordBaseline>>> LoadAllBaselinesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection,
                "SELECT guild_id, kind_id, baseline_price, baseline_at_utc FROM keyword_market_guild_baseline WHERE monitor = $monitor;");
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<ulong, Dictionary<long, KeywordBaseline>>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var guildId = checked((ulong)reader.GetInt64(0));
                if (!result.TryGetValue(guildId, out var baselines)) result[guildId] = baselines = new Dictionary<long, KeywordBaseline>();
                baselines[reader.GetInt64(1)] = new KeywordBaseline(reader.GetInt64(1), reader.GetInt64(2), Parse(reader.GetString(3)));
            }
            return result.ToDictionary(x => x.Key, x => (IReadOnlyDictionary<long, KeywordBaseline>)x.Value);
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>
    /// 한 회차의 원본 시세 이력과 새 상태를 한 트랜잭션으로 저장합니다. states에 없는 아이템(사라짐)은 상태에서 지우고,
    /// 보관 기간이 지난 이력을 정리합니다. 길드별 과거시세는 넘겨받은 길드만 통째로 바꾸며, 판정 중 등록이 해제된 길드는 저장하지 않습니다.
    /// </summary>
    public async Task SaveRunAsync(DateTimeOffset collectedAtUtc, IEnumerable<MarketPrice> prices, IEnumerable<KeywordItemState> states,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<long, KeywordBaseline>> guildBaselines, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var collectedAt = Format(collectedAtUtc);
            foreach (var price in prices.DistinctBy(x => x.KindId))
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT OR REPLACE INTO keyword_market_price_history
                        (monitor, collected_at_utc, kind_id, item_name, category, min_price, total_count, is_sold_out, priced_at_utc)
                    VALUES ($monitor, $collectedAt, $kindId, $name, $category, $minPrice, $totalCount, $isSoldOut, $pricedAt);
                    """, ct,
                    ("$collectedAt", collectedAt), ("$kindId", price.KindId), ("$name", price.Name), ("$category", price.Category),
                    ("$minPrice", price.MinPrice), ("$totalCount", price.TotalCount), ("$isSoldOut", price.IsSoldOut ? 1 : 0),
                    ("$pricedAt", Format(price.PricedAtUtc))).ConfigureAwait(false);
            }
            await ExecuteAsync(connection, transaction, "DELETE FROM keyword_market_item_state WHERE monitor = $monitor;", ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                // baseline_* 열은 과거시세를 길드별로 나누기 전의 열이라 현재시세를 같이 넣어 둡니다(읽지 않음).
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO keyword_market_item_state
                        (monitor, kind_id, item_name, baseline_price, baseline_at_utc, current_price, current_at_utc, first_seen_at_utc, missing_runs)
                    VALUES ($monitor, $kindId, $name, $current, $currentAt, $current, $currentAt, $firstSeen, $missing);
                    """, ct,
                    ("$kindId", state.KindId), ("$name", state.Name), ("$current", state.CurrentPrice), ("$currentAt", Format(state.CurrentAtUtc)),
                    ("$firstSeen", Format(state.FirstSeenAtUtc)), ("$missing", state.MissingRuns)).ConfigureAwait(false);
            }
            foreach (var (guildId, baselines) in guildBaselines)
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM keyword_market_guild_baseline WHERE monitor = $monitor AND guild_id = $guildId;", ct,
                    ("$guildId", checked((long)guildId))).ConfigureAwait(false);
                foreach (var baseline in baselines.Values)
                {
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO keyword_market_guild_baseline (monitor, guild_id, kind_id, baseline_price, baseline_at_utc)
                        SELECT $monitor, $guildId, $kindId, $price, $at
                        WHERE EXISTS (SELECT 1 FROM keyword_market_channels WHERE monitor = $monitor AND guild_id = $guildId);
                        """, ct,
                        ("$guildId", checked((long)guildId)), ("$kindId", baseline.KindId), ("$price", baseline.Price), ("$at", Format(baseline.AtUtc))).ConfigureAwait(false);
                }
            }
            await ExecuteAsync(connection, transaction, "DELETE FROM keyword_market_price_history WHERE monitor = $monitor AND collected_at_utc < $cutoff;", ct,
                ("$cutoff", Format(collectedAtUtc - KeywordMarketRules.HistoryRetention))).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<int> CountHistoryAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = CreateCommand(connection, "SELECT COUNT(*) FROM keyword_market_price_history WHERE monitor = $monitor;");
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally { m_Gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = m_DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    // 모든 쿼리에 이 저장소의 monitor 값을 $monitor로 넣습니다.
    private SqliteCommand CreateCommand(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$monitor", m_Monitor);
        return command;
    }

    private async Task<KeywordMonitorChannel?> ReadChannelAsync(SqliteConnection connection, SqliteTransaction transaction, ulong guildId, CancellationToken ct)
    {
        await using var command = CreateCommand(connection,
            $"SELECT {ChannelColumns} FROM keyword_market_channels WHERE monitor = $monitor AND guild_id = $guildId;", transaction);
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadChannel(reader) : null;
    }

    private const string ChannelColumns = "guild_id, channel_id, registered_at_utc, change_percent";

    private static KeywordMonitorChannel ReadChannel(SqliteDataReader reader) => new(
        checked((ulong)reader.GetInt64(0)), checked((ulong)reader.GetInt64(1)), Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetInt32(3));

    private async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // UTC ISO 8601 고정 길이 문자열이라 문자열 비교가 시간 순서와 같습니다.
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
