using System.Net;
using Discord;
using Molly.Crafting;
using Molly.HaeyeonMarket;
using Molly.Market;
using Molly.MobiLife;

internal static class HaeyeonMarketTests
{
    // 2026-09 실제 제작 시트 형식(gviz CSV)에서 행만 줄인 샘플
    private const string CraftCsv = """
        "이름","재료1","재료2","재료3","재료4","재료5","재료6","재료7","재료8"
        "해연의 숏소드ZZ","백금강괴/5","특급 목재/5","포식의 마력석/15","망령의 영혼석/80","","","",""
        "해연의 페리도트 링ZZ","세공된 페리도트ZZ/1","백금강괴/4","포식의 마력석/8","","","","",""
        "다른 장비","백금강괴/1","","","","","","",""
        """;

    public static async Task RunAsync()
    {
        CraftingTests();
        EvaluatorTests();
        ReportTests();
        await MonitorTestsAsync();
        await MobiLifeSourceTestsAsync();
    }

    private static void CraftingTests()
    {
        var table = CraftingCsvReader.Parse(CraftCsv, DateTimeOffset.UtcNow);
        Assert(table.Items.Count == 3 && table.ByName["해연의 숏소드ZZ"].Ingredients.Count == 4 &&
               table.ByName["해연의 숏소드ZZ"].Ingredients[2] == new CraftingIngredient("포식의 마력석", 15), "제작 시트의 '재료명/수량'을 레시피로 해석하고 빈 재료 칸은 건너뜀");
        Assert(HaeyeonMarketRules.SelectRecipes(table).Select(x => x.Name).SequenceEqual(["해연의 숏소드ZZ", "해연의 페리도트 링ZZ"]), "해연의로 시작하는 레시피만 선택");

        foreach (var (csv, name) in new[]
        {
            ("\"이름\",\"재료1\"\n\"A\",\"B/0\"", "수량 0 거부"),
            ("\"이름\",\"재료1\"\n\"A\",\"B\"", "수량 없는 재료 거부"),
            ("\"이름\",\"재료1\"\n\"A\",\"B/1\"\n\"A\",\"C/1\"", "중복 레시피 이름 거부"),
            ("\"이름\",\"재료1\",\"재료2\"\n\"A\",\"B/1\",\"B/2\"", "한 레시피 안 중복 재료 거부"),
            ("\"이름\",\"재료1\"\n\"A\",\"\"", "재료 없는 레시피 거부"),
            ("\"아이템\",\"재료1\"\n\"A\",\"B/1\"", "이름 헤더 없는 시트 거부"),
        })
        {
            var rejected = false;
            try { CraftingCsvReader.Parse(csv, DateTimeOffset.UtcNow); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "제작 시트 검증: " + name);
        }
    }

    private static readonly DateTimeOffset s_Now = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);

    private static MarketPrice P(string name, long price, long count = 100, bool soldOut = false) =>
        new(name.GetHashCode(), name, "아이템", price, count, soldOut, s_Now);

    private static Dictionary<string, MarketPrice> Prices(params MarketPrice[] prices) => prices.ToDictionary(x => x.Name, StringComparer.Ordinal);

    private static void EvaluatorTests()
    {
        var recipes = HaeyeonMarketRules.SelectRecipes(CraftingCsvReader.Parse(CraftCsv, s_Now));
        var tracked = HaeyeonMarketEvaluator.TrackedNames(recipes);
        Assert(tracked.Count == 6 && tracked["해연의 숏소드ZZ"] && !tracked["백금강괴"] && !tracked.ContainsKey("세공된 페리도트ZZ"),
            "추적 대상은 해연 제작품과 재료이며 무가치 재료(세공된 페리도트ZZ)는 제외");

        // 숏소드 재료 합계: 백금강괴 5×100 + 특급 목재 5×100 + 포식 15×100 + 망령 80×10 = 3,300
        // 링 재료 합계: 페리도트 0 + 백금강괴 4×100 + 포식 8×100 = 1,200
        var initial = Prices(P("해연의 숏소드ZZ", 4000), P("해연의 페리도트 링ZZ", 1000),
            P("백금강괴", 100), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10));
        var first = HaeyeonMarketEvaluator.Evaluate(recipes, initial, new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), s_Now);
        Assert(!first.HasAlerts && first.PriceStates["해연의 숏소드ZZ"] is { BaselinePrice: 4000, CurrentPrice: 4000 },
            "처음에는 현재시세를 과거시세로 두고 알리지 않음");
        Assert(first.CraftStates["해연의 숏소드ZZ"] is { Advantage: CraftAdvantage.Craft, MaterialCost: 3300 } &&
               first.CraftStates["해연의 페리도트 링ZZ"] is { Advantage: CraftAdvantage.Buy, MaterialCost: 1200 },
            "무가치 재료를 0으로 계산해 제작·구매 유불리 초기 상태 저장");

        var later = s_Now.AddHours(1);
        // 제작품 +9%는 알리지 않고 현재시세만 갱신, 재료 +19%는 알리지 않음
        var small = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 4360), P("해연의 페리도트 링ZZ", 1000), P("백금강괴", 119), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10)),
            first.PriceStates, first.CraftStates, later);
        Assert(small.PriceAlerts.Count == 0 && small.PriceStates["해연의 숏소드ZZ"] is { BaselinePrice: 4000, CurrentPrice: 4360 } &&
               small.PriceStates["백금강괴"] is { BaselinePrice: 100, CurrentPrice: 119 }, "기준 미만 변동은 과거시세를 유지하고 현재시세만 갱신");

        // 제작품 -10%, 재료 +20%는 알리고 과거시세를 현재시세로 갱신
        var big = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 3600), P("해연의 페리도트 링ZZ", 1000), P("백금강괴", 120), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10)),
            small.PriceStates, small.CraftStates, later);
        Assert(big.PriceAlerts.Count == 2 &&
               big.PriceAlerts.Single(x => x.IsProduct) is { Name: "해연의 숏소드ZZ", BaselinePrice: 4000, CurrentPrice: 3600 } &&
               big.PriceAlerts.Single(x => !x.IsProduct) is { Name: "백금강괴", BaselinePrice: 100, CurrentPrice: 120 } &&
               big.PriceStates["해연의 숏소드ZZ"].BaselinePrice == 3600 && big.PriceStates["백금강괴"].BaselinePrice == 120,
            "제작품 10%·재료 20% 이상 변동 시 알리고 과거시세 갱신");

        // 숏소드 재료 합계 3,400 대비 완제품 3,350(-1.5%)은 여유 폭 안이라 유불리를 바꾸지 않음
        var band = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 3350), P("해연의 페리도트 링ZZ", 1000), P("백금강괴", 120), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10)),
            big.PriceStates, big.CraftStates, later);
        Assert(band.CraftAlerts.Count == 0 && band.CraftStates["해연의 숏소드ZZ"] is { Advantage: CraftAdvantage.Craft, ProductPrice: 3350 },
            "제작비와 구매가 차이가 여유 폭(3%) 안이면 유불리 알림 없음");

        // 완제품 3,290(재료 3,400 대비 -3.2%)이면 완제품 구매가 더 저렴해진 것으로 알림
        var switched = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 3290), P("해연의 페리도트 링ZZ", 1000), P("백금강괴", 120), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10)),
            band.PriceStates, band.CraftStates, later);
        Assert(switched.CraftAlerts.Single() is { ProductName: "해연의 숏소드ZZ", Advantage: CraftAdvantage.Buy, ProductPrice: 3290, MaterialCost: 3400 } &&
               switched.CraftStates["해연의 숏소드ZZ"].Advantage == CraftAdvantage.Buy, "완제품 구매가가 재료 합계보다 싸지면 알리고 상태 갱신");

        // 링 재료 합계 1,280 대비 완제품 1,400(+9.4%)이면 제작이 더 저렴해진 것으로 알림
        var craftSwitch = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 3290), P("해연의 페리도트 링ZZ", 1400), P("백금강괴", 120), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10)),
            switched.PriceStates, switched.CraftStates, later);
        Assert(craftSwitch.CraftAlerts.Single() is { ProductName: "해연의 페리도트 링ZZ", Advantage: CraftAdvantage.Craft, MaterialCost: 1280 },
            "완제품 구매가가 재료 합계보다 비싸지면 제작 유리로 알림");

        // 매물 부족·매진·누락 시세는 판정에서 빼고 이전 상태 유지
        var unreliable = HaeyeonMarketEvaluator.Evaluate(recipes,
            Prices(P("해연의 숏소드ZZ", 1000, count: 2), P("해연의 페리도트 링ZZ", 100), P("백금강괴", 120, soldOut: true), P("특급 목재", 100), P("포식의 마력석", 100)),
            craftSwitch.PriceStates, craftSwitch.CraftStates, later);
        Assert(unreliable.PriceAlerts.All(x => x.Name == "해연의 페리도트 링ZZ") &&
               unreliable.UnreliableNames.Order().SequenceEqual(new[] { "백금강괴", "해연의 숏소드ZZ" }.Order()) &&
               unreliable.MissingNames.SequenceEqual(["망령의 영혼석"]) &&
               unreliable.PriceStates["해연의 숏소드ZZ"].CurrentPrice == 3290 &&
               unreliable.CraftAlerts.Count == 0 && unreliable.CraftStates["해연의 페리도트 링ZZ"].Advantage == CraftAdvantage.Craft,
            "매물 부족·매진·누락 시세는 변동·유불리 판정에서 제외하고 이전 상태 유지");
    }

    private static void ReportTests()
    {
        var recipes = HaeyeonMarketRules.SelectRecipes(CraftingCsvReader.Parse(CraftCsv, s_Now));
        // 숏소드 재료 합계 3,300 / 링 재료 합계 1,200(페리도트 0)
        var prices = Prices(P("해연의 숏소드ZZ", 3960), P("해연의 페리도트 링ZZ", 1000, count: 2),
            P("백금강괴", 100), P("특급 목재", 100), P("포식의 마력석", 100), P("망령의 영혼석", 10));

        var products = HaeyeonMarketReport.BuildLines(HaeyeonPriceView.Products, recipes, prices);
        Assert(products.SequenceEqual(["해연의 숏소드ZZ · **3,960** (매물 100개)", "해연의 페리도트 링ZZ · **1,000** (매물 2개, 적음)"]),
            "/해연시세 해연: 해연 아이템 시세를 시트 순서로 표시");

        var materials = HaeyeonMarketReport.BuildLines(HaeyeonPriceView.Materials, recipes, prices);
        Assert(HaeyeonMarketReport.ItemCount(HaeyeonPriceView.Materials, recipes) == 5 && materials.Count(x => x.StartsWith("백금강괴 · ")) == 1 &&
               materials.Contains("세공된 페리도트ZZ · 0 (무가치 재료, 조회하지 않음)"),
            "/해연시세 해연재료: 여러 아이템에 쓰이는 재료도 한 번만 표시하고 무가치 재료는 0으로 표시");
        Assert(materials.SequenceEqual([
                "**[마력석]**", "포식의 마력석 · **100** (매물 100개)", "",
                "**[영혼석]**", "망령의 영혼석 · **10** (매물 100개)", "",
                "**[기타]**", "백금강괴 · **100** (매물 100개)", "특급 목재 · **100** (매물 100개)", "세공된 페리도트ZZ · 0 (무가치 재료, 조회하지 않음)"]),
            "/해연시세 해연재료: 마력석·영혼석·기타 순으로 분류하고 분류 안은 시트 순서 유지");
        Assert(HaeyeonMarketReport.MaterialCategory("마력석 조각") == "마력석" && HaeyeonMarketReport.MaterialCategory("영혼석") == "영혼석" &&
               HaeyeonMarketReport.MaterialCategory("백금강괴") == "기타", "재료 분류는 이름 포함 여부로 판정");

        var totals = HaeyeonMarketReport.BuildLines(HaeyeonPriceView.ProductTotals, recipes, prices);
        Assert(totals[0] == "⚒️ **해연의 숏소드ZZ** 완제품 3,960 / 재료 합계 3,300 · 완제품이 20.0% 높음 → 재료 구매 후 제작이 저렴" &&
               totals[1] == "🛒 **해연의 페리도트 링ZZ** 완제품 1,000 (매물 2개) / 재료 합계 1,200 · 완제품이 16.7% 낮음 → 완제품 구매가 저렴",
            "/해연시세 총해연재료: 아이템별 완제품 시세·재료 합계와 높고 낮은 비율 표시");

        var missing = Prices(P("해연의 숏소드ZZ", 3960), P("백금강괴", 100, soldOut: true), P("특급 목재", 100), P("포식의 마력석", 100));
        var partial = HaeyeonMarketReport.BuildLines(HaeyeonPriceView.ProductTotals, recipes, missing);
        Assert(partial[0] == "❔ **해연의 숏소드ZZ** 완제품 3,960 / 재료 합계 계산 불가(백금강괴, 망령의 영혼석 시세 없음)" &&
               partial[1].StartsWith("❔ **해연의 페리도트 링ZZ** 완제품 시세 없음") &&
               HaeyeonMarketReport.BuildLines(HaeyeonPriceView.Products, recipes, missing)[1] == "해연의 페리도트 링ZZ · 시세 없음",
            "시세 없음·매진 재료가 있으면 재료 합계를 계산하지 않고 이유 표시");

        var kinds = new HashSet<string>();
        for (var seed = 0; seed < 200; seed++)
        {
            var test = HaeyeonMarketReport.BuildTestEvaluation(recipes, prices, new Random(seed))!;
            var alertCount = test.PriceAlerts.Count + test.CraftAlerts.Count;
            if (alertCount is < 1 or > 3 || test.PriceAlerts.Count > 0 && test.CraftAlerts.Count > 0)
                throw new Exception($"테스트 알림 개수·종류 오류 seed={seed}");
            if (test.PriceAlerts.Any(x => x.CurrentPrice != prices[x.Name].MinPrice ||
                    Math.Abs(x.ChangeRate) < HaeyeonMarketRules.ChangeThreshold(x.IsProduct) - 0.001m))
                throw new Exception($"테스트 시세 변동이 기준 미만 seed={seed}");
            if (test.CraftAlerts.Any(x => x.Advantage != (x.ProductPrice >= x.MaterialCost ? CraftAdvantage.Craft : CraftAdvantage.Buy)))
                throw new Exception($"테스트 유불리가 실제 시세와 다름 seed={seed}");
            kinds.Add(test.PriceAlerts.Count > 0 ? "price" : "craft");
        }
        Assert(kinds.SetEquals(["price", "craft"]), "알림 테스트는 저장 시세로 두 알림 중 하나를 무작위로 1~3개 만들고, 가상 변동은 기준 이상");
        Assert(HaeyeonMarketReport.BuildTestEvaluation(recipes, new Dictionary<string, MarketPrice>(), new Random(1)) is null,
            "저장 시세가 없으면 테스트 알림을 만들지 않음");
        var testEmbeds = HaeyeonMarketMessages.BuildAlertEmbeds(HaeyeonMarketReport.BuildTestEvaluation(recipes, prices, new Random(3))!, s_Now, "모비라이프 제공", "[테스트] ");
        Assert(testEmbeds[0].Title.StartsWith("[테스트] 💹 해연 시세 알림") && testEmbeds[0].Footer!.Value.Text.Contains("모비라이프 제공"),
            "테스트 알림은 제목에 [테스트]를 붙이고 출처 표기");
    }

    private static async Task MonitorTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "molly-haeyeon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HaeyeonMarketStore(Path.Combine(directory, "database", "molly.sqlite"));
            await store.InitializeAsync();
            Assert(await store.SetChannelAsync(1, 10, s_Now) is null && (await store.SetChannelAsync(1, 11, s_Now))?.ChannelId == 10 &&
                   await store.SetChannelAsync(2, 20, s_Now) is null, "길드당 알림 채널 하나만 유지하고 다시 등록하면 채널을 옮김");
            var channels = await store.GetChannelsAsync();
            Assert(channels.Count == 2 && channels[0] is { GuildId: 1, ChannelId: 11 }, "등록된 채널 목록 조회");
            Assert((await store.RemoveChannelAsync(2))?.ChannelId == 20 && await store.RemoveChannelAsync(2) is null &&
                   (await store.GetChannelsAsync()).Count == 1, "해제하면 해당 길드 등록만 삭제");

            var now = s_Now;
            var recipes = HaeyeonMarketRules.SelectRecipes(CraftingCsvReader.Parse(CraftCsv, s_Now));
            var source = new FakeSource();
            var sender = new FakeSender();
            var monitor = new HaeyeonMarketMonitor(_ => Task.FromResult(recipes), source, store, sender, () => now, _ => { });
            source.Set(("해연의 숏소드ZZ", 4000), ("해연의 페리도트 링ZZ", 1000), ("백금강괴", 100), ("특급 목재", 100), ("포식의 마력석", 100), ("망령의 영혼석", 10), ("해연의 다른 장비", 5));

            Assert(await store.GetLatestCollectedAtAsync() is null, "시세가 없으면 시작 시 즉시 수집 대상");
            var first = await monitor.CollectAsync(default);
            Assert(first.Success && sender.Sent.Count == 0 && await store.GetLatestCollectedAtAsync() == now &&
                   await store.CountHistoryAsync() == 6 && source.Keywords.SequenceEqual(HaeyeonMarketRules.SearchKeywords),
                "첫 수집은 검색어 5개로 조회해 추적 대상 시세만 저장하고 알리지 않음");

            now = now.AddHours(1);
            source.Set(("해연의 숏소드ZZ", 3000), ("해연의 페리도트 링ZZ", 1000), ("백금강괴", 100), ("특급 목재", 100), ("포식의 마력석", 100), ("망령의 영혼석", 10));
            sender.FailChannel = 99;
            await store.SetChannelAsync(3, 99, now);
            var second = await monitor.CollectAsync(default);
            var states = await store.LoadPriceStatesAsync();
            var crafts = await store.LoadCraftStatesAsync();
            Assert(second.Success && sender.Sent.Count == 1 && sender.Sent[0].Channel.ChannelId == 11 &&
                   sender.Sent[0].Embeds[0].Description.Contains("해연의 숏소드ZZ 4,000 → 3,000 (-25.0%)") &&
                   sender.Sent[0].Embeds[0].Description.Contains("완제품을 사는") &&
                   sender.Sent[0].Embeds[0].Footer!.Value.Text.Contains("모비라이프 제공"),
                "변동·유불리 전환을 등록 채널에 출처와 함께 알리고, 한 채널 전송 실패가 다른 채널을 막지 않음");
            var latest = await store.LoadLatestPricesAsync();
            Assert(latest is { Prices.Count: 6 } && latest.CollectedAtUtc == now && latest.Prices["해연의 숏소드ZZ"].MinPrice == 3000,
                "/해연시세는 마지막 정각 수집의 시세 전체를 읽음");
            Assert(states["해연의 숏소드ZZ"].BaselinePrice == 3000 && crafts["해연의 숏소드ZZ"].Advantage == CraftAdvantage.Buy &&
                   await store.CountHistoryAsync() == 12, "알림 뒤 과거시세·유불리 상태를 DB에 갱신하고 시간별 이력 누적");

            now = now.AddHours(1);
            source.FailKeyword = "영혼석";
            source.Set(("해연의 숏소드ZZ", 9000));
            var failed = await monitor.CollectAsync(default);
            Assert(!failed.Success && sender.Sent.Count == 1 && (await store.LoadPriceStatesAsync())["해연의 숏소드ZZ"].BaselinePrice == 3000 &&
                   await store.GetLatestCollectedAtAsync() == now.AddHours(-1), "검색어 하나라도 실패하면 알림·저장 없이 이번 회차를 건너뜀");

            var restarted = new HaeyeonMarketStore(Path.Combine(directory, "database", "molly.sqlite"));
            await restarted.InitializeAsync();
            Assert(await restarted.GetLatestCollectedAtAsync() is not null && (await restarted.LoadCraftStatesAsync()).Count == 2,
                "재시작 후에도 DB의 시세·상태가 남아 있어 즉시 수집하지 않음");

            // 보관 기간이 지난 이력은 다음 저장 때 정리
            source.FailKeyword = null;
            now = now.Add(HaeyeonMarketRules.HistoryRetention).AddHours(2);
            source.Set(("해연의 숏소드ZZ", 3000), ("해연의 페리도트 링ZZ", 1000), ("백금강괴", 100), ("특급 목재", 100), ("포식의 마력석", 100), ("망령의 영혼석", 10));
            await monitor.CollectAsync(default);
            Assert(await store.CountHistoryAsync() == 6, "보관 기간(90일)이 지난 시간별 이력 삭제");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        Assert(HaeyeonMarketMonitor.NextHourUtc(new DateTimeOffset(2026, 9, 24, 10, 59, 59, 900, TimeSpan.Zero)) == new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero) &&
               HaeyeonMarketMonitor.NextHourUtc(new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero)) == new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero) &&
               HaeyeonMarketMonitor.NextHourUtc(new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.FromHours(9))) == new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.FromHours(9)),
            "다음 수집 시각은 항상 다음 정각(KST 기준으로도 정각)");

        var many = new HaeyeonEvaluation(
            Enumerable.Range(0, 80).Select(i => new PriceChangeAlert($"해연의 아주 긴 이름을 가진 테스트 장비 {i}ZZ", true, 10000, 12000)).ToArray(),
            [], new Dictionary<string, ItemPriceState>(), new Dictionary<string, CraftState>(), [], []);
        var embeds = HaeyeonMarketMessages.BuildAlertEmbeds(many, s_Now, "모비라이프 제공");
        Assert(embeds.Count > 1 && embeds.All(x => x.Description.Length <= HaeyeonMarketMessages.MaxDescriptionLength && x.Title.Contains("12:00 기준")),
            "알림이 많으면 Discord 글자 수 제한 안으로 Embed를 나누고 KST 기준 시각 표시");
    }

    private static async Task MobiLifeSourceTestsAsync()
    {
        var requests = new List<Uri>();
        string Page(int offset, int count) => "{\"data\":[" + string.Join(",", Enumerable.Range(offset, count).Select(i =>
            $$"""{"kind_id":{{i}},"name":"특급 {{i}}","parent_category":"아이템","min_price":{{100 + i}},"total_count":5,"is_sold_out":false,"last_version":"2026-09-24T10:38:00Z"}""")) +
            $$$"""],"pagination":{"limit":100,"offset":{{{offset}}},"count":{{{count}}}}}""";
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["offset"]!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page(offset, offset == 0 ? 100 : 7)) };
        });
        using var client = new MobiLifeApiClient(new MobiLifeOptions { ApiKey = "k" }, handler, log: _ => { });
        var source = new MobiLifeMarketPriceSource(client);
        var result = await source.SearchAsync("특급", default);
        Assert(result.IsSuccess && result.Prices!.Count == 107 && requests.Count == 2 &&
               requests[0].Query.Contains("search=%ED%8A%B9%EA%B8%89") && requests[1].Query.Contains("offset=100") &&
               result.Prices[0] == new MarketPrice(0, "특급 0", "아이템", 100, 5, false, new DateTimeOffset(2026, 9, 24, 10, 38, 0, TimeSpan.Zero)) &&
               source.Attribution == "모비라이프 제공",
            "모비라이프 시세를 페이지 단위로 모두 받아 도메인 모델로 변환");

        using var noKey = new MobiLifeApiClient(new MobiLifeOptions(), handler, log: _ => { });
        var disabled = await new MobiLifeMarketPriceSource(noKey).SearchAsync("특급", default);
        Assert(!disabled.IsSuccess && disabled.FailureMessage!.Contains("꺼져") && requests.Count == 2, "키가 없으면 요청 없이 실패 안내");
    }

    private sealed class FakeSource : IMarketPriceSource
    {
        private Dictionary<string, long> m_Prices = new(StringComparer.Ordinal);
        public List<string> Keywords { get; } = [];
        public string? FailKeyword { get; set; }
        public string Attribution => "모비라이프 제공";

        public void Set(params (string Name, long Price)[] prices) => m_Prices = prices.ToDictionary(x => x.Name, x => x.Price, StringComparer.Ordinal);

        public Task<MarketPriceSearchResult> SearchAsync(string keyword, CancellationToken ct)
        {
            Keywords.Add(keyword);
            if (keyword == FailKeyword) return Task.FromResult(MarketPriceSearchResult.Fail("실패"));
            var matched = m_Prices.Where(x => x.Key.Contains(keyword, StringComparison.Ordinal))
                .Select(x => new MarketPrice(x.Key.GetHashCode(), x.Key, "아이템", x.Value, 100, false, s_Now)).ToArray();
            return Task.FromResult(new MarketPriceSearchResult(matched));
        }
    }

    private sealed class FakeSender : IHaeyeonAlertSender
    {
        public List<(HaeyeonMonitorChannel Channel, IReadOnlyList<Embed> Embeds)> Sent { get; } = [];
        public ulong FailChannel { get; set; }

        public Task SendAsync(HaeyeonMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct)
        {
            if (channel.ChannelId == FailChannel) throw new InvalidOperationException("권한 없음");
            Sent.Add((channel, embeds));
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
