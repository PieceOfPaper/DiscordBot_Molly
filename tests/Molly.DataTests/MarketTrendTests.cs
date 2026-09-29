using Molly.HaeyeonMarket;
using Molly.KeywordMarket;
using Molly.Market;

internal static class MarketTrendTests
{
    private static readonly DateTimeOffset s_Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static MarketPrice P(decimal? h1, decimal? h24, decimal? count, long total = 100, bool soldOut = false, long price = 1000) =>
        new(1, "보물 상자", "아이템", price, total, soldOut, s_Now, h1, h24, 5m, count);

    private static string? Text(MarketPrice price) => MarketTrendEvaluator.Evaluate(price)?.Emoji;

    public static void Run()
    {
        Assert(Text(P(-2, -5, 10)) == "📉⏬", "1시간·24시간 모두 하락하고 매물이 늘면 하락 가능성 높음");
        Assert(Text(P(-2, -5, -10)) == "📉🔽", "1시간·24시간 모두 하락하고 매물이 줄면 하락 가능성 낮음");
        Assert(Text(P(2, 5, -10)) == "📈⏫", "1시간·24시간 모두 상승하고 매물이 줄면 상승 가능성 높음");
        Assert(Text(P(2, 5, 10)) == "📈🔼", "1시간·24시간 모두 상승하고 매물이 늘면 상승 가능성 낮음");
        Assert(Text(P(2, 5, 0)) == "📈" && Text(P(-2, -5, null)) == "📉", "가격 방향은 같고 매물 변화가 0이거나 없으면 방향만 표시");
        Assert(Text(P(2, -5, 10)) is null && Text(P(-2, 5, -10)) is null, "1시간과 24시간 방향이 다르면 표시하지 않음");
        Assert(Text(P(1, 1, 10)) == "📈🔼" && Text(P(-1, -1, 10)) == "📉⏬",
            "등락 절댓값이 정확히 1%면 유의미한 변화로 봄(경계값)");
        Assert(Text(P(0.99m, 5, 10)) is null && Text(P(-5, -0.5m, 10)) is null && Text(P(0, 0, 10)) is null, "둘 중 하나라도 1% 미만이면 표시하지 않음");
        Assert(Text(P(null, -5, 10)) is null && Text(P(-2, null, 10)) is null, "1시간 또는 24시간 등락 값이 없으면 표시하지 않음");
        Assert(Text(P(-2, -5, 10, soldOut: true)) is null && Text(P(-2, -5, 10, price: 0)) is null && Text(P(-2, -5, 10, total: 2)) is null &&
               Text(P(-2, -5, 10, total: 3)) is not null, "매진·최저가 0 이하·매물 3개 미만이면 표시하지 않음");
        Assert(MarketTrendEvaluator.Evaluate(P(-2, -5, 10))!.Reason == "가격은 내리고 매물은 늘고 있어요." &&
               MarketTrendEvaluator.Evaluate(P(2, 5, 10))!.Reason == "가격은 오르고 있지만 매물도 늘고 있어요.", "판정 이유 문장");

        // 판정 변화 알림: 이모지가 바뀌면 표시 없음으로의 전환까지 모두 알림
        var previous = new Dictionary<long, MarketPrice>
        {
            [1] = P(-2, -5, 10) with { KindId = 1, Name = "가" },
            [2] = P(-2, -5, 10) with { KindId = 2, Name = "나" },
            [3] = P(0, 0, 0) with { KindId = 3, Name = "다" },
            [4] = P(2, 5, 10) with { KindId = 4, Name = "라" },
            [5] = P(-2, -5, 10) with { KindId = 5, Name = "마" },
        };
        var changes = MarketTrendEvaluator.EvaluateChanges(
        [
            P(-2, -5, -10) with { KindId = 1, Name = "가" },  // 📉⏬ → 📉🔽 가능성 변화
            P(0, 0, 0) with { KindId = 2, Name = "나" },      // 📉⏬ → ➖ 판정 사라짐
            P(2, 5, -10) with { KindId = 3, Name = "다" },    // ➖ → 📈⏫ 판정 생김
            P(-2, -5, 10) with { KindId = 4, Name = "라" },   // 📈🔼 → 📉⏬ 방향 변화
            P(-3, -9, 50) with { KindId = 5, Name = "마" },   // 📉⏬ 그대로(수치만 다름)
            P(-2, -5, 10) with { KindId = 6, Name = "바" },   // 직전 회차에 없음
        ], previous, x => x.KindId);
        Assert(changes.Select(x => $"{x.Name} {x.ChangeText}").SequenceEqual(["가 📉⏬ → 📉🔽", "나 📉⏬ → ➖", "다 ➖ → 📈⏫", "라 📈🔼 → 📉⏬"]),
            "상승·하락·가능성 이모지가 바뀌면 표시 없음으로의 전환까지 모두 알리고, 같은 이모지나 직전 회차에 없던 아이템은 알리지 않음");
        Assert(MarketTrendEvaluator.EvaluateChanges([P(-2, -5, 10)], (IReadOnlyDictionary<long, MarketPrice>?)null, x => x.KindId).Count == 0,
            "직전 회차 시세가 없으면(첫 수집) 판정 변화를 알리지 않음");

        // 해연 알림 메시지: 시세 변동 줄 끝 이모지, 상승·하락 변화 구역, 범례
        var haeyeon = new HaeyeonEvaluation(
            [new PriceChangeAlert("해연의 숏소드ZZ", true, 1000, 800) { Trend = MarketTrendEvaluator.Evaluate(P(-2, -5, 10)) }], [],
            new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], [])
            { TrendAlerts = [new MarketTrendChangeAlert(P(2, 5, -10) with { Name = "백금강괴", MinPrice = 120 }, null, MarketTrendEvaluator.Evaluate(P(2, 5, -10)))] };
        var haeyeonText = HaeyeonMarketMessages.BuildAlertEmbeds(haeyeon, s_Now, "모비라이프 제공")[0].Description;
        Assert(haeyeon.HasAlerts && haeyeonText.Contains("해연의 숏소드ZZ 1,000 → 800 (-20.0%) · 📉⏬") &&
               haeyeonText.Contains("**🔀 상승·하락 변화**\n백금강괴 · 120 · ➖ → 📈⏫") && haeyeonText.EndsWith(MarketTrendEvaluator.NoneLegend),
            "해연 알림에 시세 변동의 상승·하락 이모지와 판정 변화 구역, 이모지 설명을 붙임");
        var plain = new HaeyeonEvaluation([new PriceChangeAlert("해연의 숏소드ZZ", true, 1000, 800)], [],
            new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], []);
        Assert(!HaeyeonMarketMessages.BuildAlertEmbeds(plain, s_Now, "모비라이프 제공")[0].Description.Contains("-# "),
            "이모지가 없는 알림에는 설명을 붙이지 않음");

        // 조회 화면 적용
        var lines = KeywordMarketMessages.BuildPriceLines([P(-2, -5, 10) with { Name = "하락 상자" }, P(0, 0, 0) with { KindId = 2, Name = "보합 상자" }]);
        Assert(lines.SequenceEqual(["보합 상자 · **1,000** (매물 100개)", "하락 상자 · **1,000** (매물 100개) · 📉⏬", "", .. MarketTrendEvaluator.Legend]),
            "/상자시세·/패키지시세 목록에 변화 이모지만 붙이고 목록 아래에 범례를 붙임. 판정하지 않으면 기존 줄 그대로");
        Assert(KeywordMarketMessages.BuildPriceLines([P(0, 0, 0)]).SequenceEqual(["보물 상자 · **1,000** (매물 100개)"]),
            "변화 이모지가 하나도 없으면 범례를 붙이지 않음");
        var recipes = HaeyeonMarketRules.SelectRecipes(Molly.Crafting.CraftingCsvReader.Parse("""
            "이름","분류","재료1"
            "해연의 숏소드ZZ","무기","백금강괴/5"
            """, s_Now));
        var prices = new Dictionary<string, MarketPrice>
        {
            ["해연의 숏소드ZZ"] = P(3, 4, -1) with { Name = "해연의 숏소드ZZ" },
            ["백금강괴"] = P(0.5m, -3, 1) with { Name = "백금강괴", MinPrice = 100 },
        };
        Assert(HaeyeonMarketReport.BuildLines(HaeyeonPriceView.Weapons, recipes, prices).SequenceEqual(["해연의 숏소드ZZ · **1,000** (매물 100개) · 📈⏫", "", .. MarketTrendEvaluator.Legend]) &&
               HaeyeonMarketReport.BuildLines(HaeyeonPriceView.Materials, recipes, prices).Last() == "백금강괴 · **100** (매물 100개)",
            "/해연시세 아이템·재료 목록에 같은 판정기로 변화 이모지를 붙이고, 이모지가 없는 목록에는 범례를 붙이지 않음");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
