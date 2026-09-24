using Molly.Crafting;
using Molly.Market;

namespace Molly.HaeyeonMarket;

// Craft: 재료를 사서 제작하는 쪽이 저렴(완제품 구매가 > 재료 총합). Buy: 완제품을 바로 사는 쪽이 저렴.
public enum CraftAdvantage { Craft, Buy }

// 아이템별 과거시세(마지막 알림 기준)와 현재시세.
public sealed record ItemPriceState(string Name, bool IsProduct, long BaselinePrice, DateTimeOffset BaselineAtUtc, long CurrentPrice, DateTimeOffset CurrentAtUtc);

// 제작 아이템별 마지막 유불리 판정.
public sealed record CraftState(string ProductName, CraftAdvantage Advantage, long ProductPrice, long MaterialCost, DateTimeOffset UpdatedAtUtc);

public sealed record PriceChangeAlert(string Name, bool IsProduct, long BaselinePrice, long CurrentPrice)
{
    public decimal ChangeRate => (decimal)(CurrentPrice - BaselinePrice) / BaselinePrice;
}

public sealed record CraftSwitchAlert(string ProductName, CraftAdvantage Advantage, long ProductPrice, long MaterialCost)
{
    // 완제품 구매가가 재료 총합보다 몇 % 비싼지(음수면 저렴).
    public decimal PremiumRate => (decimal)(ProductPrice - MaterialCost) / MaterialCost;
}

public sealed record HaeyeonEvaluation(
    IReadOnlyList<PriceChangeAlert> PriceAlerts,
    IReadOnlyList<CraftSwitchAlert> CraftAlerts,
    IReadOnlyDictionary<string, ItemPriceState> PriceStates,
    IReadOnlyDictionary<string, CraftState> CraftStates,
    IReadOnlyList<string> MissingNames,
    IReadOnlyList<string> UnreliableNames)
{
    public bool HasAlerts => PriceAlerts.Count > 0 || CraftAlerts.Count > 0;
}

/// <summary>
/// 시세 판정. Discord·DB와 분리된 순수 로직이며, 새 상태(과거시세·유불리)를 돌려주면 호출자가 알림 뒤 저장합니다.
/// </summary>
public static class HaeyeonMarketEvaluator
{
    // 추적 대상: 제작 아이템과, 무가치 재료를 뺀 제작 재료. 값은 제작 아이템 여부입니다.
    public static IReadOnlyDictionary<string, bool> TrackedNames(IEnumerable<CraftingRecipe> recipes)
    {
        var tracked = new Dictionary<string, bool>(StringComparer.Ordinal);
        var list = recipes.ToArray();
        foreach (var recipe in list) tracked[recipe.Name] = true;
        foreach (var ingredient in list.SelectMany(x => x.Ingredients))
            if (!HaeyeonMarketRules.WorthlessMaterials.Contains(ingredient.Name))
                tracked.TryAdd(ingredient.Name, false);
        return tracked;
    }

    // 판정에 쓸 수 있는 시세인지. 매진·매물 부족이면 최저가를 믿기 어렵습니다.
    public static bool IsReliable(MarketPrice price) =>
        !price.IsSoldOut && price.MinPrice > 0 && price.TotalCount >= HaeyeonMarketRules.MinListingCount;

    public static HaeyeonEvaluation Evaluate(
        IReadOnlyList<CraftingRecipe> recipes,
        IReadOnlyDictionary<string, MarketPrice> prices,
        IReadOnlyDictionary<string, ItemPriceState> previousPrices,
        IReadOnlyDictionary<string, CraftState> previousCrafts,
        DateTimeOffset nowUtc)
    {
        var tracked = TrackedNames(recipes);
        var priceStates = new Dictionary<string, ItemPriceState>(previousPrices, StringComparer.Ordinal);
        var craftStates = new Dictionary<string, CraftState>(previousCrafts, StringComparer.Ordinal);
        var priceAlerts = new List<PriceChangeAlert>();
        var craftAlerts = new List<CraftSwitchAlert>();
        var missing = new List<string>();
        var unreliable = new List<string>();

        foreach (var (name, isProduct) in tracked)
        {
            if (!prices.TryGetValue(name, out var price)) { missing.Add(name); continue; }
            if (!IsReliable(price)) { unreliable.Add(name); continue; }

            var current = price.MinPrice;
            if (!previousPrices.TryGetValue(name, out var previous) || previous.BaselinePrice <= 0)
            {
                // 처음 보는 아이템은 현재시세를 과거시세로 두고 알리지 않습니다.
                priceStates[name] = new ItemPriceState(name, isProduct, current, nowUtc, current, nowUtc);
                continue;
            }

            var alert = new PriceChangeAlert(name, isProduct, previous.BaselinePrice, current);
            if (Math.Abs(alert.ChangeRate) >= HaeyeonMarketRules.ChangeThreshold(isProduct))
            {
                priceAlerts.Add(alert);
                priceStates[name] = new ItemPriceState(name, isProduct, current, nowUtc, current, nowUtc);
            }
            else
            {
                priceStates[name] = previous with { IsProduct = isProduct, CurrentPrice = current, CurrentAtUtc = nowUtc };
            }
        }

        foreach (var recipe in recipes)
        {
            if (!TryGetReliablePrice(prices, recipe.Name, out var productPrice)) continue;
            long materialCost = 0;
            var complete = true;
            foreach (var ingredient in recipe.Ingredients)
            {
                if (HaeyeonMarketRules.WorthlessMaterials.Contains(ingredient.Name)) continue;
                if (!TryGetReliablePrice(prices, ingredient.Name, out var ingredientPrice)) { complete = false; break; }
                materialCost += ingredientPrice * ingredient.Quantity;
            }
            // 재료 시세가 하나라도 없으면 이번 회차는 유불리를 판단하지 않고 이전 상태를 유지합니다.
            if (!complete || materialCost <= 0) continue;

            var decided = Decide(productPrice, materialCost);
            if (!previousCrafts.TryGetValue(recipe.Name, out var previousCraft))
            {
                var initial = decided ?? (productPrice >= materialCost ? CraftAdvantage.Craft : CraftAdvantage.Buy);
                craftStates[recipe.Name] = new CraftState(recipe.Name, initial, productPrice, materialCost, nowUtc);
                continue;
            }
            if (decided is { } advantage && advantage != previousCraft.Advantage)
            {
                craftAlerts.Add(new CraftSwitchAlert(recipe.Name, advantage, productPrice, materialCost));
                craftStates[recipe.Name] = new CraftState(recipe.Name, advantage, productPrice, materialCost, nowUtc);
            }
            else
            {
                craftStates[recipe.Name] = previousCraft with { ProductPrice = productPrice, MaterialCost = materialCost, UpdatedAtUtc = nowUtc };
            }
        }

        return new HaeyeonEvaluation(priceAlerts, craftAlerts, priceStates, craftStates, missing, unreliable);
    }

    // 여유 폭 안이면 null(판정 보류, 이전 상태 유지).
    public static CraftAdvantage? Decide(long productPrice, long materialCost)
    {
        var ratio = (decimal)productPrice / materialCost;
        if (ratio >= 1 + HaeyeonMarketRules.CraftSwitchMargin) return CraftAdvantage.Craft;
        if (ratio <= 1 - HaeyeonMarketRules.CraftSwitchMargin) return CraftAdvantage.Buy;
        return null;
    }

    private static bool TryGetReliablePrice(IReadOnlyDictionary<string, MarketPrice> prices, string name, out long price)
    {
        price = 0;
        if (!prices.TryGetValue(name, out var found) || !IsReliable(found)) return false;
        price = found.MinPrice;
        return true;
    }
}
