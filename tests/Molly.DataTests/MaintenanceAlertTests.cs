using Microsoft.Data.Sqlite;
using Molly.OfficialNews;
using static OfficialNewsTests;

/// <summary>
/// 점검 알림(/점검알림): 점검·일반 공지 분류, 공지 목록의 제목·말머리 비교와 변경 순번, 공지 알림과의 분리, 첫 도입 기준, 실패 시 기준 유지,
/// 구독 세대(해제 후 같은 채널 재등록), 기존 미발송 점검 공지 작업 전환과 기존 DB 마이그레이션, 재시작 복구, Embed를 검사한다.
/// </summary>
internal static class MaintenanceAlertTests
{
    private const ulong GuildA = 100, GuildB = 200, ChNotice = 11, ChMaint = 12, ChShared = 99;
    private static readonly string s_Notice = NewsBoards.Notice.ListUrl;

    public static async Task RunAsync()
    {
        RuleTests();
        EmbedTests();
        var directory = Path.Combine(Path.GetTempPath(), "molly-maint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await CollectorTestsAsync(Path.Combine(directory, "collector.sqlite"));
            await IntroductionTestsAsync(Path.Combine(directory, "intro.sqlite"));
            await SubscriptionTestsAsync(Path.Combine(directory, "subs.sqlite"));
            await LegacyMigrationTestsAsync(Path.Combine(directory, "legacy.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // 2026-10 실제 공지 목록의 제목·말머리 형식.
    private static void RuleTests()
    {
        foreach (var (title, category, expected) in new (string, string?, bool)[]
        {
            ("10/29(목) 정기점검 안내 (06:00 ~ 10:00)", "점검", true),
            ("서버 안정화 작업 안내", "점검", true),
            ("(완료) 10/29(목) 정기점검 안내 (06:00 ~ 08:15)", "완료", true),
            ("(완료)(추가) 9/18(금) 임시점검 안내 (05:55 ~ 08:30)", "완료", true),
            ("(완료) (추가) 9/18(금) 임시점검 안내 (18:40 ~ 20:40)", "완료", true),
            ("(완료) 11/2(월) 긴급 점검 안내", "완료", true),
            ("10/1(목) 신규 패키지 안내 🍂보너스 티켓: 풍요 포함 상품", "완료", false),
            ("(수정) 9/17(목) 패션 럭키박스 ＆ 패션샵 안내", "완료", false),
            ("10/8(목) 정기점검 미진행 안내", "안내", false),
            ("(완료) 서버 불안정 현상 확인 안내", "안내", false),
            ("(완료) 10/29(목) 정기점검 안내 (06:00 ~ 08:15)", "안내", false),
            ("점검 보상 지급 안내", "완료", false),
            ("10/29(목) 정기점검 안내", null, false),
        })
            Assert(MaintenanceRules.IsMaintenancePost(title, category) == expected, $"점검 글 분류: [{category ?? "없음"}] {title} → {expected}");
    }

    private static NewsEvent Change(string kind, string title, string? previousTitle = null, string category = "점검", string? previousCategory = null, string? thumbnail = null) =>
        new(NewsBoards.MaintenanceEventIdFor(3571370, 1, kind), "notice", "3571370", title, NewsBoards.Notice.PostUrl(3571370), category, thumbnail,
            new DateTimeOffset(2026, 10, 29, 0, 50, 0, TimeSpan.Zero), kind, previousTitle, previousCategory, 1);

    private static void EmbedTests()
    {
        var created = MaintenanceMessages.Build(Change(MaintenanceEventKinds.Created, "10/29(목) 정기점검 안내 (06:00 ~ 10:00)"));
        Assert(created.Embed is { Author.Name: "🛠️ 점검 안내", Title: "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", Url: "https://mabinogimobile.nexon.com/News/Notice/3571370", Footer.Text: "몰리 • 점검 알림", Timestamp: null }
               && created.Embed.Fields.Single() is { Name: "발견 시각", Value: "10월 29일 09:50 KST" } && created.Embed.Description.Contains("[공식 점검 공지 링크](https://mabinogimobile.nexon.com/News/Notice/3571370)"),
            "새 점검 Embed: 원문 제목·링크, KST 발견 시각, 게시 시각처럼 보이는 timestamp 없음");
        Assert(created.Embed.Image?.Url == "attachment://notice_maintenance.png" && Path.GetFileName(created.AttachmentPath) == "notice_maintenance.png", "썸네일이 없으면 점검 기본 이미지 첨부");

        var changed = MaintenanceMessages.Build(Change(MaintenanceEventKinds.TitleChanged, "(연장) 10/29(목) 정기점검 안내 (06:00 ~ 11:00)", "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", previousCategory: "점검"));
        Assert(changed.Embed is { Author.Name: "🛠️ 점검 공지가 변경되었습니다" } && changed.Embed.Fields.Single().Name == "변경 확인"
               && changed.Embed.Description.Contains("**변경 전:**\n" + Discord.Format.Sanitize("10/29(목) 정기점검 안내 (06:00 ~ 10:00)"))
               && changed.Embed.Description.Contains("**변경 후:**\n" + Discord.Format.Sanitize("(연장) 10/29(목) 정기점검 안내 (06:00 ~ 11:00)")),
            "제목 변경 Embed: 변경 전·후 제목과 변경 확인 시각");

        var completed = MaintenanceMessages.Build(Change(MaintenanceEventKinds.Completed, "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)", "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", "완료", "점검"));
        Assert(completed.Embed is { Author.Name: "✅ 점검 완료 안내", Title: "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)" }
               && completed.Embed.Description.Contains("공식 공지의 말머리가 점검 → 완료로 변경되었습니다.") && completed.Embed.Fields.Single().Name == "변경 확인"
               && completed.Embed.Color != created.Embed.Color && Path.GetFileName(completed.AttachmentPath) == "notice_maintenance.png",
            "완료 Embed: 말머리 전환 안내, 완료 색상, 말머리가 완료여도 점검 기본 이미지");
        foreach (var embed in new[] { created.Embed, changed.Embed, completed.Embed })
            Assert(!embed.Description.Contains("종료되었습니다") && !embed.Description.Contains("접속") && !embed.Description.Contains("수정 시각"),
                "실제 종료·게임 접속 확인·공지 수정 시각으로 표현하지 않음");

        var longTitle = "@everyone **" + new string('가', 3000);
        var huge = MaintenanceMessages.Build(Change(MaintenanceEventKinds.TitleChanged, longTitle, longTitle + "이전")).Embed;
        Assert(huge.Title.Length == OfficialNewsMessages.MaxTitleLength && huge.Description.Length <= 4096 && huge.Length <= 6000 && huge.Description.Contains(@"\*\*"),
            "긴 제목은 Discord 제한에 맞게 줄이고 설명의 서식 문자는 escape");
        Assert(MaintenanceMessages.Build(Change(MaintenanceEventKinds.Created, "t", thumbnail: "https://cdn.example.com/m.png")) is { AttachmentPath: null, Embed.Image.Url: "https://cdn.example.com/m.png" },
            "글 썸네일이 있으면 그 이미지 사용");

        var now = new DateTimeOffset(2026, 10, 27, 9, 10, 0, TimeSpan.Zero); // 화요일 18:10 KST
        foreach (var kind in new[] { MaintenanceEventKinds.Created, MaintenanceEventKinds.TitleChanged, MaintenanceEventKinds.Completed })
        {
            var example = MaintenanceMessages.Example(kind, now);
            var test = MaintenanceMessages.Build(example, isTest: true).Embed;
            Assert(example is { Kind: var k, Url: "https://mabinogimobile.nexon.com/News/Notice" } && k == kind && example.Title.Contains("10/29(목)")
                   && test.Author!.Value.Name.StartsWith("🧪 [테스트]") && test.Footer!.Value.Text == "몰리 • 점검 알림 (테스트)" && test.Description.Contains("테스트 알림")
                   && test.Description.Contains("[공식 공지 목록]"), $"테스트 예시({kind}): 다음 목요일 가상 제목, 테스트 표시, 원문 대신 공지 목록 링크");
        }
    }

    private static async Task<(OfficialNewsStore Store, OfficialNewsCollector Collector, FakePageSource Source, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance)> CreateAsync(string databasePath)
    {
        var store = new OfficialNewsStore(databasePath);
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var source = new FakePageSource();
        var collector = new OfficialNewsCollector(store, source, () => { }, () => now, _ => { });
        return (store, collector, source, () => now, d => now += d);
    }

    private static string List(params string[] cards) => Page(cards.Length, cards);

    private static async Task<IReadOnlyList<(ulong GuildId, ulong ChannelId, string Status)>> Deliveries(OfficialNewsStore store, string eventId) =>
        (await store.GetDeliveriesAsync(eventId)).Select(d => (d.GuildId, d.ChannelId, d.Status)).ToList();

    private static string Id(long postId, int revision, string kind) => NewsBoards.MaintenanceEventIdFor(postId, revision, kind);

    private static async Task CollectorTestsAsync(string databasePath)
    {
        var (store, collector, source, clock, advance) = await CreateAsync(databasePath);
        // A: 공지 → ChNotice, 점검 → ChMaint (다른 채널) / B: 공지·점검 모두 ChShared (같은 채널)
        await store.SetSubscriptionAsync(GuildA, "notice", ChNotice, clock());
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        await store.SetSubscriptionAsync(GuildB, "notice", ChShared, clock());
        await store.SetSubscriptionAsync(GuildB, "maintenance", ChShared, clock());
        Assert((await store.GetSubscriptionAsync(GuildA, "notice"))?.ChannelId == ChNotice && (await store.GetSubscriptionAsync(GuildA, "maintenance"))?.ChannelId == ChMaint
               && (await store.GetSubscriptionAsync(GuildB, "notice"))?.ChannelId == ChShared && (await store.GetSubscriptionAsync(GuildB, "maintenance"))?.ChannelId == ChShared,
            "공지·점검 구독은 독립(다른 채널·같은 채널 모두 가능)");

        // 1) 첫 확인: 기준만 만들고 과거 점검·완료 글로 알림을 만들지 않는다.
        source.Pages[s_Notice] = List(Card(103, "(완료) 10/22(목) 정기점검 안내 (06:00 ~ 08:10)", "완료"), Card(102, "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", "점검"), Card(101, "안내 A", "안내"));
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == 0, "첫 확인은 아무것도 보내지 않음");
        Assert(await store.GetPostStateAsync("notice", 102) is { Category: "점검", Maintenance: true, Revision: 0 }
               && await store.GetPostStateAsync("notice", 103) is { Maintenance: true } && await store.GetPostStateAsync("notice", 101) is { Maintenance: false },
            "첫 확인에 글별 제목·말머리 기준과 점검 소속 기록");
        Assert(await store.GetEventAsync(Id(102, 0, "created")) is null, "첫 확인 기준의 점검 글은 새 점검 알림이 아님");
        Assert(await store.GetPostStateAsync("update", 1) is null && !MaintenanceRules.Tracks(NewsBoards.Update), "다른 게시판은 변경을 추적하지 않음");

        // 2) 새 일반 공지·새 점검·처음부터 완료인 점검·완료된 상품 안내
        advance(TimeSpan.FromMinutes(10));
        source.Pages[s_Notice] = List(
            Card(107, "10/30(금) 신규 패키지 안내", "완료"), Card(106, "(완료) 11/1(일) 임시점검 안내 (06:00 ~ 07:00)", "완료"),
            Card(105, "11/5(목) 정기점검 안내 (06:00 ~ 10:00)", "점검"), Card(104, "안내 B", "안내"),
            Card(103, "(완료) 10/22(목) 정기점검 안내 (06:00 ~ 08:10)", "완료"), Card(102, "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", "점검"), Card(101, "안내 A", "안내"));
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == 4, "새 글 4개 발견");
        Assert((await Deliveries(store, NewsBoards.EventIdFor("notice", 104))).SequenceEqual([(GuildA, ChNotice, "pending"), (GuildB, ChShared, "pending")]),
            "새 일반 공지 → 공지 구독에만 발송 작업");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("notice", 105)) is null
               && (await Deliveries(store, Id(105, 0, "created"))).SequenceEqual([(GuildA, ChMaint, "pending"), (GuildB, ChShared, "pending")]),
            "새 점검 공지 → 공지 알림 없이 점검 구독에만 발송 작업");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("notice", 106)) is null && await store.GetEventAsync(Id(106, 0, "created")) is null
               && await store.GetPostStateAsync("notice", 106) is { Maintenance: true, Category: "완료" },
            "처음부터 완료인 점검 글: 완료 알림 없음, 일반 공지로도 보내지 않고 점검 소속으로 기록");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("notice", 107)) is { Category: "완료" }, "말머리가 완료인 상품 안내는 점검으로 오분류하지 않고 일반 공지");
        Assert((await store.GetSeenPostIdsAsync("notice", [104, 105, 106, 107])).Count == 4, "점검 글도 본 글로 표시");

        // 3) 같은 상태 반복 수집 → 추가 이벤트 없음
        advance(TimeSpan.FromMinutes(10));
        var before = await CountEventsAsync(databasePath);
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == 0 && await CountEventsAsync(databasePath) == before
               && await store.GetPostStateAsync("notice", 102) is { Revision: 0 }, "같은 상태를 다시 수집하면 이벤트·변경 순번 없음");

        // 4) 점검 유지 중 제목 변경 → 이전·현재 제목. 제목에 (완료)가 붙어도 말머리가 점검이면 제목 변경.
        advance(TimeSpan.FromMinutes(10));
        Replace(source, 102, "(연장) 10/29(목) 정기점검 안내 (06:00 ~ 11:00)", "점검");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetEventAsync(Id(102, 1, "title_changed")) is { PreviousTitle: "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", Title: "(연장) 10/29(목) 정기점검 안내 (06:00 ~ 11:00)", PreviousCategory: "점검", Revision: 1 } e1
               && e1.DetectedAt == clock() && (await Deliveries(store, Id(102, 1, "title_changed"))).SequenceEqual([(GuildA, ChMaint, "pending"), (GuildB, ChShared, "pending")]),
            "점검 중 제목 변경: 변경 전·후 제목과 확인 시각, 점검 구독에만 발송");
        advance(TimeSpan.FromMinutes(10));
        Replace(source, 102, "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 11:00)", "점검");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetEventAsync(Id(102, 2, "title_changed")) is not null && await store.GetEventAsync(Id(102, 2, "completed")) is null,
            "제목에만 (완료)가 붙고 말머리가 점검이면 제목 변경 알림");

        // 5) 제목·말머리가 함께 완료로 바뀌면 완료 알림 하나
        advance(TimeSpan.FromMinutes(10));
        Replace(source, 102, "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)", "완료");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetEventAsync(Id(102, 3, "completed")) is { PreviousCategory: "점검", Category: "완료" } && await store.GetEventAsync(Id(102, 3, "title_changed")) is null
               && (await Deliveries(store, Id(102, 3, "completed"))).Count == 2 && await store.GetEventAsync(NewsBoards.EventIdFor("notice", 102)) is null,
            "점검 → 완료(제목도 변경) → 완료 알림 하나, 일반 공지로 유입되지 않음");

        // 6) 완료 이후 제목 변경 → 알림 없음(기준·변경 순번만 갱신)
        advance(TimeSpan.FromMinutes(10));
        before = await CountEventsAsync(databasePath);
        Replace(source, 102, "(완료)(추가) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)", "완료");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await CountEventsAsync(databasePath) == before && await store.GetPostStateAsync("notice", 102) is { Revision: 4, Maintenance: true, Title: "(완료)(추가) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)" },
            "완료 이후 제목 변경은 알리지 않고 기준과 점검 소속만 유지");

        // 7) 그 외 말머리 변경(안내 → 완료) → 알림 없음
        Replace(source, 101, "안내 A", "완료");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await CountEventsAsync(databasePath) == before && await store.GetPostStateAsync("notice", 101) is { Category: "완료", Maintenance: false }, "그 외 말머리 변경은 알림 없음");

        // 8) A → B → C → A: 감지한 변경마다 서로 다른 이벤트, 과거 제목으로 원복도 새 변경
        foreach (var title in new[] { "B 제목", "C 제목", "11/5(목) 정기점검 안내 (06:00 ~ 10:00)" })
        {
            advance(TimeSpan.FromMinutes(10));
            Replace(source, 105, title, "점검");
            await collector.CheckBoardAsync(NewsBoards.Notice, default);
        }
        Assert(await store.GetEventAsync(Id(105, 1, "title_changed")) is { PreviousTitle: "11/5(목) 정기점검 안내 (06:00 ~ 10:00)", Title: "B 제목" }
               && await store.GetEventAsync(Id(105, 2, "title_changed")) is { PreviousTitle: "B 제목", Title: "C 제목" }
               && await store.GetEventAsync(Id(105, 3, "title_changed")) is { PreviousTitle: "C 제목", Title: "11/5(목) 정기점검 안내 (06:00 ~ 10:00)" }
               && (await Deliveries(store, Id(105, 3, "title_changed"))).Count == 2, "A → B → C → A의 세 변경을 모두 별도 이벤트로 처리");

        // 9) 동일 이벤트 재처리: 고유 제약으로 발송 작업 중복 없음
        var replay = await store.ReceiveAsync((await store.GetEventAsync(Id(105, 3, "title_changed")))!, clock());
        Assert(replay is { Status: NewsReceiveStatus.Duplicate, DeliveryCount: 0 } && (await Deliveries(store, Id(105, 3, "title_changed"))).Count == 2, "같은 변경 이벤트를 다시 기록해도 작업 중복 없음");

        // 10) 말머리를 읽지 못한 회차는 말머리가 사라진 변화로 보지 않음
        Replace(source, 105, "11/5(목) 정기점검 안내 (06:00 ~ 10:00)", null);
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetPostStateAsync("notice", 105) is { Category: "점검", Revision: 3 }, "말머리 없는 관측은 기준을 바꾸지 않음");
        Replace(source, 105, "11/5(목) 정기점검 안내 (06:00 ~ 10:00)", "점검");

        // 11) 수집·파싱 실패 → 마지막 정상 기준 유지, 복구되면 저장 상태와 현재 관측값 사이의 변경만 판정
        var good = source.Pages[s_Notice];
        source.Pages[s_Notice] = BlockedPage;
        advance(TimeSpan.FromMinutes(10));
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        source.Fail = _ => new HttpRequestException("down");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetPostStateAsync("notice", 105) is { Title: "11/5(목) 정기점검 안내 (06:00 ~ 10:00)", Revision: 3 } && (await store.GetBoardStateAsync("notice"))!.LastError is not null,
            "목록이 아닌 응답·네트워크 오류는 비교 기준을 바꾸지 않음");
        source.Fail = _ => null;
        source.Pages[s_Notice] = good;
        Replace(source, 105, "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 12:00)", "점검");
        advance(TimeSpan.FromMinutes(10));
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetEventAsync(Id(105, 4, "title_changed")) is { PreviousTitle: "11/5(목) 정기점검 안내 (06:00 ~ 10:00)" }, "복구 후 마지막 정상 기준과 비교");

        // 12) 목록에서 사라진 점검 글은 완료·삭제로 판단하지 않음
        before = await CountEventsAsync(databasePath);
        source.Pages[s_Notice] = List(Card(104, "안내 B", "안내"), Card(101, "안내 A", "완료"));
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await CountEventsAsync(databasePath) == before && await store.GetPostStateAsync("notice", 105) is { Category: "점검" }, "목록에서 사라진 글은 변화 없음");

        // 13) 추가 페이지: 첫 페이지가 모두 새 글이면 기존 정책대로 다음 페이지를 읽고, 그 페이지의 기존 점검 글도 비교한다. 페이지 사이 중복 카드는 한 번만 처리.
        source.Pages[s_Notice] = Page(6, [Card(111, "새 안내 111", "안내"), Card(110, "새 안내 110", "안내"), Card(109, "새 안내 109", "안내")], blockStartKey: "k");
        source.Pages[OfficialNewsParser.BuildListUrl(NewsBoards.Notice, 2, "1", "k")] =
            Page(6, [Card(109, "새 안내 109", "안내"), Card(105, "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 13:00)", "점검"), Card(104, "안내 B", "안내")], blockStartKey: "k");
        var requests = source.Requested.Count;
        advance(TimeSpan.FromMinutes(10));
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == 3 && source.Requested.Count - requests == 2, "새 글이 한 페이지를 넘을 때만 다음 페이지를 읽음");
        Assert(await store.GetEventAsync(Id(105, 5, "title_changed")) is { Title: "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 13:00)" } && await store.GetEventAsync(Id(105, 6, "title_changed")) is null,
            "다음 페이지의 기존 점검 글도 비교하고, 목록이 밀려 두 페이지에 걸친 같은 글은 한 번만 처리");
        Assert((await store.GetDeliveriesAsync(NewsBoards.EventIdFor("notice", 109))).Count == 2 && await store.GetPostStateAsync("notice", 109) is { Revision: 0 },
            "페이지 사이 중복 글은 새 글·비교 기준 모두 한 번");
        source.Pages[s_Notice] = List(Card(111, "새 안내 111", "안내"), Card(105, "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 13:00)", "점검"));
        before = await CountEventsAsync(databasePath);
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await CountEventsAsync(databasePath) == before, "첫 페이지에 이미 본 글이 있어도 비교하되 같으면 이벤트 없음");

        // 14) 발송: 점검 구독 채널로 점검 Embed, 공지 채널로는 점검 글이 가지 않음
        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(store, sender, () => clock().AddDays(1), _ => { });
        while (await worker.ProcessDueAsync(default) > 0) { }
        var toNotice = sender.Sent.Where(s => s.ChannelId == ChNotice).Select(s => s.Embed.Author!.Value.Name).ToList();
        var toMaint = sender.Sent.Where(s => s.ChannelId == ChMaint).Select(s => s.Embed.Author!.Value.Name).ToList();
        Assert(toNotice.All(n => n == "📢 공식 공지") && toNotice.Count > 0 && toMaint.Count > 0
               && toMaint.All(n => n is "🛠️ 점검 안내" or "🛠️ 점검 공지가 변경되었습니다" or "✅ 점검 완료 안내")
               && sender.Sent.Count(s => s.ChannelId == ChShared) == toNotice.Count + toMaint.Count,
            "공지 채널엔 일반 공지만, 점검 채널엔 점검 알림만, 같은 채널이면 둘 다");

        // 15) 재시작: 새 인스턴스가 비교 기준을 이어 쓰고, 처리 중이던 작업은 복구해 보낸다.
        source.Pages[s_Notice] = List(Card(105, "(완료) 11/5(목) 정기점검 안내 (06:00 ~ 13:00)", "완료"));
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        var claimed = await store.ClaimDueAsync(clock().AddDays(2), OfficialNewsDeliveryWorker.Lease, 100);
        Assert(claimed.Count == 2 && claimed.All(c => c.Board == "maintenance" && c.Event is { Kind: "completed", PreviousTitle: "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 13:00)" }),
            "점검 발송 작업이 이벤트 종류·이전 제목과 함께 처리 중으로 넘어감");
        var restarted = new OfficialNewsStore(databasePath);
        await restarted.InitializeAsync();
        Assert(await restarted.GetPostStateAsync("notice", 105) is { Category: "완료", Revision: 6 } && await restarted.GetEventAsync(Id(105, 6, "completed")) is not null,
            "재시작 후 비교 기준 유지");
        var restartSender = new FakeNewsSender();
        var restartWorker = new OfficialNewsDeliveryWorker(restarted, restartSender, () => clock().AddDays(2), _ => { });
        using var cts = new CancellationTokenSource();
        var run = restartWorker.RunAsync(cts.Token);
        for (var i = 0; i < 100 && restartSender.Sent.Count < 2; i++) await Task.Delay(50);
        await cts.CancelAsync();
        await run;
        Assert(restartSender.Sent.Count == 2 && restartSender.Sent.All(s => s.Embed.Author!.Value.Name == "✅ 점검 완료 안내"), "재시작 시 처리 중이던 점검 작업을 복구해 발송");
    }

    // 기존 DB에 점검 알림을 도입: 공지 게시판 기준과 본 글은 있지만 글별 비교 기준은 없다.
    private static async Task IntroductionTestsAsync(string databasePath)
    {
        var (store, collector, source, clock, advance) = await CreateAsync(databasePath);
        await store.BaselineBoardAsync("notice", [201, 202, 203], clock()); // 이전 버전의 첫 기준(비교 기준 없음)
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        await store.SetSubscriptionAsync(GuildA, "notice", ChNotice, clock());
        advance(TimeSpan.FromMinutes(10));
        source.Pages[s_Notice] = List(Card(204, "11/12(목) 정기점검 안내 (06:00 ~ 10:00)", "점검"),
            Card(203, "(연장) 11/5(목) 정기점검 안내 (06:00 ~ 11:00)", "점검"), Card(202, "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 10:10)", "완료"), Card(201, "안내", "안내"));
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == 1, "도입 후 새 글만 새 글로 판정");
        Assert(await store.GetEventAsync(Id(204, 0, "created")) is not null && (await Deliveries(store, Id(204, 0, "created"))).Count == 1,
            "기준 설정과 같은 회차의 새 점검 공지는 누락 없이 새 점검 안내");
        Assert(await store.GetPostStateAsync("notice", 203) is { Revision: 0, Maintenance: true } && await store.GetPostStateAsync("notice", 202) is { Revision: 0, Maintenance: true }
               && await store.GetEventAsync(Id(203, 0, "created")) is null && await store.GetEventAsync(Id(202, 0, "completed")) is null && await store.GetEventAsync(Id(202, 1, "completed")) is null,
            "비교 기록이 없던 기존 글은 현재 값으로 기준만 생성(과거 변경·완료 알림 없음)");
        Assert((await store.GetSeenPostIdsAsync("notice", [201, 202, 203])).Count == 3 && (await store.GetBoardStateAsync("notice"))!.FloorPostId == 201,
            "기존 새 글 기준과 본 글 기록은 유지");
        advance(TimeSpan.FromMinutes(10));
        Replace(source, 203, "(완료) 11/5(목) 정기점검 안내 (06:00 ~ 10:40)", "완료");
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        Assert(await store.GetEventAsync(Id(203, 1, "completed")) is not null, "도입 이후 관측한 점검 → 완료는 완료 알림");
    }

    private static async Task SubscriptionTestsAsync(string databasePath)
    {
        var (store, collector, source, clock, advance) = await CreateAsync(databasePath);
        source.Pages[s_Notice] = List(Card(301, "안내", "안내"));
        await collector.CheckBoardAsync(NewsBoards.Notice, default);
        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(store, sender, clock, _ => { });
        var post = 302L;
        async Task<string> NewMaintenanceAsync()
        {
            advance(TimeSpan.FromMinutes(10));
            var id = post++;
            source.Pages[s_Notice] = List(Card(id, $"{id} 임시점검 안내", "점검"), Card(301, "안내", "안내"));
            await collector.CheckBoardAsync(NewsBoards.Notice, default);
            return Id(id, 0, "created");
        }

        var noSubscriber = await NewMaintenanceAsync();
        Assert(await store.GetEventAsync(noSubscriber) is not null && (await Deliveries(store, noSubscriber)).Count == 0 && await store.GetPostStateAsync("notice", 302) is not null,
            "구독 서버가 없어도 이벤트·비교 기준은 기록");
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert(sender.Sent.Count == 0 && (await Deliveries(store, noSubscriber)).Count == 0, "등록 전 이벤트는 소급 발송하지 않음");

        // 해제 → 대기 작업 skipped
        var e1 = await NewMaintenanceAsync();
        await store.DisableSubscriptionAsync(GuildA, "maintenance", clock());
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await Deliveries(store, e1)).Single().Status == "skipped" && sender.Sent.Count == 0, "해제 전에 만든 대기 작업은 건너뜀");

        // 같은 채널로 해제 후 재등록 → 이전 세대 작업은 살아나지 않음
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        var e2 = await NewMaintenanceAsync();
        await store.DisableSubscriptionAsync(GuildA, "maintenance", clock());
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await store.GetDeliveriesAsync(e2)).Single() is { Status: "skipped", ErrorCode: "subscription_changed" } && sender.Sent.Count == 0,
            "같은 채널로 해제 후 재등록해도 과거 작업은 보내지 않음(구독 세대)");

        // 등록된 채널에 다시 등록(교체 없음) → 세대 유지, 대기 작업 발송
        var e3 = await NewMaintenanceAsync();
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChMaint, clock());
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await Deliveries(store, e3)).Single().Status == "sent" && sender.Sent.Single().ChannelId == ChMaint, "같은 채널 재등록(해제 없음)은 대기 작업을 유지");

        // 채널 교체 → 이전 채널 작업은 건너뛰고 새 채널로 소급하지 않음
        var e4 = await NewMaintenanceAsync();
        await store.SetSubscriptionAsync(GuildA, "maintenance", ChShared, clock());
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await Deliveries(store, e4)).Single().Status == "skipped" && sender.Sent.Count == 1, "채널 교체 전 작업은 건너뛰고 새 채널로 소급 안 함");
        var e5 = await NewMaintenanceAsync();
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await Deliveries(store, e5)).Single() is { ChannelId: ChShared, Status: "sent" }, "교체 뒤 새 변경부터 새 채널로");

        // 공지 구독 해제는 점검 구독에 영향 없음
        await store.SetSubscriptionAsync(GuildA, "notice", ChShared, clock());
        await store.DisableSubscriptionAsync(GuildA, "notice", clock());
        Assert(await store.GetSubscriptionAsync(GuildA, "maintenance") is { Enabled: true, ChannelId: ChShared }, "공지 알림 해제가 점검 알림을 끄지 않음");
    }

    // 점검 알림 도입 전 구조의 DB: 공지 구독이 점검 글 작업을 갖고 있다.
    private static async Task LegacyMigrationTestsAsync(string databasePath)
    {
        MollySqlite.EnsureProvider();
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE official_news_subscriptions (guild_id INTEGER NOT NULL, board TEXT NOT NULL, channel_id INTEGER NOT NULL, enabled INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, PRIMARY KEY (guild_id, board));
                CREATE TABLE official_news_events (event_id TEXT PRIMARY KEY, board TEXT NOT NULL, post_id TEXT NOT NULL, title TEXT NOT NULL, url TEXT NOT NULL,
                    category TEXT NULL, thumbnail_url TEXT NULL, detected_at TEXT NOT NULL, received_at_utc TEXT NOT NULL);
                CREATE TABLE official_news_deliveries (id INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL REFERENCES official_news_events(event_id),
                    guild_id INTEGER NOT NULL, board TEXT NOT NULL, channel_id INTEGER NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL, lease_until_utc TEXT NULL, sent_message_id INTEGER NULL, last_error_code TEXT NULL,
                    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, UNIQUE (event_id, guild_id));
                CREATE TABLE official_news_board_states (board TEXT PRIMARY KEY, floor_post_id INTEGER NOT NULL, baselined_at_utc TEXT NOT NULL,
                    last_checked_at_utc TEXT NULL, last_success_at_utc TEXT NULL, last_error TEXT NULL);
                CREATE TABLE official_news_seen_posts (board TEXT NOT NULL, post_id INTEGER NOT NULL, seen_at_utc TEXT NOT NULL, PRIMARY KEY (board, post_id));
                INSERT INTO official_news_subscriptions VALUES (100, 'notice', 11, 1, '2026-10-01T00:00:00.0000000+00:00', '2026-10-01T00:00:00.0000000+00:00');
                INSERT INTO official_news_board_states VALUES ('notice', 400, '2026-10-01T00:00:00.0000000+00:00', NULL, '2026-10-28T00:00:00.0000000+00:00', NULL);
                INSERT INTO official_news_seen_posts VALUES ('notice', 400, '2026-10-01T00:00:00.0000000+00:00');
                """;
            foreach (var (post, category, status) in new (int, string?, string)[] { (401, "점검", "pending"), (402, "점검", "sending"), (403, "점검", "sent"), (404, "안내", "pending"), (405, null, "pending"), (406, "완료", "pending") })
            {
                var eventId = NewsBoards.EventIdFor("notice", post);
                command.CommandText += $"""
                    INSERT INTO official_news_events VALUES ('{eventId}', 'notice', '{post}', '{post} 제목', 'https://x/{post}', {(category is null ? "NULL" : $"'{category}'")}, NULL, '2026-10-28T00:00:00.0000000+00:00', '2026-10-28T00:00:00.0000000+00:00');
                    INSERT INTO official_news_deliveries (event_id, guild_id, board, channel_id, status, attempts, next_attempt_at_utc, lease_until_utc, sent_message_id, created_at_utc, updated_at_utc)
                    VALUES ('{eventId}', 100, 'notice', 11, '{status}', 0, '2026-10-28T00:00:00.0000000+00:00', NULL, {(status == "sent" ? "5" : "NULL")}, '2026-10-28T00:00:00.0000000+00:00', '2026-10-28T00:00:00.0000000+00:00');
                    """;
            }
            await command.ExecuteNonQueryAsync();
        }

        var store = new OfficialNewsStore(databasePath);
        await store.InitializeAsync();
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        Assert(await store.SkipLegacyMaintenanceNoticeDeliveriesAsync(now) == 2 && await store.SkipLegacyMaintenanceNoticeDeliveriesAsync(now) == 0,
            "미발송 점검 공지 작업만 전환하고 다시 호출해도 같은 결과");
        async Task<(string Status, string? Error, ulong? Message)> Of(int post) =>
            (await store.GetDeliveriesAsync(NewsBoards.EventIdFor("notice", post))).Select(d => (d.Status, d.ErrorCode, d.MessageId)).Single();
        Assert(await Of(401) == ("skipped", "maintenance_split", null) && await Of(402) == ("skipped", "maintenance_split", null),
            "기존 대기·처리 중 점검 공지 작업은 skipped(maintenance_split)");
        Assert(await Of(403) == ("sent", null, 5UL), "이미 보낸 점검 공지 기록은 유지");
        Assert(await Of(404) == ("pending", null, null) && await Of(405) == ("pending", null, null) && await Of(406) == ("pending", null, null),
            "일반 공지·말머리 없는 글·완료 글(분류 불명확) 대기 작업은 유지");
        Assert(await store.GetSubscriptionAsync(100, "notice") is { Enabled: true, ChannelId: 11, Generation: 0 } && await store.GetSubscriptionAsync(100, "maintenance") is null
               && (await store.GetBoardStateAsync("notice"))!.FloorPostId == 400 && (await store.GetSeenPostIdsAsync("notice", [400])).Count == 1,
            "마이그레이션은 기존 구독·기준·본 글을 보존하고 공지 구독을 점검 구독으로 복사하지 않음");

        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(store, sender, () => now, _ => { });
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert(sender.Sent.Count == 3 && (await Of(404)).Status == "sent" && (await Of(401)).Status == "skipped",
            "마이그레이션 전 만든 일반 공지 작업은 그대로 발송(세대 0 일치), 점검 작업은 보내지 않음");
    }

    private static void Replace(FakePageSource source, long postId, string title, string? category)
    {
        // 목록 HTML에서 해당 글 카드만 바꾼다.
        var html = source.Pages[s_Notice];
        var start = html.IndexOf($"data-threadid=\"{postId}\"", StringComparison.Ordinal);
        var cardStart = html.LastIndexOf("<li", start, StringComparison.Ordinal);
        var cardEnd = html.IndexOf("</li>", start, StringComparison.Ordinal) + 5;
        source.Pages[s_Notice] = html[..cardStart] + Card(postId, title, category).TrimStart() + html[cardEnd..];
    }

    private static async Task<long> CountEventsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM official_news_events;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
