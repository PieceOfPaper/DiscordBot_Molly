using Discord;
using Microsoft.Data.Sqlite;
using Molly.OfficialNews;

/// <summary>
/// 공식 소식 목록 파싱, 수집기의 첫 기준·새 글 판정·페이지 넘김·실패 시 상태 유지, 원자적 저장·중복 제거, 게시판별 구독,
/// 발송 worker의 재시도·건너뜀·복구, Embed 표시를 검사한다.
/// </summary>
internal static class OfficialNewsTests
{
    private const ulong GuildA = 100, GuildB = 200, Ch1 = 11, Ch2 = 12, Ch9 = 99;

    public static async Task RunAsync()
    {
        ParserTests();
        EmbedTests();
        var directory = Path.Combine(Path.GetTempPath(), "molly-news-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "molly.sqlite");
            var store = new OfficialNewsStore(databasePath);
            await store.InitializeAsync();
            await store.InitializeAsync();
            await SubscriptionTestsAsync(store, databasePath);
            await ReceiveTestsAsync(store);
            await WorkerTestsAsync(store);
            await RestartTestsAsync(store, databasePath);
            await CollectorTestsAsync(Path.Combine(directory, "collector.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ── 목록 HTML 샘플: 2026-10 실제 공지·이벤트 목록(서버 렌더링 HTML)에서 카드·배너·페이지 구조만 줄여 옮김 ──

    private static string Card(long id, string title, string? type = null, string path = "Notice") => $"""
                <li class="item " data-mm-listitem="" data-threadid="{id}">
                    <div class="order_1">
                            {(type is null ? "" : $"<div class=\"type\"><span>{type}</span></div>")}
                        <a href="/News/{path}/{id}" data-link="" data-boardactionpath="/News/{path}" onclick="event.preventDefault(); mmCommunity.Thread.link({id}, this)" style="cursor:pointer" class="title new">
                            <span>{title} </span>
                        </a>
                        <div class="etc">
    <div class="etc_photo"></div>                        </div>
                    </div>
                    <div class="order_2">
                            <div class="user_info" data-mm-userinfo="" style="cursor:default" onclick="miniCardToggle(this);">
                                <span class="profile_image">
                                    <img src="https://dszw1qtcnsa5e.cloudfront.net/community/20250204/profile.png" onerror="this.src='https://lwi.nexon.com/m_mabinogim/brand/community/profile_default.png'" alt="">
                                </span>
                                <span class="name" data-mm-usernickname="">마비노기모바일</span>
                            </div>
                        <div class="sub_info"><div class="date"><span>2026.10.28</span></div></div>
                    </div>
                </li>
    """;

    private static string EventCard(long id, string title, string image) => $"""
                <li class="item " data-mm-listitem data-threadid="{id}">
                    <div class="thumbnail">
                        <img src="{image}" onerror="this.src='https://lwi.nexon.com/m_mabinogim/brand/community/thumb.png'" alt="이벤트섬네일" />
                    </div>
                    <div class="descript">
                        <div class="order_1">
                            <div class="type">진행중</div>
                            <a href='/News/Events/{id}' data-link="" onclick="event.preventDefault();mmCommunity.Thread.link({id}, this)" style="cursor:pointer" class="title new">
                                <span>{title}</span>
                            </a>
                        </div>
                        <div class="order_2"><div class="date"><span>2026.10.29(목) 점검 후 ~ 2026.11.1(일) 오후 11시 59분까지</span></div></div>
                    </div>
                </li>
    """;

    private static string Banner(long id, string image) => $"""
                <li class="banner_item swiper-slide" data-link="" data-boardactionpath="/News/Notice" data-pagetype="notice" role="group">
                        <div class="thumbnail" onclick="mmCommunity.Thread.link({id}, this)" style="cursor:pointer; background-image: url('{image}');">
                        </div>
                    <div class="descript" style="cursor:pointer" onclick="mmCommunity.Thread.link({id}, this)">
                        <a style="cursor:pointer" onclick="mmCommunity.Thread.link({id}, this)" class="title "><span>배너 글</span></a>
                    </div>
                </li>
    """;

    private static string Page(int totalCount, IEnumerable<string> cards, string banners = "", string blockStartKey = "253402300799,9223372036854775807") => $"""
        <html lang="ko"><body>
        <nav><ul class="outer"><li><a href="/News/Notice" class="on new ">에린 소식</a><ul><li><a href="/News/Notice">공지사항</a></li></ul></li></ul></nav>
        <section class="sticky_banner_wrap"><ul class="swiper-wrapper">{banners}</ul></section>
        <div class="list_area" data-mm-boardtype="NORMAL" data-mm-boardlist=""><ul class="list">
        {string.Concat(cards)}
        </ul>
        <div class="pager">
        <div class="pagination" data-pagingtype="thread" data-mm-paging="" data-blockstartno="1" data-blockstartkey="{blockStartKey}" data-totalcount="{totalCount}">
        <ul><li class="on">1</li><li onclick="event.preventDefault();mmCommunity.Thread.list(2,this)"><a href="?pageno=2&amp;blockStartNo=1&amp;blockStartKey={blockStartKey}">2</a></li></ul>
        </div></div></div>
        </body></html>
        """;

    private const string BlockedPage = "<html><head><title>Object moved</title></head><body><h2>Object moved to <a href=\"/en/Main\">here</a>.</h2></body></html>";

    private static void ParserTests()
    {
        var notice = OfficialNewsParser.ParseListPage(Page(490,
        [
            Card(3571557, "10/29(목) 할로윈 특별 상품 &amp; 🍂보너스   티켓 안내", "주요상품"),
            Card(3571370, "(완료) 10/29(목) 정기점검 안내 (06:00 ~ 08:15)", "완료"),
            Card(3565824, "서버 불안정 현상 확인 안내"),
        ], Banner(3571370, "https://dszw1qtcnsa5e.cloudfront.net/community/20260909/banner%EB%B0%B0%EB%84%88.png") + Banner(3472920, "https://cdn.example.com/old.png")));
        Assert(notice is { HasPagination: true, TotalCount: 490, BlockStartNo: "1", BlockStartKey: "253402300799,9223372036854775807" }
            && notice.Cards.Select(c => c.PostId).SequenceEqual([3571557L, 3571370L, 3565824L]), "공지 목록: 페이지 정보와 글 ID 순서");
        Assert(notice.Cards[0] is { Title: "10/29(목) 할로윈 특별 상품 & 🍂보너스 티켓 안내", Category: "주요상품", ThumbnailUrl: null },
            "제목의 HTML 엔티티·공백 정리, 말머리, 작성자 프로필 이미지는 썸네일이 아님");
        Assert(notice.Cards[1].ThumbnailUrl == "https://dszw1qtcnsa5e.cloudfront.net/community/20260909/banner%EB%B0%B0%EB%84%88.png" && notice.Cards[2] is { Category: null, ThumbnailUrl: null },
            "목록에 있는 글만 상단 배너 이미지로 보충, 말머리 없는 글은 분류 없음");

        var events = OfficialNewsParser.ParseListPage(Page(440,
        [
            EventCard(3571558, "할로윈 맞이 온타임 이벤트 안내", "https://dszw1qtcnsa5e.cloudfront.net/community/20261028/thumb770x280.png"),
            EventCard(3571550, "상대 경로 썸네일", "/community/thumb.png"),
            Card(3571558, "할로윈 맞이 온타임 이벤트 안내", path: "Events"),
        ]));
        Assert(events.Cards.Count == 2 && events.Cards[0] is { PostId: 3571558, Category: "진행중", ThumbnailUrl: "https://dszw1qtcnsa5e.cloudfront.net/community/20261028/thumb770x280.png" }
            && events.Cards[1].ThumbnailUrl == "https://mabinogimobile.nexon.com/community/thumb.png", "이벤트 목록: 썸네일 이미지·상대 경로 보정, 이미지 없는 중복 카드가 이미지를 지우지 않음");

        foreach (var (html, name) in new[] { (BlockedPage, "해외 IP의 글로벌 페이지 이동"), ("<html><body>Just a moment...</body></html>", "보안 검사 페이지"), ("", "빈 응답") })
            Assert(OfficialNewsParser.ParseListPage(html) is { HasPagination: false, Cards.Count: 0 }, $"목록이 아닌 응답은 글 없음: {name}");

        Assert(OfficialNewsParser.BuildListUrl(NewsBoards.Notice, 1, "1", "x") == "https://mabinogimobile.nexon.com/News/Notice"
            && OfficialNewsParser.BuildListUrl(NewsBoards.Devnote, 2, "1", "253402300799,9223372036854775807")
                == "https://mabinogimobile.nexon.com/News/Devnote?pageno=2&blockStartNo=1&blockStartKey=253402300799%2C9223372036854775807",
            "게시판별 목록 URL과 사이트와 같은 다음 페이지 커서");
    }

    private static NewsEvent Sample(string board = "notice", long postId = 3571370, string title = "정기점검 안내", string? category = "점검", string? thumbnail = null) =>
        new(NewsBoards.EventIdFor(board, postId), board, postId.ToString(), title, NewsBoards.Find(board)!.PostUrl(postId),
            category, thumbnail, new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero));

    private static Task<NewsReceiveResult> Receive(OfficialNewsStore store, string board, long postId, string title = "10/29(목) 정기점검 안내 (06:00 ~ 10:00)") =>
        store.ReceiveAsync(Sample(board, postId, title, category: null), DateTimeOffset.UtcNow);

    private static void EmbedTests()
    {
        var notice = OfficialNewsMessages.BuildEmbed(NewsBoards.Notice, Sample(thumbnail: "https://lwi.nexon.com/m_mabinogim/news/thumb.png"));
        Assert(notice.Author?.Name == "📢 공식 공지" && notice.Title == "정기점검 안내" && notice.Url == "https://mabinogimobile.nexon.com/News/Notice/3571370"
            && notice.Footer?.Text == "몰리 • 공식 공지 알림" && notice.Timestamp is null, "공지 Embed: 게시판 표기·제목·원문 링크·footer, 게시 시각처럼 보이는 timestamp 없음");
        Assert(notice.Image?.Url == "https://lwi.nexon.com/m_mabinogim/news/thumb.png" && notice.Thumbnail is null, "썸네일은 작은 thumbnail이 아닌 큰 Embed image");
        Assert(notice.Fields.Any(f => f.Name == "분류" && f.Value == "점검") && notice.Fields.Any(f => f.Name == "발견 시각" && f.Value == "2026-10-28 12:00 (KST)"),
            "분류 필드와 KST로 바꾼 '발견 시각' 필드");
        var colors = NewsBoards.All.Select(b => OfficialNewsMessages.BuildEmbed(b, Sample(b.Id, category: null)).Color!.Value.RawValue).ToList();
        var labels = NewsBoards.All.Select(b => OfficialNewsMessages.BuildEmbed(b, Sample(b.Id, category: null)).Author!.Value.Name).ToList();
        Assert(colors.Distinct().Count() == 4 && labels.SequenceEqual(["📢 공식 공지", "🛠️ 업데이트", "🎉 이벤트", "📝 에린 노트"]),
            "같은 채널에 모여도 네 게시판의 표기·색상이 다름");
        Assert(OfficialNewsMessages.BuildEmbed(NewsBoards.Notice, Sample(), isTest: true).Footer?.Text == "몰리 • 공식 공지 알림 (테스트)", "테스트 출력은 footer로 실제 알림과 구분");
        var plain = OfficialNewsMessages.Build(NewsBoards.Update, Sample("update", category: null));
        Assert(plain.Embed.Image?.Url == "attachment://update.png" && File.Exists(plain.AttachmentPath) && Path.GetFileName(plain.AttachmentPath) == "update.png"
            && plain.Embed.Fields.All(f => f.Name != "분류") && plain.Embed.Title.Length > 0 && plain.Embed.Url is not null, "썸네일이 없으면 게시판 기본 이미지를 첨부하고, 분류가 없으면 생략");
        foreach (var (board, label, file) in new[]
        {
            (NewsBoards.Notice, (string?)"점검", "notice_maintenance.png"), (NewsBoards.Notice, "주요상품", "notice_shop.png"), (NewsBoards.Notice, "완료", "notice.png"), (NewsBoards.Notice, null, "notice.png"),
            (NewsBoards.Devnote, "개발자노트", "devnote_developer.png"), (NewsBoards.Devnote, "CM노트", "devnote_cm.png"),
        })
        {
            var message = OfficialNewsMessages.Build(board, Sample(board.Id, category: label));
            Assert(message.Embed.Image?.Url == "attachment://" + file && Path.GetFileName(message.AttachmentPath) == file, $"기본 이미지: {board.Name} {label ?? "분류 없음"} → {file}");
        }
        Assert(OfficialNewsMessages.Build(NewsBoards.Events, Sample("events", category: null)) is { AttachmentPath: null, Embed.Image: null }, "이벤트는 기본 이미지 없음");
        Assert(OfficialNewsDefaultImages.FileNames.All(f => File.Exists(Path.Combine(OfficialNewsDefaultImages.DirectoryPath, f))), "기본 이미지 파일이 모두 배포 폴더에 있음");
        Assert(notice.Image?.Url == "https://lwi.nexon.com/m_mabinogim/news/thumb.png" && OfficialNewsMessages.Build(NewsBoards.Notice, Sample(thumbnail: "https://lwi.nexon.com/m_mabinogim/news/thumb.png")).AttachmentPath is null,
            "글 썸네일이 있으면 기본 이미지를 첨부하지 않음");
        foreach (var bad in new[] { "javascript:alert(1)", "ftp://x/y.png", "/relative.png", "not a url", "https://" + new string('a', 3000) + ".png" })
            Assert(OfficialNewsMessages.BuildEmbed(NewsBoards.Events, Sample("events", thumbnail: bad)).Image is null
                && OfficialNewsMessages.BuildEmbed(NewsBoards.Update, Sample("update", category: null, thumbnail: bad)).Image?.Url == "attachment://update.png",
                $"표시할 수 없는 이미지 주소는 버리고 기본 이미지(없으면 이미지 없이) 사용: {bad[..Math.Min(bad.Length, 20)]}");
        var longTitle = new string('가', 4000);
        var devnote = OfficialNewsMessages.BuildEmbed(NewsBoards.Devnote, Sample("devnote", title: longTitle, category: "**개발자노트** @everyone " + new string('나', 300)));
        var category = devnote.Fields.Single(f => f.Name == "분류").Value;
        Assert(devnote.Title.Length == OfficialNewsMessages.MaxTitleLength && devnote.Title.EndsWith('…') && category.Length <= 1024 && category.Contains(@"\*\*개발자노트")
            && devnote.Length <= 6000, "긴 제목·분류는 Discord 제한에 맞게 줄이고 분류의 서식 문자는 escape");
    }

    private static async Task SubscriptionTestsAsync(OfficialNewsStore store, string databasePath)
    {
        var now = DateTimeOffset.UtcNow;
        // 대표 구성: 공지+업데이트 → A, 이벤트 → B(마감 알림과 공유), 에린 노트 → A
        await store.SetSubscriptionAsync(GuildA, "notice", Ch1, now);
        await store.SetSubscriptionAsync(GuildA, "update", Ch1, now);
        await store.SetSubscriptionAsync(GuildA, "events", Ch2, now);
        await store.SetSubscriptionAsync(GuildA, "devnote", Ch1, now);
        foreach (var board in NewsBoards.All) await store.SetSubscriptionAsync(GuildB, board.Id, Ch9, now);
        Assert((await store.GetSubscriptionAsync(GuildA, "notice"))?.ChannelId == Ch1 && (await store.GetSubscriptionAsync(GuildA, "update"))?.ChannelId == Ch1
            && (await store.GetSubscriptionAsync(GuildA, "events"))?.ChannelId == Ch2 && (await store.GetSubscriptionAsync(GuildA, "devnote"))?.ChannelId == Ch1,
            "게시판별 독립 채널, 같은 채널 공유 허용");
        var allInOne = new List<NewsSubscription?>();
        foreach (var board in NewsBoards.All) allInOne.Add(await store.GetSubscriptionAsync(GuildB, board.Id));
        Assert(allInOne.All(s => s is { Enabled: true, ChannelId: Ch9 }), "네 게시판을 한 채널에 등록해도 서로 덮어쓰지 않음");
        Assert(await store.GetSubscriptionAsync(300, "notice") is null, "서버별 설정 격리");

        // 이벤트 마감 알림은 같은 DB·같은 채널이어도 별도 테이블이라 독립적으로 켜고 끈다.
        var expire = new EventExpireAlertSettingStore(databasePath: databasePath);
        await expire.InitializeAsync();
        await expire.SaveAsync(GuildA, new EventExpireAlertSetting { Enabled = true, ChannelId = Ch2, HoursBefore = 24 });
        await store.DisableSubscriptionAsync(GuildA, "events", now);
        Assert((await expire.LoadAsync(GuildA))?.Enabled == true, "이벤트 새 글 알림 해제가 마감 알림을 끄지 않음");
        await store.SetSubscriptionAsync(GuildA, "events", Ch2, now);
        await expire.SaveAsync(GuildA, new EventExpireAlertSetting());
        Assert((await store.GetSubscriptionAsync(GuildA, "events")) is { Enabled: true, ChannelId: Ch2 }, "마감 알림 해제가 이벤트 새 글 알림을 끄지 않음");
        Assert(await store.DisableSubscriptionAsync(300, "notice", now) is null, "등록 안 된 게시판 해제는 null");
    }

    private static async Task ReceiveTestsAsync(OfficialNewsStore store)
    {
        const string eventId = "mabinogi-mobile:notice:1001:created";
        var first = await store.ReceiveAsync(Sample(postId: 1001, title: "10/29(목) 정기점검 안내 (06:00 ~ 10:00)"), DateTimeOffset.UtcNow);
        Assert(first is { Status: NewsReceiveStatus.Created, DeliveryCount: 2 }, "새 글 기록 시 발송 작업 생성");
        var deliveries = await store.GetDeliveriesAsync(eventId);
        Assert(deliveries.Select(d => (d.GuildId, d.ChannelId, d.Status)).SequenceEqual([(GuildA, Ch1, "pending"), (GuildB, Ch9, "pending")]),
            "그 순간 켜진 해당 게시판 구독마다 발송 작업 하나");
        var again = await Receive(store, "notice", 1001, "바뀐 제목");
        Assert(again is { Status: NewsReceiveStatus.Duplicate, DeliveryCount: 0 } && (await store.GetDeliveriesAsync(eventId)).Count == 2
            && (await store.GetEventAsync(eventId))?.Title == "10/29(목) 정기점검 안내 (06:00 ~ 10:00)", "같은 글을 다시 기록해도 작업을 다시 만들지 않고 최초 내용 보존");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Receive(store, "update", 2002))));
        Assert(results.Count(r => r.Status == NewsReceiveStatus.Created) == 1 && results.Count(r => r.Status == NewsReceiveStatus.Duplicate) == 11
            && (await store.GetDeliveriesAsync("mabinogi-mobile:update:2002:created")).Count == 2, "동시에 같은 글을 기록해도 한 번만 생성, 서버별 작업 하나");

        Assert((await Receive(store, "devnote", 3003)).Status == NewsReceiveStatus.Created
            && (await store.GetDeliveriesAsync("mabinogi-mobile:devnote:3003:created")).Select(d => d.ChannelId).SequenceEqual([Ch1, Ch9]), "에린 노트 기록");
        Assert((await Receive(store, "events", 4004)).Status == NewsReceiveStatus.Created
            && (await store.GetDeliveriesAsync("mabinogi-mobile:events:4004:created")).Select(d => d.ChannelId).SequenceEqual([Ch2, Ch9]), "이벤트 기록");

        // 구독이 없는 글도 기록하고, 나중에 구독해도 소급 발송하지 않는다.
        await store.DisableSubscriptionAsync(GuildA, "notice", DateTimeOffset.UtcNow);
        await store.DisableSubscriptionAsync(GuildB, "notice", DateTimeOffset.UtcNow);
        Assert((await Receive(store, "notice", 5005)) is { Status: NewsReceiveStatus.Created, DeliveryCount: 0 }, "구독이 없어도 새 글 기록");
        await store.SetSubscriptionAsync(GuildA, "notice", Ch1, DateTimeOffset.UtcNow);
        await store.SetSubscriptionAsync(GuildB, "notice", Ch9, DateTimeOffset.UtcNow);
        Assert((await Receive(store, "notice", 5005)).Status == NewsReceiveStatus.Duplicate && (await store.GetDeliveriesAsync("mabinogi-mobile:notice:5005:created")).Count == 0,
            "나중에 구독한 서버에 과거 글을 보내지 않음");
    }

    private static async Task WorkerTestsAsync(OfficialNewsStore store)
    {
        var now = new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero).AddYears(10); // 앞 테스트의 실제 시각 작업보다 늦게 둔다
        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(store, sender, () => now, _ => { });

        // Discord 연결 전에는 시도 횟수를 쓰지 않고 대기로 돌린다.
        sender.IsReady = false;
        await worker.ProcessDueAsync(default);
        Assert((await store.GetDeliveriesAsync("mabinogi-mobile:notice:1001:created")).All(d => d is { Status: "pending", Attempts: 0 }) && sender.Sent.Count == 0,
            "Discord가 준비되지 않으면 보내지 않고 시도 횟수도 늘리지 않음");

        // 일부 서버 실패: B는 일시 실패, A는 성공
        sender.IsReady = true;
        sender.Fail = channel => channel == Ch9 ? new NewsDeliveryException("discord_503", false, "일시 장애") : null;
        while (await worker.ProcessDueAsync(default) > 0) { }
        var notice = await store.GetDeliveriesAsync("mabinogi-mobile:notice:1001:created");
        Assert(notice[0] is { GuildId: GuildA, Status: "sent", Attempts: 1, MessageId: not null } && notice[1] is { GuildId: GuildB, Status: "pending", Attempts: 1, ErrorCode: "discord_503" },
            "한 서버의 일시 실패가 다른 서버 성공을 막지 않고 실패 대상만 재시도 대기");
        var sentToCh1 = sender.Sent.Where(s => s.ChannelId == Ch1).Select(s => s.Embed.Author!.Value.Name).ToList();
        Assert(sentToCh1.Contains("📢 공식 공지") && sentToCh1.Contains("🛠️ 업데이트") && sentToCh1.Contains("📝 에린 노트")
            && sender.Sent.Single(s => s.ChannelId == Ch2).Embed.Author!.Value.Name == "🎉 이벤트", "같은 채널의 공지·업데이트·에린 노트가 구분되는 Embed로 발송");
        Assert(sender.Sent.Single(s => s.ChannelId == Ch1 && s.Embed.Author!.Value.Name == "📢 공식 공지").Embed.Fields.Any(f => f is { Name: "분류", Value: "점검" }),
            "공지 분류 표시");
        var sentCount = sender.Sent.Count;
        await worker.ProcessDueAsync(default);
        Assert(sender.Sent.Count == sentCount, "재시도 시각 전에는 다시 보내지 않음");
        now += OfficialNewsDeliveryWorker.BackoffFor(1);
        sender.Fail = _ => null;
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await store.GetDeliveriesAsync("mabinogi-mobile:notice:1001:created"))[1] is { Status: "sent", Attempts: 2 }, "1분 뒤 실패 대상만 다시 보내 성공");
        Assert(OfficialNewsDeliveryWorker.BackoffFor(1) == TimeSpan.FromMinutes(1) && OfficialNewsDeliveryWorker.BackoffFor(2) == TimeSpan.FromMinutes(5)
            && OfficialNewsDeliveryWorker.BackoffFor(3) == TimeSpan.FromMinutes(15) && OfficialNewsDeliveryWorker.BackoffFor(9) == TimeSpan.FromMinutes(60), "재시도 간격 1→5→15→60분 상한");

        // 영구 실패(채널 삭제·권한 상실)는 다시 시도하지 않고, 다른 서버는 계속 보낸다.
        sender.Fail = channel => channel == Ch1 ? new NewsDeliveryException("discord_403_50013", true, "권한 없음") : null;
        await Receive(store, "update", 6006);
        while (await worker.ProcessDueAsync(default) > 0) { }
        now += TimeSpan.FromDays(1);
        while (await worker.ProcessDueAsync(default) > 0) { }
        var permanent = await store.GetDeliveriesAsync("mabinogi-mobile:update:6006:created");
        Assert(permanent[0] is { Status: "failed", Attempts: 1, ErrorCode: "discord_403_50013" } && permanent[1].Status == "sent", "영구 실패는 failed로 남기고 재시도 안 함");

        // 일시 실패도 한도를 넘으면 멈춘다.
        sender.Fail = channel => channel == Ch1 ? new HttpRequestException("network") : null;
        await Receive(store, "update", 7007);
        for (var i = 0; i < OfficialNewsDeliveryWorker.MaxAttempts + 2; i++)
        {
            while (await worker.ProcessDueAsync(default) > 0) { }
            now += TimeSpan.FromHours(1);
        }
        Assert((await store.GetDeliveriesAsync("mabinogi-mobile:update:7007:created"))[0] is { Status: "failed", Attempts: OfficialNewsDeliveryWorker.MaxAttempts } d
            && d.ErrorCode!.StartsWith("retry_exhausted:"), "일시 실패는 최대 시도 횟수 후 failed");
        var summary = await store.GetDeliverySummaryAsync(GuildA, "update");
        Assert(summary is { Waiting: 0, Failed: 2, LastSentAt: not null }, "확인 명령용 발송 현황(대기·실패·마지막 발송)");

        // 해제·채널 변경 뒤 대기 작업은 건너뛰고 새 채널로 소급하지 않는다. 다음 이벤트부터 새 채널로 보낸다.
        sender.Fail = _ => null;
        sender.Sent.Clear();
        await Receive(store, "events", 8008);
        await store.SetSubscriptionAsync(GuildA, "events", Ch1, now);
        await store.DisableSubscriptionAsync(GuildB, "events", now);
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await store.GetDeliveriesAsync("mabinogi-mobile:events:8008:created")).All(x => x is { Status: "skipped", Attempts: 0, ErrorCode: "subscription_changed" })
            && sender.Sent.Count == 0, "구독 해제·채널 변경 전 대기 작업은 skipped, 새 채널로 소급 안 함");
        await Receive(store, "events", 8009);
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert(sender.Sent.Select(s => s.ChannelId).SequenceEqual([Ch1]), "변경 뒤 새 이벤트는 새 채널로만 발송");

        // 긴 제목은 원문 그대로 저장하고 Embed만 줄인다.
        var longTitle = new string('가', 3000);
        await Receive(store, "events", 8010, longTitle);
        while (await worker.ProcessDueAsync(default) > 0) { }
        Assert((await store.GetEventAsync("mabinogi-mobile:events:8010:created"))?.Title == longTitle && sender.Sent[^1].Embed.Title.Length == OfficialNewsMessages.MaxTitleLength,
            "긴 제목은 원문 저장값 유지, Embed 제한 준수");

        // 전송 시간 초과는 일시 실패로 재시도한다.
        sender.Hang = true;
        await Receive(store, "events", 8011);
        var hangWorker = new OfficialNewsDeliveryWorker(store, sender, () => now, _ => { });
        // SendTimeout(60초)을 기다리지 않도록 가짜 전송이 시간 초과 취소를 바로 던진다(앱 종료 토큰은 취소되지 않음).
        while (await hangWorker.ProcessDueAsync(default) > 0) { }
        sender.Hang = false;
        Assert((await store.GetDeliveriesAsync("mabinogi-mobile:events:8011:created"))[0] is { Status: "pending", Attempts: 1, ErrorCode: "timeout" }, "전송 시간 초과는 재시도 대기");
    }

    private static async Task RestartTestsAsync(OfficialNewsStore store, string databasePath)
    {
        var now = DateTimeOffset.UtcNow.AddYears(20);
        await store.SetSubscriptionAsync(GuildA, "notice", Ch1, now);
        await Receive(store, "notice", 9009);
        // 처리 중에 프로세스가 끝난 상황: sending으로 남는다.
        var claimed = await store.ClaimDueAsync(now, OfficialNewsDeliveryWorker.Lease, 100);
        Assert(claimed.Any(c => c.Event.EventId == "mabinogi-mobile:notice:9009:created"), "작업을 sending으로 가져감");
        Assert((await store.ClaimDueAsync(now, OfficialNewsDeliveryWorker.Lease, 100)).Count == 0, "lease 동안 같은 작업을 두 번 가져가지 않음");

        // 재시작: 새 저장소 인스턴스에서 구독·대기 작업을 그대로 읽고, worker는 처리 중 작업을 복구해 보낸다.
        var restarted = new OfficialNewsStore(databasePath);
        await restarted.InitializeAsync();
        Assert((await restarted.GetSubscriptionAsync(GuildA, "notice"))?.ChannelId == Ch1, "재시작 후 구독 유지");
        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(restarted, sender, () => now, _ => { });
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        var second = worker.RunAsync(cts.Token);
        Assert(second.IsCompleted, "worker를 두 번 시작해도 하나만 실행(재연결 중복 방지)");
        for (var i = 0; i < 100 && sender.Sent.All(s => s.Embed.Url != "https://mabinogimobile.nexon.com/News/Notice/9009"); i++) await Task.Delay(50);
        await cts.CancelAsync();
        await run;
        Assert((await restarted.GetDeliveriesAsync("mabinogi-mobile:notice:9009:created")).Any(d => d is { GuildId: GuildA, Status: "sent" }),
            "재시작 시 처리 중이던 작업을 복구해 발송(전송 직후 종료됐다면 중복 가능)");
    }

    private static async Task CollectorTestsAsync(string databasePath)
    {
        var store = new OfficialNewsStore(databasePath);
        await store.InitializeAsync();
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var now = start;
        var source = new FakePageSource();
        var signals = 0;
        var logs = new List<string>();
        var collector = new OfficialNewsCollector(store, source, () => signals++, () => now, logs.Add);
        await store.SetSubscriptionAsync(GuildA, "notice", Ch1, now);
        await store.SetSubscriptionAsync(GuildA, "events", Ch2, now);

        static string Url(NewsBoard board) => board.ListUrl;
        source.Pages[Url(NewsBoards.Notice)] = Page(3, [Card(103, "점검 안내", "점검"), Card(102, "안내 B", "안내"), Card(101, "안내 A", "안내")]);
        source.Pages[Url(NewsBoards.Update)] = Page(2, [Card(202, "업데이트 노트", path: "Update"), Card(201, "업데이트 노트 이전", path: "Update")]);
        source.Pages[Url(NewsBoards.Events)] = Page(1, [EventCard(301, "출석 이벤트", "https://cdn.example.com/e301.png")]);
        source.Pages[Url(NewsBoards.Devnote)] = Page(1, [Card(401, "개발자노트", "개발자노트", "Devnote")]);

        // 1) 첫 확인은 지금 목록을 기준으로만 기록하고 아무것도 보내지 않는다.
        Assert(await collector.CheckAllAsync(default) == 0 && signals == 0, "첫 확인은 기존 글을 보내지 않음");
        Assert(await store.GetBoardStateAsync("notice") is { FloorPostId: 101, LastSuccessAt: not null, LastError: null }
            && (await store.GetSeenPostIdsAsync("notice", [101, 102, 103, 104])).SetEquals([101L, 102L, 103L]), "첫 확인 기준: 가장 작은 글 ID와 본 글 기록");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("notice", 103)) is null, "기준 글은 발송용 글로 기록하지 않음");

        // 2) 새 글은 목록 위에 생긴다. 기준보다 오래된 글이 위로 올라와도 새 글이 아니다.
        now = start.AddMinutes(10);
        source.Pages[Url(NewsBoards.Notice)] = Page(6,
        [
            Card(105, "새 공지 &amp; 둘째", "안내"), Card(104, "새 점검 공지", "점검"), Card(99, "오래된 글 재노출", "안내"),
            Card(103, "점검 안내", "점검"), Card(102, "안내 B", "안내"), Card(101, "안내 A", "안내"),
        ], Banner(105, "https://cdn.example.com/banner105.png"));
        source.Pages[Url(NewsBoards.Events)] = Page(2, [EventCard(302, "할로윈 이벤트", "https://cdn.example.com/e302.png"), EventCard(301, "출석 이벤트", "https://cdn.example.com/e301.png")]);
        Assert(await collector.CheckAllAsync(default) == 3 && signals == 2, "공지 2개·이벤트 1개를 새 글로 찾고 발송 worker를 깨움");
        var n104 = await store.GetEventAsync(NewsBoards.EventIdFor("notice", 104));
        var n105 = await store.GetEventAsync(NewsBoards.EventIdFor("notice", 105));
        var e302 = await store.GetEventAsync(NewsBoards.EventIdFor("events", 302));
        Assert(n104 is { Title: "새 점검 공지", Category: "점검", Url: "https://mabinogimobile.nexon.com/News/Notice/104", ThumbnailUrl: null } && n104.DetectedAt == now
            && n105 is { Title: "새 공지 & 둘째", ThumbnailUrl: "https://cdn.example.com/banner105.png" }, "공지 새 글: 제목·분류·원문 링크·발견 시각·배너 이미지");
        Assert(e302 is { Category: null, ThumbnailUrl: "https://cdn.example.com/e302.png", Url: "https://mabinogimobile.nexon.com/News/Events/302" },
            "이벤트 새 글: 진행중 같은 상태는 분류로 보내지 않고 썸네일 사용");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("notice", 99)) is null, "기준보다 오래된 글이 목록 위로 올라와도 보내지 않음");
        Assert((await store.GetDeliveriesAsync(NewsBoards.EventIdFor("notice", 104))).Select(d => (d.GuildId, d.ChannelId)).SequenceEqual([(GuildA, Ch1)])
            && (await store.GetDeliveriesAsync(NewsBoards.EventIdFor("events", 302))).Select(d => d.ChannelId).SequenceEqual([Ch2]), "게시판별 구독 채널로 발송 작업");

        // 3) 같은 목록을 다시 확인해도 다시 보내지 않는다.
        now = start.AddMinutes(20);
        Assert(await collector.CheckAllAsync(default) == 0 && signals == 2, "이미 본 글은 다시 찾지 않음");

        // 4) 한 페이지가 모두 새 글이면 이미 본 글이 나올 때까지 다음 페이지를 읽는다.
        source.Pages[Url(NewsBoards.Update)] = Page(6, [Card(206, "u206", path: "Update"), Card(205, "u205", path: "Update"), Card(204, "u204", path: "Update")], blockStartKey: "k1");
        source.Pages[OfficialNewsParser.BuildListUrl(NewsBoards.Update, 2, "1", "k1")] =
            Page(6, [Card(203, "u203", path: "Update"), Card(202, "업데이트 노트", path: "Update"), Card(201, "업데이트 노트 이전", path: "Update")], blockStartKey: "k1");
        Assert(await collector.CheckBoardAsync(NewsBoards.Update, default) == 4
            && source.Requested.Contains("https://mabinogimobile.nexon.com/News/Update?pageno=2&blockStartNo=1&blockStartKey=k1"), "새 글이 한 페이지를 넘으면 다음 페이지까지 찾음");

        // 5) 차단·지역 이동 페이지·네트워크 오류는 상태를 진행하지 않고, 복구되면 그동안 올라온 글을 찾는다.
        var lastSuccess = (await store.GetBoardStateAsync("devnote"))!.LastSuccessAt;
        now = start.AddMinutes(30);
        source.Pages[Url(NewsBoards.Devnote)] = BlockedPage;
        Assert(await collector.CheckBoardAsync(NewsBoards.Devnote, default) == 0, "목록이 아닌 응답이면 새 글 없음");
        var failed = await store.GetBoardStateAsync("devnote");
        Assert(failed is { LastError: not null } && failed.LastSuccessAt == lastSuccess && failed.LastCheckedAt == now && failed.LastError.Contains("목록 구조"),
            "확인 실패는 오류만 남기고 마지막 성공 시각 유지");
        now = start.AddMinutes(40);
        source.Pages[Url(NewsBoards.Devnote)] = Page(2, [Card(402, "CM노트", "CM노트", "Devnote"), Card(401, "개발자노트", "개발자노트", "Devnote")]);
        source.Fail = url => url == Url(NewsBoards.Notice) ? new HttpRequestException("network down") : null;
        Assert(await collector.CheckAllAsync(default) == 1, "한 게시판의 네트워크 오류가 다른 게시판 확인을 막지 않음");
        Assert(await store.GetEventAsync(NewsBoards.EventIdFor("devnote", 402)) is { Category: "CM노트" } && await store.GetBoardStateAsync("devnote") is { LastError: null, LastSuccessAt: var ok } && ok == now,
            "복구 후 실패 동안 올라온 에린 노트를 찾고 분류 표시");
        Assert((await store.GetBoardStateAsync("notice"))!.LastError!.Contains("network down") && logs.Count(l => l.Contains("에린 노트 목록 확인 복구")) == 1,
            "네트워크 오류 기록과 복구 로그");

        // 6) 새 글이 최대 페이지 수를 넘으면 읽은 만큼만 기록하고 멈춘다(다음 확인은 이어서 진행).
        source.Fail = _ => null;
        var cursor = "k2";
        for (var pageNo = 1; pageNo <= OfficialNewsCollector.MaxPagesPerCheck + 1; pageNo++)
        {
            var ids = Enumerable.Range(0, 3).Select(i => 1000L - pageNo * 3 - i).ToList();
            source.Pages[OfficialNewsParser.BuildListUrl(NewsBoards.Notice, pageNo, "1", cursor)] = Page(100, ids.Select(id => Card(id, $"공지 {id}", "안내")), blockStartKey: cursor);
        }
        var requestsBefore = source.Requested.Count;
        Assert(await collector.CheckBoardAsync(NewsBoards.Notice, default) == OfficialNewsCollector.MaxPagesPerCheck * 3
            && source.Requested.Count - requestsBefore == OfficialNewsCollector.MaxPagesPerCheck, "한 번에 최대 페이지 수까지만 읽음");

        // 7) 재시작: 새 인스턴스는 기준을 다시 잡지 않고 이어서 찾는다.
        var restarted = new OfficialNewsCollector(new OfficialNewsStore(databasePath), source, () => signals++, () => now, _ => { });
        source.Pages[Url(NewsBoards.Events)] = Page(3, [EventCard(303, "재시작 뒤 이벤트", "https://cdn.example.com/e303.png"),
            EventCard(302, "할로윈 이벤트", "https://cdn.example.com/e302.png"), EventCard(301, "출석 이벤트", "https://cdn.example.com/e301.png")]);
        Assert(await restarted.CheckBoardAsync(NewsBoards.Events, default) == 1 && await store.GetEventAsync(NewsBoards.EventIdFor("events", 303)) is not null,
            "재시작 후에도 기준을 유지하고 새 글만 찾음");
    }

    private sealed class FakePageSource : IMobiEventPageSource
    {
        public Dictionary<string, string> Pages { get; } = [];
        public List<string> Requested { get; } = [];
        public Func<string, Exception?> Fail { get; set; } = _ => null;
        public string Name => "Fake";

        public Task<string> FetchPageAsync(string url, CancellationToken ct)
        {
            Requested.Add(url);
            if (Fail(url) is { } error) throw error;
            return Pages.TryGetValue(url, out var html) ? Task.FromResult(html) : throw new MobiEventFetchException($"HTTP 404 ({url})");
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class FakeNewsSender : IOfficialNewsSender
    {
        private ulong m_NextId = 1000;
        public bool IsReady { get; set; } = true;
        public Func<ulong, Exception?> Fail { get; set; } = _ => null;
        public bool Hang { get; set; }
        public List<(ulong ChannelId, Embed Embed)> Sent { get; } = [];

        public Task<ulong> SendAsync(ulong channelId, NewsMessage message, CancellationToken ct)
        {
            if (Hang) throw new TaskCanceledException("timeout");
            if (Fail(channelId) is { } error) throw error;
            lock (Sent) Sent.Add((channelId, message.Embed));
            return Task.FromResult(Interlocked.Increment(ref m_NextId));
        }
    }
}
