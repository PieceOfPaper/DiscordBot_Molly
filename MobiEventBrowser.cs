using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

public record MobiEventResult(
    string eventName, string url, DateTime start, DateTime end
);

public static class MobiEventBrowser
{
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

            //Init!
            if (m_IsInited == false) await Init(ct, log);

            Log($"start search");

            var page = await m_BrowserContext.NewPageAsync();
            Log("NewPageAsync success");
            await page.RouteAsync("**/*.{png,jpg,jpeg,gif,webp,mp4,mp3,woff,woff2,ttf}", r => r.AbortAsync());
            Log("page.RouteAsync success");
            await page.GotoAsync($"TODO:주소입력", s_PageGotoOpt);
            Log("page.GotoAsync success");

            List<MobiEventResult> result = new();
            
            //TODO - 페이지에서 가져온 결과물을 파싱해서 result에 저장한다.
            
            await page.CloseAsync();
            m_IsRunning = false;
            return result;
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
}
