using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// 서버는 그대로 사용
public enum MobiServer { 데이안=1, 아이라, 던컨, 알리사, 메이븐, 라사, 칼릭스 }

public record MobiRankResult(
    int Rank, int Power, string ServerName, string ClassName
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
    private const int SELECT_TIMEOUT = 2000;
    private const int SELECT_RENDER_WAIT_TIME = 2000; //무언가 선택했을 때 렌더링까지 대기하는 시간

    public class BrowserContainer : IAsyncDisposable
    {
        public int rankingIndex = 0;
        public int index = 0;

        private bool m_IsInited = false;
        private IPlaywright m_Pw;
        private IBrowser m_Browser;
        private IBrowserContext m_BrowserContext;

        private bool m_IsRunning = false;
        public bool isRunning => m_IsRunning;

        private async Task Init(CancellationToken ct = default, Action<string>? log = null)
        {
            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {rankingIndex}_{index}: {msg}");

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

        public async Task<MobiRankResult?> Run(
            string nickname,
            MobiServer server,
            string? className = null,
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            m_IsRunning = true;

            void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {rankingIndex}_{index}: {msg}");

            //Init!
            if (m_IsInited == false) await Init(ct, log);

            var keyword = "전투력";
            switch (rankingIndex)
            {
                case 1:
                    keyword = "전투력";
                    break;
                case 2:
                    keyword = "매력";
                    break;
                case 3:
                    keyword = "생활력";
                    break;
            }

            if (string.IsNullOrWhiteSpace(nickname)) throw new ArgumentException("nickname is required");
            if (nickname.Length > 12) nickname = nickname[..12]; // maxlength=12

            Log($"start search(nickname='{nickname}', server={server}, class='{className ?? "전체 클래스"}')");

            var page = await m_BrowserContext.NewPageAsync();
            Log("NewPageAsync success");
            await page.RouteAsync("**/*.{png,jpg,jpeg,gif,webp,mp4,mp3,woff,woff2,ttf}", r => r.AbortAsync());
            Log("page.RouteAsync success");
            await page.GotoAsync($"https://mabinogimobile.nexon.com/Ranking/List?t={rankingIndex}", s_PageGotoOpt);
            Log("page.GotoAsync success");


            // 서버 선택
            var serverId = (int)server;          // 예: 칼릭스=7
            var serverOk = await SelectByDataAsync(page, "serverid", serverId.ToString(), server.ToString(), SELECT_TIMEOUT, Log);
            await page.WaitForTimeoutAsync(SELECT_RENDER_WAIT_TIME);
            Log($"select server - {serverOk}");

            
            // -------------------- 클래스 선택 --------------------
            long classId = 0;
            if (!string.IsNullOrWhiteSpace(className) && CLASSNAME_TO_ID.TryGetValue(className.Trim(), out var cid))
                classId = cid;

            // classBox 먼저 안정적으로 찾기
            var classBox = FindSelectBox(page, "class");

            // 표시명(검증용). 0이면 '전체 클래스'로 기대.
            var classDisplay = classId == 0 ? "전체 클래스" : className?.Trim();
            var classOk = await SelectFromDropdownAsync(
                page,
                classBox,
                $"li[data-searchtype='classid'][data-classid='{classId}']",
                classDisplay,                 // 표시명 검증. 필요 없다면 null
                SELECT_TIMEOUT,
                Log
            );
            await page.WaitForTimeoutAsync(SELECT_RENDER_WAIT_TIME);
            Log($"select class - {classOk}");


            // 3) 닉네임 검색
            var nicknameFillResult = await TryFill(page, "input[name='search']", nickname, SELECT_TIMEOUT);
            var nicknameClickResult = await TryClick(page, "button[data-searchtype='search']", SELECT_TIMEOUT);
            await page.WaitForTimeoutAsync(SELECT_RENDER_WAIT_TIME); // 부분 렌더링 안정 대기
            Log($"send nickname - {nicknameFillResult}, {nicknameClickResult}");


            // 4) 결과 파싱
            var allText = await page.EvaluateAsync<string>("() => document.documentElement.innerText || ''");
            var normAll = Regex.Replace(allText ?? "", @"\\s+", " ").Trim(); // <- 실수 방지! 아래서 즉시 올바른 버전으로 다시 계산
            normAll = Regex.Replace(allText ?? "", @"\s+", " ").Trim();

            var idx = normAll.IndexOf(nickname, StringComparison.Ordinal);
            if (idx < 0)
            {
                Log("Nickname not present after search.");
                await page.CloseAsync();
                m_IsRunning = false;
                return null;
            }

            // 후보 블록 텍스트(닉네임+전투력 라벨 포함) 우선 추출
            var block = await ExtractRecordBlockAsync(page, nickname, keyword);
            var targetText = string.IsNullOrWhiteSpace(block)
                ? SliceAround(normAll, nickname, 500, 800) // 최후 폴백
                : block;

            var rankMatch = Regex.Match(targetText, @"([\d,]+)\s*위");
            var powerMatch = Regex.Match(targetText, @$"{keyword}\s*([\d,]+)");
            var serverMatch = Regex.Match(targetText, @"서버명\s*([^\s]+)");
            var classMatch = Regex.Match(targetText, @"클래스\s*([^\s]+)");
            if (rankMatch.Success && powerMatch.Success)
            {
                int rank = int.Parse(rankMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                int power = int.Parse(powerMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                string serverName = serverMatch.Success ? serverMatch.Groups[1].Value : server.ToString();
                string classSel = classMatch.Success ? classMatch.Groups[1].Value : (classId == 0 ? "전체 클래스" : (className ?? "-"));
                await page.CloseAsync();
                m_IsRunning = false;
                return new MobiRankResult(rank, power, serverName, classSel);
            }

            Log("Parse failed. (Consider saving a screenshot for debugging.)");
            await page.CloseAsync();
            m_IsRunning = false;
            return null;
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

    public static bool IsFullRunning(int rankingIndex)
    {
        lock (m_BrowserLock)
        {
            if (m_BrowserQueues.ContainsKey(rankingIndex) == false)
                return false;

            if (m_BrowserQueues[rankingIndex].Count < BROWSER_COUNT)
                return false;

            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (m_BrowserQueues[rankingIndex][i].isRunning == false)
                    return false;
            }
        }

        return true;
    }

    public static async Task<MobiRankResult?> GetRankBySearchAsync(
        int rankingIndex,
        string nickname,
        MobiServer server,
        string? className = null,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        BrowserContainer browserContainer = null;
        lock (m_BrowserLock)
        {
            if (m_BrowserQueues.ContainsKey(rankingIndex) == false)
                m_BrowserQueues.Add(rankingIndex, new());

            var list = m_BrowserQueues[rankingIndex];
            for (var i = 0; i < BROWSER_COUNT; i ++)
            {
                if (i >= list.Count)
                    list.Add(new() { rankingIndex = rankingIndex, index = i });

                if (list[i].isRunning) continue;

                browserContainer = list[i];
                break;
            }
        }

        if (browserContainer != null)
            return await browserContainer.Run(nickname, server, className, ct, log);
        return null;
    }

    // ---------------- helpers ----------------
    private static async Task<bool> TryClick(IPage page, string selector, int timeoutMs = 1000)
    {
        try
        {
            await page.Locator(selector).First.ClickAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch { return false; }
    }
    private static async Task<bool> TryFill(IPage page, string selector, string text, int timeoutMs = 1000)
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

    private static async Task<string> ExtractRecordBlockAsync(IPage page, string nickname, string keyword)
    {
        // 닉네임/라벨(서버명/캐릭터명/클래스/전투력)을 모두 포함하는 후보를 좁혀감
        var nicknameLoc = page.Locator($":text('{nickname}')");

        var candidates = page.Locator("li, div, article, section, tr")
            .Filter(new() { Has = nicknameLoc }) // 닉네임 포함
            .Filter(new() { HasTextString = "서버명" })
            .Filter(new() { HasTextString = "캐릭터명" })
            .Filter(new() { HasTextString = "클래스" })
            .Filter(new() { HasTextString = keyword });

        var count = await candidates.CountAsync();

        if (count == 0)
        {
            return "";
        }

        // 여러 개면 텍스트 길이가 가장 짧은(=개별 항목일 확률이 높은) 것을 선택
        string? best = null;
        for (int i = 0; i < count; i ++)
        {
            var t = await candidates.Nth(i).InnerTextAsync() ?? "";
            var norm = Regex.Replace(t, @"\s+", " ").Trim();

            // 너무 큰 컨테이너는 배제되도록 길이 기준 사용
            if (best == null || norm.Length < best.Length)
                best = norm;
        }

        return best!;
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

    // select_box를 "서버/클래스" 타입으로 안정적으로 찾기
    private static ILocator FindSelectBox(IPage page, string type)
    {
        // 1순위: 내부에 해당 타입의 li가 실제로 존재하는 박스 매칭
        var byType = type switch
        {
            "server" => page.Locator("div.select_box").Filter(new() { Has = page.Locator("li[data-searchtype='serverid']") }),
            "class" => page.Locator("div.select_box").Filter(new() { Has = page.Locator("li[data-searchtype='classid']") }),
            _ => page.Locator("div.select_box")
        };

        return byType.CountAsync().GetAwaiter().GetResult() > 0
            ? byType.First
            : // 2순위: 위치 휴리스틱(페이지 구조가 고정이라면)
            (type == "server"
                ? page.Locator("div.select_box").Nth(0)
                : page.Locator("div.select_box").Nth(1));
    }

    // 공용 드롭다운 선택 루틴
    private static async Task<bool> SelectFromDropdownAsync(
        IPage page,
        ILocator selectBox, // div.select_box
        string optionCss, // 예: "li[data-searchtype='serverid'][data-serverid='7']"
        string? expectSelectedText, // 선택 후 .selected에 포함될(기대) 텍스트. 검증 생략하려면 null
        int timeoutMs,
        Action<string> log)
    {
        // 1) 드롭다운 펼치기
        await selectBox.Locator(".selected").ClickAsync(new() { Timeout = timeoutMs });

        // 2) 옵션 목록 노출 대기(최소 하나의 항목이 보이는지)
        var option = page.Locator(optionCss).First;
        await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs });

        // 3) 옵션 클릭
        await option.ClickAsync(new() { Timeout = timeoutMs });

        // 4) 렌더링 안정화 약간
        await page.WaitForTimeoutAsync(150);

        // 5) 선택 검증
        // (A) 기대 텍스트가 있으면 .selected에 포함되는지 확인
        if (!string.IsNullOrWhiteSpace(expectSelectedText))
        {
            var selText = (await selectBox.Locator(".selected").InnerTextAsync()).Trim();
            var ok = selText.Contains(expectSelectedText!, StringComparison.OrdinalIgnoreCase);
            log($"select verify (.selected contains): '{selText}' vs '{expectSelectedText}' -> {ok}");
            if (ok) return true;
        }

        // (B) 아니면 해당 li가 data-selected="true" 또는 class="on"인지로 확인
        try
        {
            var selectedAttr = await option.GetAttributeAsync("data-selected");
            var cls = await option.GetAttributeAsync("class");
            var ok = string.Equals(selectedAttr, "true", StringComparison.OrdinalIgnoreCase)
                     || (cls?.Split(' ').Contains("on") ?? false);
            log($"select verify (attr/class): data-selected={selectedAttr}, class={cls} -> {ok}");
            return ok;
        }
        catch
        {
            // 최악의 경우 한 번 더 드롭다운을 열어 현재 표시 텍스트로 재검증
            if (!string.IsNullOrWhiteSpace(expectSelectedText))
            {
                await selectBox.Locator(".selected").ClickAsync(new() { Timeout = 1000 });
                var selText = (await selectBox.Locator(".selected").InnerTextAsync()).Trim();
                var ok = selText.Contains(expectSelectedText!, StringComparison.OrdinalIgnoreCase);
                log($"select re-verify: '{selText}' vs '{expectSelectedText}' -> {ok}");
                return ok;
            }
            return false;
        }
    }

    private static async Task<bool> SelectByDataAsync(
        IPage page,
        string dataType, // "serverid" 또는 "classid"
        string value, // 예: "7" (칼릭스) / "0" (전체 클래스)
        string? expectSelectedText, // 선택 후 .selected에 기대하는 텍스트(검증용). 모르면 null
        int timeoutMs,
        Action<string> log)
    {
        // 페이지에 select_box가 최소 2개 나타날 때까지 대기
        var boxes = page.Locator("div.select_box");
        await boxes.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Math.Max(timeoutMs, 4000) });

        var boxCount = await boxes.CountAsync();
        if (boxCount == 0)
        {
            log("no select_box found");
            return false;
        }

        // 최대 2개만 검사 (서버/클래스)
        var indices = new int[] { 0, 1 }.Where(i => i < boxCount);

        foreach (var i in indices)
        {
            var box = boxes.Nth(i);

            // 1) 드롭다운 펼치기 (광고/서드파티 네비게이션에 영향 안 받게 NoWaitAfter)
            await box.Locator(".selected").ClickAsync(new() { Timeout = timeoutMs, NoWaitAfter = true });

            // 2) 올바른 타입의 항목이 나타나는지 짧게 확인
            var pattern = $"li[data-searchtype='{dataType}']";
            var found = await page.Locator(pattern).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = 1200 }
            ).ContinueWith(t => t.Status == TaskStatus.RanToCompletion);

            if (!found)
            {
                // 다른 박스일 가능성 → 닫고 다음 후보로
                await page.Keyboard.PressAsync("Escape");
                continue;
            }

            // 3) 원하는 값 클릭
            var option = page.Locator($"li[data-searchtype='{dataType}'][data-{dataType}='{value}']").First;
            await option.ClickAsync(new() { Timeout = timeoutMs, NoWaitAfter = true });

            // 4) 약간의 안정화
            await page.WaitForTimeoutAsync(150);

            // 5) 선택 검증
            try
            {
                // (A) 기대 텍스트로 검증
                if (!string.IsNullOrWhiteSpace(expectSelectedText))
                {
                    var selText = (await box.Locator(".selected").InnerTextAsync()).Trim();
                    var ok = selText.Contains(expectSelectedText!, StringComparison.OrdinalIgnoreCase);
                    log($"verify text: '{selText}' ?~ '{expectSelectedText}' => {ok}");
                    if (ok) return true;
                }

                // (B) li의 상태로 검증
                var selectedAttr = await option.GetAttributeAsync("data-selected");
                var cls = await option.GetAttributeAsync("class");
                var ok2 = string.Equals(selectedAttr, "true", StringComparison.OrdinalIgnoreCase)
                          || (cls?.Split(' ').Contains("on") ?? false);
                log($"verify attr/class: data-selected={selectedAttr}, class={cls} => {ok2}");
                return ok2;
            }
            finally
            {
                // 혹시 열려 있으면 닫기 (다음 동작에 간섭 방지)
                await page.Keyboard.PressAsync("Escape");
            }
        }

        log("no matching select_box responded with requested dataType.");
        return false;
    }

}
