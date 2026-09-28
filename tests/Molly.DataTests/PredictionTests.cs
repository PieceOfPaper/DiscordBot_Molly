using DiscordBot_Molly.Commands;
using Microsoft.Data.Sqlite;
using Molly.Currency;
using Molly.Prediction;

/// <summary>배틀 예측의 배당 계산, 규칙 스냅샷, 베팅 판정·누적·상한, 마감, 정산·무효·재시작, 중복 정산 방지, 안내 문구를 검사한다.</summary>
internal static class PredictionTests
{
    private const ulong Guild = 1, OtherGuild = 2, Challenger = 100, Opponent = 101;
    private const ulong U1 = 11, U2 = 12, U3 = 13, U4 = 14, U5 = 15, U6 = 16;
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        PayoutTests();
        SharePercentTests();
        RuleTests();
        var directory = Path.Combine(Path.GetTempPath(), "molly-prediction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "molly.sqlite");
            var tokens = new MollyTokenStore(databasePath);
            await tokens.InitializeAsync();
            var store = new BattlePredictionStore(databasePath);
            await store.InitializeAsync();
            await store.InitializeAsync();
            await BetAndResolveTestsAsync(store, tokens);
            await PendingElsewhereTestsAsync(store, tokens);
            await CancelTestsAsync(store, tokens);
            await SettlementFailureTestsAsync(store, tokens);
            await RestartTestsAsync(store, tokens);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        MessageTests();
    }

    private static PredictionStake A(ulong user, long amount, long order) => new(user, PredictionSide.Challenger, amount, order);
    private static PredictionStake B(ulong user, long amount, long order) => new(user, PredictionSide.Opponent, amount, order);

    private static void PayoutTests()
    {
        var example = PredictionPayout.Calculate([A(1, 60, 1), A(2, 240, 2), B(3, 200, 3)], PredictionSide.Challenger);
        Assert(example[1] == 100 && example[2] == 400 && !example.ContainsKey(3), "기획서 예시: 판돈 500 × 60 ÷ 300 = 100");

        var equal = PredictionPayout.Calculate([A(1, 10, 3), A(2, 10, 1), A(3, 10, 2), B(4, 70, 4)], PredictionSide.Challenger);
        Assert(equal[2] == 34 && equal[1] == 33 && equal[3] == 33, "나머지·베팅액이 같으면 먼저 건 사람이 남은 1개를 받음");

        var remainder = PredictionPayout.Calculate([A(1, 20, 1), A(2, 10, 2), B(3, 20, 3)], PredictionSide.Challenger);
        Assert(remainder[1] == 33 && remainder[2] == 17, "소수점 나머지가 큰 사람이 남은 1개를 먼저 받음");

        // 판돈 7, 승리 진영 2·4: 14/6 = 2 r2, 28/6 = 4 r4 → 남은 1개는 나머지가 큰 4에게. 나머지가 같고 베팅액이 다른 경우는 무작위 검사로 총합만 확인한다.
        var small = PredictionPayout.Calculate([A(1, 2, 1), A(2, 4, 2), B(3, 1, 3)], PredictionSide.Challenger);
        Assert(small[1] == 2 && small[2] == 5, "작은 판돈에서도 총합이 판돈과 같음");
        var random = new Random(1234);
        for (var i = 0; i < 500; i++)
        {
            var stakes = Enumerable.Range(0, random.Next(2, 12))
                .Select(j => new PredictionStake((ulong)j + 1, random.Next(2) == 0 ? PredictionSide.Challenger : PredictionSide.Opponent, random.Next(1, 50) * 10, j))
                .ToList();
            var winner = random.Next(2) == 0 ? PredictionSide.Challenger : PredictionSide.Opponent;
            var payouts = PredictionPayout.Calculate(stakes, winner);
            var winners = stakes.Where(x => x.Side == winner).ToList();
            if (winners.Count == 0) { Check(payouts.Count == 0, "승리 진영에 건 사람이 없으면 배당 없음"); continue; }
            Check(payouts.Values.Sum() == stakes.Sum(x => x.Amount), "배당 총합은 전체 판돈과 같음");
            Check(winners.All(x => payouts[x.UserId] >= x.Amount), "적중자는 건 금액 이상을 받음");
        }
        Console.WriteLine("PASS 무작위 500회: 배당 총합 = 판돈, 적중자는 건 금액 이상");
    }

    private static void SharePercentTests()
    {
        Assert(PredictionPayout.SharePercent(300, 200) == (60, 40), "진영 비율 60% · 40%");
        Assert(PredictionPayout.SharePercent(1, 999) == (1, 99), "한쪽이 아주 적어도 0%로 보이지 않음");
        Assert(PredictionPayout.SharePercent(100, 0) == (100, 0), "한쪽에만 걸리면 100% · 0%");
        Assert(PredictionPayout.SharePercent(0, 0) is null, "아무도 걸지 않으면 비율 없음");
        Assert(PredictionPayout.SharePercent(1, 2) == (33, 67), "비율 합은 100");
    }

    private static void RuleTests()
    {
        var defaults = PredictionRules.From(TokenRuleTable.Empty);
        Assert(defaults.BetAmounts.SequenceEqual([10L, 50L, 100L]) && defaults.MaxBalanceRatio == 0.5m && defaults.MaxBetAmount == 500 && defaults.MinPredictorCount == 2,
            "시트 행이 없으면 코드 기본값");
        const string header = "ID,활성화,값,자료형,최소값,최대값,단위,설명,비고\n";
        var table = TokenRuleCsvReader.Parse(header
            + "prediction_bet_amounts,TRUE,5|20,정수목록,1,10000,,,\n"
            + "prediction_max_balance_ratio,TRUE,0.3,실수,0.01,1,,,\n"
            + "prediction_max_bet_amount,TRUE,200,정수,1,100000,,,\n"
            + "prediction_min_predictor_count,TRUE,3,정수,2,100,,,\n", Now);
        var rules = PredictionRules.From(table);
        Assert(rules.BetAmounts.SequenceEqual([5L, 20L]) && rules.MaxBalanceRatio == 0.3m && rules.MaxBetAmount == 200 && rules.MinPredictorCount == 3,
            "시트 값으로 예측 규칙 구성");
        var tooMany = PredictionRules.From(TokenRuleCsvReader.Parse(header + "prediction_bet_amounts,TRUE,1|2|3|4|5,정수목록,,,,,\n", Now));
        Assert(tooMany.BetAmounts.SequenceEqual([10L, 50L, 100L]), "버튼 금액이 4개를 넘으면 기본값");
        var fromJson = PredictionRules.FromJson(rules.ToJson());
        Assert(fromJson.BetAmounts.SequenceEqual(rules.BetAmounts) && fromJson.MaxBalanceRatio == rules.MaxBalanceRatio, "규칙 스냅샷 JSON 왕복");
        var limit = PredictionRules.Default;
        Assert(limit.LimitFor(55) == 27 && limit.LimitFor(5000) == 500 && limit.LimitFor(0) == 0 && limit.LimitFor(-10) == 0,
            "누적 상한 = min(고정 상한, 잔액 × 비율 내림)");
    }

    private static async Task GiveAsync(MollyTokenStore tokens, ulong user, long amount, string reference = "day-1")
        => await tokens.ApplyAsync(Guild, new TokenChange(user, amount, TokenReasons.AttendanceReward, reference));

    private static Task OpenAsync(BattlePredictionStore store, ulong id)
        => store.OpenAsync(id, Guild, Challenger, Opponent, "종잇장", "핏피", PredictionRules.Default, Now);

    private static async Task BetAndResolveTestsAsync(BattlePredictionStore store, MollyTokenStore tokens)
    {
        const ulong id = 1000;
        await GiveAsync(tokens, U1, 200);
        await GiveAsync(tokens, U3, 100);
        await GiveAsync(tokens, U4, 1000);
        await OpenAsync(store, id);
        Assert(await store.GetStatusAsync(id) == PredictionStatus.Open, "예측 열기");

        Assert((await store.PlaceBetAsync(OtherGuild, id, U1, PredictionSide.Challenger, 10, Now)).Status == PredictionBetStatus.Closed,
            "다른 서버에서 온 버튼은 거부");
        Assert((await store.PlaceBetAsync(Guild, id, U1, PredictionSide.Challenger, 7, Now)).Status == PredictionBetStatus.InvalidAmount,
            "규칙에 없는 금액 거부");

        var first = await store.PlaceBetAsync(Guild, id, U1, PredictionSide.Challenger, 50, Now);
        Assert(first is { Status: PredictionBetStatus.Placed, Amount: 50, Total: 50, Limit: 100, SideName: "종잇장" }, "처음 베팅");
        var second = await store.PlaceBetAsync(Guild, id, U1, PredictionSide.Challenger, 50, Now);
        Assert(second is { Status: PredictionBetStatus.Placed, Total: 100 }, "같은 진영 버튼을 다시 누르면 누적");
        var over = await store.PlaceBetAsync(Guild, id, U1, PredictionSide.Challenger, 10, Now);
        Assert(over is { Status: PredictionBetStatus.OverLimit, Total: 100, Limit: 100 }, "누적 상한(잔액 50%)을 넘으면 거부하고 누적액 유지");
        var otherSide = await store.PlaceBetAsync(Guild, id, U1, PredictionSide.Opponent, 10, Now);
        Assert(otherSide is { Status: PredictionBetStatus.OtherSide, Side: PredictionSide.Challenger, SideName: "종잇장" }, "반대 진영에는 걸 수 없음");
        var empty = await store.PlaceBetAsync(Guild, id, U2, PredictionSide.Opponent, 10, Now);
        Assert(empty is { Status: PredictionBetStatus.OverLimit, Total: 0, Limit: 0, Balance: 0 }, "증표가 없으면 걸 수 없음");
        Assert(await tokens.GetBalanceAsync(Guild, U1) == 200, "걸 때는 증표를 차감하지 않음");

        await GiveAsync(tokens, U2, 100);
        await store.PlaceBetAsync(Guild, id, U2, PredictionSide.Opponent, 50, Now);
        await store.PlaceBetAsync(Guild, id, U3, PredictionSide.Challenger, 10, Now);
        // 서로 다른 연결에서 동시에 눌러도 상한(500)을 넘지 않는다.
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.PlaceBetAsync(Guild, id, U4, PredictionSide.Challenger, 100, Now))));
        Assert(concurrent.Count(x => x.Status == PredictionBetStatus.Placed) == 5 && concurrent.Max(x => x.Total) == 500, "동시 베팅도 누적 상한을 넘지 않음");

        var locked = await store.LockAsync(id, Now);
        Assert(locked is { ChallengerTotal: 610, OpponentTotal: 50, PredictorCount: 4, IsValid: true }, "마감 시 진영별 누적액·인원");
        Assert((await store.PlaceBetAsync(Guild, id, U3, PredictionSide.Challenger, 10, Now)).Status == PredictionBetStatus.Closed, "마감 후 베팅 거부");
        Assert((await store.LockAsync(id, Now))!.ChallengerTotal == 610, "다시 마감해도 같은 결과");

        var settlement = await store.ResolveAsync(id, PredictionSide.Opponent, Now);
        Assert(settlement is { Status: PredictionStatus.Resolved, WinningSide: PredictionSide.Opponent, Pool: 660 }
            && settlement.Entries.Single(x => x.UserId == U2).Payout == 660 && settlement.Entries.Where(x => x.UserId != U2).All(x => x.Payout == 0),
            "정상 정산: 적중자가 전체 판돈을 받음");
        Assert(await tokens.GetBalanceAsync(Guild, U1) == 100 && await tokens.GetBalanceAsync(Guild, U2) == 710
            && await tokens.GetBalanceAsync(Guild, U3) == 90 && await tokens.GetBalanceAsync(Guild, U4) == 500, "정산할 때 차감·배당 반영");
        var ledger = await tokens.GetTransactionsAsync(Guild, U2);
        var reference = BattlePredictionStore.ReferenceId(id);
        Assert(ledger.Any(x => x is { ReasonType: TokenReasons.PredictionBet, Amount: -50 } && x.ReferenceId == reference)
            && ledger.Any(x => x is { ReasonType: TokenReasons.PredictionPayout, Amount: 660 } && x.ReferenceId == reference), "적중자 원장에 차감과 배당이 모두 남음");

        var again = await store.ResolveAsync(id, PredictionSide.Challenger, Now);
        var cancelAfter = await store.CancelAsync(id, PredictionCancelReason.Stopped, Now);
        Assert(again is { Status: PredictionStatus.Resolved, WinningSide: PredictionSide.Opponent } && cancelAfter is { Status: PredictionStatus.Resolved }
            && await tokens.GetBalanceAsync(Guild, U2) == 710 && await tokens.GetBalanceAsync(Guild, U1) == 100, "정산을 다시 요청해도 한 번만 반영");
        Assert(await store.ResolveAsync(9999, PredictionSide.Challenger, Now) is null && await store.LockAsync(9999, Now) is null, "없는 예측은 null");
    }

    private static async Task PendingElsewhereTestsAsync(BattlePredictionStore store, MollyTokenStore tokens)
    {
        // 정상적으로는 한 서버에 예측이 하나뿐이지만, 정산되지 않은 다른 예측에 건 금액은 잔액에서 빼고 상한을 계산한다.
        await OpenAsync(store, 2000);
        await OpenAsync(store, 2001);
        Assert((await store.PlaceBetAsync(Guild, 2000, U1, PredictionSide.Challenger, 50, Now)).Status == PredictionBetStatus.Placed, "잔액 100의 50% 베팅");
        var second = await store.PlaceBetAsync(Guild, 2001, U1, PredictionSide.Challenger, 50, Now);
        Assert(second is { Status: PredictionBetStatus.OverLimit, Limit: 25 }, "다른 예측에 건 금액을 빼고 상한 계산");
        await store.CancelAsync(2000, PredictionCancelReason.Error, Now);
        await store.CancelAsync(2001, PredictionCancelReason.Error, Now);
    }

    private static async Task CancelTestsAsync(BattlePredictionStore store, MollyTokenStore tokens)
    {
        var before = (U1: await tokens.GetBalanceAsync(Guild, U1), U2: await tokens.GetBalanceAsync(Guild, U2));

        await OpenAsync(store, 3000);
        await store.PlaceBetAsync(Guild, 3000, U1, PredictionSide.Challenger, 10, Now);
        await store.PlaceBetAsync(Guild, 3000, U2, PredictionSide.Opponent, 10, Now);
        await GiveAsync(tokens, Challenger, 100);
        Assert((await store.PlaceBetAsync(Guild, 3000, Challenger, PredictionSide.Opponent, 10, Now)).Status == PredictionBetStatus.Placed,
            "배틀 참가자도 자기 배틀에 걸 수 있음(상대 쪽 포함)");
        var draw = await store.ResolveAsync(3000, null, Now);
        Assert(draw is { Status: PredictionStatus.Cancelled, CancelReason: PredictionCancelReason.Draw } && draw.Entries.All(x => x.Payout is null), "무승부는 무효");

        await OpenAsync(store, 3001);
        await store.PlaceBetAsync(Guild, 3001, U1, PredictionSide.Challenger, 10, Now);
        await store.PlaceBetAsync(Guild, 3001, U2, PredictionSide.Challenger, 10, Now);
        var locked = await store.LockAsync(3001, Now);
        Assert(locked is { IsValid: false, InvalidReason: PredictionCancelReason.OneSided }, "마감 시 한쪽 쏠림을 무효로 알림");
        Assert((await store.ResolveAsync(3001, PredictionSide.Challenger, Now)) is { Status: PredictionStatus.Cancelled, CancelReason: PredictionCancelReason.OneSided },
            "한쪽에만 걸리면 무효");

        await OpenAsync(store, 3002);
        await store.PlaceBetAsync(Guild, 3002, U1, PredictionSide.Challenger, 10, Now);
        Assert((await store.LockAsync(3002, Now))!.InvalidReason == PredictionCancelReason.TooFewPredictors
            && (await store.ResolveAsync(3002, PredictionSide.Challenger, Now))!.CancelReason == PredictionCancelReason.TooFewPredictors, "최소 인원 미달이면 무효");

        await OpenAsync(store, 3003);
        await store.PlaceBetAsync(Guild, 3003, U1, PredictionSide.Challenger, 10, Now);
        await store.PlaceBetAsync(Guild, 3003, U2, PredictionSide.Opponent, 10, Now);
        var stopped = await store.CancelAsync(3003, PredictionCancelReason.Stopped, Now);
        Assert(stopped is { Status: PredictionStatus.Cancelled, CancelReason: PredictionCancelReason.Stopped }
            && (await store.ResolveAsync(3003, PredictionSide.Challenger, Now))!.Status == PredictionStatus.Cancelled, "강제 종료 무효 후 정산 요청은 무시");

        Assert(await tokens.GetBalanceAsync(Guild, U1) == before.U1 && await tokens.GetBalanceAsync(Guild, U2) == before.U2, "무효 예측은 증표를 움직이지 않음");
    }

    private static async Task SettlementFailureTestsAsync(BattlePredictionStore store, MollyTokenStore tokens)
    {
        await GiveAsync(tokens, U5, 100);
        await GiveAsync(tokens, U6, 100);
        await OpenAsync(store, 4000);
        await store.PlaceBetAsync(Guild, 4000, U5, PredictionSide.Challenger, 50, Now);
        await store.PlaceBetAsync(Guild, 4000, U6, PredictionSide.Opponent, 50, Now);
        // 정상적으로는 일어나지 않지만, 패배 진영의 잔액이 모자라면 모두 되돌리고 무효 처리한다.
        await tokens.ApplyAsync(Guild, new TokenChange(U6, -80, TokenReasons.PredictionBet, "drain"));
        var failed = await store.ResolveAsync(4000, PredictionSide.Challenger, Now);
        Assert(failed is { Status: PredictionStatus.Cancelled, CancelReason: PredictionCancelReason.SettlementFailed }
            && await tokens.GetBalanceAsync(Guild, U5) == 100 && await tokens.GetBalanceAsync(Guild, U6) == 20, "정산 중 잔액 부족이면 전부 되돌리고 무효");
    }

    private static async Task RestartTestsAsync(BattlePredictionStore store, MollyTokenStore tokens)
    {
        await OpenAsync(store, 5000);
        await store.PlaceBetAsync(Guild, 5000, U5, PredictionSide.Challenger, 10, Now);
        await OpenAsync(store, 5001);
        await store.LockAsync(5001, Now);
        var cancelled = await store.CancelUnsettledAsync(Now);
        Assert(cancelled == 2 && await store.GetStatusAsync(5000) == PredictionStatus.Cancelled && await store.GetStatusAsync(5001) == PredictionStatus.Cancelled
            && await store.GetStatusAsync(1000) == PredictionStatus.Resolved, "재시작 시 정산되지 않은 예측만 무효");
        Assert((await store.CancelAsync(5000, PredictionCancelReason.Error, Now))!.CancelReason == PredictionCancelReason.Restart, "재시작 무효 사유 유지");
        Assert((await store.PlaceBetAsync(Guild, 5000, U5, PredictionSide.Challenger, 10, Now)).Status == PredictionBetStatus.Closed
            && await tokens.GetBalanceAsync(Guild, U5) == 100, "재시작 후 버튼은 거부하고 증표는 그대로");
    }

    private static void MessageTests()
    {
        var placed = BattlePredictionMessages.BetReply(new(PredictionBetStatus.Placed, PredictionSide.Challenger, "종잇장", 50, 60, 100, 200));
        Assert(placed.StartsWith("종잇장의 승리에 ") && placed.Contains(MollyToken.Named(50) + "를 걸었어요.") && placed.Contains("(누적 " + MollyToken.Amount(60) + ")"),
            "베팅 응답: 풀네임 문장 + 누적 수량");
        Assert(!BattlePredictionMessages.BetReply(new(PredictionBetStatus.Placed, PredictionSide.Challenger, "종잇장", 50, 50, 100, 200)).Contains("누적"),
            "처음 베팅에는 누적 표기 없음");
        Assert(BattlePredictionMessages.BetReply(new(PredictionBetStatus.OtherSide, PredictionSide.Opponent, "핏피")).Contains("이미 핏피의 승리에"), "반대 진영 안내");
        Assert(BattlePredictionMessages.BetReply(new(PredictionBetStatus.Closed)) == "예측이 마감되었어요.", "마감 안내");

        var locked = BattlePredictionMessages.Locked(new(300, 200, 4, null), "종잇장", "핏피", PredictionRules.Default);
        Assert(locked.Contains("종잇장 **60%**") && locked.Contains("핏피 **40%**") && !locked.Contains("300"), "마감 안내는 수량 없이 비율만");
        Assert(locked.Contains("\n🟦🟦🟦🟦🟦🟦🟥🟥🟥🟥\n"), "마감 안내에 비율 게이지 한 줄");
        Assert(BattlePredictionMessages.Gauge(67, 33) == "🟦🟦🟦🟦🟦🟦🟦🟥🟥🟥", "67%는 7칸");
        Assert(BattlePredictionMessages.Gauge(1, 99) == "🟦🟥🟥🟥🟥🟥🟥🟥🟥🟥" && BattlePredictionMessages.Gauge(97, 3) == "🟦🟦🟦🟦🟦🟦🟦🟦🟦🟥",
            "양쪽 모두 걸렸으면 적은 쪽도 최소 1칸");
        Assert(BattlePredictionMessages.Gauge(100, 0) == string.Concat(Enumerable.Repeat("🟦", 10)), "한쪽에만 걸리면 한 색 10칸");
        Assert(BattlePredictionMessages.Locked(new(100, 0, 2, PredictionCancelReason.OneSided), "종잇장", "핏피", PredictionRules.Default).Contains("무효"),
            "마감 때 무효 사유 안내");
        Assert(BattlePredictionMessages.Locked(new(0, 0, 0, PredictionCancelReason.TooFewPredictors), "종잇장", "핏피", PredictionRules.Default).Contains("참여한 사람이 없어요"),
            "참여자가 없으면 비율 대신 안내");

        var resolved = BattlePredictionMessages.Settlement(new(PredictionStatus.Resolved, null, PredictionSide.Challenger, "종잇장", "핏피",
            [new(U1, PredictionSide.Challenger, 60, 100), new(U2, PredictionSide.Opponent, 40, 0)]))!;
        Assert(resolved.Contains("**종잇장**의 승리를 맞힌 1명") && resolved.Contains("<@" + U1 + "> " + MollyToken.Amount(100) + "\n")
            && !resolved.Contains("(+") && !resolved.Contains(MollyToken.Amount(60)) && resolved.Contains("틀린 1명"), "정산 안내: 적중자별 받은 총액만(건 양·순이익 없음)");
        Assert(!resolved.Contains("가방") && !resolved.Contains("보유") && !locked.Contains("가방"), "공개 메시지(마감·정산)에는 잔액을 쓰지 않음");
        Assert(BattlePredictionMessages.Settlement(new(PredictionStatus.Cancelled, PredictionCancelReason.Draw, null, "종잇장", "핏피",
            [new(U1, PredictionSide.Challenger, 10, null)]))!.Contains("무승부"), "무승부 무효 안내");
        Assert(BattlePredictionMessages.Settlement(new(PredictionStatus.Cancelled, PredictionCancelReason.Stopped, null, "종잇장", "핏피", [])) is null,
            "참여자가 없으면 정산 안내를 보내지 않음");

        var buttons = BattlePredictionMessages.Buttons(1234, "종잇장", "핏피", PredictionRules.Default);
        var rows = buttons.Components.ToList();
        Assert(rows.Count == 2, "진영마다 버튼 한 줄");
        Assert(BattlePredictionMessages.ParseSide("A") == PredictionSide.Challenger && BattlePredictionMessages.ParseSide("X") is null, "버튼 진영 키 해석");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
