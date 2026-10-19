using Molly.Crafting;
using Molly.HaeyeonMarket;
using Molly.Market;

namespace Molly.HaeyeonCost;

/// <summary>한 그룹의 평균 단가. Average는 반올림하지 않은 산술평균이고 ItemCount는 평균에 쓴 품목 수입니다.</summary>
public sealed record HaeyeonUnitPrice(HaeyeonSlot Slot, decimal Average, int ItemCount);

/// <summary>
/// 평균 단가 계산 결과. Missing이 비어 있지 않으면 해당 그룹의 유효 시세가 없어 추정할 수 없습니다.
/// Unmapped는 해연 완제품이지만 부위 매핑이 없어 평균에서 뺀 품목입니다.
/// </summary>
public sealed record HaeyeonUnitPrices(
    IReadOnlyDictionary<HaeyeonSlot, HaeyeonUnitPrice> Prices,
    IReadOnlyList<HaeyeonSlot> Missing,
    IReadOnlyList<string> Unmapped,
    DateTimeOffset CollectedAtUtc)
{
    public bool IsComplete => Missing.Count == 0;
}

public static class HaeyeonCostPricing
{
    /// <summary>제작 시트에서 해연 완제품을 찾아 슬롯을 정합니다. 매핑할 수 없으면 null입니다.</summary>
    public static HaeyeonSlot? SlotOf(CraftingRecipe recipe)
    {
        if (!recipe.Name.StartsWith(HaeyeonMarketRules.ProductPrefix, StringComparison.Ordinal)) return null;
        if (HaeyeonCostRules.ExplicitSlots.TryGetValue(recipe.Name, out var mapped))
            return mapped.Category == recipe.Category ? mapped.Slot : null;
        return recipe.Category == HaeyeonCostRules.WeaponCategory ? HaeyeonSlot.Weapon : null;
    }

    /// <summary>
    /// 마지막 저장 시세로 그룹별 평균 단가를 구합니다. 같은 품목은 한 번만 세고, 거래 수량으로 가중하지 않으며,
    /// 시세가 없거나 IsReliable(매진·0원 이하·매물 부족)을 통과하지 못한 품목은 빼고 0으로 치지 않습니다.
    /// </summary>
    public static HaeyeonUnitPrices Average(IEnumerable<CraftingRecipe> recipes, HaeyeonLatestPrices latest)
    {
        var names = new Dictionary<HaeyeonSlot, HashSet<string>>();
        var unmapped = new List<string>();
        foreach (var recipe in recipes)
        {
            if (!recipe.Name.StartsWith(HaeyeonMarketRules.ProductPrefix, StringComparison.Ordinal)) continue;
            if (SlotOf(recipe) is not { } slot) { unmapped.Add(recipe.Name); continue; }
            if (!names.TryGetValue(slot, out var set)) names[slot] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(recipe.Name);
        }

        var prices = new Dictionary<HaeyeonSlot, HaeyeonUnitPrice>();
        var missing = new List<HaeyeonSlot>();
        foreach (var slot in Enum.GetValues<HaeyeonSlot>())
        {
            var valid = names.GetValueOrDefault(slot)?
                .Select(name => latest.Prices.GetValueOrDefault(name))
                .Where(price => price is not null && HaeyeonMarketEvaluator.IsReliable(price))
                .Select(price => (decimal)price!.MinPrice).ToArray() ?? [];
            if (valid.Length == 0) missing.Add(slot);
            else prices[slot] = new HaeyeonUnitPrice(slot, valid.Sum() / valid.Length, valid.Length);
        }
        return new HaeyeonUnitPrices(prices, missing, unmapped, latest.CollectedAtUtc);
    }

    /// <summary>가장 최근 정각보다 한 회차 이상 이전에 수집한 시세면 오래된 시세로 봅니다.</summary>
    public static bool IsStale(DateTimeOffset collectedAtUtc, DateTimeOffset nowUtc) =>
        MarketSchedule.HourOf(collectedAtUtc) < MarketSchedule.HourOf(nowUtc).AddHours(-1);
}
