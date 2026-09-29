using Molly.Crafting;

namespace Molly.HaeyeonMarket;

/// <summary>/해연시세모니터링의 조절 가능한 기준값. 값을 바꾸면 다음 정각 갱신부터 적용됩니다.</summary>
public static class HaeyeonMarketRules
{
    // 제작 시트에서 이 이름으로 시작하는 아이템만 추적합니다.
    public const string ProductPrefix = "해연의";

    // 모비라이프 시세 검색어. 추적 대상 이름은 제작 시트에서 뽑고, 검색 결과에서 이름이 정확히 같은 항목만 씁니다.
    public static readonly IReadOnlyList<string> SearchKeywords = ["해연의", "마력석", "영혼석", "특급", "백금강괴"];

    // 시세를 조회하지 않고 0으로 계산하는 무가치한 재료.
    public static readonly IReadOnlySet<string> WorthlessMaterials = new HashSet<string>(StringComparer.Ordinal) { "세공된 페리도트ZZ" };

    // 과거시세 대비 이 비율 이상 변동하면 알립니다(0.10 = 10%). 서버가 /해연시세모니터링에서 등락률을 정하지 않으면 이 기본값을 씁니다.
    public const decimal ProductChangeThreshold = 0.10m;
    public const decimal MaterialChangeThreshold = 0.20m;

    // /해연시세모니터링 등락률 옵션 범위(%).
    public const int MinChangePercent = 1;
    public const int MaxChangePercent = 100;

    // 제작비와 완제품 구매가가 거의 같을 때 알림이 반복되지 않도록, 이 비율 이상 벌어져야 유불리가 바뀐 것으로 봅니다(0.03 = 3%, 0이면 여유 없음).
    public const decimal CraftSwitchMargin = 0.03m;

    // 매물이 이 수보다 적으면 최저가를 믿기 어려워 해당 회차의 판정에서 뺍니다(마지막 시세로는 저장합니다).
    public const long MinListingCount = 3;

    public static IReadOnlyList<CraftingRecipe> SelectRecipes(CraftingTable table) =>
        table.Items.Where(x => x.Name.StartsWith(ProductPrefix, StringComparison.Ordinal)).ToArray();

    public static decimal ChangeThreshold(bool isProduct) => HaeyeonThresholds.Default.For(isProduct);
}

/// <summary>서버별 시세 변동 알림 기준 비율(0.10 = 10%).</summary>
public sealed record HaeyeonThresholds(decimal Product, decimal Material)
{
    public static readonly HaeyeonThresholds Default = new(HaeyeonMarketRules.ProductChangeThreshold, HaeyeonMarketRules.MaterialChangeThreshold);

    public decimal For(bool isProduct) => isProduct ? Product : Material;
}
