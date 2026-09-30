using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Molly.Market;

// 추세 알림에서 쓰는 방향. 가능성 단계(⏫🔼⏬🔽)는 버리고 방향만 봅니다. None은 표시 없음(➖)입니다.
public enum MarketTrendFlow { None, Up, Down }

/// <summary>
/// 아이템 하나의 추세 알림 확인 상태. 최근 회차 시세를 보관하지 않고 후보 방향과 연속 횟수만으로 연속 확인을 이어갑니다.
/// EvaluatedHourUtc는 마지막으로 평가한 정각 회차이며, 재시도·재시작으로 같은 회차를 두 번 세지 않도록 씁니다.
/// </summary>
public sealed record MarketTrendAlertState(
    string ItemKey,
    MarketTrendFlow Candidate,
    int CandidateCount,
    DateTimeOffset EvaluatedHourUtc,
    MarketTrendFlow Confirmed,
    MarketTrendFlow LastAlerted);

/// <summary>상승·하락 흐름이 확정된 아이템. Previous가 None이면 새 흐름, 아니면 반대 방향에서 바뀐 것입니다.</summary>
public sealed record MarketTrendFlowAlert(MarketPrice Price, MarketTrendFlow Previous, MarketTrendFlow Current)
{
    public string Name => Price.Name;
    public bool IsReversal => Previous != MarketTrendFlow.None;
}

public sealed record MarketTrendAlertEvaluation(IReadOnlyList<MarketTrendFlowAlert> Alerts, IReadOnlyDictionary<string, MarketTrendAlertState> States);

public static class MarketTrendAlertRules
{
    // 같은 방향이 이 횟수만큼 정각 회차에서 연속으로 확인되어야 확정합니다.
    public const int ConfirmRuns = 3;
}

/// <summary>
/// 정각 알림용 추세 확정 판정. 조회 화면의 즉시 판정(MarketTrendEvaluator)을 회차마다 받아
/// 같은 상승·하락 방향이 3회 연속 확인될 때만 알립니다. API·DB를 참조하지 않는 순수 로직입니다.
/// </summary>
public static class MarketTrendAlertEvaluator
{
    public static MarketTrendFlow FlowOf(MarketTrend? trend) => trend?.Direction switch
    {
        MarketTrendDirection.Up => MarketTrendFlow.Up,
        MarketTrendDirection.Down => MarketTrendFlow.Down,
        _ => MarketTrendFlow.None,
    };

    /// <summary>
    /// 이번 회차 시세로 아이템별 상태를 한 단계 진행합니다. 돌려주는 States에는 이번 회차에 없던 아이템의 이전 상태도 그대로 들어 있어,
    /// 호출자가 추적에서 빠진 아이템만 걸러 저장합니다(응답에서 잠시 빠진 아이템은 다시 나타날 때 연속이 끊긴 것으로 1회부터 셉니다).
    /// </summary>
    public static MarketTrendAlertEvaluation Evaluate(
        IEnumerable<MarketPrice> current,
        IReadOnlyDictionary<string, MarketTrendAlertState> previous,
        Func<MarketPrice, string> key,
        DateTimeOffset hourUtc)
    {
        var states = new Dictionary<string, MarketTrendAlertState>(previous, StringComparer.Ordinal);
        var alerts = new List<MarketTrendFlowAlert>();
        foreach (var price in current)
        {
            var itemKey = key(price);
            var next = Step(previous.GetValueOrDefault(itemKey), itemKey, FlowOf(MarketTrendEvaluator.Evaluate(price)), hourUtc, out var alertFrom);
            states[itemKey] = next;
            if (alertFrom is { } from) alerts.Add(new MarketTrendFlowAlert(price, from, next.Confirmed));
        }
        return new MarketTrendAlertEvaluation(alerts, states);
    }

    /// <summary>
    /// 한 아이템의 상태를 한 회차 진행합니다. 알려야 하면 alertFrom에 이전 확정 방향(새 흐름이면 None)을 돌려줍니다.
    /// 같은 회차(재시도·재시작)나 더 이전 회차는 상태를 바꾸지 않고, 직전 정각이 빠졌으면 후보를 1회부터 다시 셉니다.
    /// </summary>
    public static MarketTrendAlertState Step(MarketTrendAlertState? previous, string itemKey, MarketTrendFlow flow, DateTimeOffset hourUtc, out MarketTrendFlow? alertFrom)
    {
        alertFrom = null;
        if (previous is not null && previous.EvaluatedHourUtc >= hourUtc) return previous;

        var continuous = previous is not null && previous.EvaluatedHourUtc == hourUtc.AddHours(-1) && previous.Candidate == flow;
        var count = continuous ? Math.Min(previous!.CandidateCount + 1, MarketTrendAlertRules.ConfirmRuns) : 1;
        var confirmed = previous?.Confirmed ?? MarketTrendFlow.None;
        var lastAlerted = previous?.LastAlerted ?? MarketTrendFlow.None;

        if (count >= MarketTrendAlertRules.ConfirmRuns)
        {
            if (flow == MarketTrendFlow.None)
            {
                // 표시 없음이 3회 이어지면 흐름이 끝난 것으로 보고 조용히 해제합니다. 이후 같은 방향이 다시 확정되면 새 흐름으로 알립니다.
                confirmed = MarketTrendFlow.None;
                lastAlerted = MarketTrendFlow.None;
            }
            else if (confirmed != flow)
            {
                if (lastAlerted != flow) alertFrom = confirmed;
                confirmed = flow;
                lastAlerted = flow;
            }
        }
        return new MarketTrendAlertState(itemKey, flow, count, hourUtc, confirmed, lastAlerted);
    }

    public static string Emoji(MarketTrendFlow flow) => flow == MarketTrendFlow.Up ? "📈" : "📉";
    public static string Label(MarketTrendFlow flow) => flow == MarketTrendFlow.Up ? "상승 흐름" : "하락 흐름";

    // 알림 한 줄. 예: "📉 백금강괴 · 120 · 하락 흐름", 반대 방향에서 바뀌면 "📈 백금강괴 · 150 · 하락 → 상승 흐름"
    public static string Line(MarketTrendFlowAlert alert, Func<long, string> price)
    {
        var label = alert.IsReversal
            ? $"{(alert.Previous == MarketTrendFlow.Up ? "상승" : "하락")} → {Label(alert.Current)}"
            : Label(alert.Current);
        return $"{Emoji(alert.Current)} {alert.Name} · {price(alert.Price.MinPrice)} · {label}";
    }

    /// <summary>
    /// 알림 테스트용 확정 흐름 예시 한 건. 실제 확정이 아니라 저장 시세 중 하나로 만든 형식 예시이며, 매진·시세 없음이면 만들지 않습니다.
    /// </summary>
    public static IReadOnlyList<MarketTrendFlowAlert> BuildTestAlerts(IEnumerable<MarketPrice> prices, Random random)
    {
        var candidates = prices.Where(x => !x.IsSoldOut && x.MinPrice > 0).ToArray();
        if (candidates.Length == 0) return [];
        var current = random.Next(2) == 0 ? MarketTrendFlow.Up : MarketTrendFlow.Down;
        var previous = random.Next(2) == 0 ? MarketTrendFlow.None : current == MarketTrendFlow.Up ? MarketTrendFlow.Down : MarketTrendFlow.Up;
        return [new MarketTrendFlowAlert(candidates[random.Next(candidates.Length)], previous, current)];
    }

    public const string SectionTitle = "**🔀 상승·하락 흐름**";
    public const string SectionNote = "-# 정각 수집에서 같은 방향이 3회 연속 확인된 흐름만 알려요.";
}

/// <summary>
/// 추세 알림 확인 상태 표(market_trend_alert_state). 해연·상자·패키지 저장소가 monitor 값으로 나눠 쓰며,
/// 각 저장소의 회차 저장 트랜잭션 안에서 호출해 마지막 시세와 상태가 어긋나지 않게 합니다.
/// </summary>
public static class MarketTrendAlertTable
{
    public static async Task CreateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS market_trend_alert_state (
                monitor TEXT NOT NULL,
                item_key TEXT NOT NULL,
                candidate TEXT NOT NULL,
                candidate_count INTEGER NOT NULL,
                evaluated_hour_utc TEXT NOT NULL,
                confirmed TEXT NOT NULL,
                last_alerted TEXT NOT NULL,
                PRIMARY KEY (monitor, item_key)
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyDictionary<string, MarketTrendAlertState>> LoadAsync(SqliteConnection connection, string monitor, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_key, candidate, candidate_count, evaluated_hour_utc, confirmed, last_alerted
            FROM market_trend_alert_state WHERE monitor = $monitor;
            """;
        command.Parameters.AddWithValue("$monitor", monitor);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, MarketTrendAlertState>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!Enum.TryParse<MarketTrendFlow>(reader.GetString(1), out var candidate) ||
                !Enum.TryParse<MarketTrendFlow>(reader.GetString(4), out var confirmed) ||
                !Enum.TryParse<MarketTrendFlow>(reader.GetString(5), out var lastAlerted)) continue;
            result[reader.GetString(0)] = new MarketTrendAlertState(reader.GetString(0), candidate, reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                confirmed, lastAlerted);
        }
        return result;
    }

    // 이 모니터링의 상태를 통째로 바꿉니다. 넘기지 않은 아이템(추적에서 빠짐)의 상태는 지워집니다.
    public static async Task ReplaceAsync(SqliteConnection connection, SqliteTransaction transaction, string monitor,
        IEnumerable<MarketTrendAlertState> states, CancellationToken ct)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM market_trend_alert_state WHERE monitor = $monitor;";
            delete.Parameters.AddWithValue("$monitor", monitor);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var state in states)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO market_trend_alert_state
                    (monitor, item_key, candidate, candidate_count, evaluated_hour_utc, confirmed, last_alerted)
                VALUES ($monitor, $key, $candidate, $count, $hour, $confirmed, $lastAlerted);
                """;
            insert.Parameters.AddWithValue("$monitor", monitor);
            insert.Parameters.AddWithValue("$key", state.ItemKey);
            insert.Parameters.AddWithValue("$candidate", state.Candidate.ToString());
            insert.Parameters.AddWithValue("$count", state.CandidateCount);
            insert.Parameters.AddWithValue("$hour", state.EvaluatedHourUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$confirmed", state.Confirmed.ToString());
            insert.Parameters.AddWithValue("$lastAlerted", state.LastAlerted.ToString());
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
