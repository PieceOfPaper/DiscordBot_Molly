using Discord;
using DiscordBot_Molly.Commands;
using Microsoft.Data.Sqlite;
using Molly.Currency;

/// <summary>마물 퇴치 증표의 서버별 잔액·원장·중복 방지·동시 차감, 증표규칙 시트 검증, 수량 표기를 검사한다.</summary>
internal static class CurrencyTests
{
    private const ulong GuildA = 1, GuildB = 2, User = 10, Other = 11;

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "molly-currency-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "molly.sqlite");
            var store = new MollyTokenStore(databasePath);
            await store.InitializeAsync();
            await store.InitializeAsync();
            await LedgerTestsAsync(store);
            await ConcurrencyTestsAsync(store);
            await ExternalTransactionTestsAsync(store, databasePath);
            await RuleCatalogTestsAsync(directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        RuleParseTests();
        await FormatTestsAsync();
    }

    private static TokenChange Reward(ulong user, long amount, string reference) => new(user, amount, TokenReasons.AttendanceReward, reference);
    private static TokenChange Bet(ulong user, long amount, string reference) => new(user, -amount, TokenReasons.PredictionBet, reference);

    private static async Task LedgerTestsAsync(MollyTokenStore store)
    {
        Assert(await store.GetBalanceAsync(GuildA, User) == 0, "받은 적 없는 사용자의 증표는 0개");

        var first = await store.ApplyAsync(GuildA, Reward(User, 50, "2026-09-28"));
        Assert(first is { Status: TokenChangeStatus.Applied, Balance: 50 }, "출석 보상 지급 후 잔액 반영");
        var retry = await store.ApplyAsync(GuildA, Reward(User, 50, "2026-09-28"));
        Assert(retry is { Status: TokenChangeStatus.AlreadyApplied, Balance: 50 } && await store.GetBalanceAsync(GuildA, User) == 50,
            "같은 사유·참조 ID의 지급을 다시 요청해도 중복 지급하지 않음");
        await AssertThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(GuildA, Reward(User, 70, "2026-09-28")),
            "같은 사유·참조 ID로 금액이 다른 변경은 거부");
        Assert((await store.ApplyAsync(GuildA, Reward(Other, 50, "2026-09-28"))).Status == TokenChangeStatus.Applied,
            "같은 참조 ID라도 다른 사용자에게는 따로 지급");

        Assert(await store.GetBalanceAsync(GuildB, User) == 0, "다른 서버의 잔액은 분리");
        await store.ApplyAsync(GuildB, Reward(User, 30, "2026-09-28"));
        Assert(await store.GetBalanceAsync(GuildA, User) == 50 && await store.GetBalanceAsync(GuildB, User) == 30, "서버별로 따로 쌓임");

        var tooMuch = await store.ApplyAsync(GuildB, Bet(User, 31, "prediction-1"));
        Assert(tooMuch is { Status: TokenChangeStatus.InsufficientBalance, Balance: 30 } && await store.GetBalanceAsync(GuildB, User) == 30,
            "잔액보다 많이 차감하면 거부하고 잔액 유지");
        var bet = await store.ApplyAsync(GuildB, Bet(User, 30, "prediction-1"));
        Assert(bet is { Status: TokenChangeStatus.Applied, Balance: 0 }, "잔액 전부 차감 가능");

        await AssertThrowsAsync<ArgumentException>(() => store.ApplyAsync(GuildA, new(User, 0, TokenReasons.AttendanceReward, "x")), "0개 변경 거부");
        await AssertThrowsAsync<ArgumentException>(() => store.ApplyAsync(GuildA, new(User, 5, "gift", "x")), "알 수 없는 사유 거부");
        await AssertThrowsAsync<ArgumentException>(() => store.ApplyAsync(GuildA, new(User, 5, TokenReasons.AttendanceReward, " ")), "빈 참조 ID 거부");

        // 정산 일괄 처리: 한 건이라도 잔액이 모자라면 전부 되돌린다.
        var batch = await store.ApplyAllAsync(GuildA, [new(User, 20, TokenReasons.PredictionPayout, "prediction-2"), Bet(Other, 999, "prediction-2")]);
        Assert(batch.All(x => x.Status == TokenChangeStatus.InsufficientBalance) && batch[0].Balance == 50
            && await store.GetBalanceAsync(GuildA, User) == 50, "일괄 처리 중 잔액 부족이 있으면 모두 되돌림");
        batch = await store.ApplyAllAsync(GuildA, [new(User, 20, TokenReasons.PredictionPayout, "prediction-2"), Bet(Other, 10, "prediction-2")]);
        Assert(batch.All(x => x.Status == TokenChangeStatus.Applied) && batch[0].Balance == 70 && batch[1].Balance == 40, "일괄 처리를 한 번에 반영");

        var ledger = await store.GetTransactionsAsync(GuildA, User);
        Assert(ledger.Select(x => (x.Amount, x.BalanceAfter, x.ReasonType)).SequenceEqual([(20L, 70L, TokenReasons.PredictionPayout), (50L, 50L, TokenReasons.AttendanceReward)]),
            "원장은 반영된 변경만 거래 후 잔액과 함께 최신순으로 기록");
        var ledgerB = await store.GetTransactionsAsync(GuildB, User);
        Assert(ledgerB.Count == 2 && ledgerB.Sum(x => x.Amount) == await store.GetBalanceAsync(GuildB, User), "원장 합계가 잔액과 같음");
    }

    private static async Task ConcurrencyTestsAsync(MollyTokenStore store)
    {
        const ulong guild = 3;
        await store.ApplyAsync(guild, Reward(User, 100, "day"));
        // 서로 다른 연결에서 동시에 60개씩 걸어도 하나만 성공해야 한다.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.ApplyAsync(guild, Bet(User, 60, "bet-" + i)))));
        Assert(results.Count(x => x.Status == TokenChangeStatus.Applied) == 1 && await store.GetBalanceAsync(guild, User) == 40,
            "동시 차감 요청은 잔액을 넘겨 쓰지 않음");
        // 같은 버튼을 동시에 여러 번 눌러도 한 번만 지급한다.
        results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.ApplyAsync(guild, Reward(Other, 50, "day")))));
        Assert(results.Count(x => x.Status == TokenChangeStatus.Applied) == 1 && results.All(x => x.Succeeded && x.Balance == 50)
            && await store.GetBalanceAsync(guild, Other) == 50, "동시 중복 지급 요청은 한 번만 반영");
    }

    private static async Task ExternalTransactionTestsAsync(MollyTokenStore store, string databasePath)
    {
        const ulong guild = 4;
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            var result = await MollyTokenStore.ApplyAsync(connection, transaction, guild, Reward(User, 50, "rollback"), DateTimeOffset.UtcNow, default);
            Assert(result.Status == TokenChangeStatus.Applied, "호출한 쪽 트랜잭션 안에서 지급");
            await transaction.RollbackAsync();
        }
        Assert(await store.GetBalanceAsync(guild, User) == 0 && (await store.GetTransactionsAsync(guild, User)).Count == 0,
            "호출한 쪽이 롤백하면 잔액·원장 모두 되돌림(출석 기록 저장 실패 시 지급 취소)");

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await MollyTokenStore.ApplyAsync(connection, transaction, guild, Reward(User, 50, "commit"), DateTimeOffset.UtcNow, default);
            await transaction.CommitAsync();
        }
        Assert(await store.GetBalanceAsync(guild, User) == 50, "호출한 쪽이 커밋하면 반영");
    }

    private const string RuleHeader = "ID,활성화,값,자료형,최소값,최대값,단위,설명,비고\n";

    private static void RuleParseTests()
    {
        var now = DateTimeOffset.UtcNow;
        Assert(TokenRuleCsvReader.Parse(RuleHeader, now).Rules.Count == 0, "헤더만 있는 증표규칙 시트는 정상(모두 코드 기본값)");
        Assert(TokenRuleCsvReader.Headers.SequenceEqual(RuleHeader.TrimEnd().Split(',')), "시트 헤더 순서 고정");

        var table = TokenRuleCsvReader.Parse(RuleHeader
            + "reward,TRUE,50,정수,1,1000,개,출석 지급량,\n"
            + "ratio,TRUE,0.5,실수,0,1,비율,,\n"
            + "label,TRUE,메모,문자열,,,,,\n"
            + "quick,TRUE,10|50| 100,정수목록,1,1000,개,,\n"
            + "off,FALSE,7,정수,,,,,\n"
            + ",,,,,,,,\n", now);
        Assert(table.GetInteger("reward", 1) == 50 && table.GetDecimal("ratio", 0) == 0.5m && table.GetText("label", "") == "메모"
            && table.GetIntegerList("quick", []).SequenceEqual([10L, 50L, 100L]), "자료형별 값 읽기");
        Assert(table.GetInteger("off", 3) == 3 && table.Find("off") is null, "비활성화 규칙은 코드 기본값 사용");
        Assert(table.GetInteger("missing", 9) == 9 && table.GetInteger("ratio", 9) == 9 && table.GetDecimal("reward", 0) == 50m,
            "없는 규칙·자료형이 다른 규칙은 코드 기본값 사용(정수는 실수로 읽기 허용)");

        (string Csv, string Name)[] invalid =
        [
            ("ID,값\n", "필수 헤더 누락"),
            ("Reward,TRUE,1,정수,,,,,\n", "ID 형식"),
            ("a,TRUE,1,정수,,,,,\na,TRUE,2,정수,,,,,\n", "ID 중복"),
            ("a,네,1,정수,,,,,\n", "활성화 값"),
            ("a,TRUE,1,숫자,,,,,\n", "알 수 없는 자료형"),
            ("a,TRUE,1.5,정수,,,,,\n", "정수가 아닌 값"),
            ("a,TRUE,1001,정수,1,1000,,,\n", "최대값 초과"),
            ("a,FALSE,0,정수,1,1000,,,\n", "비활성화 행도 범위 검사"),
            ("a,TRUE,10|x,정수목록,,,,,\n", "정수목록 형식"),
            ("a,TRUE,10|2000,정수목록,1,1000,,,\n", "정수목록 원소 범위"),
            ("a,TRUE,텍스트,문자열,1,,,,\n", "문자열에 범위 지정"),
            ("a,TRUE,1,정수,10,1,,,\n", "최소값이 최대값보다 큼"),
            ("a,TRUE,1,정수,,,\n", "열 수 불일치"),
        ];
        foreach (var (csv, name) in invalid)
        {
            var full = csv.StartsWith("ID,") ? csv : RuleHeader + csv;
            try { TokenRuleCsvReader.Parse(full, now); throw new Exception("증표규칙 거부 실패: " + name); }
            catch (InvalidDataException) { Console.WriteLine("PASS 증표규칙 거부: " + name); }
        }
    }

    private sealed class FakeRuleSource(Func<string> csv) : ITokenRuleSource
    {
        public string CacheKey => "test-token-rules";
        public Task<string> FetchCsvAsync(CancellationToken ct) => Task.FromResult(csv());
    }

    private static async Task RuleCatalogTestsAsync(string directory)
    {
        var csv = RuleHeader + "reward,TRUE,50,정수,1,1000,,,\n";
        var logs = new List<string>();
        var catalog = new TokenRuleCatalog(new FakeRuleSource(() => csv), directory, logs.Add);
        Assert(catalog.Current.GetInteger("reward", 10) == 10, "시트를 읽기 전에는 코드 기본값");
        await catalog.InitializeAsync();
        Assert(catalog.Current.GetInteger("reward", 10) == 50, "시트 값 반영");

        csv = RuleHeader + "reward,TRUE,5000,정수,1,1000,,,\n";
        Assert(!await catalog.RefreshAsync() && catalog.Current.GetInteger("reward", 10) == 50, "잘못된 시트는 마지막 정상 규칙 유지");

        // 재시작 후 시트를 읽지 못해도 마지막 정상 캐시로 시작한다.
        var restarted = new TokenRuleCatalog(new FakeRuleSource(() => throw new HttpRequestException("offline")), directory, logs.Add);
        await restarted.InitializeAsync();
        Assert(restarted.Current.GetInteger("reward", 10) == 50, "시트를 읽지 못하면 로컬 캐시의 마지막 정상 규칙 사용");
    }

    private static async Task FormatTestsAsync()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert(File.Exists(MollyToken.EmojiPath) && File.ReadAllBytes(MollyToken.EmojiPath).AsSpan().StartsWith(png)
            && new FileInfo(MollyToken.EmojiPath).Length < 256 * 1024, "증표 이모지 PNG 존재·256KB 미만");
        var missingImage = Path.Combine(Path.GetTempPath(), "molly-no-token-" + Guid.NewGuid().ToString("N") + ".png");
        var created = new List<string>();
        Task<Emote> Create(string name, Image _) { created.Add(name); return Task.FromResult(new Emote(77, name, false)); }

        await MollyToken.SyncEmojiAsync(() => Task.FromResult<IReadOnlyCollection<Emote>>([]), Create, missingImage, _ => { });
        Assert(created.Count == 0 && MollyToken.Emoji is null, "이모지 그림이 없으면 등록하지 않음");
        Assert(MollyToken.Named(10) == "마물 퇴치 증표 10개" && MollyToken.Amount(1234) == "마물 퇴치 증표 1,234개",
            "이모지가 없으면 수량 표기도 풀네임으로 대신");
        Assert(BagCommand.Format("테스트서버", 0, null).Contains("**마물 퇴치 증표** 0개") && BagCommand.Format("테스트서버", 0, null).Contains("몰리 전용 놀이 재화"),
            "가방은 이모지 없이도 증표 이름·수량·놀이 재화 안내를 표시");

        await MollyToken.SyncEmojiAsync(() => Task.FromResult<IReadOnlyCollection<Emote>>([new Emote(5, "molly_token", false)]), Create, missingImage, _ => { });
        Assert(created.Count == 0 && MollyToken.Amount(10) == "<:molly_token:5>10개" && MollyToken.Named(10) == "마물 퇴치 증표 10개",
            "등록된 이모지는 다시 올리지 않고 이름 없는 수량 앞에 공백 없이 붙임");
        Assert(BagCommand.Format("테스트서버", 50, MollyToken.Emoji).Contains("<:molly_token:5>**마물 퇴치 증표** 50개"), "가방은 증표 이름 앞에 이모지 표시");

        await MollyToken.SyncEmojiAsync(() => Task.FromResult<IReadOnlyCollection<Emote>>([]), Create, missingImage, _ => { });
        Assert(MollyToken.Emoji is null, "이모지가 사라지면 풀네임 표기로 복귀");
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
