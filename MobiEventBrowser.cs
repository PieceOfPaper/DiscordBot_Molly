using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

public record MobiEventResult(
    string eventName, string url, DateTime start, DateTime end
);

public static class MobiEventBrowser
{
    // 진행중 이벤트 1페이지
    private const string EVENTS_URL =
        "https://mabinogimobile.nexon.com/News/Events?headlineId=2501&directionType=DEFAULT&pageno=1";

    // JS에서 받아올 DTO (※ 메서드 내부가 아니라 클래스 스코프에 두어야 컴파일됨)
    private class JsEventDto
    {
        public string title { get; set; } = "";
        public string href { get; set; } = "";
        public string range { get; set; } = "";
        public string fullText { get; set; } = "";
    }

    private static readonly BrowserTypeLaunchOptions s_BrowserTypeLaunchOpt = new()
    {
        Headless = true,
        Args = new[]
        {
            "--no-sandbox", // ★ 핵심: systemd 하드닝과 충돌 회피
            "--disable-setuid-sandbox", // 보조
            "--disable-dev-shm-usage",
            "--no-default-browser-check",
            "--disable-background-networking",
            "--disable-features=Translate,BackForwardCache,AcceptCHFrame",
            "--mute-audio",
            "--no-zygote", // (선택) 프로세스 수 감축
            "--renderer-process-limit=1", // 렌더러 동시 수 최소화
        },
    };
    private static readonly BrowserNewContextOptions s_BrowserNewContextOpt = new()
    {
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        Locale = "ko-KR",
        TimezoneId = "Asia/Seoul",
        ServiceWorkers = ServiceWorkerPolicy.Block,
        BypassCSP = true,
        ViewportSize = new() { Width = 800, Height = 600 }, // 불필요하게 큰 해상도 지양
        DeviceScaleFactor = 1,
    };
    private static readonly PageGotoOptions s_PageGotoOpt = new()
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 30000,
    };

    public class BrowserContainer : IAsyncDisposable
    {
        public int index = 0;

        private bool m_IsInited = false;
        private IPlaywright m_Pw;
        private IBrowser m_Browser;
        private IBrowserContext m_BrowserContext;

        private bool m_IsRunning = false;
        public bool isRunning => m_IsRunning;

        private async Task Init(CancellationToken ct = default, Action<string>? log = null)
        {
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MobiEventBrowser] {index}: {msg}");

            m_Pw = await Playwright.CreateAsync();
            Log("init pw");
            m_Browser = await m_Pw.Chromium.LaunchAsync(s_BrowserTypeLaunchOpt);
            Log("init browser");

            m_BrowserContext = await m_Browser.NewContextAsync(s_BrowserNewContextOpt);
            await m_BrowserContext.RouteAsync("**/*",
                async route =>
                {
                    var t = route.Request.ResourceType;
                    if (t is "image" or "media" or "font")
                        await route.AbortAsync();
                    else
                        await route.ContinueAsync();
                });
            m_BrowserContext.SetDefaultTimeout(5000); // 일반 동작(클릭/채우기)은 5초
            m_BrowserContext.SetDefaultNavigationTimeout(30000); // 네비게이션은 30초로 별도 설정
            Log("init browser context");

            m_IsInited = true;
        }

        public async Task<IEnumerable<MobiEventResult>> Run(
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            m_IsRunning = true;
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MobiEventBrowser] {index}: {msg}");

            // Init!
            if (m_IsInited == false) await Init(ct, log);

            Log("start search");
            var page = await m_BrowserContext.NewPageAsync();

            try
            {
                await page.RouteAsync("**/*.{png,jpg,jpeg,gif,webp,mp4,mp3,woff,woff2,ttf}", r => r.AbortAsync());
                Log("page.RouteAsync success");

                // 1) 목록 진입
                await page.GotoAsync(EVENTS_URL, s_PageGotoOpt);
                Log("page.GotoAsync success");

                // 2) '진행중' 탭 클릭 (있을 때만)
                try
                {
                    var tab = page.Locator("a:has-text('진행중'), button:has-text('진행중')");
                    if (await tab.CountAsync() > 0)
                    {
                        await tab.First.ClickAsync();
                        await page.WaitForTimeoutAsync(400);
                    }
                }
                catch
                {
                    /* 기본이 진행중일 수도 있음 */
                }

                var results = new List<MobiEventResult>();
                var nowKst = MobiTime.now;

                // 3) 1~5페이지 순회 (숫자 페이저)
                for (int p = 1; p <= 5; p ++)
                {
                    ct.ThrowIfCancellationRequested();
                    Log($"extract page {p}");

                    var items = await ExtractEventsOnCurrentPageAsync(page);
                    Log($"extract page {p} result: {items.Count}");

                    foreach (var it in items)
                    {
                        if (TryParseRangeKst(it.range, out var startKst, out var endKst, out var isPerma))
                        {
                            // 지금(KST) 기준으로 진행중만 수집. 상시는 end = DateTime.MaxValue로 반환
                            bool ongoing = isPerma || (startKst <= nowKst && nowKst <= endKst);
                            if (ongoing)
                            {
                                results.Add(new MobiEventResult(
                                    it.title,
                                    it.href,
                                    startKst,
                                    isPerma ? DateTime.MaxValue : endKst
                                ));
                            }
                        }
                    }

                    // 다음 페이지 URL을 정밀 탐색 (href의 pageno= 값으로 판단)
                    var nextHref = await GetNextPageHrefAsync(page, p + 1);
                    if (string.IsNullOrEmpty(nextHref))
                        break;

                    await page.GotoAsync(nextHref, s_PageGotoOpt);
                }

                // 4) (제목+URL) 기준 중복 제거
                var dedup = results
                    .GroupBy(x => (x.eventName, x.url))
                    .Select(g => g.First())
                    .ToList();

                return dedup;
            }
            finally
            {
                await page.CloseAsync();
                m_IsRunning = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (m_IsInited == false)
                return;

            await m_BrowserContext.DisposeAsync();
            await m_Browser.DisposeAsync();
            m_Pw.Dispose();
        }
    }

    private const int BROWSER_COUNT = 1;
    private static Dictionary<int, List<BrowserContainer>> m_BrowserQueues = new();
    private static object m_BrowserLock = new();

    public static bool IsFullRunning()
    {
        lock (m_BrowserLock)
        {
            if (m_BrowserQueues.ContainsKey(0) == false)
                return false;

            if (m_BrowserQueues[9].Count < BROWSER_COUNT)
                return false;

            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (m_BrowserQueues[0][i].isRunning == false)
                    return false;
            }
        }

        return true;
    }

    public static async Task<IEnumerable<MobiEventResult>?> GetCurrentEventsAsync(
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        BrowserContainer browserContainer = null;
        lock (m_BrowserLock)
        {
            if (m_BrowserQueues.ContainsKey(0) == false)
                m_BrowserQueues.Add(0, new());

            var list = m_BrowserQueues[0];
            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (i >= list.Count)
                    list.Add(new() { index = i });

                if (list[i].isRunning) continue;

                browserContainer = list[i];
                break;
            }
        }

        if (browserContainer != null)
            return await browserContainer.Run(ct, log);
        return null;
    }
    private static async Task<List<(string title, string href, string range)>> ExtractEventsOnCurrentPageAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<List<JsEventDto>>("""
                                                             () => {
                                                               const items = [];
                                                               const nodes = Array.from(document.querySelectorAll(
                                                                 'li, article, .list li, .board_list li, .article_list li, .item, .card, .row > div'
                                                               ));
                                                               for (const el of nodes) {
                                                                 const text = (el.textContent || "").trim();
                                                                 if (!text) continue;
                                                                 if (!text.includes("진행중")) continue;

                                                                 const a = el.querySelector('a[href*="/News/Events"], a[href*="/news/events"]');
                                                                 const title = (a?.textContent || "").trim();
                                                                 const href  = a?.href || "";
                                                                 if (!title || !href) continue;

                                                                 let range = "";
                                                                 const lines = text.split("\n").map(s => s.trim()).filter(Boolean);
                                                                 for (const line of lines) {
                                                                   if (line.includes("~") || line.includes("까지") || line.includes("별도 안내 시까지") || line.includes("상시")) {
                                                                     range = line;
                                                                     break;
                                                                   }
                                                                 }
                                                                 items.push({ title, href, range, fullText: text });
                                                               }
                                                               return items;
                                                             }
                                                             """);

        var list = new List<(string title, string href, string range)>();
        foreach (var r in raw)
            list.Add((r.title, r.href, r.range));
        return list;
    }


    private static bool TryParseRangeKst(string range, out DateTime startKst, out DateTime endKst, out bool isPerma)
    {
        // 기본값
        isPerma = false;
        startKst = DateTime.MinValue;
        endKst = DateTime.MaxValue;

        if (string.IsNullOrWhiteSpace(range))
            return false;

        // 상시/무기한
        if (range.Contains("별도 안내 시까지") || range.Contains("상시"))
        {
            // 시작은 있으면 파싱, 없으면 오늘 00:00 KST
            var startSide = range.Split('~').FirstOrDefault() ?? "";
            if (!TryParseKoreanDateTime(startSide, defaultHour: 0, defaultMinute: 0, out startKst))
                startKst = MobiTime.now.Date;
            isPerma = true;
            endKst = DateTime.MaxValue;
            return true;
        }

        // 일반: "시작 ~ 종료"
        var parts = range.Split('~');
        if (parts.Length < 2)
            return false;

        var startSideStr = parts[0].Trim();
        var endSideStr = parts[1].Trim().Replace("까지", "").Trim();

        // 시작: 시간 없으면 00:00, '점검 후'면 06:00 가정
        if (!TryParseKoreanDateTime(startSideStr,
                defaultHour: (startSideStr.Contains("점검 후") ? 6 : 0),
                defaultMinute: 0,
                out startKst))
            return false;

        // 종료: 시간 없으면 23:59
        if (!TryParseKoreanDateTime(endSideStr, defaultHour: 23, defaultMinute: 59, out endKst))
            return false;

        return true;
    }

    private static bool TryParseKoreanDateTime(string src, int defaultHour, int defaultMinute, out DateTime kst)
    {
        // 예: 2025.09.11(목) 오전 6시  /  2025.09.24(수) 오후 11시 59분  /  2025.09.24(수)
        kst = DateTime.MinValue;

        var d = Regex.Match(src, @"(?<y>\d{4})\.(?<m>\d{2})\.(?<d>\d{2})");
        if (!d.Success) return false;

        int y = int.Parse(d.Groups["y"].Value);
        int mo = int.Parse(d.Groups["m"].Value);
        int da = int.Parse(d.Groups["d"].Value);

        int h = defaultHour;
        int min = defaultMinute;

        var t = Regex.Match(src, @"(?<ampm>오전|오후)?\s*(?<h>\d{1,2})\s*시(?:\s*(?<min>\d{1,2})\s*분)?");
        if (t.Success)
        {
            if (t.Groups["h"].Success) h = int.Parse(t.Groups["h"].Value);
            if (t.Groups["min"].Success) min = int.Parse(t.Groups["min"].Value);
            var ampm = t.Groups["ampm"].Success ? t.Groups["ampm"].Value : null;

            if (ampm == "오전")
            {
                if (h == 12) h = 0; // 오전 12 = 00
            }
            else if (ampm == "오후")
            {
                if (h != 12) h += 12; // 오후 1~11
            }
        }

        // KST 의미의 Unspecified DateTime (비교/정렬에 충분)
        kst = new DateTime(y, mo, da, h, min, 0, DateTimeKind.Unspecified);
        return true;
    }
    
    private static async Task<string?> GetNextPageHrefAsync(IPage page, int nextPageNo)
    {
        // /News/Events 경로이면서 pageno=nextPageNo 인 a[href]를 전역에서 찾는다.
        // (상단 메뉴 등 다른 링크를 배제하기 위해 pathname과 쿼리를 모두 검사)
        var href = await page.EvaluateAsync<string?>("""
                                                     (pageNo) => {
                                                       const anchors = Array.from(document.querySelectorAll('a[href]'));
                                                       // 1) 전체 a[href] 중에서 정확히 pageno=pageNo & /News/Events 인 링크
                                                       for (const a of anchors) {
                                                         const raw = a.getAttribute('href');
                                                         if (!raw) continue;
                                                         try {
                                                           const u = new URL(raw, location.href);
                                                           if (!u.pathname.includes('/News/Events')) continue;
                                                           const pn = u.searchParams.get('pageno');
                                                           if (pn === String(pageNo)) return u.href;
                                                         } catch (e) {}
                                                       }
                                                       // 2) 페이저 컨테이너 내부 우선 탐색 (보수적 보강)
                                                       const pagers = document.querySelectorAll('.paging, .pagination, .board_paging, .page_list, .pager, nav[class*="paging"]');
                                                       for (const pager of pagers) {
                                                         const as = pager.querySelectorAll('a[href*="pageno="]');
                                                         for (const a of as) {
                                                           try {
                                                             const u = new URL(a.getAttribute('href'), location.href);
                                                             if (!u.pathname.includes('/News/Events')) continue;
                                                             const pn = u.searchParams.get('pageno');
                                                             if (pn === String(pageNo)) return u.href;
                                                           } catch (e) {}
                                                         }
                                                       }
                                                       return null;
                                                     }
                                                     """, nextPageNo);

        return href;
    }
}
