using Molly.Market;

namespace Molly.KeywordMarket;

// 추적 중인 아이템의 공용 상태. 거래소 kind_id로 구분합니다.
// CurrentPrice는 마지막으로 본 믿을 만한 시세이며, 0이면 아직 믿을 만한 시세를 본 적이 없습니다.
// MissingRuns는 검색 결과에서 연속으로 빠진 회차 수입니다.
public sealed record KeywordItemState(
    long KindId, string Name, long CurrentPrice, DateTimeOffset CurrentAtUtc, DateTimeOffset FirstSeenAtUtc, int MissingRuns);

// 길드별 과거시세(마지막 알림 기준). 길드마다 등락률이 달라 따로 둡니다.
public sealed record KeywordBaseline(long KindId, long Price, DateTimeOffset AtUtc);

public sealed record KeywordPriceChangeAlert(string Name, long BaselinePrice, long CurrentPrice)
{
    public decimal ChangeRate => (decimal)(CurrentPrice - BaselinePrice) / BaselinePrice;

    // 현재 시세의 상승·하락 판정. 알림 줄 끝에 이모지로 붙입니다(없으면 붙이지 않음).
    public MarketTrend? Trend { get; init; }
}

public sealed record KeywordNewItemAlert(MarketPrice Price);

// LastPrice가 0이면 믿을 만한 시세를 본 적이 없는 아이템입니다.
public sealed record KeywordRemovedItemAlert(string Name, long LastPrice);

// 신규·사라짐 판정(모든 길드 공용).
public sealed record KeywordItemsEvaluation(
    IReadOnlyList<KeywordNewItemAlert> NewItems,
    IReadOnlyList<KeywordRemovedItemAlert> RemovedItems,
    IReadOnlyDictionary<long, KeywordItemState> States);

// 한 길드의 시세 변동 판정.
public sealed record KeywordPriceEvaluation(IReadOnlyList<KeywordPriceChangeAlert> Alerts, IReadOnlyDictionary<long, KeywordBaseline> Baselines);

// 한 채널에 보낼 알림 내용.
public sealed record KeywordEvaluation(
    IReadOnlyList<KeywordPriceChangeAlert> PriceAlerts,
    IReadOnlyList<KeywordNewItemAlert> NewItems,
    IReadOnlyList<KeywordRemovedItemAlert> RemovedItems)
{
    // 상승·하락 판정이 바뀐 아이템(모든 서버 공용).
    public IReadOnlyList<MarketTrendChangeAlert> TrendAlerts { get; init; } = [];

    public bool HasAlerts => PriceAlerts.Count > 0 || NewItems.Count > 0 || RemovedItems.Count > 0 || TrendAlerts.Count > 0;
}

/// <summary>
/// 검색어 시세 판정. Discord·DB와 분리된 순수 로직이며, 새 상태를 돌려주면 호출자가 알림 뒤 저장합니다.
/// 신규·사라짐은 모든 길드가 같이 쓰는 EvaluateItems로, 시세 변동은 길드별 등락률·과거시세로 EvaluatePrices에서 판정합니다.
/// </summary>
public static class KeywordMarketEvaluator
{
    public static bool IsReliable(MarketPrice price) =>
        !price.IsSoldOut && price.MinPrice > 0 && price.TotalCount >= KeywordMarketRules.MinListingCount;

    /// <summary>돌려주는 States가 추적 중인 아이템 전체이며, 빠진 아이템(사라짐)은 저장소에서도 지웁니다.</summary>
    /// <param name="isFirstRun">처음 수집이면 발견한 아이템을 신규로 알리지 않고 기준으로만 저장합니다.</param>
    /// <param name="canDetectRemoval">검색 결과가 잘렸을 수 있으면 false로 두어 빠진 아이템을 사라짐으로 세지 않습니다.</param>
    public static KeywordItemsEvaluation EvaluateItems(
        IReadOnlyList<MarketPrice> prices,
        IReadOnlyDictionary<long, KeywordItemState> previous,
        bool isFirstRun,
        bool canDetectRemoval,
        DateTimeOffset nowUtc)
    {
        var states = new Dictionary<long, KeywordItemState>();
        var newItems = new List<KeywordNewItemAlert>();
        var removed = new List<KeywordRemovedItemAlert>();

        foreach (var price in prices.DistinctBy(x => x.KindId))
        {
            var reliable = IsReliable(price);
            if (!previous.TryGetValue(price.KindId, out var old))
            {
                if (!isFirstRun) newItems.Add(new KeywordNewItemAlert(price));
                var current = reliable ? price.MinPrice : 0;
                states[price.KindId] = new KeywordItemState(price.KindId, price.Name, current, nowUtc, nowUtc, 0);
                continue;
            }

            var seen = old with { Name = price.Name, MissingRuns = 0 };
            states[price.KindId] = reliable ? seen with { CurrentPrice = price.MinPrice, CurrentAtUtc = nowUtc } : seen;
        }

        foreach (var (kindId, old) in previous)
        {
            if (states.ContainsKey(kindId)) continue;
            if (!canDetectRemoval) { states[kindId] = old; continue; }
            var missing = old.MissingRuns + 1;
            if (missing >= KeywordMarketRules.RemovalMissingRuns)
                removed.Add(new KeywordRemovedItemAlert(old.Name, old.CurrentPrice));
            else
                states[kindId] = old with { MissingRuns = missing };
        }

        return new KeywordItemsEvaluation(newItems, removed, states);
    }

    /// <summary>
    /// 한 길드의 과거시세와 등락률(threshold, 0.10 = 10%)로 시세 변동을 판정합니다. 추적에서 빠진(states에 없는) 아이템의 과거시세는 지웁니다.
    /// 과거시세가 없는 아이템은 직전 수집의 공용 시세(previousStates)가 있으면 그 값을, 없으면 현재시세를 과거시세로 두고 알리지 않습니다.
    /// </summary>
    public static KeywordPriceEvaluation EvaluatePrices(
        IReadOnlyList<MarketPrice> prices,
        IReadOnlyDictionary<long, KeywordBaseline> previousBaselines,
        IReadOnlyDictionary<long, KeywordItemState> previousStates,
        IReadOnlyDictionary<long, KeywordItemState> states,
        decimal threshold,
        DateTimeOffset nowUtc)
    {
        var baselines = previousBaselines.Where(x => states.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var alerts = new List<KeywordPriceChangeAlert>();

        foreach (var price in prices.DistinctBy(x => x.KindId))
        {
            if (!IsReliable(price)) continue;
            var current = price.MinPrice;
            if (!baselines.TryGetValue(price.KindId, out var baseline))
            {
                // 새로 등록한 길드도 등록 직전 수집 시세부터 비교합니다.
                if (previousStates.TryGetValue(price.KindId, out var old) && old.CurrentPrice > 0)
                    baseline = new KeywordBaseline(price.KindId, old.CurrentPrice, old.CurrentAtUtc);
                else
                {
                    baselines[price.KindId] = new KeywordBaseline(price.KindId, current, nowUtc);
                    continue;
                }
            }

            var alert = new KeywordPriceChangeAlert(price.Name, baseline.Price, current) { Trend = MarketTrendEvaluator.Evaluate(price) };
            if (Math.Abs(alert.ChangeRate) >= threshold)
            {
                alerts.Add(alert);
                baseline = new KeywordBaseline(price.KindId, current, nowUtc);
            }
            baselines[price.KindId] = baseline;
        }

        return new KeywordPriceEvaluation(alerts, baselines);
    }
}
