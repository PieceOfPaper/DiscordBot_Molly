using System.Globalization;
using Microsoft.Data.Sqlite;
using Molly.Market;

namespace Molly.HaeyeonMarket;

public sealed record HaeyeonMonitorChannel(ulong GuildId, ulong ChannelId, DateTimeOffset RegisteredAtUtc);

// 한 번의 정각 수집에서 저장할 원본 시세. IsProduct는 제작 아이템 여부입니다.
public sealed record HaeyeonPriceSnapshot(MarketPrice Price, bool IsProduct);

// 마지막 정각 수집에서 저장한 시세 전체(매진·매물 부족 포함).
public sealed record HaeyeonLatestPrices(DateTimeOffset CollectedAtUtc, IReadOnlyDictionary<string, MarketPrice> Prices);

/// <summary>
/// /해연시세모니터링 SQLite 저장소. 길드별 알림 채널(길드당 1개), 시간별 시세 이력,
/// 아이템별 과거시세·현재시세, 제작 아이템별 유불리 상태를 보관합니다.
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
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS haeyeon_monitor_channels (
                    guild_id INTEGER PRIMARY KEY,
                    channel_id INTEGER NOT NULL,
                    registered_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS haeyeon_price_history (
                    collected_at_utc TEXT NOT NULL,
                    item_name TEXT NOT NULL,
                    kind_id INTEGER NOT NULL,
                    is_product INTEGER NOT NULL,
                    min_price INTEGER NOT NULL,
                    total_count INTEGER NOT NULL,
                    is_sold_out INTEGER NOT NULL,
                    priced_at_utc TEXT NOT NULL,
                    PRIMARY KEY (collected_at_utc, item_name)
                );
                CREATE TABLE IF NOT EXISTS haeyeon_price_state (
                    item_name TEXT PRIMARY KEY,
                    is_product INTEGER NOT NULL,
                    baseline_price INTEGER NOT NULL,
                    baseline_at_utc TEXT NOT NULL,
                    current_price INTEGER NOT NULL,
                    current_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS haeyeon_craft_state (
                    product_name TEXT PRIMARY KEY,
                    advantage TEXT NOT NULL,
                    product_price INTEGER NOT NULL,
                    material_cost INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { m_Gate.Release(); }
    }

    // 이전에 등록된 채널을 돌려줍니다(없으면 null). 길드당 하나만 유지하므로 다른 채널에서 등록하면 옮겨집니다.
    public async Task<HaeyeonMonitorChannel?> SetChannelAsync(ulong guildId, ulong channelId, DateTimeOffset nowUtc, CancellationToken ct = default)
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
                INSERT INTO haeyeon_monitor_channels (guild_id, channel_id, registered_at_utc) VALUES ($guildId, $channelId, $at)
                ON CONFLICT(guild_id) DO UPDATE SET channel_id = excluded.channel_id, registered_at_utc = excluded.registered_at_utc;
                """;
            command.Parameters.AddWithValue("$guildId", checked((long)guildId));
            command.Parameters.AddWithValue("$channelId", checked((long)channelId));
            command.Parameters.AddWithValue("$at", Format(nowUtc));
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
            command.CommandText = "DELETE FROM haeyeon_monitor_channels WHERE guild_id = $guildId;";
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
            command.CommandText = "SELECT guild_id, channel_id, registered_at_utc FROM haeyeon_monitor_channels ORDER BY guild_id;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<HaeyeonMonitorChannel>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new HaeyeonMonitorChannel(checked((ulong)reader.GetInt64(0)), checked((ulong)reader.GetInt64(1)), Parse(reader.GetString(2))));
            return result;
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
            command.CommandText = "SELECT MAX(collected_at_utc) FROM haeyeon_price_history;";
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
                SELECT collected_at_utc, item_name, kind_id, min_price, total_count, is_sold_out, priced_at_utc FROM haeyeon_price_history
                WHERE collected_at_utc = (SELECT MAX(collected_at_utc) FROM haeyeon_price_history);
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            string? collectedAt = null;
            var prices = new Dictionary<string, MarketPrice>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                collectedAt = reader.GetString(0);
                prices[reader.GetString(1)] = new MarketPrice(reader.GetInt64(2), reader.GetString(1), "", reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5) != 0, Parse(reader.GetString(6)));
            }
            return collectedAt is null ? null : new HaeyeonLatestPrices(Parse(collectedAt), prices);
        }
        finally { m_Gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, ItemPriceState>> LoadPriceStatesAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_name, is_product, baseline_price, baseline_at_utc, current_price, current_at_utc FROM haeyeon_price_state;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<string, ItemPriceState>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result[reader.GetString(0)] = new ItemPriceState(reader.GetString(0), reader.GetInt64(1) != 0,
                    reader.GetInt64(2), Parse(reader.GetString(3)), reader.GetInt64(4), Parse(reader.GetString(5)));
            return result;
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

    /// <summary>한 회차의 원본 시세 이력과 새 판정 상태를 한 트랜잭션으로 저장하고, 보관 기간이 지난 이력을 지웁니다.</summary>
    public async Task SaveRunAsync(
        DateTimeOffset collectedAtUtc,
        IEnumerable<HaeyeonPriceSnapshot> snapshots,
        IEnumerable<ItemPriceState> priceStates,
        IEnumerable<CraftState> craftStates,
        CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var collectedAt = Format(collectedAtUtc);
            foreach (var snapshot in snapshots)
            {
                var price = snapshot.Price;
                await ExecuteAsync(connection, transaction, """
                    INSERT OR REPLACE INTO haeyeon_price_history
                        (collected_at_utc, item_name, kind_id, is_product, min_price, total_count, is_sold_out, priced_at_utc)
                    VALUES ($collectedAt, $name, $kindId, $isProduct, $minPrice, $totalCount, $isSoldOut, $pricedAt);
                    """, ct,
                    ("$collectedAt", collectedAt), ("$name", price.Name), ("$kindId", price.KindId), ("$isProduct", snapshot.IsProduct ? 1 : 0),
                    ("$minPrice", price.MinPrice), ("$totalCount", price.TotalCount), ("$isSoldOut", price.IsSoldOut ? 1 : 0),
                    ("$pricedAt", Format(price.PricedAtUtc))).ConfigureAwait(false);
            }
            foreach (var state in priceStates)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT OR REPLACE INTO haeyeon_price_state (item_name, is_product, baseline_price, baseline_at_utc, current_price, current_at_utc)
                    VALUES ($name, $isProduct, $baseline, $baselineAt, $current, $currentAt);
                    """, ct,
                    ("$name", state.Name), ("$isProduct", state.IsProduct ? 1 : 0), ("$baseline", state.BaselinePrice),
                    ("$baselineAt", Format(state.BaselineAtUtc)), ("$current", state.CurrentPrice), ("$currentAt", Format(state.CurrentAtUtc))).ConfigureAwait(false);
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
            await ExecuteAsync(connection, transaction, "DELETE FROM haeyeon_price_history WHERE collected_at_utc < $cutoff;", ct,
                ("$cutoff", Format(collectedAtUtc - HaeyeonMarketRules.HistoryRetention))).ConfigureAwait(false);
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
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM haeyeon_price_history;";
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
        command.CommandText = "SELECT channel_id, registered_at_utc FROM haeyeon_monitor_channels WHERE guild_id = $guildId;";
        command.Parameters.AddWithValue("$guildId", checked((long)guildId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new HaeyeonMonitorChannel(guildId, checked((ulong)reader.GetInt64(0)), Parse(reader.GetString(1)));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // UTC ISO 8601 고정 길이 문자열이라 문자열 비교가 시간 순서와 같습니다.
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
