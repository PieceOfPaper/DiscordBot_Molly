using System.Globalization;
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
                case 4:
                    keyword = "점수";
                    break;
            }

            if (string.IsNullOrWhiteSpace(nickname)) throw new ArgumentException("nickname is required");
            if (nickname.Length > 12) nickname = nickname[..12]; // maxlength=12

            Log($"start search(nickname='{nickname}', server={server}, class='{className ?? "전체 클래스"}')");

            IPage? page = null;
            try
            {
                page = await m_BrowserContext.NewPageAsync();
                Log("NewPageAsync success");
                await page.RouteAsync("**/*.{png,jpg,jpeg,gif,webp,mp4,mp3,woff,woff2,ttf}", r => r.AbortAsync());
                Log("page.RouteAsync success");
                var rankingUrl = $"https://mabinogimobile.nexon.com/Ranking/List?t={rankingIndex}";
                var navigationResponse = await page.GotoAsync(rankingUrl, s_PageGotoOpt);
                Log($"랭킹 페이지 초기 응답 - HTTP 상태: {navigationResponse?.Status.ToString() ?? "응답 없음"}");

                if (navigationResponse?.Status == 403)
                {
                    Log("랭킹 페이지 보안 검사 감지 - 공식 홈페이지 선행 방문 후 재시도");
                    var homeResponse = await page.GotoAsync(
                        "https://mabinogimobile.nexon.com/",
                        s_PageGotoOpt);
                    Log($"공식 홈페이지 초기 응답 - HTTP 상태: {homeResponse?.Status.ToString() ?? "응답 없음"}");
                    await Task.Delay(POLL_INTERVAL, ct);

                    navigationResponse = await page.GotoAsync(rankingUrl, s_PageGotoOpt);
                    Log($"랭킹 페이지 재시도 응답 - HTTP 상태: {navigationResponse?.Status.ToString() ?? "응답 없음"}");
                }

                await WaitForRankingControlsAsync(page, PAGE_READY_TIMEOUT, ct, Log);
    
    
                // 서버 선택
                var serverId = (int)server;          // 예: 칼릭스=7, 몰리=8
                var serverOk = await SelectByDataAsync(
                    page, "serverid", serverId.ToString(), server.ToString(), CONTROL_READY_TIMEOUT, ct, Log);
                Log($"select server - {serverOk}");
                if (!serverOk)
                    throw new TimeoutException($"서버 '{server}' 선택 준비 시간이 초과되었습니다.");
    
                
                // -------------------- 클래스 선택 --------------------
                long classId = 0;
                if (!string.IsNullOrWhiteSpace(className) && CLASSNAME_TO_ID.TryGetValue(className.Trim(), out var cid))
                    classId = cid;
    
                var classDisplay = classId == 0 ? "전체 클래스" : className?.Trim();
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
                await page.CloseAsync();
                m_IsRunning = false;
                return result;
            }
            catch (Exception ex)
            {
                Log($"랭킹 페이지 처리 중 예외: {ex.GetType().Name}: {ex.Message}");
                await LogPageOnExceptionAsync(page, Log);
                throw;
            }
            finally
            {
                m_IsRunning = false;

                if (page is not null && !page.IsClosed)
                {
                    try
                    {
                        await page.CloseAsync();
                    }
                    catch (Exception closeException)
                    {
                        Log($"랭킹 페이지 닫기 실패: {closeException.GetType().Name}: {closeException.Message}");
                    }
                }
            }
        }

        private static async Task LogPageOnExceptionAsync(IPage? page, Action<string> log)
        {
            if (page is null)
            {
                log("예외 발생 시 랭킹 페이지가 생성되지 않아 페이지 내용을 확인할 수 없습니다.");
                return;
            }

            try
            {
                var title = await page.TitleAsync();
                var pageText = await page.EvaluateAsync<string>(
                    "() => document.documentElement.innerText || ''");

                log($"예외 발생 페이지 URL: {page.Url}");
                log($"예외 발생 페이지 제목: {title}");
                log($"예외 발생 페이지 텍스트 시작\n{pageText}\n예외 발생 페이지 텍스트 끝");
            }
            catch (Exception logException)
            {
                log($"예외 발생 페이지 내용 출력 실패: {logException.GetType().Name}: {logException.Message}");
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
        BrowserContainer? browserContainer = null;
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

    private static async Task<string> ExtractRecordBlockAsync(IPage page, string nickname, string keyword)
    {
        // 검색 결과가 재렌더링되는 도중 Locator 개수와 실제 노드가 달라지는 경쟁 조건을 피하기 위해
        // 한 번의 브라우저 DOM 평가 안에서 후보 탐색과 텍스트 추출을 끝냅니다.
        return await page.EvaluateAsync<string>(
            @"args => {
                const normalize = value => (value || """").replace(/\s+/g, "" "").trim();
                const expectedCharacter = `캐릭터명 ${args.nickname}`;

                const candidates = Array.from(
                    document.querySelectorAll(""li, div, article, section, tr""))
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
            new { nickname, keyword });
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

    private static async Task<(int? Total, int? Combat, int? Charm, int? Life)> ExtractOverallScoresAsync(IPage page, string nickname)
    {
        try
        {
            var item = page.Locator("li.item").Filter(new() { Has = page.Locator($"dd[data-charactername='{nickname}']") });
            if (await item.CountAsync() == 0)
            {
                item = page.Locator("li.item").Filter(new() { Has = page.Locator($"dd:has-text('{nickname}')") });
            }

            if (await item.CountAsync() == 0)
                return (null, null, null, null);

            var root = item.First;

            var dt = root.Locator("dt").Filter(new() { HasTextString = "종합" });
            if (await dt.CountAsync() == 0)
                return (null, null, null, null);

            var dtText = await dt.First.InnerTextAsync();
            var total = ExtractKoreanNumber(dtText);

            var dl = dt.First.Locator("xpath=..");
            var dd = dl.Locator("dd").First;

            int? combat = null;
            int? charm = null;
            int? life = null;

            try { combat = ExtractKoreanNumber(await dd.Locator("span.type_1").InnerTextAsync()); } catch { }
            try { charm = ExtractKoreanNumber(await dd.Locator("span.type_3").InnerTextAsync()); } catch { }
            try { life = ExtractKoreanNumber(await dd.Locator("span.type_2").InnerTextAsync()); } catch { }

            return (total, combat, charm, life);
        }
        catch
        {
            return (null, null, null, null);
        }
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

            var boxCount = await page.Locator("div.select_box").CountAsync();
            var searchCount = await page.Locator("input[name='search']").CountAsync();
            if (boxCount >= 2 && searchCount > 0)
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
        var boxIndex = dataType == "serverid" ? 0 : 1;
        var optionSelector = $"li[data-searchtype='{dataType}'][data-{dataType}='{value}']";

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var selectedText = await page.EvaluateAsync<string?>(
                    @"args => {
                        const boxes = Array.from(document.querySelectorAll(""div.select_box""));
                        const box = boxes[args.boxIndex];
                        const selected = box?.querySelector("".selected"");
                        if (!(selected instanceof HTMLElement))
                            return null;

                        const currentText = (selected.textContent || """").trim();
                        if (args.expectSelectedText
                            && currentText.includes(args.expectSelectedText))
                            return currentText;

                        selected.click();

                        const option = document.querySelector(args.optionSelector);
                        if (!(option instanceof HTMLElement))
                            return currentText;

                        option.click();
                        return (selected.textContent || """").trim();
                    }",
                    new { boxIndex, optionSelector, expectSelectedText });

                if (!string.IsNullOrWhiteSpace(selectedText)
                    && (string.IsNullOrWhiteSpace(expectSelectedText)
                        || selectedText.Contains(expectSelectedText, StringComparison.OrdinalIgnoreCase)))
                {
                    log($"선택 완료: '{selectedText}'");
                    return true;
                }
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
        string keyword)
    {
        var block = await ExtractRecordBlockAsync(page, nickname, keyword);
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

        if (rankingIndex != 4)
            return (new MobiRankResult(rank, power, serverName, className), "완료");

        var overall = await ExtractOverallScoresAsync(page, nickname);
        missingFields.Clear();
        if (overall.Total is null) missingFields.Add("종합 점수");
        if (overall.Combat is null) missingFields.Add("전투력");
        if (overall.Charm is null) missingFields.Add("매력");
        if (overall.Life is null) missingFields.Add("생활력");
        if (missingFields.Count > 0)
            return (null, $"종합 랭킹 필드 부족: {string.Join(", ", missingFields)}");

        return (new MobiRankResult(
            rank,
            overall.Total!.Value,
            serverName,
            className,
            overall.Total,
            overall.Combat,
            overall.Charm,
            overall.Life), "완료");
    }


}
