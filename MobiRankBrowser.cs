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
    private const int SEARCH_RESULT_TIMEOUT = 20000;
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
    
    
                // 3) 닉네임 검색
                var nicknameFillResult = await TryFill(page, "input[name='search']", nickname, SINGLE_ACTION_TIMEOUT);
                var nicknameClickResult = await TryClick(page, "button[data-searchtype='search']", SINGLE_ACTION_TIMEOUT);
                Log($"send nickname - {nicknameFillResult}, {nicknameClickResult}");
                if (!nicknameFillResult || !nicknameClickResult)
                    throw new TimeoutException("캐릭터 검색 입력 또는 실행 준비 시간이 초과되었습니다.");

                // 4) 필요한 값이 모두 채워진 동일 결과를 두 번 연속 확인하면 반환
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
        var optionSelector = $"li[data-searchtype='{dataType}'][data-{dataType}='{value}']";

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var boxes = page.Locator("div.select_box");
            var boxCount = Math.Min(await boxes.CountAsync(), 2);
            for (var i = 0; i < boxCount; i++)
            {
                var box = boxes.Nth(i);
                try
                {
                    var selected = box.Locator(".selected").First;
                    if (await selected.CountAsync() == 0)
                        continue;

                    var selectedText = (await selected.EvaluateAsync<string>(
                        "element => (element.textContent || '').trim()")).Trim();
                    if (!string.IsNullOrWhiteSpace(expectSelectedText)
                        && selectedText.Contains(expectSelectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        log($"선택값 확인: '{selectedText}'");
                        return true;
                    }

                    // 동적 렌더링 중에는 Playwright의 화면 클릭 가능 판정이 오래 걸릴 수 있습니다.
                    // DOM에 연결된 최신 요소를 매 폴링마다 다시 찾아 직접 클릭합니다.
                    await selected.EvaluateAsync("element => element.click()");

                    var option = box.Locator(optionSelector).First;
                    if (await option.CountAsync() == 0)
                        continue;

                    await option.EvaluateAsync("element => element.click()");
                    await Task.Delay(250, ct);

                    selectedText = (await selected.EvaluateAsync<string>(
                        "element => (element.textContent || '').trim()")).Trim();
                    if (string.IsNullOrWhiteSpace(expectSelectedText)
                        || selectedText.Contains(expectSelectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        log($"선택 완료: '{selectedText}'");
                        return true;
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // 동적 재렌더링으로 기존 요소가 교체될 수 있으므로 다음 폴링에서 다시 찾습니다.
                }
                finally
                {
                    try { await page.Keyboard.PressAsync("Escape"); } catch { }
                }
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        return false;
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
        MobiRankResult? previous = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var current = await TryParseCompleteRankResultAsync(
                page, rankingIndex, nickname, requestedServer, requestedClass, keyword);
            if (current is not null)
            {
                if (current == previous)
                {
                    log($"완성된 랭킹 결과 확인: {current.Rank}위, {current.ClassName}");
                    return current;
                }

                previous = current;
                log("랭킹 결과 필수 필드 확인 완료, 안정성 재확인 중");
            }
            else
            {
                previous = null;
            }

            await Task.Delay(POLL_INTERVAL, ct);
        }

        log($"'{nickname}'의 완성된 랭킹 결과가 {timeoutMs}ms 안에 나타나지 않았습니다.");
        return null;
    }

    private static async Task<MobiRankResult?> TryParseCompleteRankResultAsync(
        IPage page,
        int rankingIndex,
        string nickname,
        MobiServer requestedServer,
        string? requestedClass,
        string keyword)
    {
        var block = await ExtractRecordBlockAsync(page, nickname, keyword);
        if (string.IsNullOrWhiteSpace(block))
            return null;

        var rankMatch = Regex.Match(block, @"([\d,]+)\s*위");
        var powerMatch = Regex.Match(block, @$"{keyword}\s*([\d,]+)");
        var serverMatch = Regex.Match(block, @"서버명\s*([^\s]+)");
        var classMatch = Regex.Match(block, @"클래스\s*([^\s]+)");
        if (!rankMatch.Success || !powerMatch.Success || !serverMatch.Success || !classMatch.Success)
            return null;

        var rank = int.Parse(rankMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var power = int.Parse(powerMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var serverName = serverMatch.Groups[1].Value;
        var className = classMatch.Groups[1].Value;

        if (rankingIndex != 4)
            return new MobiRankResult(rank, power, serverName, className);

        var overall = await ExtractOverallScoresAsync(page, nickname);
        if (overall.Total is null || overall.Combat is null || overall.Charm is null || overall.Life is null)
            return null;

        return new MobiRankResult(
            rank,
            overall.Total.Value,
            serverName,
            className,
            overall.Total,
            overall.Combat,
            overall.Charm,
            overall.Life);
    }


}
