using Molly.Market;

namespace Molly.KeywordMarket;

// 아이템별 과거시세(마지막 알림 기준)와 현재시세. 거래소 kind_id로 구분합니다.
// BaselinePrice가 0이면 아직 믿을 만한 시세를 본 적이 없어 변동을 비교하지 않습니다.
// MissingRuns는 검색 결과에서 연속으로 빠진 회차 수입니다.
public sealed record KeywordItemState(
    long KindId, string Name, long BaselinePrice, DateTimeOffset BaselineAtUtc,
    long CurrentPrice, DateTimeOffset CurrentAtUtc, DateTimeOffset FirstSeenAtUtc, int MissingRuns);

public sealed record KeywordPriceChangeAlert(string Name, long BaselinePrice, long CurrentPrice)
{
    public decimal ChangeRate => (decimal)(CurrentPrice - BaselinePrice) / BaselinePrice;
}

public sealed record KeywordNewItemAlert(MarketPrice Price);

// LastPrice가 0이면 믿을 만한 시세를 본 적이 없는 아이템입니다.
public sealed record KeywordRemovedItemAlert(string Name, long LastPrice);

public sealed record KeywordEvaluation(
    IReadOnlyList<KeywordPriceChangeAlert> PriceAlerts,
    IReadOnlyList<KeywordNewItemAlert> NewItems,
    IReadOnlyList<KeywordRemovedItemAlert> RemovedItems,
    IReadOnlyDictionary<long, KeywordItemState> States)
{
    public bool HasAlerts => PriceAlerts.Count > 0 || NewItems.Count > 0 || RemovedItems.Count > 0;
}

/// <summary>
/// 검색어 시세 판정. Discord·DB와 분리된 순수 로직이며, 새 상태를 돌려주면 호출자가 알림 뒤 저장합니다.
/// 돌려주는 States가 추적 중인 아이템 전체이며, 빠진 아이템(사라짐)은 저장소에서도 지웁니다.
/// </summary>
public static class KeywordMarketEvaluator
{
    public static bool IsReliable(MarketPrice price) =>
        !price.IsSoldOut && price.MinPrice > 0 && price.TotalCount >= KeywordMarketRules.MinListingCount;

    /// <param name="isFirstRun">처음 수집이면 발견한 아이템을 신규로 알리지 않고 기준으로만 저장합니다.</param>
    /// <param name="canDetectRemoval">검색 결과가 잘렸을 수 있으면 false로 두어 빠진 아이템을 사라짐으로 세지 않습니다.</param>
    public static KeywordEvaluation Evaluate(
        IReadOnlyList<MarketPrice> prices,
        IReadOnlyDictionary<long, KeywordItemState> previous,
        bool isFirstRun,
        bool canDetectRemoval,
        DateTimeOffset nowUtc)
    {
        var states = new Dictionary<long, KeywordItemState>();
        var priceAlerts = new List<KeywordPriceChangeAlert>();
        var newItems = new List<KeywordNewItemAlert>();
        var removed = new List<KeywordRemovedItemAlert>();

        foreach (var price in prices.DistinctBy(x => x.KindId))
        {
            var reliable = IsReliable(price);
            if (!previous.TryGetValue(price.KindId, out var old))
            {
                if (!isFirstRun) newItems.Add(new KeywordNewItemAlert(price));
                var baseline = reliable ? price.MinPrice : 0;
                states[price.KindId] = new KeywordItemState(price.KindId, price.Name, baseline, nowUtc, baseline, nowUtc, nowUtc, 0);
                continue;
            }

            var seen = old with { Name = price.Name, MissingRuns = 0 };
            if (!reliable)
            {
                states[price.KindId] = seen;
                continue;
            }
            var current = price.MinPrice;
            if (old.BaselinePrice <= 0)
            {
                states[price.KindId] = seen with { BaselinePrice = current, BaselineAtUtc = nowUtc, CurrentPrice = current, CurrentAtUtc = nowUtc };
                continue;
            }
            var alert = new KeywordPriceChangeAlert(price.Name, old.BaselinePrice, current);
            if (Math.Abs(alert.ChangeRate) >= KeywordMarketRules.ChangeThreshold)
            {
                priceAlerts.Add(alert);
                states[price.KindId] = seen with { BaselinePrice = current, BaselineAtUtc = nowUtc, CurrentPrice = current, CurrentAtUtc = nowUtc };
            }
            else
            {
                states[price.KindId] = seen with { CurrentPrice = current, CurrentAtUtc = nowUtc };
            }
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

        return new KeywordEvaluation(priceAlerts, newItems, removed, states);
    }
}
