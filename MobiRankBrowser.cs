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
    private static readonly Dictionary<string, long> ClassNameToId = new(StringComparer.Ordinal)
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

    public static async Task<MobiRankResult?> GetRankBySearchAsync(
        int rankingIndex,
        string nickname,
        MobiServer? server = null,
        string? className = null,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        void Log(string msg) => (log ?? Console.WriteLine).Invoke($"[MabiRankBrowser] {msg}");

        var keyword = "전투력";
        switch (rankingIndex)
        {
            case 1: keyword = "전투력"; break;
            case 2: keyword = "매력"; break;
            case 3: keyword = "생활력"; break;
        }

        if (string.IsNullOrWhiteSpace(nickname)) throw new ArgumentException("nickname is required");
        if (nickname.Length > 12) nickname = nickname[..12]; // maxlength=12

        Log($"Start Search(nickname='{nickname}', server={(server?.ToString() ?? "null")}, class='{className ?? "전체 클래스"}')");

        var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });

        var context = await browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            Locale = "ko-KR",
            TimezoneId = "Asia/Seoul"
        });
        context.SetDefaultTimeout(5000);                // 일반 동작(클릭/채우기)은 5초
        context.SetDefaultNavigationTimeout(30000);     // 네비게이션은 30초로 별도 설정

        var page = await context.NewPageAsync();
        await page.GotoAsync($"https://mabinogimobile.nexon.com/Ranking/List?t={rankingIndex}",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

        // 팝업/쿠키 동의(있을 때만)
        await TryClick(page, "button:has-text('동의')", 800);
        await TryClick(page, "button:has-text('확인')", 800);

        // 1) 서버 선택 (옵션)
        if (server is not null)
        {
            // 서버 드롭다운(현재 선택 텍스트가 서버명인 select_box)을 열고 서버 li 클릭
            // 보통 서버/클래스 2개의 select_box가 있는데, 서버는 '칼릭스/데이안...'처럼 서버명이 보입니다.
            await TryClick(page, "div.select_box .selected", 1500); // 첫 번째 박스가 서버일 확률이 높음
            var ok = await TryClick(page, $"li[data-searchtype='serverid'][data-serverid='{(int)server.Value}']", 2000);
            await page.WaitForTimeoutAsync(600);
        }

        // 2) 클래스 선택 (옵션, 기본 전체 클래스)
        var classId = 0L;
        if (!string.IsNullOrWhiteSpace(className) && ClassNameToId.TryGetValue(className.Trim(), out var cid))
            classId = cid;

        if (classId != 0)
        {
            // 클래스 드롭다운은 기본 텍스트가 '전체 클래스' — 그 상자만 명시적으로 찾자
            var classBox = page.Locator("div.select_box:has(.selected:has-text('전체 클래스'))").First;
            // 혹시 이미 바뀐 상태(= selected가 다른 클래스명)일 수도 있으니 fallback도 준비
            if (await classBox.CountAsync() == 0)
                classBox = page.Locator("div.select_box").Nth(1); // 2번째 select_box로 추정

            await SafeClick(classBox.Locator(".selected"), Log);
            var ok = await SafeClick(classBox.Locator($"li[data-searchtype='classid'][data-classid='{classId}']"), Log);
            await page.WaitForTimeoutAsync(600);
        }
        else
        {
            Log("Class: 전체 클래스(기본) 그대로 사용.");
        }

        // 3) 닉네임 검색
        await TryFill(page, "input[name='search']", nickname, 2000);
        await TryClick(page, "button[data-searchtype='search']", 2000);
        await page.WaitForTimeoutAsync(800); // 부분 렌더링 안정 대기

        // 4) 결과 파싱
        var allText = await page.EvaluateAsync<string>("() => document.documentElement.innerText || ''");
        var normAll = Regex.Replace(allText ?? "", @"\\s+", " ").Trim(); // <- 실수 방지! 아래서 즉시 올바른 버전으로 다시 계산
        normAll = Regex.Replace(allText ?? "", @"\s+", " ").Trim();

        var idx = normAll.IndexOf(nickname, StringComparison.Ordinal);
        if (idx < 0)
        {
            Log("Nickname not present after search.");
            return null;
        }

        // 후보 블록 텍스트(닉네임+전투력 라벨 포함) 우선 추출
        var block = await ExtractRecordBlockAsync(page, nickname, keyword);
        var targetText = string.IsNullOrWhiteSpace(block)
            ? SliceAround(normAll, nickname, 500, 800)   // 최후 폴백
            : block;
        
        var rankMatch  = Regex.Match(targetText, @"([\d,]+)\s*위");
        var powerMatch = Regex.Match(targetText, @$"{keyword}\s*([\d,]+)");
        var serverMatch = Regex.Match(targetText, @"서버명\s*([^\s]+)");
        var classMatch  = Regex.Match(targetText, @"클래스\s*([^\s]+)");
        if (rankMatch.Success && powerMatch.Success)
        {
            int rank = int.Parse(rankMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            int power = int.Parse(powerMatch.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            string serverName = serverMatch.Success ? serverMatch.Groups[1].Value : (server is not null ? server.Value.ToString() : "-");
            string classSel   = classMatch.Success ? classMatch.Groups[1].Value : (classId == 0 ? "전체 클래스" : (className ?? "-"));
            return new MobiRankResult(rank, power, serverName, classSel);
        }

        Log("Parse failed. (Consider saving a screenshot for debugging.)");
        return null;
    }

    // ---------------- helpers ----------------
    private static async Task<bool> TryClick(IPage page, string selector, int timeoutMs = 1000)
    {
        try { await page.Locator(selector).First.ClickAsync(new() { Timeout = timeoutMs }); return true; }
        catch { return false; }
    }
    private static async Task<bool> TryFill(IPage page, string selector, string text, int timeoutMs = 1000)
    {
        try { await page.Locator(selector).First.FillAsync(text, new() { Timeout = timeoutMs }); return true; }
        catch { return false; }
    }
    private static async Task<bool> SafeClick(ILocator locator, Action<string> log)
    {
        try { await locator.First.ClickAsync(); return true; }
        catch (Exception ex) { log($"Click failed: {ex.Message}"); return false; }
    }

    private static string SliceAround(string text, string key, int back, int fwd)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return text;
        int start = Math.Max(0, i - back);
        int end   = Math.Min(text.Length, i + fwd);
        return text[start..end];
    }

    private static async Task<string> ExtractRecordBlockAsync(IPage page, string nickname, string keyword)
    {
        // 닉네임/라벨(서버명/캐릭터명/클래스/전투력)을 모두 포함하는 후보를 좁혀감
        var nicknameLoc = page.Locator($":text('{nickname}')");

        var candidates = page.Locator("li, div, article, section, tr")
            .Filter(new() { Has = nicknameLoc })                  // 닉네임 포함
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
        for (int i = 0; i < count; i++)
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
}
