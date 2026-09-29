using System.Globalization;
using Microsoft.Data.Sqlite;
using Molly.Market;

namespace Molly.HaeyeonMarket;

// 등락률(%)이 null이면 저장된 값이 없는 것이라 기본값(HaeyeonMarketRules)을 씁니다.
public sealed record HaeyeonMonitorChannel(ulong GuildId, ulong ChannelId, DateTimeOffset RegisteredAtUtc, int? ProductChangePercent = null, int? MaterialChangePercent = null)
{
    public HaeyeonThresholds Thresholds => new(
        ProductChangePercent is { } product ? product / 100m : HaeyeonMarketRules.ProductChangeThreshold,
        MaterialChangePercent is { } material ? material / 100m : HaeyeonMarketRules.MaterialChangeThreshold);
}

// 한 번의 정각 수집에서 저장할 원본 시세. IsProduct는 제작 아이템 여부입니다.
public sealed record HaeyeonPriceSnapshot(MarketPrice Price, bool IsProduct);

// 마지막 정각 수집에서 저장한 시세 전체(매진·매물 부족 포함).
public sealed record HaeyeonLatestPrices(DateTimeOffset CollectedAtUtc, IReadOnlyDictionary<string, MarketPrice> Prices);

/// <summary>
/// /해연시세모니터링 SQLite 저장소. 길드별 알림 채널(길드당 1개)과 등락률, 마지막 정각 수집의 시세,
/// 길드·아이템별 과거시세·현재시세, 제작 아이템별 유불리 상태를 보관합니다.
/// 시세 이력은 모비라이프가 보관하므로 몰리는 마지막 성공 회차의 시세만 남깁니다(회차마다 통째로 교체).
/// </summary>
public sealed class HaeyeonMarketStore
{
    private readonly string m_DatabasePath;
    private readonly SemaphoreSlim m_Gate = new(1, 1);

    public HaeyeonMarketStore(string? databasePath = null)
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
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var hasGuildPriceState = await MollySqliteSchema.TableExistsAsync(connection, transaction, "haeyeon_guild_price_state", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS haeyeon_monitor_channels (
                    guild_id INTEGER PRIMARY KEY,
                    channel_id INTEGER NOT NULL,
                    registered_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS haeyeon_latest_prices (
                    item_name TEXT PRIMARY KEY,
                    kind_id INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    is_product INTEGER NOT NULL,
                    min_price INTEGER NOT NULL,
                    total_count INTEGER NOT NULL,
                    is_sold_out INTEGER NOT NULL,
                    priced_at_utc TEXT NOT NULL,
                    price_change_1h_pct REAL,
                    price_change_24h_pct REAL,
                    price_change_7d_pct REAL,
                    count_change_24h REAL,
                    collected_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS haeyeon_guild_price_state (
                    guild_id INTEGER NOT NULL,
                    item_name TEXT NOT NULL,
                    is_product INTEGER NOT NULL,
                    baseline_price INTEGER NOT NULL,
                    baseline_at_utc TEXT NOT NULL,
                    current_price INTEGER NOT NULL,
                    current_at_utc TEXT NOT NULL,
                    PRIMARY KEY (guild_id, item_name)
                );
                CREATE TABLE IF NOT EXISTS haeyeon_craft_state (
                    product_name TEXT PRIMARY KEY,
                    advantage TEXT NOT NULL,
                    product_price INTEGER NOT NULL,
                    material_cost INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """, ct).ConfigureAwait(false);
            // 등락률 열은 나중에 추가되어 이전 DB에는 없습니다. 값이 없으면(NULL) 기본값을 씁니다.
            await MollySqliteSchema.AddColumnIfMissingAsync(connection, transaction, "haeyeon_monitor_channels", "product_change_percent", "INTEGER", ct).ConfigureAwait(false);
            await MollySqliteSchema.AddColumnIfMissingAsync(connection, transaction, "haeyeon_monitor_channels", "material_change_percent", "INTEGER", ct).ConfigureAwait(false);
            // 이전에는 과거시세를 모든 길드가 함께 썼습니다(haeyeon_price_state). 길드별 표를 처음 만들 때 등록된 길드마다 복사해 이어갑니다.
            if (!hasGuildPriceState && await MollySqliteSchema.TableExistsAsync(connection, transaction, "haeyeon_price_state", ct).ConfigureAwait(false))
                await ExecuteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO haeyeon_guild_price_state
                        (guild_id, item_name, is_product, baseline_price, baseline_at_utc, current_price, current_at_utc)
                    SELECT c.guild_id, s.item_name, s.is_product, s.baseline_price, s.baseline_at_utc, s.current_price, s.current_at_utc
                    FROM haeyeon_monitor_channels c CROSS JOIN haeyeon_price_state s;
                    """, ct).ConfigureAwait(false);
            // 이전에는 정각마다 시세를 90일간 쌓았습니다(haeyeon_price_history). 마지막 회차만 옮기고 이력 표는 지웁니다.
            if (await MollySqliteSchema.TableExistsAsync(connection, transaction, "haeyeon_price_history", ct).ConfigureAwait(false))
                await ExecuteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO haeyeon_latest_prices
                        (item_name, kind_id, category, is_product, min_price, total_count, is_sold_out, priced_at_utc, collected_at_utc)
                    SELECT item_name, kind_id, '', is_product, min_price, total_count, is_sold_out, priced_at_utc, collected_at_utc
                    FROM haeyeon_price_history WHERE collected_at_utc = (SELECT MAX(collected_at_utc) FROM haeyeon_price_history);
                    DROP TABLE haeyeon_price_history;
                    """, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    // 이전에 등록된 채널을 돌려줍니다(없으면 null). 길드당 하나만 유지하므로 다른 채널에서 등록하면 옮겨집니다.
    // 등락률(%)은 null이면 저장하지 않고(NULL) 기본값을 따르게 합니다.
    public async Task<HaeyeonMonitorChannel?> SetChannelAsync(ulong guildId, ulong channelId, DateTimeOffset nowUtc,
        int? productChangePercent = null, int? materialChangePercent = null, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var previous = await ReadChannelAsync(connection, transaction, guildId, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO haeyeon_monitor_channels (guild_id, channel_id, registered_at_utc, product_change_percent, material_change_percent)
                VALUES ($guildId, $channelId, $at, $product, $material)
                ON CONFLICT(guild_id) DO UPDATE SET channel_id = excluded.channel_id, registered_at_utc = excluded.registered_at_utc,
                    product_change_percent = excluded.product_change_percent, material_change_percent = excluded.material_change_percent;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$channelId", checked((long)channelId));
            command.Parameters.AddWithValue("$at", Format(nowUtc));
            command.Parameters.AddWithValue("$product", (object?)productChangePercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$material", (object?)materialChangePercent ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return previous;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<HaeyeonMonitorChannel?> RemoveChannelAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var previous = await ReadChannelAsync(connection, transaction, guildId, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // 다시 등록하면 과거시세를 새로 시작하도록 길드별 과거시세도 지웁니다.
            command.CommandText = "DELETE FROM haeyeon_monitor_channels WHERE guild_id = $guildId; DELETE FROM haeyeon_guild_price_state WHERE guild_id = $guildId;";
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return previous;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyList<HaeyeonMonitorChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {ChannelColumns} FROM haeyeon_monitor_channels ORDER BY guild_id;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<HaeyeonMonitorChannel>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(ReadChannel(reader));
            return result;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<HaeyeonMonitorChannel?> GetChannelAsync(ulong guildId, CancellationToken ct = default)
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
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(collected_at_utc) FROM haeyeon_latest_prices;";
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is string text ? Parse(text) : null;
        }
        finally { m_Gate.Release(); }
    }

    public async Task<HaeyeonLatestPrices?> LoadLatestPricesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT collected_at_utc, item_name, kind_id, category, min_price, total_count, is_sold_out, priced_at_utc,
                    price_change_1h_pct, price_change_24h_pct, price_change_7d_pct, count_change_24h
                FROM haeyeon_latest_prices;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            string? collectedAt = null;
            var prices = new Dictionary<string, MarketPrice>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                collectedAt = reader.GetString(0);
                prices[reader.GetString(1)] = new MarketPrice(reader.GetInt64(2), reader.GetString(1), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6) != 0, Parse(reader.GetString(7)),
                    NullableDecimal(reader, 8), NullableDecimal(reader, 9), NullableDecimal(reader, 10), NullableDecimal(reader, 11));
            }
            return collectedAt is null ? null : new HaeyeonLatestPrices(Parse(collectedAt), prices);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, ItemPriceState>> LoadPriceStatesAsync(ulong guildId, CancellationToken ct = default) =>
        (await LoadAllPriceStatesAsync(ct).ConfigureAwait(false)).GetValueOrDefault(guildId) ?? new Dictionary<string, ItemPriceState>(StringComparer.Ordinal);

    // 길드별 과거시세·현재시세.
    public async Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, ItemPriceState>>> LoadAllPriceStatesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT guild_id, item_name, is_product, baseline_price, baseline_at_utc, current_price, current_at_utc FROM haeyeon_guild_price_state;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<ulong, Dictionary<string, ItemPriceState>>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var guildId = checked((ulong)reader.GetInt64(0));
                if (!result.TryGetValue(guildId, out var states)) result[guildId] = states = new Dictionary<string, ItemPriceState>(StringComparer.Ordinal);
                states[reader.GetString(1)] = new ItemPriceState(reader.GetString(1), reader.GetInt64(2) != 0,
                    reader.GetInt64(3), Parse(reader.GetString(4)), reader.GetInt64(5), Parse(reader.GetString(6)));
            }
            return result.ToDictionary(x => x.Key, x => (IReadOnlyDictionary<string, ItemPriceState>)x.Value);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, CraftState>> LoadCraftStatesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_name, advantage, product_price, material_cost, updated_at_utc FROM haeyeon_craft_state;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<string, CraftState>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Enum.TryParse<CraftAdvantage>(reader.GetString(1), out var advantage)) continue;
                result[reader.GetString(0)] = new CraftState(reader.GetString(0), advantage, reader.GetInt64(2), reader.GetInt64(3), Parse(reader.GetString(4)));
            }
            return result;
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>
    /// 한 회차의 시세와 새 판정 상태를 한 트랜잭션으로 저장합니다. 마지막 시세는 이번 회차 시세로 통째로 바꿔
    /// 검색에서 빠진 아이템의 옛 시세가 남지 않게 합니다.
    /// 길드별 과거시세는 판정 중 등록이 해제된 길드면 저장하지 않습니다.
    /// </summary>
    public async Task SaveRunAsync(
        DateTimeOffset collectedAtUtc,
        IEnumerable<HaeyeonPriceSnapshot> snapshots,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, ItemPriceState>> guildPriceStates,
        IEnumerable<CraftState> craftStates,
        CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var collectedAt = Format(collectedAtUtc);
            await ExecuteAsync(connection, transaction, "DELETE FROM haeyeon_latest_prices;", ct).ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                var price = snapshot.Price;
                await ExecuteAsync(connection, transaction, """
                    INSERT OR REPLACE INTO haeyeon_latest_prices
                        (item_name, kind_id, category, is_product, min_price, total_count, is_sold_out, priced_at_utc,
                         price_change_1h_pct, price_change_24h_pct, price_change_7d_pct, count_change_24h, collected_at_utc)
                    VALUES ($name, $kindId, $category, $isProduct, $minPrice, $totalCount, $isSoldOut, $pricedAt,
                        $change1h, $change24h, $change7d, $countChange24h, $collectedAt);
                    """, ct,
                    ("$collectedAt", collectedAt), ("$name", price.Name), ("$kindId", price.KindId), ("$category", price.Category),
                    ("$isProduct", snapshot.IsProduct ? 1 : 0), ("$minPrice", price.MinPrice), ("$totalCount", price.TotalCount),
                    ("$isSoldOut", price.IsSoldOut ? 1 : 0), ("$pricedAt", Format(price.PricedAtUtc)),
                    ("$change1h", DbValue(price.PriceChange1hPercent)), ("$change24h", DbValue(price.PriceChange24hPercent)),
                    ("$change7d", DbValue(price.PriceChange7dPercent)), ("$countChange24h", DbValue(price.CountChange24hPercent))).ConfigureAwait(false);
            }
            foreach (var (guildId, states) in guildPriceStates)
            {
                foreach (var state in states.Values)
                {
                    await ExecuteAsync(connection, transaction, """
                        INSERT OR REPLACE INTO haeyeon_guild_price_state
                            (guild_id, item_name, is_product, baseline_price, baseline_at_utc, current_price, current_at_utc)
                        SELECT $guildId, $name, $isProduct, $baseline, $baselineAt, $current, $currentAt
                        WHERE EXISTS (SELECT 1 FROM haeyeon_monitor_channels WHERE guild_id = $guildId);
                        """, ct,
                        ("$guildId", checked((long)guildId)), ("$name", state.Name), ("$isProduct", state.IsProduct ? 1 : 0), ("$baseline", state.BaselinePrice),
                        ("$baselineAt", Format(state.BaselineAtUtc)), ("$current", state.CurrentPrice), ("$currentAt", Format(state.CurrentAtUtc))).ConfigureAwait(false);
                }
            }
            foreach (var state in craftStates)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT OR REPLACE INTO haeyeon_craft_state (product_name, advantage, product_price, material_cost, updated_at_utc)
                    VALUES ($name, $advantage, $productPrice, $materialCost, $updatedAt);
                    """, ct,
                    ("$name", state.ProductName), ("$advantage", state.Advantage.ToString()), ("$productPrice", state.ProductPrice),
                    ("$materialCost", state.MaterialCost), ("$updatedAt", Format(state.UpdatedAtUtc))).ConfigureAwait(false);
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    // 저장된 마지막 시세 행 수. 회차가 거듭되어도 추적 아이템 수를 넘지 않아야 합니다.
    public async Task<int> CountLatestPricesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM haeyeon_latest_prices;";
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

    private static async Task<HaeyeonMonitorChannel?> ReadChannelAsync(SqliteConnection connection, SqliteTransaction transaction, ulong guildId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ChannelColumns} FROM haeyeon_monitor_channels WHERE guild_id = $guildId;";
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadChannel(reader) : null;
    }

    private const string ChannelColumns = "guild_id, channel_id, registered_at_utc, product_change_percent, material_change_percent";

    private static HaeyeonMonitorChannel ReadChannel(SqliteDataReader reader) => new(
        checked((ulong)reader.GetInt64(0)), checked((ulong)reader.GetInt64(1)), Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4));

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // decimal은 문자열로 바인딩되므로 REAL 열에 맞게 double로 넘깁니다.
    private static object DbValue(decimal? value) => value is { } number ? (double)number : DBNull.Value;
    private static decimal? NullableDecimal(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    // UTC ISO 8601 고정 길이 문자열이라 문자열 비교가 시간 순서와 같습니다.
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
