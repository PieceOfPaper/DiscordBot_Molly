internal static class MobiEventTests
{
    // 2026-09 실제 이벤트 목록(/News/Events?headlineId=2501) 서버 렌더링 HTML에서 카드·페이지 구조만 줄여 옮긴 샘플
    private static string Card(string id, string title, string date, string thumb = "https://cdn.example.com/a%EC%9D%B4.png") => $"""
            <li class="item " data-mm-listitem="" data-threadid="{id}">
                <div class="thumbnail">
                    <img src="{thumb}" onerror="this.src='https://lwi.nexon.com/m_mabinogim/brand/community/thumb.png'" alt="이벤트섬네일">
                </div>
                <div class="descript">
                    <div class="order_1">
                        <div class="type">진행중</div>
                        <a href="/News/Events/{id}" data-link="" onclick="event.preventDefault();mmCommunity.Thread.link({id}, this)" style="cursor:pointer" class="title new">
                            <span>{title}</span>
                        </a>
                    </div>
                    <div class="order_2">
                        <div class="date">
                                <span>
            {date}                                </span>
                        </div>
                        <div class="sub_info"><div class="like"><span data-mm-threadlikecount="">0</span></div></div>
                    </div>
                </div>
            </li>
            """;

    private static string Page(int totalCount, params string[] cards) => $"""
        <html><body>
        <ul class="banner"><li class="banner_item swiper-slide" data-link="" data-boardactionpath="/News/Events"><div class="type">진행중</div></li></ul>
        <div class="list_area" data-mm-boardlist=""><ul class="list">
        {string.Concat(cards)}
        </ul>
        <div class="pager">
        <div class="pagination" data-pagingtype="thread" data-mm-paging="" data-blockstartno="1" data-blockstartkey="253402300799,9223372036854775807" data-totalcount="{totalCount}">
        <ul><li class="on">1</li><li onclick="event.preventDefault();mmCommunity.Thread.list(2,this)"><a href="?pageno=2&amp;blockStartNo=1&amp;blockStartKey=253402300799,9223372036854775807">2</a></li></ul>
        </div></div></div>
        </body></html>
        """;

    private static readonly string s_Page1 = Page(3,
        Card("3550530", "추석맞이 잔칫상! 온타임 이벤트 &amp; 안내", "2026.9.24(목) 오전 0시 ~ 2026.09.27(일) 오후 11시 59분까지"),
        Card("3550529", "추석맞이 보름달 상자를 모아라 이벤트 안내", "2026.9.17(목) 점검 후 ~ 2026.10.15(목) 오전 5시 59분까지", "/community/thumb.png"));
    private static readonly string s_Page2 = Page(3,
        Card("3546070", "출석 체크 이벤트", "2026.8.1(토) 오전 6시 ~ 별도 안내 시까지"));

    public static async Task RunAsync()
    {
        var parsed = MobiEventParser.ParseListPage(s_Page1);
        Assert(parsed.HasPagination && parsed.TotalCount == 3 && parsed.BlockStartNo == "1" &&
               parsed.BlockStartKey == "253402300799,9223372036854775807", "이벤트 목록 pagination 메타 해석");
        Assert(parsed.Cards.Count == 2, "이벤트 카드만 추출하고 배너 항목은 제외");
        var first = parsed.Cards[0];
        Assert(first.ThreadId == "3550530" && first.Title == "추석맞이 잔칫상! 온타임 이벤트 & 안내" &&
               first.Url == "https://mabinogimobile.nexon.com/News/Events/3550530", "이벤트 제목(엔티티 디코드)과 상세 URL");
        Assert(first.Range == "2026.9.24(목) 오전 0시 ~ 2026.09.27(일) 오후 11시 59분까지", "이벤트 기간 문구 공백 정리");
        Assert(first.ThumbnailUrl == "https://cdn.example.com/a%EC%9D%B4.png" &&
               parsed.Cards[1].ThumbnailUrl == "https://mabinogimobile.nexon.com/community/thumb.png", "썸네일 절대 URL 변환");
        Assert(MobiEventParser.ParseListPage("<html><body>Just a moment...</body></html>") is { HasPagination: false, Cards.Count: 0 },
            "차단·다른 페이지에서는 카드를 만들지 않음");
        Assert(MobiEventParser.BuildListUrl(2, "1", "253402300799,9223372036854775807") ==
               "https://mabinogimobile.nexon.com/News/Events?headlineId=2501&directionType=DEFAULT&pageno=2&blockStartNo=1&blockStartKey=253402300799%2C9223372036854775807",
            "사이트 페이지 링크 형식의 목록 URL 생성");

        Assert(MobiEventParser.TryParseRange(first.Range, out var s1, out var e1, out var p1) && !p1 &&
               s1 == new DateTime(2026, 9, 24, 0, 0, 0) && e1 == new DateTime(2026, 9, 27, 23, 59, 0), "오전 0시·오후 11시 59분 기간 해석");
        Assert(MobiEventParser.TryParseRange(parsed.Cards[1].Range, out var s2, out var e2, out _) &&
               s2 == new DateTime(2026, 9, 17, 6, 0, 0) && e2 == new DateTime(2026, 10, 15, 5, 59, 0), "점검 후 시작은 06:00으로 해석");
        Assert(MobiEventParser.TryParseRange("2026.8.1(토) 오전 6시 ~ 별도 안내 시까지", out var s3, out var e3, out var p3) &&
               p3 && s3 == new DateTime(2026, 8, 1, 6, 0, 0) && e3 == DateTime.MaxValue, "별도 안내 시까지는 마감 미정");
        Assert(MobiEventParser.TryParseRange("2026.9.1 오후 12시 ~ 2026.9.2", out var s4, out var e4, out _) &&
               s4.Hour == 12 && e4 == new DateTime(2026, 9, 2, 23, 59, 0), "오후 12시는 정오, 종료 시각 생략은 23:59");
        Assert(!MobiEventParser.TryParseRange("2026.13.40 ~ 2026.9.2", out _, out _, out _) &&
               !MobiEventParser.TryParseRange("기간 미정", out _, out _, out _), "잘못된 날짜는 예외 없이 해석 실패");

        await ServiceTestsAsync();
    }

    private static async Task ServiceTestsAsync()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        var logs = new List<string>();
        var http = new FakeSource("HTTP", new() { [1] = s_Page1, [2] = s_Page2 });
        var browser = new FakeSource("Playwright", new() { [1] = s_Page1, [2] = s_Page2 });
        var browserCreated = 0;
        var service = new MobiEventService(http, () => { browserCreated++; return browser; }, () => now,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), msg => { lock (logs) logs.Add(msg); });

        var initial = await service.GetCurrentEventsAsync();
        Assert(initial is { IsStale: false, Events.Count: 3 } && http.Requests == 2 && browserCreated == 0,
            "이벤트 목록을 HTTP로 전체 페이지 수집(브라우저 미사용)");
        Assert(http.LastUrl.Contains("pageno=2&blockStartNo=1&blockStartKey="), "2페이지 요청에 1페이지 pagination 키 사용");
        Assert(initial!.Events.Single(x => x.eventName == "출석 체크 이벤트").isPerma, "다른 페이지의 마감 미정 이벤트도 포함");

        // 호출자가 받은 목록을 정렬·수정해도 캐시에 영향이 없어야 합니다.
        ((List<MobiEventResult>)initial!.Events).Clear();
        var cached = await service.GetCurrentEventsAsync();
        Assert(cached is { Events.Count: 3 } && http.Requests == 2, "캐시 유효 시간 안에서는 재수집 없이 복사본 반환");

        var concurrentHttp = new FakeSource("HTTP", new() { [1] = s_Page1, [2] = s_Page2 }) { Delay = TimeSpan.FromMilliseconds(50) };
        var concurrentService = new MobiEventService(concurrentHttp, null, () => now, log: _ => { });
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => concurrentService.GetCurrentEventsAsync()));
        Assert(concurrent.All(x => x is { Events.Count: 3 }) && concurrentHttp.Requests == 2, "동시 조회는 한 번의 수집으로 합쳐 처리");

        // HTTP가 차단되면 Playwright로 대체하고, 대체 소스는 사용 후 정리합니다.
        now = now.AddMinutes(2);
        http.Blocked = true;
        var viaFallback = await service.GetCurrentEventsAsync();
        Assert(viaFallback is { IsStale: false, Events.Count: 3 } && browserCreated == 1 && browser.Disposed &&
               service.LastSnapshot?.SourceName == "Playwright", "HTTP 차단 시 Playwright 대체 수집 후 브라우저 정리");

        // 둘 다 실패하면 마지막 정상 데이터를 유지하고 오래된 데이터임을 알립니다.
        now = now.AddMinutes(2);
        browser.Blocked = true;
        browser.Disposed = false;
        var stale = await service.GetCurrentEventsAsync();
        Assert(stale is { IsStale: true, Events.Count: 3 } && browserCreated == 2 && browser.Disposed,
            "모든 수집 실패 시 마지막 정상 데이터를 오래된 데이터로 반환");

        now = now.AddMinutes(2);
        var cooldown = await service.GetCurrentEventsAsync();
        Assert(cooldown is { IsStale: true } && browserCreated == 2 && logs.Any(x => x.Contains("보류")),
            "대체 수집 실패 후 쿨다운 동안 브라우저를 다시 띄우지 않음");

        // 구조가 달라져 카드가 비면 검증 실패로 보고 캐시를 교체하지 않습니다.
        now = now.AddMinutes(2);
        http.Blocked = false;
        http.Pages[2] = Page(3);
        var partial = await service.GetCurrentEventsAsync();
        Assert(partial is { IsStale: true, Events.Count: 3 } && logs.Any(x => x.Contains("2페이지에 이벤트 카드가 없음")),
            "일부 페이지가 비면 새 데이터로 교체하지 않음");

        var empty = new MobiEventService(new FakeSource("HTTP", new() { [1] = "<html></html>" }), null, () => now, log: _ => { });
        Assert(await empty.GetCurrentEventsAsync() == null && !await empty.RefreshAsync(), "정상 데이터가 한 번도 없으면 null");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = false;
        try { await new MobiEventService(http, null, () => now, log: _ => { }).RefreshAsync(cts.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "호출자 취소는 수집 실패로 삼키지 않고 전달");
    }

    private sealed class FakeSource(string name, Dictionary<int, string> pages) : IMobiEventPageSource, IAsyncDisposable
    {
        private int m_Requests;
        public Dictionary<int, string> Pages { get; } = pages;
        public bool Blocked { get; set; }
        public bool Disposed { get; set; }
        public TimeSpan Delay { get; init; }
        public int Requests => m_Requests;
        public string LastUrl { get; private set; } = "";
        public string Name => name;

        public async Task<string> FetchPageAsync(string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref m_Requests);
            LastUrl = url;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Blocked) throw new MobiEventFetchException("HTTP 403");
            var pageNo = int.Parse(System.Text.RegularExpressions.Regex.Match(url, @"pageno=(\d+)").Groups[1].Value);
            return Pages[pageNo];
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
