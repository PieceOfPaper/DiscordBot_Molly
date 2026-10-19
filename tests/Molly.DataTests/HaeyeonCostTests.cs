using DiscordBot_Molly.Commands;
using Molly.Crafting;
using Molly.HaeyeonCost;
using Molly.HaeyeonMarket;
using Molly.Market;

internal static class HaeyeonCostTests
{
    private static readonly DateTimeOffset s_Now = new(2026, 10, 19, 13, 20, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_Collected = new(2026, 10, 19, 13, 0, 30, TimeSpan.Zero);

    // 검증용 단가: 무기 12,000, 방어구 각 부위·목걸이·반지 7,000
    private static readonly IReadOnlyDictionary<HaeyeonSlot, decimal> s_TestPrices =
        Enum.GetValues<HaeyeonSlot>().ToDictionary(x => x, x => x == HaeyeonSlot.Weapon ? 12_000m : 7_000m);

    private static IReadOnlyDictionary<HaeyeonSlot, decimal> With(HaeyeonSlot slot, decimal price) =>
        s_TestPrices.ToDictionary(x => x.Key, x => x.Key == slot ? price : x.Value);

    private static readonly string[] s_Weapons = ["해연의 숏소드ZZ", "해연의 그레이트 소드ZZ"];

    private static CraftingTable Crafting(params (string Name, string Category)[] extra)
    {
        var rows = s_Weapons.Select(x => (x, "무기"))
            .Concat(HaeyeonCostRules.ExplicitSlots.Select(x => (x.Key, x.Value.Category)))
            .Concat(extra)
            .Select(x => $"{x.Item1},{x.Item2},백금강괴/1");
        return CraftingCsvReader.Parse("이름,분류,재료1\n" + string.Join("\n", rows), s_Now);
    }

    private static MarketPrice P(string name, long price, long count = 10, bool soldOut = false) =>
        new(name.GetHashCode(), name, "장비", price, count, soldOut, s_Collected);

    private static HaeyeonLatestPrices TestLatest() => new(s_Collected,
        s_Weapons.Select(x => P(x, 12_000)).Concat(HaeyeonCostRules.ExplicitSlots.Keys.Select(x => P(x, 7_000)))
            .ToDictionary(x => x.Name, StringComparer.Ordinal));

    public static async Task RunAsync()
    {
        GrowthTests();
        PlanTests();
        PricingTests();
        ResponseTests();
        await CacheTestsAsync();
    }

    private static void GrowthTests()
    {
        var slots = Enum.GetValues<HaeyeonSlot>();
        Assert(HaeyeonCostRules.RuneResistance + slots.Sum(HaeyeonCostRules.InitialEquipment) == 7_200
               && HaeyeonCostRules.RuneResistance + slots.Sum(x => HaeyeonCostRules.InitialEquipment(x) + HaeyeonCostRules.Gain(x, HaeyeonCostRules.MaxCount(x))) == 10_000,
            "초기 총 저항 7,200(룬 3,000 + 장비 4,200), 최대 10,000(룬 3,000 + 장비 7,000)");
        Assert(HaeyeonCostRules.Gain(HaeyeonSlot.Weapon, 1) == 28 && 840 + HaeyeonCostRules.Gain(HaeyeonSlot.Weapon, 20) == 1_400,
            "무기는 감정마다 +28, 최대 1,400");
        Assert(new[] { HaeyeonSlot.Helmet, HaeyeonSlot.Top, HaeyeonSlot.Bottom, HaeyeonSlot.Gloves, HaeyeonSlot.Shoes, HaeyeonSlot.Necklace }
                .All(x => HaeyeonCostRules.Gain(x, 1) == 14 && 420 + HaeyeonCostRules.Gain(x, 20) == 700),
            "방어구 각 부위·목걸이는 감정마다 +14, 최대 700");
        Assert(new[] { 0, 1, 2, 3, 19, 20, 21 }.Select(q => HaeyeonCostRules.Gain(HaeyeonSlot.Ring, q)).SequenceEqual(new[] { 0, 14, 42, 70, 518, 546, 560 }),
            "반지 추가 0/1/2/3/19/20/21개 상승량 0/14/42/70/518/546/560(첫·마지막 +14, 중간 +28)");
        Assert(slots.All(x => Throws(() => HaeyeonCostRules.Gain(x, HaeyeonCostRules.MaxCount(x) + 1))),
            "최대치 이후 추가 감정은 없음(무기·부위 20개, 반지 21개)");
        var full = HaeyeonCostPlanner.Plan(7_200, 10_000, s_TestPrices);
        Assert(full.ReachedResistance == 10_000 && slots.All(x => full.Purchases[x] == HaeyeonCostRules.MaxCount(x)),
            "목표 10,000은 모든 슬롯을 최대까지 올리고 그 이상은 사지 않음");
    }

    private static void PlanTests()
    {
        var plan = HaeyeonCostPlanner.Plan(7_200, 7_800, s_TestPrices);
        Assert(plan.Cost == 164_000 && plan.ReachedResistance == 7_802
               && plan.Purchases.OrderBy(x => x.Key).SequenceEqual(new Dictionary<HaeyeonSlot, int> { [HaeyeonSlot.Weapon] = 2, [HaeyeonSlot.Ring] = 20 }),
            "검증용 단가 7,200 → 7,800: 반지 20개 + 무기 2개, 164,000데카, 도달 7,802(반지 21개 먼저 고정 시 171,000)");

        var expensiveRing = HaeyeonCostPlanner.Plan(7_200, 7_800, With(HaeyeonSlot.Ring, 20_000m));
        Assert(!expensiveRing.Purchases.ContainsKey(HaeyeonSlot.Ring) && expensiveRing.Purchases[HaeyeonSlot.Weapon] == 20
               && expensiveRing.Cost == 20 * 12_000 + 3 * 7_000 && expensiveRing.ReachedResistance == 7_802,
            "반지가 비싸지면 반지 대신 무기 20개 + 방어구·목걸이 3개로 바뀜");
        var cheapWeapon = HaeyeonCostPlanner.Plan(7_200, 7_256, With(HaeyeonSlot.Weapon, 1_000m));
        Assert(cheapWeapon.Purchases.Keys.SequenceEqual([HaeyeonSlot.Weapon]) && cheapWeapon.Cost == 2_000, "무기가 싸면 무기만 사서 해결");

        // 현재 7,228: 대표 상태는 무기 1개(12,000). 목표 7,242는 처음부터면 반지 2개(14,000)지만, 무기를 되돌리지 않으므로 +14 한 번(7,000)이 필요
        var kept = HaeyeonCostPlanner.Plan(7_228, 7_242, s_TestPrices);
        var fromScratch = HaeyeonCostPlanner.Plan(7_200, 7_242, s_TestPrices).Cost - HaeyeonCostPlanner.Plan(7_200, 7_228, s_TestPrices).Cost;
        Assert(kept.EstimatedCurrent[HaeyeonSlot.Weapon] == 1 && kept.EstimatedCurrent.Where(x => x.Key != HaeyeonSlot.Weapon).All(x => x.Value == 0)
               && kept.Cost == 7_000 && kept.ReachedResistance == 7_242 && fromScratch == 2_000,
            "추정한 현재 상태(무기 1개)를 유지한 채 추가 비용 계산(처음부터 목표 − 처음부터 현재 = 2,000을 쓰지 않음)");
        var mid = HaeyeonCostPlanner.Plan(7_242, 7_800, s_TestPrices);
        Assert(mid.EstimatedCurrent[HaeyeonSlot.Ring] == 2 && mid.Purchases.All(x => x.Value > 0 && mid.EstimatedCurrent[x.Key] + x.Value <= HaeyeonCostRules.MaxCount(x.Key))
               && mid.Cost == 18 * 7_000 + 2 * 12_000 && mid.ReachedResistance == 7_802,
            "현재 7,242(반지 2개로 추정)에서 반지 18개 + 무기 2개만 추가 구매");

        var again = HaeyeonCostPlanner.Plan(7_200, 7_800, s_TestPrices);
        Assert(again.Cost == plan.Cost && again.ReachedResistance == plan.ReachedResistance && again.Purchases.OrderBy(x => x.Key).SequenceEqual(plan.Purchases.OrderBy(x => x.Key))
               && HaeyeonCostPlanner.Plan(7_214, 7_400, s_TestPrices).EstimatedCurrent.SequenceEqual(HaeyeonCostPlanner.Plan(7_214, 7_400, s_TestPrices).EstimatedCurrent),
            "같은 입력·시세면 같은 결과");

        var between = HaeyeonCostPlanner.Plan(7_200, 7_801, s_TestPrices);
        var tiny = HaeyeonCostPlanner.Plan(7_200, 7_210, s_TestPrices);
        Assert(between.ReachedResistance == 7_802 && between.Cost == 164_000 && tiny.ReachedResistance == 7_214 && tiny.Cost == 7_000,
            "목표가 14 간격 사이여도 목표 이상(7,801 → 7,802, 7,210 → 7,214)에 도달");

        Assert(HaeyeonCostPlanner.Validate(7_199, 7_800) == HaeyeonCostInputError.OutOfRange && HaeyeonCostPlanner.Validate(7_200, 10_001) == HaeyeonCostInputError.OutOfRange
               && HaeyeonCostPlanner.Validate(10_014, 10_000) == HaeyeonCostInputError.OutOfRange && HaeyeonCostPlanner.Validate(7_201, 7_800) == HaeyeonCostInputError.OffGrid
               && HaeyeonCostPlanner.Validate(7_214, 7_801) == HaeyeonCostInputError.None && HaeyeonCostPlanner.Validate(10_000, 10_000) == HaeyeonCostInputError.None,
            "지원 범위(7,200~10,000) 밖과 14 간격이 아닌 현재 입력 판정(목표는 간격 무관)");

        var fractional = new HaeyeonCostPlan(7_200, 7_228, 7_228, s_TestPrices.ToDictionary(x => x.Key, _ => 0), new Dictionary<HaeyeonSlot, int>(), 10_000.0001m);
        var thirds = HaeyeonCostPlanner.Plan(7_200, 7_256, With(HaeyeonSlot.Weapon, 30_001m / 3));
        Assert(fractional.Cost == 10_001 && thirds.ExactCost == 2 * (30_001m / 3) && thirds.Cost == 20_001,
            "평균 단가는 반올림하지 않고 합산한 뒤 최종 비용만 올림(10,000.33… × 2 = 20,000.67 → 20,001)");
    }

    private static void PricingTests()
    {
        var latest = TestLatest();
        var complete = HaeyeonCostPricing.Average(HaeyeonMarketRules.SelectRecipes(Crafting()), latest);
        Assert(complete.IsComplete && complete.Prices[HaeyeonSlot.Weapon] is { Average: 12_000m, ItemCount: 2 }
               && complete.Prices[HaeyeonSlot.Helmet] is { Average: 7_000m, ItemCount: 3 } && complete.Prices[HaeyeonSlot.Ring].ItemCount == 1
               && complete.CollectedAtUtc == s_Collected, "무기 종류별·방어구 부위별 재질 평균과 품목 수·수집 시각 보존");

        var prices = latest.Prices.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        prices["해연의 숏소드ZZ"] = P("해연의 숏소드ZZ", 10_000, count: 1_000);
        prices["해연의 그레이트 소드ZZ"] = P("해연의 그레이트 소드ZZ", 10_001, count: 3);
        prices["해연의 비늘 갑옷 투구ZZ"] = P("해연의 비늘 갑옷 투구ZZ", 1, soldOut: true);
        prices["해연의 가죽 갑옷 투구ZZ"] = P("해연의 가죽 갑옷 투구ZZ", 0);
        prices["해연의 전투복 투구ZZ"] = P("해연의 전투복 투구ZZ", 9_000);
        prices["해연의 비늘 갑옷 상의ZZ"] = P("해연의 비늘 갑옷 상의ZZ", 1, count: 2);
        prices.Remove("해연의 가죽 갑옷 상의ZZ");
        prices.Remove("해연의 페리도트 링ZZ");
        // 같은 레시피가 두 번 들어와도 한 번만 셉니다.
        var recipes = HaeyeonMarketRules.SelectRecipes(Crafting()).Concat([new CraftingRecipe("해연의 숏소드ZZ", [new("백금강괴", 1)], "무기")]);
        var mixed = HaeyeonCostPricing.Average(recipes, latest with { Prices = prices });
        Assert(mixed.Prices[HaeyeonSlot.Weapon] is { Average: 10_000.5m, ItemCount: 2 }, "같은 품목은 한 번만, 거래 수량 가중 없이 산술평균");
        Assert(mixed.Prices[HaeyeonSlot.Helmet] is { Average: 9_000m, ItemCount: 1 }, "매진·0원 시세는 평균에서 제외");
        Assert(mixed.Prices[HaeyeonSlot.Top] is { Average: 7_000m, ItemCount: 1 }, "매물 3개 미만과 시세 누락은 0데카로 치지 않고 제외");
        Assert(!mixed.IsComplete && mixed.Missing.SequenceEqual([HaeyeonSlot.Ring]) && !mixed.Prices.ContainsKey(HaeyeonSlot.Ring),
            "유효 시세가 하나도 없는 그룹은 추정 불가로 표시");

        var unmapped = HaeyeonCostPricing.Average(HaeyeonMarketRules.SelectRecipes(Crafting(("해연의 신규 갑옷 투구ZZ", "방어구"), ("해연의 사파이어 링ZZ", "장신구"), ("백금강괴 묶음", "무기"))), latest);
        Assert(unmapped.Unmapped.SequenceEqual(["해연의 신규 갑옷 투구ZZ", "해연의 사파이어 링ZZ"]) && unmapped.Prices[HaeyeonSlot.Helmet].ItemCount == 3,
            "부위 매핑이 없는 해연 방어구·장신구는 이름으로 추측하지 않고 평균에서 제외");
        Assert(HaeyeonCostPricing.SlotOf(new CraftingRecipe("해연의 페리도트 네크리스ZZ", [new("백금강괴", 1)], "방어구")) is null,
            "매핑된 이름이라도 시트 분류가 다르면 부위를 정하지 않음");
    }

    private static void ResponseTests()
    {
        var response = HaeyeonCostCommand.Build(7_200, 7_800, Crafting(), TestLatest(), "모비라이프 제공", s_Now);
        var embed = response.Embed!;
        var text = embed.Description + string.Join("\n", embed.Fields.Select(f => f.Name + "\n" + f.Value)) + embed.Footer!.Value.Text;
        Assert(embed.Title == "해연 비용 계산" && embed.Description.Contains("현재 7,200 → 목표 7,800") && embed.Description.Contains("예상 도달 저항: 7,802")
               && embed.Description.Contains("예상 추가 비용: **약 164,000데카**"), "현재·목표·도달 저항과 예상 비용을 쉼표로 표시");
        Assert(embed.Fields[0].Value == "• 무기 2개\n• 반지 20개" && embed.Fields[1].Value == "• 무기 12,000데카\n• 반지 7,000데카",
            "추가 구매 수량과 적용 평균 단가를 줄 목록으로 표시(표 미사용)");
        Assert(text.Contains(HaeyeonCostCommand.EstimateNotice) && text.Contains(HaeyeonCostCommand.PremiseNotice)
               && text.Contains("시세 수집: 2026-10-19 22:00 KST") && embed.Footer.Value.Text.Contains("모비라이프 제공") && !text.Contains("⚠️"),
            "응답에 추정 전제·시세 수집 시각(KST)·모비라이프 출처 포함");
        var stale = HaeyeonCostCommand.Build(7_200, 7_800, Crafting(), TestLatest(), "모비라이프 제공", s_Now.AddHours(2)).Embed!;
        Assert(stale.Fields.Any(f => f.Value.Contains("시세 수집: 2026-10-19 22:00 KST · ⚠️ 최근 수집에 실패해 이전 시세를 사용했어요")),
            "최근 정각 수집을 놓친 이전 시세면 수집 시각과 오래된 시세임을 표시");

        var done = HaeyeonCostCommand.Build(7_802, 7_500, Crafting(), null, "모비라이프 제공", s_Now).Embed!;
        Assert(done.Description.Contains("추가 비용은 0데카"), "목표가 현재 이하면 시세 없이도 0데카");
        Assert(HaeyeonCostCommand.Build(7_100, 7_800, Crafting(), TestLatest(), "모비라이프 제공", s_Now).Message is { } range
               && range.Contains("7,200~10,000") && range.Contains("10성 룬"), "범위 밖이면 지원 범위와 계산 전제를 안내");
        Assert(HaeyeonCostCommand.Build(7_210, 7_800, Crafting(), TestLatest(), "모비라이프 제공", s_Now).Message is { } grid
               && grid.Contains("계산 전제와 맞지 않아요") && grid.Contains("14 단위"), "현재 입력이 14 간격이 아니면 반올림하지 않고 전제 불일치를 안내");
        Assert(HaeyeonCostCommand.Build(7_200, 7_800, Crafting(), null, "모비라이프 제공", s_Now).Message == "아직 해연 시세를 준비하지 못했어요. 정각 시세 수집 이후에 다시 시도해 주세요.",
            "시세 캐시가 없으면 준비 전 안내");
        var noRing = TestLatest();
        noRing = noRing with { Prices = noRing.Prices.Where(x => x.Key != "해연의 페리도트 링ZZ").ToDictionary(x => x.Key, x => x.Value) };
        Assert(HaeyeonCostCommand.Build(7_200, 7_800, Crafting(), noRing, "모비라이프 제공", s_Now).Message is { } missing
               && missing.Contains("반지의 유효한 해연 시세가 없어") && missing.Contains("모비라이프 제공"), "필요 그룹 시세가 없으면 0데카로 대체하지 않고 추정 불가 안내");
    }

    private static async Task CacheTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "molly-haeyeon-cost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HaeyeonMarketStore(Path.Combine(directory, "database", "molly.sqlite"));
            await store.InitializeAsync();
            await store.SaveRunAsync(s_Collected, TestLatest().Prices.Values.Select(x => new HaeyeonPriceSnapshot(x, true)),
                new Dictionary<ulong, IReadOnlyDictionary<string, ItemPriceState>>(), [], []);
            // 명령은 시세 공급자(IMarketPriceSource)를 받지 않고 저장소의 마지막 정각 시세만 읽습니다.
            var latest = await store.LoadLatestPricesAsync();
            var embed = HaeyeonCostCommand.Build(7_200, 7_800, Crafting(), latest, "모비라이프 제공", s_Now).Embed!;
            Assert(embed.Description.Contains("약 164,000데카") && embed.Timestamp == s_Collected,
                "저장소 캐시(정각 수집 결과)만으로 응답하고 API를 추가로 호출하지 않음");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
