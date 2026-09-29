using System.Net;
using Discord;
using Molly.KeywordMarket;
using Molly.Market;
using Molly.MobiLife;

internal static class KeywordMarketTests
{
    private static readonly DateTimeOffset s_Now = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        EvaluatorTests();
        MessageTests();
        await MonitorTestsAsync();
        await GuildThresholdTestsAsync();
        await MobiLifeSourceTestsAsync();
        await RetryTestsAsync();
    }

    private static async Task RetryTestsAsync()
    {
        var now = new DateTimeOffset(2026, 9, 25, 3, 0, 5, TimeSpan.Zero);
        var delays = new List<TimeSpan>();
        var logs = new List<string>();
        Task Delay(TimeSpan delay, CancellationToken _) { delays.Add(delay); now += delay; return Task.CompletedTask; }

        async Task<(bool Success, int Attempts)> Run(Func<int, bool> succeedsOn)
        {
            delays.Clear();
            var attempts = 0;
            var success = await MarketSchedule.CollectWithRetryAsync(_ => Task.FromResult(succeedsOn(++attempts)), () => now, Delay, logs.Add, default);
            return (success, attempts);
        }

        Assert(await Run(_ => true) == (true, 1) && delays.Count == 0, "수집이 성공하면 재시도하지 않음");
        now = new DateTimeOffset(2026, 9, 25, 3, 0, 5, TimeSpan.Zero);
        Assert(await Run(attempt => attempt == 3) == (true, 3) && delays.SequenceEqual([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)]),
            "실패하면 10분 뒤 다시 시도하고 성공하면 멈춤");
        now = new DateTimeOffset(2026, 9, 25, 3, 0, 5, TimeSpan.Zero);
        Assert(await Run(_ => false) == (false, 1 + MarketSchedule.MaxRetries) && delays.Count == 3 && logs[^1].Contains("다음 정각"),
            "재시도는 최대 3번까지만 하고 다음 정각 수집을 기다림");
        now = new DateTimeOffset(2026, 9, 25, 3, 45, 0, TimeSpan.Zero);
        Assert(await Run(_ => false) == (false, 2) && delays.Count == 1,
            "다음 재시도 시각이 다음 정각에 닿으면 재시도하지 않고 정각 수집에 맡김");
    }

    private static MarketPrice P(long kindId, string name, long price, long count = 100, bool soldOut = false) =>
        new(kindId, name, "아이템", price, count, soldOut, s_Now);

    // 한 길드 기준으로 공용 신규·사라짐 판정과 길드별 시세 변동 판정을 차례로 실행한 결과.
    private sealed record Step(KeywordItemsEvaluation Items, KeywordPriceEvaluation Prices)
    {
        public bool HasAlerts => Items.NewItems.Count > 0 || Items.RemovedItems.Count > 0 || Prices.Alerts.Count > 0;
        public IReadOnlyDictionary<long, KeywordItemState> States => Items.States;
        public IReadOnlyDictionary<long, KeywordBaseline> Baselines => Prices.Baselines;
        public IReadOnlyList<KeywordPriceChangeAlert> PriceAlerts => Prices.Alerts;
        public IReadOnlyList<KeywordNewItemAlert> NewItems => Items.NewItems;
        public IReadOnlyList<KeywordRemovedItemAlert> RemovedItems => Items.RemovedItems;
    }

    private static Step Eval(IReadOnlyList<MarketPrice> prices, Step? previous,
        bool isFirstRun = false, bool canDetectRemoval = true, decimal threshold = KeywordMarketRules.ChangeThreshold)
    {
        var previousStates = previous?.States ?? new Dictionary<long, KeywordItemState>();
        var items = KeywordMarketEvaluator.EvaluateItems(prices, previousStates, isFirstRun, canDetectRemoval, s_Now);
        var priceResult = KeywordMarketEvaluator.EvaluatePrices(prices, previous?.Baselines ?? new Dictionary<long, KeywordBaseline>(),
            previousStates, items.States, threshold, s_Now);
        return new Step(items, priceResult);
    }

    private static void EvaluatorTests()
    {
        var first = Eval([P(1, "보물 상자", 1000), P(2, "빛나는 상자", 500, count: 1)], null, isFirstRun: true);
        Assert(!first.HasAlerts && first.States.Count == 2 && first.States[1] is { CurrentPrice: 1000, MissingRuns: 0 } && first.Baselines[1].Price == 1000 &&
               first.States[2].CurrentPrice == 0 && !first.Baselines.ContainsKey(2),
            "첫 수집은 발견한 아이템을 신규로 알리지 않고 기준으로 저장(매물 부족이면 기준 시세 없음)");

        var added = Eval([P(1, "보물 상자", 1050), P(2, "빛나는 상자", 500), P(3, "신규 상자", 700, soldOut: true)], first);
        Assert(added.NewItems.Single().Price.Name == "신규 상자" && added.PriceAlerts.Count == 0 &&
               added.States[1].CurrentPrice == 1050 && added.Baselines[1].Price == 1000 && added.Baselines[2].Price == 500 &&
               !added.Baselines.ContainsKey(3),
            "이후 처음 보는 아이템은 신규로 알리고, 기준 미만 변동은 현재시세만 갱신, 기준 시세가 없던 아이템은 믿을 만한 시세로 기준 설정");

        var changed = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560), P(3, "신규 상자", 700, soldOut: true)], added);
        Assert(changed.PriceAlerts.Count == 2 &&
               changed.PriceAlerts.Any(x => x is { Name: "보물 상자", BaselinePrice: 1000, CurrentPrice: 900 }) &&
               changed.PriceAlerts.Any(x => x is { Name: "빛나는 상자", BaselinePrice: 500, CurrentPrice: 560 }) &&
               changed.Baselines[1].Price == 900 && !changed.NewItems.Any(),
            "과거시세 대비 10% 이상 변동하면 알리고 과거시세 갱신");

        var loose = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560), P(3, "신규 상자", 700, soldOut: true)], added, threshold: 0.11m);
        Assert(loose.PriceAlerts.Single() is { Name: "빛나는 상자" } && loose.Baselines[1].Price == 1000 && loose.Baselines[2].Price == 560,
            "길드 등락률 11%면 -10%는 알리지 않고 과거시세 유지, +12%는 알림");

        var unreliable = Eval([P(1, "보물 상자", 100, count: 2), P(2, "빛나는 상자", 100, soldOut: true), P(3, "신규 상자", 700, soldOut: true)], changed);
        Assert(!unreliable.HasAlerts && unreliable.States[1].CurrentPrice == 900 && unreliable.Baselines[1].Price == 900,
            "매진·매물 부족 시세는 변동 판정에서 빼고 이전 상태 유지");

        var missingOnce = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560)], changed);
        Assert(!missingOnce.HasAlerts && missingOnce.States[3].MissingRuns == 1,
            "검색 결과에서 한 번 빠진 아이템은 아직 사라짐으로 알리지 않음");
        var back = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560), P(3, "신규 상자", 700, soldOut: true)], missingOnce);
        Assert(!back.HasAlerts && back.States[3].MissingRuns == 0, "일시적으로 빠졌다 돌아온 아이템은 신규로 알리지 않고 누락 횟수 초기화");

        var truncated = Eval([P(1, "보물 상자", 900)], missingOnce, canDetectRemoval: false);
        Assert(!truncated.HasAlerts && truncated.States.Count == 3 && truncated.States[3].MissingRuns == 1,
            "검색 결과가 잘렸을 수 있는 회차는 빠진 아이템을 사라짐으로 세지 않음");

        var removed = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560)], missingOnce);
        Assert(removed.RemovedItems.Single() is { Name: "신규 상자", LastPrice: 0 } && !removed.States.ContainsKey(3),
            "2회 연속 검색되지 않으면 사라짐으로 알리고 추적 상태에서 제거");
        var removedPriced = Eval([P(1, "보물 상자", 900)], Eval([P(1, "보물 상자", 900)], changed));
        Assert(removedPriced.RemovedItems.Any(x => x.Name == "빛나는 상자") && !removedPriced.Baselines.ContainsKey(2),
            "사라진 아이템은 길드 과거시세에서도 제거");

        var reappeared = Eval([P(1, "보물 상자", 900), P(2, "빛나는 상자", 560), P(3, "신규 상자", 800)], removed);
        Assert(reappeared.NewItems.Single().Price.KindId == 3, "사라졌던 아이템이 다시 올라오면 신규로 알림");

        var renamed = Eval([P(1, "보물 상자 (이벤트)", 900), P(2, "빛나는 상자", 560)], removed);
        Assert(!renamed.HasAlerts && renamed.States[1].Name == "보물 상자 (이벤트)", "아이템은 kind_id로 구분하며 이름이 바뀌어도 신규·사라짐으로 보지 않음");

        // 새로 등록한 길드(과거시세 없음)는 직전 수집 시세부터 비교
        var joined = KeywordMarketEvaluator.EvaluatePrices([P(1, "보물 상자", 700)], new Dictionary<long, KeywordBaseline>(), changed.States,
            changed.States, KeywordMarketRules.ChangeThreshold, s_Now);
        Assert(joined.Alerts.Single() is { BaselinePrice: 900, CurrentPrice: 700 } && joined.Baselines[1].Price == 700,
            "과거시세가 없는 길드는 직전 수집 시세를 과거시세로 삼아 비교");
    }

    private static void MessageTests()
    {
        var definition = KeywordMarketRules.Box;
        var evaluation = new KeywordEvaluation(
            [new KeywordPriceChangeAlert("보물 상자", 1000, 1200)],
            [new KeywordNewItemAlert(P(3, "신규 상자", 700, count: 2)), new KeywordNewItemAlert(P(4, "매진 상자", 0, soldOut: true))],
            [new KeywordRemovedItemAlert("기간 종료 상자", 5000), new KeywordRemovedItemAlert("시세 없던 상자", 0)]);
        var embeds = KeywordMarketMessages.BuildAlertEmbeds(definition, evaluation, s_Now, "모비라이프 제공");
        var text = embeds[0].Description;
        Assert(embeds.Count == 1 && embeds[0].Title == "📦 상자 시세 알림 · 2026-09-25 12:00 기준" &&
               text.Contains("🆕 신규 상자 · **700** (매물 2개, 적음)") && text.Contains("🆕 매진 상자 · 매진") &&
               text.Contains("👋 기간 종료 상자 · 마지막 시세 5,000") && text.Contains("👋 시세 없던 상자 · 확인된 시세 없음") &&
               text.Contains("📈 보물 상자 1,000 → 1,200 (+20.0%)") &&
               text.IndexOf("새로 올라온", StringComparison.Ordinal) < text.IndexOf("사라진", StringComparison.Ordinal) &&
               text.IndexOf("사라진", StringComparison.Ordinal) < text.IndexOf("시세 변동", StringComparison.Ordinal) &&
               embeds[0].Footer!.Value.Text == "몰리 • 상자 시세 모니터링 • 데이터: 모비라이프 제공",
            "알림은 신규·사라짐·시세 변동 순서로 KST 기준 시각과 출처를 표기");

        Assert(KeywordMarketMessages.BuildPriceLines([P(2, "나 상자", 0, soldOut: true), P(1, "가 상자", 1500, count: 2), P(3, "다 상자", 20000)]).SequenceEqual([
                "가 상자 · **1,500** (매물 2개, 적음)", "나 상자 · 매진", "다 상자 · **20,000** (매물 100개)"]),
            "/상자시세·/패키지시세: 저장 시세를 이름순으로 매진·매물 적음과 함께 표시");

        var many = new KeywordEvaluation([], Enumerable.Range(0, 120).Select(i => new KeywordNewItemAlert(P(i, $"아주 긴 이름을 가진 테스트 패키지 {i}", 1000))).ToArray(), []);
        var split = KeywordMarketMessages.BuildAlertEmbeds(KeywordMarketRules.Package, many, s_Now, "모비라이프 제공");
        Assert(split.Count > 1 && split.All(x => x.Description.Length <= KeywordMarketMessages.MaxDescriptionLength && x.Title.StartsWith("🎁 패키지 시세 알림") &&
               x.Footer!.Value.Text.Contains("모비라이프 제공")), "알림이 많으면 Discord 글자 수 제한 안으로 Embed를 나누고 모두 출처 표기");

        var prices = new[] { P(1, "A 상자", 1000), P(2, "B 상자", 2000), P(3, "C 상자", 0, soldOut: true), P(4, "D 상자", 4000), P(5, "E 상자", 5000) };
        for (var seed = 0; seed < 100; seed++)
        {
            var test = KeywordMarketMessages.BuildTestEvaluation(prices, new Random(seed))!;
            var names = test.NewItems.Select(x => x.Price.Name).Concat(test.RemovedItems.Select(x => x.Name)).Concat(test.PriceAlerts.Select(x => x.Name)).ToArray();
            if (test.NewItems.Count != 1 || test.RemovedItems.Count != 1 || test.PriceAlerts.Count is < 1 or > 2 || names.Distinct().Count() != names.Length)
                throw new Exception($"테스트 알림 구성 오류 seed={seed}");
            if (test.PriceAlerts.Any(x => x.CurrentPrice != prices.Single(p => p.Name == x.Name).MinPrice || Math.Abs(x.ChangeRate) < KeywordMarketRules.ChangeThreshold - 0.001m))
                throw new Exception($"테스트 시세 변동이 기준 미만 seed={seed}");
        }
        Assert(true, "알림 테스트는 저장 시세로 신규 1개·사라짐 1개·시세 변동 1~2개를 겹치지 않게 만들고 가상 변동은 기준 이상");
        Assert(new[] { 0.01m, 0.30m, 1.00m }.All(threshold => Enumerable.Range(0, 50).All(seed =>
                KeywordMarketMessages.BuildTestEvaluation(prices, new Random(seed), threshold)!.PriceAlerts.All(x => x.BaselinePrice > 0 && Math.Abs(x.ChangeRate) >= threshold))),
            "알림 테스트의 가상 변동은 길드 등락률(1~100%) 이상");
        Assert(KeywordMarketMessages.BuildTestEvaluation([], new Random(1)) is null &&
               KeywordMarketMessages.BuildTestEvaluation([P(1, "A 상자", 1000)], new Random(1)) is { NewItems.Count: 1, RemovedItems.Count: 0 },
            "저장 시세가 없으면 테스트 알림을 만들지 않고, 아이템이 적으면 가능한 알림만 표시");
        var testEmbeds = KeywordMarketMessages.BuildAlertEmbeds(definition, KeywordMarketMessages.BuildTestEvaluation(prices, new Random(2))!, s_Now, "모비라이프 제공", "[테스트] ");
        Assert(testEmbeds[0].Title.StartsWith("[테스트] 📦 상자 시세 알림"), "테스트 알림은 제목에 [테스트]를 붙임");
    }

    private static async Task MonitorTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "molly-keyword-market-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "database", "molly.sqlite");
            var boxStore = new KeywordMarketStore(KeywordMarketRules.Box.Id, databasePath);
            var packageStore = new KeywordMarketStore(KeywordMarketRules.Package.Id, databasePath);
            await boxStore.InitializeAsync();
            await packageStore.InitializeAsync();

            Assert(await boxStore.SetChannelAsync(1, 10, s_Now) is null && (await boxStore.SetChannelAsync(1, 11, s_Now))?.ChannelId == 10 &&
                   await packageStore.SetChannelAsync(1, 30, s_Now) is null, "길드당 알림 채널 하나만 유지하고, 상자·패키지 등록은 서로 독립");
            Assert((await boxStore.GetChannelsAsync()).Single() is { GuildId: 1, ChannelId: 11 } && (await packageStore.GetChannelsAsync()).Single().ChannelId == 30,
                "모니터링별 채널 목록 조회");
            Assert((await packageStore.RemoveChannelAsync(1))?.ChannelId == 30 && await packageStore.RemoveChannelAsync(1) is null &&
                   (await boxStore.GetChannelsAsync()).Count == 1, "패키지 해제는 상자 등록에 영향 없음");

            var now = s_Now;
            var source = new FakeSource();
            var sender = new FakeSender();
            var monitor = new KeywordMarketMonitor(KeywordMarketRules.Box, source, boxStore, sender, () => now, _ => { });
            source.Set((1, "보물 상자", 1000), (2, "빛나는 상자", 500));

            var first = await monitor.CollectAsync(default);
            Assert(first.Success && sender.Sent.Count == 0 && await boxStore.CountLatestPricesAsync() == 2 && await packageStore.CountLatestPricesAsync() == 0 &&
                   source.Calls.Single() == ("상자", "아이템"), "첫 수집은 '아이템' 분류의 '상자' 검색 결과를 기준으로 저장하고 알리지 않음");

            now = now.AddHours(1);
            source.Set((1, "보물 상자", 800), (3, "신규 상자", 300));
            await boxStore.SetChannelAsync(2, 99, now);
            sender.FailChannel = 99;
            var second = await monitor.CollectAsync(default);
            Assert(second.Success && sender.Sent.Count == 1 && sender.Sent[0].Channel.ChannelId == 11 &&
                   sender.Sent[0].Embeds[0].Description.Contains("🆕 신규 상자") && sender.Sent[0].Embeds[0].Description.Contains("보물 상자 1,000 → 800 (-20.0%)") &&
                   !sender.Sent[0].Embeds[0].Description.Contains("빛나는 상자") && sender.Sent[0].Embeds[0].Footer!.Value.Text.Contains("모비라이프 제공"),
                "신규·변동을 등록 채널에 출처와 함께 알리고, 한 채널 전송 실패가 다른 채널을 막지 않음. 한 번 빠진 아이템은 아직 알리지 않음");

            now = now.AddHours(1);
            source.Set((1, "보물 상자", 800), (3, "신규 상자", 300));
            var third = await monitor.CollectAsync(default);
            var states = await boxStore.LoadStatesAsync();
            Assert(third.Success && sender.Sent.Count == 2 && sender.Sent[1].Embeds[0].Description.Contains("👋 빛나는 상자 · 마지막 시세 500") &&
                   states.Keys.Order().SequenceEqual([1L, 3L]) && (await boxStore.LoadBaselinesAsync(1))[1].Price == 800,
                "2회 연속 검색되지 않은 아이템을 사라짐으로 알리고 DB 상태에서 제거");
            var latest = await boxStore.LoadLatestPricesAsync();
            Assert(latest is { Prices.Count: 2 } && latest.CollectedAtUtc == now && await boxStore.CountLatestPricesAsync() == 2 &&
                   latest.Prices.Select(x => x.Name).SequenceEqual(["보물 상자", "신규 상자"]),
                "마지막 정각 수집 시세만 남기고, 검색에서 빠진 아이템의 옛 시세는 지움(회차별 이력은 쌓지 않음)");

            now = now.AddHours(1);
            source.Fail = true;
            var failed = await monitor.CollectAsync(default);
            source.Fail = false;
            source.Set();
            now = now.AddHours(1);
            var empty = await monitor.CollectAsync(default);
            Assert(!failed.Success && !empty.Success && sender.Sent.Count == 2 && (await boxStore.LoadStatesAsync()).Count == 2 &&
                   await boxStore.GetLatestCollectedAtAsync() == now.AddHours(-2),
                "조회 실패나 추적 중인데 결과가 0건인 회차는 알림·저장 없이 건너뜀(전체 사라짐 오알림 방지)");

            now = now.AddHours(1);
            source.Set((1, "보물 상자", 800));
            source.Truncated = true;
            var truncated = await monitor.CollectAsync(default);
            source.Truncated = false;
            Assert(truncated.Success && sender.Sent.Count == 2 && (await boxStore.LoadStatesAsync())[3].MissingRuns == 0,
                "검색 결과가 잘렸을 수 있으면 빠진 아이템의 누락 횟수를 늘리지 않음");

            var restarted = new KeywordMarketStore(KeywordMarketRules.Box.Id, databasePath);
            await restarted.InitializeAsync();
            Assert((await restarted.LoadStatesAsync()).Count == 2 && (await restarted.GetChannelsAsync()).Count == 2, "재시작 후에도 DB의 상태·채널이 남아 있음");

            Assert((await restarted.LoadLatestPricesAsync())?.Prices.Single().MinPrice == 800, "재시작 후에도 마지막 정상 시세가 남아 있음");

            // 모비라이프 변화량이 있으면 마지막 시세에 함께 저장
            now = now.AddHours(1);
            source.Set((1, "보물 상자", 800), (3, "신규 상자", 300));
            source.Changes = (-1.5m, -3.25m, 12m, 4m);
            var sentBeforeTrend = sender.Sent.Count;
            await monitor.CollectAsync(default);
            source.Changes = null;
            var trendMessage = sender.Sent.Last().Embeds[0].Description;
            Assert(sender.Sent.Count == sentBeforeTrend + 1 && trendMessage.Contains("**🔀 상승·하락 변화**") &&
                   trendMessage.Contains("보물 상자 · 800 · ➖ → 📉⏬") && !trendMessage.Contains("신규 상자 · 300 · ➖") &&
                   trendMessage.Contains(MarketTrendEvaluator.Legend[0]) && trendMessage.Contains(MarketTrendEvaluator.NoneLegend),
                "직전 회차와 판정이 바뀐 아이템을 상승·하락 변화로 알리고(표시 없음 → 판정 포함), 직전 회차에 없던 아이템은 비교하지 않음. 이모지 설명을 붙임");
            var withChanges = (await boxStore.LoadLatestPricesAsync())!.Prices.Single(x => x.KindId == 1);
            Assert(withChanges is { PriceChange1hPercent: -1.5m, PriceChange24hPercent: -3.25m, PriceChange7dPercent: 12m, CountChange24hPercent: 4m },
                "변화량(퍼센트·매물 증감)을 마지막 시세에 저장하고 다시 읽음");

            // 재시작: 같은 정각 구간 안이면 수집하지 않고, 마지막 수집 이후 정각이 지났으면 즉시 수집
            var last = (await boxStore.GetLatestCollectedAtAsync())!.Value;
            source.Calls.Clear();
            await RunUntilScheduledAsync(source, boxStore, sender, last);
            Assert(source.Calls.Count == 0, "재시작 시 직전 정각 이후 수집 기록이 있으면 즉시 수집하지 않음");
            var missedAt = MarketSchedule.NextHourUtc(last).AddMinutes(3);
            await RunUntilScheduledAsync(source, boxStore, sender, missedAt);
            Assert(source.Calls.Count == 1 && await boxStore.GetLatestCollectedAtAsync() == missedAt, "재시작 시 마지막 수집 이후 정각을 놓쳤으면 즉시 수집");

            var packageMonitor = new KeywordMarketMonitor(KeywordMarketRules.Package, source, packageStore, sender, () => now, _ => { });
            source.Calls.Clear();
            await packageMonitor.CollectAsync(default);
            Assert(source.Calls.Single() == ("패키지", null), "패키지는 분류 지정 없이 '패키지'로 검색");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static async Task GuildThresholdTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "molly-keyword-threshold-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "database", "molly.sqlite");
            var store = new KeywordMarketStore(KeywordMarketRules.Box.Id, databasePath);
            await store.InitializeAsync();
            await store.SetChannelAsync(1, 10, s_Now);
            await store.SetChannelAsync(2, 20, s_Now, changePercent: 30);
            Assert(await store.GetChannelAsync(1) is { ChangePercent: null, Threshold: 0.10m } && await store.GetChannelAsync(2) is { ChangePercent: 30, Threshold: 0.30m },
                "등록 시 등락률을 길드별로 저장하고, 정하지 않으면 기본값(10%) 사용");

            var now = s_Now;
            var source = new FakeSource();
            var sender = new FakeSender();
            var monitor = new KeywordMarketMonitor(KeywordMarketRules.Box, source, store, sender, () => now, _ => { });
            source.Set((1, "보물 상자", 1000));
            await monitor.CollectAsync(default);

            now = now.AddHours(1);
            source.Set((1, "보물 상자", 800));
            await monitor.CollectAsync(default);
            Assert(sender.Sent.Single().Channel.GuildId == 1 && sender.Sent[0].Embeds[0].Description.Contains("보물 상자 1,000 → 800 (-20.0%)") &&
                   (await store.LoadBaselinesAsync(1))[1].Price == 800 && (await store.LoadBaselinesAsync(2))[1].Price == 1000,
                "-20%는 기본 10% 길드에만 알리고, 30% 길드는 과거시세를 유지");

            now = now.AddHours(1);
            source.Set((1, "보물 상자", 690));
            await monitor.CollectAsync(default);
            Assert(sender.Sent.Count == 3 && sender.Sent[1].Embeds[0].Description.Contains("보물 상자 800 → 690") &&
                   sender.Sent[2].Channel.GuildId == 2 && sender.Sent[2].Embeds[0].Description.Contains("보물 상자 1,000 → 690 (-31.0%)"),
                "길드마다 자기 과거시세와 등락률로 변동을 판정");

            await store.SetChannelAsync(2, 20, now);
            Assert(await store.GetChannelAsync(2) is { ChangePercent: null, Threshold: 0.10m }, "등락률 없이 다시 등록하면 기본값으로 돌아감");
            await store.RemoveChannelAsync(2);
            Assert((await store.LoadBaselinesAsync(2)).Count == 0 && (await store.LoadBaselinesAsync(1)).Count == 1, "해제하면 해당 길드의 과거시세만 삭제");

            // 등락률 열·길드별 과거시세 표가 없던 이전 DB: 공용 과거시세를 등록 길드마다 이어받고 등락률은 기본값
            var legacyPath = Path.Combine(directory, "legacy", "molly.sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE keyword_market_channels (monitor TEXT NOT NULL, guild_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, registered_at_utc TEXT NOT NULL, PRIMARY KEY (monitor, guild_id));
                    CREATE TABLE keyword_market_item_state (monitor TEXT NOT NULL, kind_id INTEGER NOT NULL, item_name TEXT NOT NULL, baseline_price INTEGER NOT NULL,
                        baseline_at_utc TEXT NOT NULL, current_price INTEGER NOT NULL, current_at_utc TEXT NOT NULL, first_seen_at_utc TEXT NOT NULL,
                        missing_runs INTEGER NOT NULL, PRIMARY KEY (monitor, kind_id));
                    INSERT INTO keyword_market_channels VALUES ('box', 5, 50, '2026-09-25T03:00:00.0000000Z'), ('package', 6, 60, '2026-09-25T03:00:00.0000000Z');
                    INSERT INTO keyword_market_item_state VALUES
                        ('box', 1, '보물 상자', 1234, '2026-09-25T03:00:00.0000000Z', 1300, '2026-09-25T04:00:00.0000000Z', '2026-09-25T03:00:00.0000000Z', 0),
                        ('box', 2, '빈 상자', 0, '2026-09-25T03:00:00.0000000Z', 0, '2026-09-25T04:00:00.0000000Z', '2026-09-25T03:00:00.0000000Z', 0),
                        ('package', 7, '패키지', 500, '2026-09-25T03:00:00.0000000Z', 500, '2026-09-25T04:00:00.0000000Z', '2026-09-25T03:00:00.0000000Z', 0);
                    CREATE TABLE keyword_market_price_history (monitor TEXT NOT NULL, collected_at_utc TEXT NOT NULL, kind_id INTEGER NOT NULL, item_name TEXT NOT NULL,
                        category TEXT NOT NULL, min_price INTEGER NOT NULL, total_count INTEGER NOT NULL, is_sold_out INTEGER NOT NULL, priced_at_utc TEXT NOT NULL,
                        PRIMARY KEY (monitor, collected_at_utc, kind_id));
                    INSERT INTO keyword_market_price_history VALUES
                        ('box', '2026-09-25T03:00:00.0000000Z', 1, '보물 상자', '아이템', 1234, 10, 0, '2026-09-25T03:00:00.0000000Z'),
                        ('box', '2026-09-25T03:00:00.0000000Z', 2, '빈 상자', '아이템', 10, 10, 0, '2026-09-25T03:00:00.0000000Z'),
                        ('box', '2026-09-25T04:00:00.0000000Z', 1, '보물 상자', '아이템', 1300, 10, 0, '2026-09-25T04:00:00.0000000Z'),
                        ('package', '2026-09-25T03:00:00.0000000Z', 7, '패키지', '아이템', 500, 10, 0, '2026-09-25T03:00:00.0000000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var legacyBox = new KeywordMarketStore(KeywordMarketRules.Box.Id, legacyPath);
            var legacyPackage = new KeywordMarketStore(KeywordMarketRules.Package.Id, legacyPath);
            await legacyBox.InitializeAsync();
            await legacyPackage.InitializeAsync();
            var migrated = await legacyBox.LoadBaselinesAsync(5);
            Assert(migrated.Count == 1 && migrated[1].Price == 1234 && (await legacyPackage.LoadBaselinesAsync(6))[7].Price == 500 &&
                   await legacyBox.GetChannelAsync(5) is { ChangePercent: null, Threshold: 0.10m } && (await legacyBox.LoadStatesAsync())[1].CurrentPrice == 1300,
                "이전 DB의 공용 과거시세를 모든 모니터링의 등록 길드로 옮기고, 저장된 등락률이 없으면 기본값 사용");
            var boxLatest = await legacyBox.LoadLatestPricesAsync();
            var packageLatest = await legacyPackage.LoadLatestPricesAsync();
            Assert(boxLatest is { Prices.Count: 1 } && boxLatest.Prices[0].MinPrice == 1300 && boxLatest.CollectedAtUtc == s_Now.AddHours(1) &&
                   boxLatest.Prices[0].PriceChange24hPercent is null && packageLatest?.Prices.Single().MinPrice == 500,
                "이전 DB의 시간별 이력에서 모니터링마다 마지막 회차만 마지막 시세로 옮김");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'keyword_market_price_history';";
                Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "옮긴 뒤 이전 시간별 이력 표를 지움");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MobiLifeSourceTestsAsync()
    {
        var requests = new List<Uri>();
        string Page(int offset, int count) => "{\"data\":[" + string.Join(",", Enumerable.Range(offset, count).Select(i =>
            $$"""{"kind_id":{{i}},"name":"상자 {{i}}","parent_category":"아이템","min_price":{{100 + i}},"total_count":5,"is_sold_out":false,"last_version":"2026-09-24T10:38:00Z"}""")) +
            $$$"""],"pagination":{"limit":100,"offset":{{{offset}}},"count":{{{count}}}}}""";
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["offset"]!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page(offset, 100)) };
        });
        using var client = new MobiLifeApiClient(new MobiLifeOptions { ApiKey = "k" }, handler, log: _ => { });
        var result = await new MobiLifeMarketPriceSource(client).SearchAsync("상자", "아이템", default);
        var query = System.Web.HttpUtility.ParseQueryString(requests[0].Query);
        Assert(result.IsSuccess && result.IsTruncated && result.Prices!.Count == MobiLifeMarketPriceSource.PageSize * MobiLifeMarketPriceSource.MaxPages &&
               query["search"] == "상자" && query["parent_category"] == "아이템",
            "분류 조건을 parent_category로 보내고, 페이지 제한까지 가득 차면 결과가 잘렸을 수 있다고 표시");

        requests.Clear();
        await new MobiLifeMarketPriceSource(client).SearchAsync("패키지", null, default);
        Assert(!requests[0].Query.Contains("parent_category"), "분류가 없으면 parent_category를 보내지 않음");

        // 변화량 필드: 있으면 도메인 모델로 옮기고, 없거나 null이면 null, 가격이 100%보다 더 내렸다면 응답 이상
        string Body(string changes, int totalCount) => $$$"""
            {"data":[{"kind_id":1,"name":"상자","parent_category":"아이템","min_price":100,"total_count":{{{totalCount}}},"is_sold_out":false,"last_version":"2026-09-24T10:38:00Z"{{{changes}}}}],
             "pagination":{"limit":100,"offset":0,"count":1}}
            """;
        async Task<MarketPriceSearchResult> SearchWith(string changes, int totalCount = 5)
        {
            var body = Body(changes, totalCount);
            using var changeClient = new MobiLifeApiClient(new MobiLifeOptions { ApiKey = "k" },
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }), log: _ => { });
            return await new MobiLifeMarketPriceSource(changeClient).SearchAsync("상자", null, default);
        }
        var full = await SearchWith(""","pct_change_1h":-1.5,"pct_change_24h":2.25,"pct_change_7d":-100,"count_change_24h":-3""");
        Assert(full.Prices?.Single() is { PriceChange1hPercent: -1.5m, PriceChange24hPercent: 2.25m, PriceChange7dPercent: -100m, CountChange24hPercent: -3m },
            "모비라이프 가격 등락(퍼센트)·매물 증감을 도메인 모델로 옮김");
        var missing = await SearchWith(""","pct_change_1h":null""");
        Assert(missing.Prices?.Single() is { PriceChange1hPercent: null, PriceChange24hPercent: null, PriceChange7dPercent: null, CountChange24hPercent: null },
            "변화량 필드가 없거나 null이면 null로 두고 시세는 정상 사용");
        var invalid = await SearchWith(""","pct_change_24h":-150""");
        Assert(!invalid.IsSuccess && invalid.FailureMessage!.Contains("상자 pct_change_24h=-150"),
            "가격이 100%보다 더 내렸다는 변화량은 일시적 데이터 오류로 보고 검색 실패(회차 재시도 대상)");
        // 2026-09-29 실제 응답: 매물 수가 -1로 오고 매물 증감(퍼센트)도 -100 아래로 계산됨
        var negativeCount = await SearchWith(""","count_change_24h":-100.02379252914584""", totalCount: -1);
        Assert(!negativeCount.IsSuccess && negativeCount.FailureMessage!.Contains("일시적") && negativeCount.FailureMessage.Contains("상자 total_count=-1"),
            "매물 수 -1은 일시적 데이터 오류로 검색 실패시키고 어느 아이템의 어느 값인지 알림");
        var lowCountChange = await SearchWith(""","count_change_24h":-100.5""");
        Assert(lowCountChange.IsSuccess && lowCountChange.Prices!.Single().CountChange24hPercent == -100.5m,
            "매물 증감(퍼센트)은 -100 아래여도 그 자체로는 실패시키지 않음");
    }

    // RunAsync의 시작 시 수집 판단까지만 실행하고, 다음 정각 대기에 들어가면 중단합니다.
    private static async Task RunUntilScheduledAsync(IMarketPriceSource source, KeywordMarketStore store, IKeywordAlertSender sender, DateTimeOffset now)
    {
        using var cts = new CancellationTokenSource();
        var monitor = new KeywordMarketMonitor(KeywordMarketRules.Box, source, store, sender, () => now,
            msg => { if (msg.StartsWith("다음 수집", StringComparison.Ordinal)) cts.Cancel(); });
        await monitor.RunAsync(cts.Token);
    }

    private sealed class FakeSource : IMarketPriceSource
    {
        private (long KindId, string Name, long Price)[] m_Items = [];
        public List<(string Keyword, string? Category)> Calls { get; } = [];
        public bool Fail { get; set; }
        public bool Truncated { get; set; }
        public (decimal H1, decimal H24, decimal D7, decimal Count24h)? Changes { get; set; }
        public string Attribution => "모비라이프 제공";

        public void Set(params (long KindId, string Name, long Price)[] items) => m_Items = items;

        public Task<MarketPriceSearchResult> SearchAsync(string keyword, string? category, CancellationToken ct)
        {
            Calls.Add((keyword, category));
            if (Fail) return Task.FromResult(MarketPriceSearchResult.Fail("실패"));
            var prices = m_Items.Select(x => new MarketPrice(x.KindId, x.Name, "아이템", x.Price, 100, false, s_Now,
                Changes?.H1, Changes?.H24, Changes?.D7, Changes?.Count24h)).ToArray();
            return Task.FromResult(new MarketPriceSearchResult(prices) { IsTruncated = Truncated });
        }
    }

    private sealed class FakeSender : IKeywordAlertSender
    {
        public List<(KeywordMonitorChannel Channel, IReadOnlyList<Embed> Embeds)> Sent { get; } = [];
        public ulong FailChannel { get; set; }

        public Task SendAsync(KeywordMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct)
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
