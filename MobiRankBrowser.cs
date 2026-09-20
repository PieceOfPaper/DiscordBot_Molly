using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// 공식 랭킹 페이지의 data-serverid와 일치해야 합니다.
public enum MobiServer { 데이안 = 1, 아이라, 던컨, 알리사, 메이븐, 라사, 칼릭스, 몰리 = 8 }

public record MobiRankResult(
    int Rank,
    int Power,
    string ServerName,
    string ClassName,
    int? TotalScore = null,
    int? Combat = null,
    int? Charm = null,
    int? Life = null
);

public static class MobiRankBrowser
{
    // 클래스명 → classid 매핑 (질문에 제공된 값 기준)
    private static readonly Dictionary<string, long> CLASSNAME_TO_ID = new(StringComparer.Ordinal)
    {
        ["전체 클래스"] = 0,
        ["전사"] = 1285686831,
        ["대검전사"] = 2077040965,
        ["검술사"] = 958792831,
        ["기사"] = 1196285044,
        ["궁수"] = 995607437,
        ["석궁사수"] = 1468161402,
        ["장궁병"] = 1901800669,
        ["마법사"] = 1876490724,
        ["화염술사"] = 1452582855,
        ["빙결술사"] = 1262278397,
        ["전격술사"] = 589957914,
        ["힐러"] = 323147599,
        ["사제"] = 1504253211,
        ["수도사"] = 204163716,
        ["암흑술사"] = 1063887341,
        ["음유시인"] = 1319349030,
        ["댄서"] = 413919140,
        ["악사"] = 956241373,
        ["도적"] = 1443648579,
        ["격투가"] = 1790463651,
        ["듀얼블레이드"] = 1957076952,
        ["견습 전사"] = 33220478,
        ["견습 궁수"] = 1600175531,
        ["견습 마법사"] = 1497581170,
        ["견습 힐러"] = 1795991954,
        ["견습 음유시인"] = 2017961297,
        ["견습 도적"] = 2058842272,
    };

    private static readonly BrowserTypeLaunchOptions s_BrowserTypeLaunchOpt = new()
    {
        Headless = false,
        Args = new[]
        {
            "--no-sandbox", // ★ 핵심: systemd 하드닝과 충돌 회피
            "--disable-setuid-sandbox", // 보조
            "--disable-dev-shm-usage",
            "--no-default-browser-check",
            "--disable-features=Translate,BackForwardCache",
            "--mute-audio",
            "--no-zygote", // (선택) 프로세스 수 감축
            "--renderer-process-limit=1", // 렌더러 동시 수 최소화
        },
    };
    private static readonly BrowserNewContextOptions s_BrowserNewContextOpt = new()
    {
        Locale = "ko-KR",
        TimezoneId = "Asia/Seoul",
        ViewportSize = new() { Width = 800, Height = 600 }, // 불필요하게 큰 해상도 지양
        DeviceScaleFactor = 1,
    };
    private static readonly PageGotoOptions s_PageGotoOpt = new()
    {
        WaitUntil = WaitUntilState.Commit,
        Timeout = 15000,
    };
    private const int PAGE_READY_TIMEOUT = 60000; // headed 브라우저에서 보안 검사 후 랭킹 UI가 나타날 때까지의 실측 기반 상한
    private const int CONTROL_READY_TIMEOUT = 10000;
    private const int SINGLE_ACTION_TIMEOUT = 2000;
    private const int SEARCH_SUBMIT_TIMEOUT = 10000;
    private const int SEARCH_RESULT_TIMEOUT = 45000;
    private const int POLL_INTERVAL = 1000;

    public class BrowserContainer : IAsyncDisposable
    {
        public int rankingIndex = 0;
        public int index = 0;

        private bool m_IsInited = false;
        private IPlaywright m_Pw = null!;
        private IBrowser m_Browser = null!;
        private IBrowserContext m_BrowserContext = null!;
        private IPage? m_RankingPage;

        // 풀을 얻는 시점과 실제 작업 시작 사이에 같은 컨테이너가 두 번 선택되지 않도록
        // 원자적으로 예약합니다. 예약 해제는 Run의 finally에서만 수행합니다.
        private int m_IsRunning;
        public bool isRunning => Volatile.Read(ref m_IsRunning) != 0;

        public bool TryReserve(int requestedRankingIndex)
        {
            if (Interlocked.CompareExchange(ref m_IsRunning, 1, 0) != 0)
                return false;

            rankingIndex = requestedRankingIndex;
            return true;
        }

        private void ReleaseReservation() => Volatile.Write(ref m_IsRunning, 0);

        private async Task<IPage> GetRankingPageAsync(Action<string> log)
        {
            if (m_RankingPage is { IsClosed: false })
            {
                log("기존 랭킹 탭 재사용");
                return m_RankingPage;
            }

            m_RankingPage = await m_BrowserContext.NewPageAsync();
            await m_RankingPage.RouteAsync(
                "**/*.{png,jpg,jpeg,gif,webp,mp4,mp3,woff,woff2,ttf}", r => r.AbortAsync());
            log("새 랭킹 탭 생성");
            return m_RankingPage;
        }

        private async Task Init(CancellationToken ct = default, Action<string>? log = null)
        {
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {index}: {msg}");

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

        // Run()의 세션 준비 구간과 예열(WarmUpAsync)이 공유합니다. 초기화, 탭 확보,
        // 페이지 이동, 403 보안 검사 우회, 기능 UI 대기까지를 담당합니다.
        private async Task<IPage> EnsureRankingPageReadyAsync(
            int targetRankingIndex,
            CancellationToken ct,
            Action<string> log)
        {
            // Init 실패도 호출부 finally에서 예약을 해제해야 다음 요청이 복구할 수 있습니다.
            if (!m_IsInited) await Init(ct, log);

            var page = await GetRankingPageAsync(log);
            var rankingUrl = $"https://mabinogimobile.nexon.com/Ranking/List?t={targetRankingIndex}";
            var navigationResponse = await page.GotoAsync(rankingUrl, s_PageGotoOpt);
            log($"랭킹 페이지 초기 응답 - HTTP 상태: {navigationResponse?.Status.ToString() ?? "응답 없음"}");

            if (navigationResponse?.Status == 403)
            {
                log("랭킹 페이지 보안 검사 감지 - 공식 홈페이지 선행 방문 후 재시도");
                var homeResponse = await page.GotoAsync(
                    "https://mabinogimobile.nexon.com/",
                    s_PageGotoOpt);
                log($"공식 홈페이지 초기 응답 - HTTP 상태: {homeResponse?.Status.ToString() ?? "응답 없음"}");
                await Task.Delay(POLL_INTERVAL, ct);

                navigationResponse = await page.GotoAsync(rankingUrl, s_PageGotoOpt);
                log($"랭킹 페이지 재시도 응답 - HTTP 상태: {navigationResponse?.Status.ToString() ?? "응답 없음"}");
            }

            await WaitForRankingControlsAsync(page, PAGE_READY_TIMEOUT, ct, log);
            return page;
        }

        // 봇 시작 직후 Discord 연결과 별개로 호출되어 브라우저 기동과 세션 확보(403 우회)를
        // 미리 끝내둡니다. 실패해도 다른 기능에 영향이 없도록 예외를 밖으로 던지지 않습니다.
        public async Task WarmUpAsync(CancellationToken ct = default, Action<string>? log = null)
        {
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {index}: {msg}");

            IPage? page = null;
            try
            {
                page = await EnsureRankingPageReadyAsync(rankingIndex, ct, Log);
                Log("예열 완료 - 랭킹 페이지 세션 준비됨");
            }
            catch (Exception ex)
            {
                Log($"예열 중 예외: {ex.GetType().Name}: {ex.Message}");
                await LogPageOnExceptionAsync(page, Log);
                if (page?.IsClosed == true)
                    m_RankingPage = null;
            }
            finally
            {
                ReleaseReservation();
            }
        }

        public async Task<MobiRankResult?> Run(
            string nickname,
            MobiServer server,
            string? className = null,
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {index}: {msg}");

            IPage? page = null;
            try
            {
                var keyword = rankingIndex switch
                {
                    2 => "매력",
                    3 => "생활력",
                    4 => "점수",
                    _ => "전투력",
                };

                if (string.IsNullOrWhiteSpace(nickname)) throw new ArgumentException("nickname is required");
                if (nickname.Length > 12) nickname = nickname[..12]; // maxlength=12

                Log($"start search(nickname='{nickname}', server={server}, class='{className ?? "전체 클래스"}')");

                page = await EnsureRankingPageReadyAsync(rankingIndex, ct, Log);

                var serverId = (int)server;          // 예: 칼릭스=7, 몰리=8
                long classId = 0;
                if (!string.IsNullOrWhiteSpace(className) && CLASSNAME_TO_ID.TryGetValue(className.Trim(), out var cid))
                    classId = cid;
                var classDisplay = classId == 0 ? "전체 클래스" : className?.Trim();

                // 0) 공식 JS(mmRanking.list)가 쓰는 rankdata를 페이지 컨텍스트에서 직접 호출합니다.
                // UI 클릭·폴링을 모두 건너뛰므로 훨씬 빠르지만, 실패하면 기존 브라우저 조작 흐름으로 그대로 넘어갑니다.
                var (fastHtml, fastReason) = await TryFetchRankDataFragmentAsync(
                    page, rankingIndex, nickname, serverId, classId, ct, Log);
                if (fastHtml != null)
                {
                    var fastParsed = await TryParseCompleteRankResultAsync(
                        page, rankingIndex, nickname, server, className, keyword, fastHtml);
                    if (fastParsed.Result is not null)
                    {
                        Log($"rankdata 직접 호출로 결과 확인: {fastParsed.Result.Rank}위, {fastParsed.Result.ClassName}");
                        return fastParsed.Result;
                    }
                    Log($"rankdata 직접 호출 응답은 받았지만 결과 파싱 실패({fastParsed.Reason}) - 브라우저 흐름으로 재시도");
                }
                else
                {
                    Log($"rankdata 직접 호출 실패({fastReason}) - 브라우저 흐름으로 재시도");
                }

                // 서버 선택
                var serverOk = await SelectByDataAsync(
                    page, "serverid", serverId.ToString(), server.ToString(), CONTROL_READY_TIMEOUT, ct, Log);
                Log($"select server - {serverOk}");
                if (!serverOk)
                    throw new TimeoutException($"서버 '{server}' 선택 준비 시간이 초과되었습니다.");


                // -------------------- 클래스 선택 --------------------
                var classOk = await SelectByDataAsync(
                    page, "classid", classId.ToString(), classDisplay, CONTROL_READY_TIMEOUT, ct, Log);
                Log($"select class - {classOk}");
                if (!classOk)
                    throw new TimeoutException($"클래스 '{classDisplay}' 선택 준비 시간이 초과되었습니다.");
    
    
                // 3) 닉네임 입력과 검색 실행을 같은 DOM 작업으로 처리
                var searchSubmitted = await SubmitSearchAsync(
                    page, nickname, SEARCH_SUBMIT_TIMEOUT, ct, Log);
                if (!searchSubmitted)
                    throw new TimeoutException("캐릭터 검색 입력 또는 실행 준비 시간이 초과되었습니다.");

                // 4) 필요한 값이 모두 채워진 첫 결과를 즉시 반환
                var result = await WaitForCompleteRankResultAsync(
                    page, rankingIndex, nickname, server, className, keyword, SEARCH_RESULT_TIMEOUT, ct, Log);
                return result;
            }
            catch (Exception ex)
            {
                Log($"랭킹 페이지 처리 중 예외: {ex.GetType().Name}: {ex.Message}");
                await LogPageOnExceptionAsync(page, Log);
                if (page?.IsClosed == true)
                    m_RankingPage = null;
                throw;
            }
            finally
            {
                ReleaseReservation();
            }
        }

        private static async Task LogPageOnExceptionAsync(IPage? page, Action<string> log)
        {
            if (page is null)
            {
                log("예외 발생 시 랭킹 페이지가 생성되지 않아 URL과 제목을 확인할 수 없습니다.");
                return;
            }

            try
            {
                var title = await page.TitleAsync();

                log($"예외 발생 페이지 URL: {page.Url}");
                log($"예외 발생 페이지 제목: {title}");
            }
            catch (Exception logException)
            {
                log($"예외 발생 페이지 정보 출력 실패: {logException.GetType().Name}: {logException.Message}");
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

    // Lightsail에서 headed Chromium 두 개는 동시에 처리할 수 있으면서도 메모리·CPU
    // 부담을 보수적으로 제한하는 값입니다. 랭킹 종류와 무관하게 전역으로 적용합니다.
    private const int BROWSER_COUNT = 2;
    private const int MAX_BROWSERS_PER_GUILD = 2;
    private static readonly List<BrowserContainer> m_BrowserPool = new();
    private static readonly Dictionary<ulong, int> m_GuildReservations = new();
    private static readonly object m_BrowserLock = new();

    public static bool IsFullRunning(ulong guildId)
    {
        lock (m_BrowserLock)
        {
            if (m_GuildReservations.GetValueOrDefault(guildId) >= MAX_BROWSERS_PER_GUILD)
                return true;

            if (m_BrowserPool.Count < BROWSER_COUNT)
                return false;

            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (!m_BrowserPool[i].isRunning)
                    return false;
            }
        }

        return true;
    }

    // 봇 시작 직후 호출되어 풀의 브라우저들을 미리 기동하고 세션을 확보합니다.
    // 헤드풀 Chromium을 동시에 여러 개 기동·탐색시키면 자원이 제한된 환경(Lightsail 등)에서
    // 프로세스 전체가 눌려 Discord 인터랙션 처리(DeferAsync 등)까지 지연될 수 있어,
    // 한 번에 컨테이너 하나씩만 예약·예열합니다. 실제 사용자 요청이 먼저 컨테이너를
    // 예약했다면 그 컨테이너는 건너뛰고, 해당 요청 처리 과정에서 자연스럽게 초기화됩니다.
    public static async Task WarmUpAllAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        for (var i = 0; i < BROWSER_COUNT; i++)
        {
            BrowserContainer? container = null;
            lock (m_BrowserLock)
            {
                if (i >= m_BrowserPool.Count)
                    m_BrowserPool.Add(new() { index = i });

                if (m_BrowserPool[i].TryReserve(1))
                    container = m_BrowserPool[i];
            }

            if (container is null)
                continue;

            await container.WarmUpAsync(ct, log);
        }
    }

    public static async Task<MobiRankResult?> GetRankBySearchAsync(
        int rankingIndex,
        string nickname,
        MobiServer server,
        string? className = null,
        CancellationToken ct = default,
        Action<string>? log = null,
        ulong guildId = 0)
    {
        BrowserContainer? browserContainer = null;
        lock (m_BrowserLock)
        {
            if (m_GuildReservations.GetValueOrDefault(guildId) >= MAX_BROWSERS_PER_GUILD)
                return null;

            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (i >= m_BrowserPool.Count)
                    m_BrowserPool.Add(new() { index = i });

                if (!m_BrowserPool[i].TryReserve(rankingIndex))
                    continue;

                browserContainer = m_BrowserPool[i];
                m_GuildReservations[guildId] = m_GuildReservations.GetValueOrDefault(guildId) + 1;
                break;
            }
        }

        if (browserContainer != null)
        {
            try
            {
                return await browserContainer.Run(nickname, server, className, ct, log);
            }
            finally
            {
                lock (m_BrowserLock)
                {
                    var remaining = m_GuildReservations.GetValueOrDefault(guildId) - 1;
                    if (remaining <= 0)
                        m_GuildReservations.Remove(guildId);
                    else
                        m_GuildReservations[guildId] = remaining;
                }
            }
        }

        return null;
    }

    // ---------------- helpers ----------------
    private static async Task<bool> TryClick(IPage page, string selector, int timeoutMs = 2000)
    {
        try
        {
            await page.Locator(selector).First.ClickAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch { return false; }
    }
    private static async Task<bool> TryFill(IPage page, string selector, string text, int timeoutMs = 2000)
    {
        try
        {
            await page.Locator(selector).First.FillAsync(text, new() { Timeout = timeoutMs });
            return true;
        }
        catch { return false; }
    }
    private static async Task<bool> SafeClick(ILocator locator, Action<string> log)
    {
        try
        {
            await locator.First.ClickAsync();
            return true;
        }
        catch (Exception ex)
        {
            log($"Click failed: {ex.Message}");
            return false;
        }
    }

    private static string SliceAround(string text, string key, int back, int fwd)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return text;
        int start = Math.Max(0, i - back);
        int end = Math.Min(text.Length, i + fwd);
        return text[start..end];
    }

    private sealed record RankDataFetchResult(bool Ok, string? Html, string? Reason);

    // 공식 mmRanking.js(list 함수)가 검색 시 호출하는 것과 같은 요청을 페이지 컨텍스트
    // 안에서 직접 보냅니다. 이미 보안 검사를 통과한 탭에서 실행하므로 쿠키는 same-origin
    // fetch 기본 동작으로 자동 전송됩니다. 실패하면 호출부가 기존 브라우저 조작 흐름으로
    // 넘어갈 수 있도록 (null, 실패사유)를 돌려줍니다.
    private static async Task<(string? Html, string Reason)> TryFetchRankDataFragmentAsync(
        IPage page,
        int rankingIndex,
        string nickname,
        int serverId,
        long classId,
        CancellationToken ct,
        Action<string> log)
    {
        try
        {
            var json = await page.EvaluateAsync<string>(
                @"async (args) => {
                    const form = new FormData();
                    form.append(""t"", String(args.rankingIndex));
                    form.append(""pageno"", ""1"");
                    form.append(""s"", String(args.serverId));
                    form.append(""c"", String(args.classId));
                    form.append(""search"", args.nickname);

                    let response;
                    try {
                        response = await fetch(""/Ranking/List/rankdata"", {
                            method: ""POST"",
                            cache: ""no-cache"",
                            headers: { ""X-Requested-With"": ""XMLHttpRequest"" },
                            body: form
                        });
                    } catch (e) {
                        return JSON.stringify({ Ok: false, Reason: ""fetch 예외: "" + (e?.message || String(e)) });
                    }

                    if (!response.ok)
                        return JSON.stringify({ Ok: false, Reason: ""HTTP "" + response.status });

                    const text = await response.text();
                    try {
                        const parsed = JSON.parse(text);
                        if (parsed && typeof parsed === ""object"")
                            return JSON.stringify({ Ok: false, Reason: parsed.ResultMessage || ""서버가 JSON 오류 응답을 반환함"" });
                    } catch (e) { /* JSON이 아니면 정상 HTML 응답 */ }

                    if (text.indexOf(""결과가 없습니다."") > -1)
                        return JSON.stringify({ Ok: false, Reason: ""검색 결과 없음"" });

                    return JSON.stringify({ Ok: true, Html: text });
                }",
                new { rankingIndex, nickname, serverId, classId });

            var result = JsonSerializer.Deserialize<RankDataFetchResult>(json);
            if (result is { Ok: true })
                return (result.Html, "완료");

            return (null, result?.Reason ?? "알 수 없는 실패");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log($"rankdata 직접 호출 예외: {ex.GetType().Name}: {ex.Message}");
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // html이 없으면 실제 페이지(document)를, 있으면 rankdata 직접 호출로 받은 HTML 조각을
    // 대상으로 탐색합니다. 조각은 document에 붙이지 않은 임시 div에 innerHTML로 채워 넣습니다.
    private static async Task<string> ExtractRecordBlockAsync(IPage page, string nickname, string keyword, string? html = null)
    {
        // 검색 결과가 재렌더링되는 도중 Locator 개수와 실제 노드가 달라지는 경쟁 조건을 피하기 위해
        // 한 번의 브라우저 DOM 평가 안에서 후보 탐색과 텍스트 추출을 끝냅니다.
        return await page.EvaluateAsync<string>(
            @"args => {
                const normalize = value => (value || """").replace(/\s+/g, "" "").trim();
                const root = args.html != null
                    ? Object.assign(document.createElement(""div""), { innerHTML: args.html })
                    : document;
                const expectedCharacter = `캐릭터명 ${args.nickname}`;

                const candidates = Array.from(
                    root.querySelectorAll(""li, div, article, section, tr""))
                    .map(element => normalize(element.innerText || element.textContent))
                    .filter(text =>
                        text.includes(expectedCharacter)
                        && text.includes(""서버명"")
                        && text.includes(""클래스"")
                        && text.includes(args.keyword));

                if (candidates.length === 0)
                    return """";

                candidates.sort((left, right) => left.length - right.length);
                return candidates[0];
            }",
            new { nickname, keyword, html });
    }

    private static string SliceOneRecordFromPlain(string all, string nickname)
    {
        // 공백 정규화
        var text = Regex.Replace(all ?? "", @"\s+", " ").Trim();
        var i = text.IndexOf(nickname, StringComparison.Ordinal);
        if (i < 0) return "";

        // 닉네임 앞쪽에서 가장 가까운 "NNN위"의 시작을 찾고,
        // 뒤쪽에서 다음 "NNN위" 또는 "서버명 " 경계를 찾는다.
        var startRank = Regex.Matches(text[..i], @"(\d+)\s*위").Cast<Match>().LastOrDefault()?.Index ?? Math.Max(0, i - 80);
        var nextRank = Regex.Match(text[(i + nickname.Length)..], @"(\d+)\s*위");
        var nextServer = Regex.Match(text[(i + nickname.Length)..], @"서버명\s+[^\s]+");

        int end = text.Length;
        if (nextRank.Success) end = Math.Min(end, i + nickname.Length + nextRank.Index);
        if (nextServer.Success) end = Math.Min(end, i + nickname.Length + nextServer.Index);

        var block = text.Substring(startRank, Math.Min(end - startRank, 600));
        return block;
    }

    private static int? ExtractKoreanNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"([\d,]+)");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
    }

    private sealed record OverallRankFields(
        string? Rank,
        string? ServerName,
        string? ClassName,
        string? TotalScore,
        string? Combat,
        string? Charm,
        string? Life);

    // html이 없으면 실제 페이지(document)를, 있으면 rankdata 직접 호출로 받은 HTML 조각을 대상으로 탐색합니다.
    private static async Task<OverallRankFields?> ExtractOverallRankFieldsAsync(IPage page, string nickname, string? html = null)
    {
        // 결과 항목을 찾고 필드를 읽는 일을 하나의 DOM 평가에서 끝냅니다. 닉네임을
        // CSS selector에 삽입하지 않아 특수문자가 있어도 selector가 깨지지 않습니다.
        var json = await page.EvaluateAsync<string?>(
            @"args => {
                const normalize = value => (value || """").replace(/\s+/g, "" "").trim();
                const root = args.html != null
                    ? Object.assign(document.createElement(""div""), { innerHTML: args.html })
                    : document;
                const character = Array.from(
                    root.querySelectorAll(""li.item dd[data-charactername]""))
                    .find(node => node.getAttribute(""data-charactername"") === args.nickname);
                const item = character?.closest(""li.item"");
                if (!item)
                    return null;

                const dlFor = label => Array.from(item.querySelectorAll(""dl""))
                    .find(dl => normalize(dl.querySelector(""dt"")?.textContent) === label);
                const textFor = label => normalize(dlFor(label)?.querySelector(""dd"")?.textContent);
                const scoreDl = Array.from(item.querySelectorAll(""dl""))
                    .find(dl => normalize(dl.querySelector(""dt"")?.textContent)
                        .startsWith(""종합 점수""));
                // 1,000위 이상은 ""3,453위""처럼 쉼표가 붙으므로 숫자만 매치하면 안 됩니다.
                // 이 패턴은 tests/Molly.DataTests/Program.cs의 overallRankPattern과 맞춰야 합니다.
                const rank = Array.from(item.querySelectorAll(""dt""))
                    .map(node => normalize(node.textContent))
                    .find(text => /^[\d,]+위$/.test(text)) || """";

                return JSON.stringify({
                    Rank: rank,
                    ServerName: textFor(""서버명""),
                    ClassName: textFor(""클래스""),
                    TotalScore: normalize(scoreDl?.querySelector(""dt"")?.textContent),
                    Combat: normalize(scoreDl?.querySelector(""span.type_1"")?.textContent),
                    Charm: normalize(scoreDl?.querySelector(""span.type_2"")?.textContent),
                    Life: normalize(scoreDl?.querySelector(""span.type_3"")?.textContent)
                });
            }",
            new { nickname, html });

        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<OverallRankFields>(json);
    }

    private static async Task WaitForRankingControlsAsync(
        IPage page,
        int timeoutMs,
        CancellationToken ct,
        Action<string> log)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var controlsReady = await page.EvaluateAsync<bool>(
                @"() => {
                    const hasSelectBox = dataType => Array.from(
                        document.querySelectorAll(""div.select_box""))
                        .some(box => box.querySelector(
                            `li[data-searchtype='${dataType}']`));
                    return hasSelectBox(""serverid"")
                        && hasSelectBox(""classid"")
                        && document.querySelector(""input[name='search']"") instanceof HTMLInputElement;
                }");
            if (controlsReady)
            {
                log("랭킹 페이지 기능 UI 준비 완료");
                return;
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        throw new TimeoutException($"랭킹 페이지 기능 UI가 {timeoutMs}ms 안에 준비되지 않았습니다.");
    }

    private static async Task<bool> SelectByDataAsync(
        IPage page,
        string dataType,
        string value,
        string? expectSelectedText,
        int timeoutMs,
        CancellationToken ct,
        Action<string> log)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var verifyOnly = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var selectionState = await page.EvaluateAsync<string?>(
                    @"args => {
                        const box = Array.from(document.querySelectorAll(""div.select_box""))
                            .find(candidate => candidate.querySelector(
                                `li[data-searchtype='${args.dataType}']`));
                        const selected = box?.querySelector("".selected"");
                        const option = box?.querySelector(
                            `li[data-searchtype='${args.dataType}'][data-${args.dataType}='${args.value}']`);
                        if (!(selected instanceof HTMLElement)
                            || !(option instanceof HTMLElement))
                            return null;

                        const currentText = (selected.textContent || """").trim();
                        const optionIsSelected = option.dataset.selected === ""true""
                            || option.classList.contains(""on"");
                        const selectedTextMatches = !args.expectSelectedText
                            || currentText.includes(args.expectSelectedText);
                        if (optionIsSelected && selectedTextMatches)
                            return `selected:${currentText}`;

                        if (!args.verifyOnly) {
                            selected.click();

                            option.click();
                            return `clicked:${currentText}`;
                        }

                        return `pending:${currentText}`;
                    }",
                    new { dataType, value, expectSelectedText, verifyOnly });

                if (selectionState?.StartsWith("selected:", StringComparison.Ordinal) == true)
                {
                    log($"선택 완료: '{selectionState["selected:".Length..]}'");
                    return true;
                }

                // 클릭 직후의 노드는 재렌더링으로 사라질 수 있으므로 다음 폴링에서는
                // 최신 DOM의 선택 상태만 확인하고, 실패했을 때 그 다음 폴링에 재시도합니다.
                verifyOnly = selectionState?.StartsWith("clicked:", StringComparison.Ordinal) == true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 동적 재렌더링 중이면 다음 폴링에서 최신 DOM으로 다시 시도합니다.
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        return false;
    }

    private static async Task<bool> SubmitSearchAsync(
        IPage page,
        string nickname,
        int timeoutMs,
        CancellationToken ct,
        Action<string> log)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // 동적 재렌더링 중 Locator가 오래된 노드를 기다리지 않도록 현재 DOM에서 입력만 수행합니다.
                var actualValue = await page.EvaluateAsync<string?>(
                    @"nickname => {
                        const input = document.querySelector(""input[name='search']"");
                        if (!(input instanceof HTMLInputElement))
                            return null;

                        const setter = Object.getOwnPropertyDescriptor(
                            HTMLInputElement.prototype, ""value"")?.set;
                        if (setter)
                            setter.call(input, nickname);
                        else
                            input.value = nickname;

                        input.dispatchEvent(new InputEvent(""input"", {
                            bubbles: true,
                            inputType: ""insertText"",
                            data: nickname
                        }));
                        input.dispatchEvent(new Event(""change"", { bubbles: true }));
                        return input.value;
                    }",
                    nickname);

                if (!string.Equals(actualValue, nickname, StringComparison.Ordinal))
                {
                    log($"캐릭터 검색 입력값 불일치 - 요청: '{nickname}', 실제: '{actualValue ?? "입력창 없음"}'");
                    await Task.Delay(POLL_INTERVAL, ct);
                    continue;
                }

                // 프런트엔드 상태 갱신을 한 이벤트 루프 이상 기다린 뒤 별도 DOM 작업으로 클릭합니다.
                await Task.Delay(500, ct);
                var beforeNames = await GetVisibleCharacterNamesAsync(page);
                var clicked = await page.EvaluateAsync<bool>(
                    @"() => {
                        const button = document.querySelector(
                            ""button[data-searchtype='search']"");
                        if (!(button instanceof HTMLElement))
                            return false;

                        button.click();
                        return true;
                    }");

                if (!clicked)
                {
                    log("캐릭터 검색 버튼을 현재 DOM에서 찾지 못해 재시도합니다.");
                    await Task.Delay(POLL_INTERVAL, ct);
                    continue;
                }

                log(
                    $"캐릭터 검색 입력 및 실행 완료 - 실제 입력값: '{actualValue}', " +
                    $"검색 전 표시 캐릭터: {string.Join(", ", beforeNames.Take(5))}");
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log($"캐릭터 검색 입력 또는 실행 재시도: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        return false;
    }

    private static async Task<string[]> GetVisibleCharacterNamesAsync(IPage page)
    {
        return await page.EvaluateAsync<string[]>(
            @"() => Array.from(document.querySelectorAll(""li.item""))
                .map(item => {
                    const node = item.querySelector(""dd[data-charactername]"");
                    return (node?.getAttribute(""data-charactername"")
                        || node?.textContent
                        || """").trim();
                })
                .filter(Boolean)");
    }

    private static async Task<MobiRankResult?> WaitForCompleteRankResultAsync(
        IPage page,
        int rankingIndex,
        string nickname,
        MobiServer requestedServer,
        string? requestedClass,
        string keyword,
        int timeoutMs,
        CancellationToken ct,
        Action<string> log)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var lastReason = "아직 검사하지 않음";
        var pollCount = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            pollCount++;

            try
            {
                var parsed = await TryParseCompleteRankResultAsync(
                    page, rankingIndex, nickname, requestedServer, requestedClass, keyword);
                if (parsed.Result is not null)
                {
                    log($"완성된 랭킹 결과 확인: {parsed.Result.Rank}위, {parsed.Result.ClassName}");
                    return parsed.Result;
                }

                if (!string.Equals(lastReason, parsed.Reason, StringComparison.Ordinal)
                    || pollCount % 5 == 0)
                {
                    lastReason = parsed.Reason;
                    log($"랭킹 결과 대기 중: {lastReason}");
                }
            }
            catch (PlaywrightException ex) when (!ct.IsCancellationRequested)
            {
                lastReason = $"결과 DOM 갱신 중: {ex.GetType().Name}";
                log($"랭킹 결과 대기 중: {lastReason}");
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        log($"'{nickname}'의 완성된 랭킹 결과가 {timeoutMs}ms 안에 나타나지 않았습니다. 마지막 상태: {lastReason}");
        await LogSearchTimeoutDiagnosticsAsync(page, nickname, log);
        return null;
    }

    private static async Task LogSearchTimeoutDiagnosticsAsync(
        IPage page,
        string nickname,
        Action<string> log)
    {
        try
        {
            var diagnostics = await page.EvaluateAsync<string>(
                @"nickname => {
                    const normalize = value => (value || """")
                        .replace(/[\u200B-\u200D\uFEFF]/g, """")
                        .replace(/\s+/g, "" "")
                        .trim();
                    const bodyText = normalize(document.body?.innerText || """");
                    const lowerBody = bodyText.toLocaleLowerCase();
                    const lowerNickname = nickname.toLocaleLowerCase();
                    const nicknameIndex = lowerBody.indexOf(lowerNickname);
                    const snippetStart = nicknameIndex < 0 ? 0 : Math.max(0, nicknameIndex - 120);
                    const snippet = nicknameIndex < 0
                        ? ""찾지 못함""
                        : bodyText.substring(snippetStart, nicknameIndex + nickname.length + 240);

                    const input = document.querySelector(""input[name='search']"");
                    const selected = Array.from(document.querySelectorAll(""div.select_box .selected""))
                        .map(element => normalize(element.textContent));
                    const items = Array.from(document.querySelectorAll(""li.item""));
                    const characterNames = items
                        .map(item => {
                            const byAttribute = item.querySelector(""dd[data-charactername]"");
                            if (byAttribute)
                                return normalize(
                                    byAttribute.getAttribute(""data-charactername"")
                                    || byAttribute.textContent);

                            const text = normalize(item.innerText || item.textContent);
                            const match = text.match(/캐릭터명\s+([^\s]+)/);
                            return match ? match[1] : """";
                        })
                        .filter(Boolean);

                    const uniqueNames = Array.from(new Set(characterNames));
                    const noResultText = [
                        ""검색 결과가 없습니다"",
                        ""검색 결과가 없어요"",
                        ""조회 결과가 없습니다"",
                        ""캐릭터를 찾을 수 없습니다""
                    ].find(text => bodyText.includes(text)) || ""없음"";

                    return [
                        `검색 입력값: '${input?.value || """"}'`,
                        `선택 상태: ${selected.join("" / "") || ""확인 불가""}`,
                        `li.item 개수: ${items.length}`,
                        `표시 캐릭터 수: ${uniqueNames.length}`,
                        `표시 캐릭터 목록(최대 30명): ${uniqueNames.slice(0, 30).join("", "") || ""없음""}`,
                        `페이지의 닉네임 정확 포함: ${bodyText.includes(nickname)}`,
                        `페이지의 닉네임 대소문자 무시 포함: ${nicknameIndex >= 0}`,
                        `검색 결과 없음 문구: ${noResultText}`,
                        `닉네임 주변 텍스트: ${snippet}`
                    ].join(""\n"");
                }",
                nickname);

            log($"랭킹 검색 시간 초과 진단 시작\n{diagnostics}\n랭킹 검색 시간 초과 진단 끝");
        }
        catch (Exception ex)
        {
            log($"랭킹 검색 시간 초과 진단 실패: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<(MobiRankResult? Result, string Reason)> TryParseCompleteRankResultAsync(
        IPage page,
        int rankingIndex,
        string nickname,
        MobiServer requestedServer,
        string? requestedClass,
        string keyword,
        string? html = null)
    {
        if (rankingIndex == 4)
        {
            var overall = await ExtractOverallRankFieldsAsync(page, nickname, html);
            if (overall is null)
                return (null, "대상 캐릭터 항목을 찾지 못함");

            var overallMissingFields = new List<string>();
            var overallRank = ExtractKoreanNumber(overall.Rank);
            var total = ExtractKoreanNumber(overall.TotalScore);
            var combat = ExtractKoreanNumber(overall.Combat);
            var charm = ExtractKoreanNumber(overall.Charm);
            var life = ExtractKoreanNumber(overall.Life);
            if (overallRank is null) overallMissingFields.Add("순위");
            if (string.IsNullOrWhiteSpace(overall.ServerName)) overallMissingFields.Add("서버");
            if (string.IsNullOrWhiteSpace(overall.ClassName)) overallMissingFields.Add("클래스");
            if (total is null) overallMissingFields.Add("종합 점수");
            if (combat is null) overallMissingFields.Add("전투력");
            if (charm is null) overallMissingFields.Add("매력");
            if (life is null) overallMissingFields.Add("생활력");
            if (overallMissingFields.Count > 0)
                return (null, $"종합 랭킹 필드 부족: {string.Join(", ", overallMissingFields)}");

            return (new MobiRankResult(
                overallRank!.Value,
                total!.Value,
                overall.ServerName!,
                overall.ClassName!,
                total,
                combat,
                charm,
                life), "완료");
        }

        var block = await ExtractRecordBlockAsync(page, nickname, keyword, html);
        if (string.IsNullOrWhiteSpace(block))
            return (null, "대상 캐릭터 항목을 찾지 못함");

        var rankMatch = Regex.Match(block, @"([\d,]+)\s*위");
        var powerMatch = Regex.Match(block, @$"{keyword}\s*([\d,]+)");
        var serverMatch = Regex.Match(block, @"서버명\s*([^\s]+)");
        var classMatch = Regex.Match(block, @"클래스\s*([^\s]+)");

        var missingFields = new List<string>();
        if (!rankMatch.Success) missingFields.Add("순위");
        if (!powerMatch.Success) missingFields.Add(keyword);
        if (!serverMatch.Success) missingFields.Add("서버");
        if (!classMatch.Success) missingFields.Add("클래스");
        if (missingFields.Count > 0)
            return (null, $"기본 필드 부족: {string.Join(", ", missingFields)}");

        var rank = int.Parse(rankMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var power = int.Parse(powerMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var serverName = serverMatch.Groups[1].Value;
        var className = classMatch.Groups[1].Value;

        return (new MobiRankResult(rank, power, serverName, className), "완료");
    }


}
