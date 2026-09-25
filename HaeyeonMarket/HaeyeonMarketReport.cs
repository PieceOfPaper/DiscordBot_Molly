using Molly.Crafting;
using Molly.Market;

namespace Molly.HaeyeonMarket;

public enum HaeyeonPriceView
{
    // 해연 제작 아이템 시세
    Products,
    // 해연 재료 시세(중복 없이, 마력석·영혼석·기타 분류)
    Materials,
    // 해연 아이템별 완제품 시세와 재료 합계 비교
    ProductTotals,
}

/// <summary>/해연시세와 /해연시세모니터링테스트에서 마지막 저장 시세를 보여주는 순수 로직입니다.</summary>
public static class HaeyeonMarketReport
{
    public static string Title(HaeyeonPriceView view) => view switch
    {
        HaeyeonPriceView.Products => "💹 해연 아이템 시세",
        HaeyeonPriceView.Materials => "💹 해연 재료 시세",
        _ => "💹 해연 아이템 · 재료 합계 비교",
    };

    public static IReadOnlyList<string> BuildLines(HaeyeonPriceView view, IReadOnlyList<CraftingRecipe> recipes, IReadOnlyDictionary<string, MarketPrice> prices) => view switch
    {
        HaeyeonPriceView.Products => recipes.Select(x => $"{x.Name} · {PriceText(prices, x.Name)}").ToArray(),
        HaeyeonPriceView.Materials => MaterialLines(recipes, prices),
        _ => recipes.Select(x => TotalLine(x, prices)).ToArray(),
    };

    // 분류 머리글을 뺀 실제 아이템·재료 개수
    public static int ItemCount(HaeyeonPriceView view, IReadOnlyList<CraftingRecipe> recipes) =>
        view == HaeyeonPriceView.Materials ? MaterialNames(recipes).Count : recipes.Count;

    // 재료 분류: 이름에 "마력석"이 들어가면 마력석, "영혼석"이 들어가면 영혼석, 나머지는 기타
    public static string MaterialCategory(string name) =>
        name.Contains("마력석", StringComparison.Ordinal) ? "마력석" :
        name.Contains("영혼석", StringComparison.Ordinal) ? "영혼석" : "기타";

    private static readonly string[] s_MaterialCategories = ["마력석", "영혼석", "기타"];

    private static IReadOnlyList<string> MaterialNames(IReadOnlyList<CraftingRecipe> recipes) =>
        recipes.SelectMany(x => x.Ingredients).Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();

    // 분류별 머리글 아래에 시트 순서대로 재료를 나열하고, 재료가 없는 분류는 생략합니다.
    private static IReadOnlyList<string> MaterialLines(IReadOnlyList<CraftingRecipe> recipes, IReadOnlyDictionary<string, MarketPrice> prices)
    {
        var lines = new List<string>();
        var names = MaterialNames(recipes);
        foreach (var category in s_MaterialCategories)
        {
            var group = names.Where(x => MaterialCategory(x) == category).ToArray();
            if (group.Length == 0) continue;
            if (lines.Count > 0) lines.Add("");
            lines.Add($"**[{category}]**");
            lines.AddRange(group.Select(name => HaeyeonMarketRules.WorthlessMaterials.Contains(name)
                ? $"{name} · 0 (무가치 재료, 조회하지 않음)"
                : $"{name} · {PriceText(prices, name)}"));
        }
        return lines;
    }

    // 재료 합계 = Σ(재료 최저가 × 수량). 무가치 재료는 0, 시세가 없거나 매진인 재료가 있으면 계산하지 않습니다.
    public static long? MaterialCost(CraftingRecipe recipe, IReadOnlyDictionary<string, MarketPrice> prices, out IReadOnlyList<string> unavailable)
    {
        var missing = new List<string>();
        long total = 0;
        foreach (var ingredient in recipe.Ingredients)
        {
            if (HaeyeonMarketRules.WorthlessMaterials.Contains(ingredient.Name)) continue;
            if (!prices.TryGetValue(ingredient.Name, out var price) || price.IsSoldOut || price.MinPrice <= 0) { missing.Add(ingredient.Name); continue; }
            total += price.MinPrice * ingredient.Quantity;
        }
        unavailable = missing;
        return missing.Count == 0 ? total : null;
    }

    private static string TotalLine(CraftingRecipe recipe, IReadOnlyDictionary<string, MarketPrice> prices)
    {
        var cost = MaterialCost(recipe, prices, out var unavailable);
        var hasProduct = prices.TryGetValue(recipe.Name, out var product) && !product.IsSoldOut && product.MinPrice > 0;
        var productText = hasProduct ? HaeyeonMarketMessages.Price(product!.MinPrice) : PriceText(prices, recipe.Name);
        if (cost is null)
            return $"❔ **{recipe.Name}** 완제품 {productText} / 재료 합계 계산 불가({string.Join(", ", unavailable)} 시세 없음)";
        if (!hasProduct)
            return $"❔ **{recipe.Name}** 완제품 {productText} / 재료 합계 {HaeyeonMarketMessages.Price(cost.Value)}";

        var rate = (decimal)(product!.MinPrice - cost.Value) / cost.Value;
        var comparison = rate switch
        {
            > 0 => $"완제품이 {Math.Abs(rate) * 100:0.0}% 높음 → 재료 구매 후 제작이 저렴",
            < 0 => $"완제품이 {Math.Abs(rate) * 100:0.0}% 낮음 → 완제품 구매가 저렴",
            _ => "같음",
        };
        var icon = rate > 0 ? "⚒️" : rate < 0 ? "🛒" : "⚖️";
        var fewListings = product.TotalCount < HaeyeonMarketRules.MinListingCount ? $" (매물 {product.TotalCount}개)" : "";
        return $"{icon} **{recipe.Name}** 완제품 {HaeyeonMarketMessages.Price(product.MinPrice)}{fewListings} / 재료 합계 {HaeyeonMarketMessages.Price(cost.Value)} · {comparison}";
    }

    private static string PriceText(IReadOnlyDictionary<string, MarketPrice> prices, string name)
    {
        if (!prices.TryGetValue(name, out var price)) return "시세 없음";
        if (price.IsSoldOut || price.MinPrice <= 0) return "매진";
        var few = price.TotalCount < HaeyeonMarketRules.MinListingCount ? ", 적음" : "";
        return $"**{HaeyeonMarketMessages.Price(price.MinPrice)}** (매물 {HaeyeonMarketMessages.Price(price.TotalCount)}개{few})";
    }

    /// <summary>
    /// 알림 테스트용 가상 판정. 두 알림 중 하나를 무작위로 고르고(데이터가 없으면 다른 쪽), 저장된 시세에서 아이템을 1~3개 뽑습니다.
    /// 시세 변동은 현재시세는 실제 값, 과거시세는 기준을 넘도록 만든 가상 값이며, 유불리는 실제 시세로 계산한 현재 상태입니다.
    /// </summary>
    public static HaeyeonEvaluation? BuildTestEvaluation(IReadOnlyList<CraftingRecipe> recipes, IReadOnlyDictionary<string, MarketPrice> prices, Random random)
    {
        var tracked = HaeyeonMarketEvaluator.TrackedNames(recipes);
        var priceCandidates = tracked
            .Where(x => prices.TryGetValue(x.Key, out var price) && !price.IsSoldOut && price.MinPrice > 0)
            .Select(x => (Name: x.Key, IsProduct: x.Value, Current: prices[x.Key].MinPrice)).ToArray();
        var craftCandidates = recipes
            .Select(x => (Recipe: x, Cost: MaterialCost(x, prices, out _)))
            .Where(x => x.Cost > 0 && prices.TryGetValue(x.Recipe.Name, out var product) && !product.IsSoldOut && product.MinPrice > 0)
            .Select(x => new CraftSwitchAlert(x.Recipe.Name,
                prices[x.Recipe.Name].MinPrice >= x.Cost!.Value ? CraftAdvantage.Craft : CraftAdvantage.Buy,
                prices[x.Recipe.Name].MinPrice, x.Cost.Value)).ToArray();
        if (priceCandidates.Length == 0 && craftCandidates.Length == 0) return null;

        var usePriceAlert = craftCandidates.Length == 0 || priceCandidates.Length > 0 && random.Next(2) == 0;
        var priceAlerts = new List<PriceChangeAlert>();
        var craftAlerts = new List<CraftSwitchAlert>();
        if (usePriceAlert)
        {
            foreach (var (name, isProduct, current) in Pick(priceCandidates, random))
            {
                // 기준값보다 0~10%p 더 큰 가상 변동률로 과거시세를 역산합니다.
                var threshold = (double)HaeyeonMarketRules.ChangeThreshold(isProduct);
                var rate = (threshold + random.NextDouble() * 0.10) * (random.Next(2) == 0 ? 1 : -1);
                var baseline = Math.Max(1, (long)Math.Round(current / (1 + rate)));
                priceAlerts.Add(new PriceChangeAlert(name, isProduct, baseline, current));
            }
        }
        else
        {
            craftAlerts.AddRange(Pick(craftCandidates, random));
        }
        return new HaeyeonEvaluation(priceAlerts, craftAlerts,
            new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], []);
    }

    private static IEnumerable<T> Pick<T>(IReadOnlyList<T> items, Random random) =>
        items.OrderBy(_ => random.Next()).Take(random.Next(1, Math.Min(3, items.Count) + 1));
}
