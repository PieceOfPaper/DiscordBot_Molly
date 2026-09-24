using System.Text.Json;
using Microsoft.Playwright;

/// <summary>
/// HTTP 수집이 막혔을 때만 쓰는 Playwright 대체 경로. 1페이지로 한 번 이동해 사이트의 보안 검사와
/// 쿠키를 통과한 뒤, 모든 페이지는 페이지 컨텍스트 안에서 같은 출처 fetch로 원본 HTML을 받습니다.
/// 결과 HTML은 HTTP 경로와 같은 MobiEventParser로 해석합니다.
/// 대체 수집 한 번마다 새로 만들고 끝나면 Dispose해서 Chromium을 상주시키지 않습니다.
/// </summary>
public sealed class MobiEventBrowserSource : IMobiEventPageSource, IAsyncDisposable
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
    private static readonly byte[] s_TransparentPng1x1 =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");

    private sealed record FetchResult(bool Ok, string? Html, string? Reason);

    private IPlaywright? m_Pw;
    private IBrowser? m_Browser;
    private IPage? m_Page;

    public string Name => "Playwright";

    public async Task<string> FetchPageAsync(string url, CancellationToken ct)
    {
        var page = m_Page ?? await OpenAsync(url, ct);
        ct.ThrowIfCancellationRequested();

        var json = await page.EvaluateAsync<string>(
            @"async (url) => {
                let response;
                try {
                    response = await fetch(url, { credentials: ""same-origin"", cache: ""no-cache"" });
                } catch (e) {
                    return JSON.stringify({ Ok: false, Reason: ""fetch 예외: "" + (e?.message || String(e)) });
                }
                if (!response.ok)
                    return JSON.stringify({ Ok: false, Reason: ""HTTP "" + response.status });
                return JSON.stringify({ Ok: true, Html: await response.text() });
            }",
            url);

        var result = JsonSerializer.Deserialize<FetchResult>(json);
        if (result is not { Ok: true, Html: not null })
            throw new MobiEventFetchException($"브라우저 fetch 실패: {result?.Reason ?? "알 수 없음"} ({url})");
        return result.Html;
    }

    private async Task<IPage> OpenAsync(string url, CancellationToken ct)
    {
        m_Pw = await Playwright.CreateAsync();
        m_Browser = await m_Pw.Chromium.LaunchAsync(s_BrowserTypeLaunchOpt);
        var context = await m_Browser.NewContextAsync(s_BrowserNewContextOpt);
        await context.RouteAsync("**/*", async route =>
        {
            var t = route.Request.ResourceType;
            if (t is "image")
                await route.FulfillAsync(new() { Status = 200, BodyBytes = s_TransparentPng1x1, ContentType = "image/png" });
            else if (t is "media" or "font")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });
        context.SetDefaultTimeout(5000);
        context.SetDefaultNavigationTimeout(30000);

        ct.ThrowIfCancellationRequested();
        var page = await context.NewPageAsync();
        await page.GotoAsync(url, s_PageGotoOpt);
        // 보안 검사 페이지를 거치는 경우 실제 목록이 나타날 때까지 기다립니다.
        await page.WaitForSelectorAsync("[data-threadid], [data-pagingtype='thread']", new() { Timeout = 20000 });
        m_Page = page;
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (m_Browser != null)
            await m_Browser.DisposeAsync();
        m_Pw?.Dispose();
        m_Browser = null;
        m_Pw = null;
        m_Page = null;
    }
}
