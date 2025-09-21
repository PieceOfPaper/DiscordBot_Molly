using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using System.Text.Json;

public record MobiEventResult(
    string eventName, string url, DateTime start, DateTime end, bool isPerma
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

        public async Task<List<MobiEventResult>> Run(CancellationToken ct = default, Action<string>? log = null)
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

                // 1) 목록 진입 (1페이지)
                await page.GotoAsync(EVENTS_URL, s_PageGotoOpt);
                Log("page.GotoAsync success");

                // 목록 로드 보장 (data-threadid 카드 기준)
                await page.WaitForSelectorAsync("[data-threadid]", new() { Timeout = 10000 });
                Log("page.WaitForSelectorAsync success");

                // (선택) '진행중' 탭 클릭 – 페이지가 탭 구조일 때만 존재
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
                    /* 기본이 진행중일 수 있음 */
                }

                var results = new List<MobiEventResult>();
                var nowKst = MobiTime.now;

                // 2) 전체 페이지 수 계산 (.pagination의 data-totalcount / 1페이지의 카드 수)
                var (totalCount, itemsPerPage, totalPages) = await GetPaginationMetaAsync(page);
                if (totalPages <= 0) totalPages = 1;
                Log($@"[dbg] totalCount={totalCount}, itemsPerPage={itemsPerPage}, totalPages={totalPages}");

                // 3) pageno=1..totalPages 순회 (data-blockStart*를 그대로 써서 URL 생성)
                for (int pageno = 1; pageno <= totalPages; pageno ++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (pageno > 1)
                    {
                        var nextUrl = await BuildEventsPageUrlAsync(page, pageno);
                        Log($@"[dbg] nav to p{pageno}: {nextUrl ?? "null"}");
                        if (string.IsNullOrEmpty(nextUrl))
                        {
                            Log($@"[dbg] cannot build url for p{pageno}, break.");
                            break;
                        }

                        await page.GotoAsync(nextUrl, s_PageGotoOpt);
                        await page.WaitForSelectorAsync("[data-threadid]", new() { Timeout = 10000 });

                        // (선택) 탭이 유지되지 않는 레이아웃이면 다시 클릭
                        try
                        {
                            var tab = page.Locator("a:has-text('진행중'), button:has-text('진행중')");
                            if (await tab.CountAsync() > 0)
                            {
                                await tab.First.ClickAsync();
                                await page.WaitForTimeoutAsync(300);
                            }
                        }
                        catch
                        {
                            /* 무시 */
                        }
                    }

                    Log($"extract page {pageno} (url={page.Url})");

                    // 1페이지든 이후 페이지든 동일 추출(data-threadid 기반)
                    var items = await ExtractEventsOnCurrentPageAsync(page);
                    Log($@"[dbg] p{pageno} extracted items={items.Count}");

                    // 기간 파싱 → 진행중 필터링 → 결과 적재
                    foreach (var it in items)
                    {
                        if (TryParseRange(it.range, out var startDateTime, out var endDateTime, out var isPerma))
                        {
                            results.Add(new MobiEventResult(
                                it.title,
                                it.href,
                                startDateTime,
                                endDateTime,
                                isPerma
                            ));
                        }
                    }
                }

                // 4) (제목+URL) 기준 중복 제거 후 반환
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

    public static async Task<List<MobiEventResult>?> GetCurrentEventsAsync(
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
        var results = new List<(string title, string href, string range)>();

        // 카드가 실제로 뜰 때까지 대기
        await page.WaitForSelectorAsync("[data-threadid]", new() { Timeout = 10000 });

        // JS에서 배열을 만들어 JSON.stringfy로 문자열로 반환 (Playwright 제네릭 역직렬화 우회)
        var json = await page.EvaluateAsync<string>(@"
() => {
  const items = [];
  const cards = Array.from(document.querySelectorAll('[data-threadid]'));

  for (const el of cards) {
    const id = (el.getAttribute('data-threadid') || '').trim();
    if (!/^\d+$/.test(id)) continue;

    const href = new URL('/News/Events/' + id, location.href).href;

    // 제목
    let title = (el.querySelector('.title, .tit, .subject, h3, h4, strong, .txt_tit')?.textContent || '').trim();
    if (!title) {
      const lines = (el.innerText || '').split('\n').map(s => s.trim()).filter(Boolean);
      // ‘이벤트 공지/바로가기/진행중/종료’ 같은 잡텍스트는 제외
      title = (lines.find(s => !/이벤트\s*공지|바로가기|진행중|종료|이벤트\s*종료일/.test(s)) || lines[0] || '').trim();
    }
    if (!title) continue;

    // 기간 한 줄
    let range = '';
    const text = (el.innerText || '').trim();
    for (const line of text.split('\n').map(s => s.trim()).filter(Boolean)) {
      if (line.includes('~') || line.includes('까지') || line.includes('별도 안내 시까지') || line.includes('상시')) {
        range = line;
        break;
      }
    }

    items.push({ title, href, range });
  }

  return JSON.stringify(items);
}
");

        if (string.IsNullOrEmpty(json))
            return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                var href = el.TryGetProperty("href", out var h) ? (h.GetString() ?? "") : "";
                var range = el.TryGetProperty("range", out var r) ? (r.GetString() ?? "") : "";

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(href))
                    results.Add((title, href, range));
            }
        }
        catch (Exception)
        {
            // JSON 파싱 실패 시 빈 결과 반환
        }

        // 중복 제거 (URL 기준)
        return results
            .GroupBy(x => x.href)
            .Select(g => g.First())
            .ToList();
    }

    private static bool TryParseRange(string range, out DateTime startDateTime, out DateTime endDateTime, out bool isPerma)
    {
        // 기본값
        isPerma = false;
        startDateTime = DateTime.MinValue;
        endDateTime = DateTime.MaxValue;

        if (string.IsNullOrWhiteSpace(range))
            return false;

        // 상시/무기한
        if (range.Contains("별도 안내 시까지") || range.Contains("상시"))
        {
            // 시작은 있으면 파싱, 없으면 오늘 00:00 KST
            var startSide = range.Split('~').FirstOrDefault() ?? "";
            if (!TryParseKoreanDateTime(startSide, defaultHour: 0, defaultMinute: 0, out startDateTime))
                startDateTime = DateTime.MinValue;
            isPerma = true;
            endDateTime = DateTime.MaxValue;
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
                out startDateTime))
            return false;

        // 종료: 시간 없으면 23:59
        if (!TryParseKoreanDateTime(endSideStr, defaultHour: 23, defaultMinute: 59, out endDateTime))
            return false;

        return true;
    }

    private static bool TryParseKoreanDateTime(string src, int defaultHour, int defaultMinute, out DateTime dateTime)
    {
        // 예: 2025.09.11(목) 오전 6시  /  2025.09.24(수) 오후 11시 59분  /  2025.09.24(수)
        dateTime = DateTime.MinValue;

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

        dateTime = new DateTime(y, mo, da, h, min, 0, DateTimeKind.Unspecified);
        return true;
    }
    
    // .pagination의 data-*와 현재 페이지의 카드 수로 총 페이지수를 계산
    private static async Task<(int totalCount, int itemsPerPage, int totalPages)> GetPaginationMetaAsync(IPage page)
    {
        // JSON 문자열로 받아와서 C#에서 파싱(Playwright의 제네릭 역직렬화 우회)
        var json = await page.EvaluateAsync<string>(@"
() => {
  const pag = document.querySelector('.pagination[data-pagingtype=""thread""]');
  const total = pag ? parseInt(pag.getAttribute('data-totalcount') || '0') : 0;
  const per   = document.querySelectorAll('[data-threadid]').length || 10;
  const pages = (per > 0 && total > 0) ? Math.ceil(total / per) : 1;
  return JSON.stringify({ total, per, pages });
}
");
        int total = 0, per = 10, pages = 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            total = doc.RootElement.GetProperty("total").GetInt32();
            per = doc.RootElement.GetProperty("per").GetInt32();
            pages = doc.RootElement.GetProperty("pages").GetInt32();
        }
        catch
        {
            /* keep defaults */
        }

        if (pages <= 0) pages = 1;
        return (total, per, pages);
    }
    
    // 현재 페이지의 .pagination data-attrs와 location.href를 기반으로 target 페이지의 URL을 생성
    private static async Task<string?> BuildEventsPageUrlAsync(IPage page, int targetPageNo)
    {
        var href = await page.EvaluateAsync<string?>(@"
(pageNo) => {
  const pag = document.querySelector('.pagination[data-pagingtype=""thread""]');
  if (!pag) return null;

  const blockStartNo  = pag.getAttribute('data-blockstartno')  || '';
  const blockStartKey = pag.getAttribute('data-blockstartkey') || '';

  const u = new URL(location.href);
  // 확실히 /News/Events 로 고정
  if (!u.pathname.includes('/News/Events')) u.pathname = '/News/Events';

  // 기존 headlineId, directionType 유지
  // pageno 교체 + blockStartNo/Key 추가
  u.searchParams.set('pageno', String(pageNo));
  if (blockStartNo)  u.searchParams.set('blockStartNo', blockStartNo);
  if (blockStartKey) u.searchParams.set('blockStartKey', blockStartKey);

  return u.href;
}
",
            targetPageNo);

        return href;
    }
}
