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

        StepTests();

        // 해연 알림 메시지: 시세 변동 줄에는 즉시 판정 이모지를 붙이지 않고, 확정 흐름 구역만 안내
        var haeyeon = new HaeyeonEvaluation(
            [new PriceChangeAlert("해연의 숏소드ZZ", true, 1000, 800)], [],
            new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], [])
            {
                TrendAlerts =
                [
                    new MarketTrendFlowAlert(P(2, 5, -10) with { Name = "백금강괴", MinPrice = 120 }, MarketTrendFlow.None, MarketTrendFlow.Up),
                    new MarketTrendFlowAlert(P(2, 5, -10) with { Name = "특급 목재", MinPrice = 90 }, MarketTrendFlow.Up, MarketTrendFlow.Down),
                ],
            };
        var haeyeonText = HaeyeonMarketMessages.BuildAlertEmbeds(haeyeon, s_Now, "모비라이프 제공")[0].Description;
        Assert(haeyeon.HasAlerts && haeyeonText.Contains("해연의 숏소드ZZ 1,000 → 800 (-20.0%)\n") &&
               haeyeonText.Contains("**🔀 상승·하락 흐름**\n📈 백금강괴 · 120 · 상승 흐름\n📉 특급 목재 · 90 · 상승 → 하락 흐름") &&
               haeyeonText.EndsWith(MarketTrendAlertEvaluator.SectionNote) && !haeyeonText.Contains("⏫") && !haeyeonText.Contains(MarketTrendEvaluator.Legend[0]),
            "해연 알림은 시세 변동 줄에 가능성 이모지·범례를 붙이지 않고, 확정된 흐름만 새 흐름·방향 전환으로 안내");
        var plain = new HaeyeonEvaluation([new PriceChangeAlert("해연의 숏소드ZZ", true, 1000, 800)], [],
            new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], []);
        Assert(!HaeyeonMarketMessages.BuildAlertEmbeds(plain, s_Now, "모비라이프 제공")[0].Description.Contains("-# "),
            "확정 흐름이 없는 알림에는 안내 문구를 붙이지 않음");

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

    // 추세 알림 연속 확인(3회) 규칙. 정각 회차마다 방향만 비교합니다.
    private static void StepTests()
    {
        var hour = new DateTimeOffset(2026, 9, 30, 1, 0, 0, TimeSpan.Zero);
        MarketTrendAlertState? state = null;
        var alerts = new List<string>();
        // flows를 정각마다 차례로 넣고, 알림이 나간 회차를 "시:이전→현재"로 모읍니다.
        void Run(params MarketTrendFlow[] flows)
        {
            foreach (var flow in flows)
            {
                state = MarketTrendAlertEvaluator.Step(state, "가", flow, hour, out var from);
                if (from is { } f) alerts.Add($"{hour.Hour}:{f}→{state.Confirmed}");
                hour = hour.AddHours(1);
            }
        }
        const MarketTrendFlow Up = MarketTrendFlow.Up, Down = MarketTrendFlow.Down, None = MarketTrendFlow.None;

        Run(Down, Down);
        Assert(alerts.Count == 0 && state is { Candidate: Down, CandidateCount: 2, Confirmed: None }, "상승·하락이 3회 연속 확인되기 전에는 추세 알림을 보내지 않음");
        Run(Down);
        Assert(alerts.SequenceEqual(["3:None→Down"]) && state is { CandidateCount: 3, Confirmed: Down, LastAlerted: Down }, "같은 방향 3회 연속이면 확정하고 한 번 알림");
        Run(Down, Down);
        Assert(alerts.Count == 1 && state!.CandidateCount == 3, "이미 확정된 방향이 이어지면 다시 알리지 않고 횟수는 3에서 멈춤");
        Run(None, Down, None, None, Down);
        Assert(alerts.Count == 1 && state is { Confirmed: Down, Candidate: Down, CandidateCount: 1 }, "표시 없음 1~2회는 확정 방향을 해제하지 않고, 표시 없음이 생기거나 사라져도 알리지 않음");
        Run(Up, Up);
        Assert(alerts.Count == 1 && state!.Confirmed == Down, "반대 방향 2회까지는 확정 방향을 유지");
        Run(Up);
        Assert(alerts.Last() == "13:Down→Up" && state!.Confirmed == Up, "반대 방향이 3회 연속이면 방향 전환 알림");
        Run(None, None, None);
        Assert(alerts.Count == 2 && state is { Confirmed: None, LastAlerted: None, Candidate: None, CandidateCount: 3 }, "표시 없음 3회 연속이면 알림 없이 확정 방향만 해제");
        Run(Up, Up, Up);
        Assert(alerts.Last() == "19:None→Up", "해제 뒤 같은 방향이 다시 3회 확인되면 새 흐름으로 알림");

        // 가능성 단계는 연속 횟수에 영향을 주지 않음: 📉⏬ → 📉🔽 → 📉⏬
        var prices = new[] { P(-2, -5, 10), P(-2, -5, -10), P(-2, -5, 10) };
        var states = (IReadOnlyDictionary<string, MarketTrendAlertState>)new Dictionary<string, MarketTrendAlertState>();
        var flowAlerts = new List<MarketTrendFlowAlert>();
        var at = s_Now;
        foreach (var price in prices)
        {
            var result = MarketTrendAlertEvaluator.Evaluate([price], states, x => x.Name, at);
            flowAlerts.AddRange(result.Alerts);
            states = result.States;
            at = at.AddHours(1);
        }
        Assert(flowAlerts.Single() is { Previous: None, Current: Down } && flowAlerts[0].Price == prices[2],
            "가능성 높음·낮음만 바뀌어도 같은 방향으로 세고, 가능성 변화만으로는 알리지 않음");

        // 회차 연속성
        var two = new MarketTrendAlertState("가", Down, 2, s_Now, None, None);
        var retried = MarketTrendAlertEvaluator.Step(two, "가", Down, MarketSchedule.HourOf(s_Now.AddMinutes(10)), out var retryAlert);
        Assert(retried == two && retryAlert is null, "같은 정각 회차의 재시도 성공은 연속 횟수를 올리지 않음(한 회차에 한 번)");
        Assert(MarketTrendAlertEvaluator.Step(two, "가", Down, s_Now.AddHours(-1), out _) == two, "이미 평가한 회차보다 이전 회차는 상태를 바꾸지 않음");
        var skipped = MarketTrendAlertEvaluator.Step(two, "가", Down, s_Now.AddHours(2), out var skippedAlert);
        Assert(skipped is { CandidateCount: 1 } && skippedAlert is null, "정각 성공 회차가 빠지면 다음 성공 회차는 후보 1회부터 다시 시작");
        Assert(MarketSchedule.HourOf(new DateTimeOffset(2026, 9, 30, 13, 3, 0, TimeSpan.FromHours(9))) == new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero),
            "수집 시각은 해당 정각 회차로 봄(13:03 KST 수집 → 13:00 회차)");

        var kept = MarketTrendAlertEvaluator.Evaluate([P(-2, -5, 10) with { Name = "나" }],
            new Dictionary<string, MarketTrendAlertState> { ["가"] = two }, x => x.Name, s_Now.AddHours(1));
        Assert(kept.States["가"] == two && kept.States["나"].CandidateCount == 1, "이번 응답에 없는 아이템은 이전 상태를 그대로 둠");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
