using System.Net;
using Discord;
using Microsoft.Extensions.Configuration;
using Molly.MobiLife;

internal static class MobiLifeTests
{
    private const string Key = "test-secret-key";
    private const string CategoriesJson = """{"data":[{"parent_category":"무기","item_count":12},{"parent_category":"재료","item_count":340}]}""";

    public static async Task RunAsync()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        var logs = new List<string>();
        MobiLifeApiClient Create(StubHandler handler, MobiLifeOptions? options = null) =>
            new(options ?? new MobiLifeOptions { ApiKey = Key }, handler, () => now, msg => { lock (logs) logs.Add(msg); });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MobiLife:ApiKey"] = " dev-key ", ["MobiLife:Enabled"] = "false", ["MobiLife:BaseUrl"] = "https://example.com/v2",
        }).Build();
        var parsed = MobiLifeOptions.FromConfiguration(config);
        Assert(parsed is { ApiKey: "dev-key", Enabled: false } && parsed.BaseUrl.AbsoluteUri == "https://example.com/v2/",
            "모비라이프 설정을 Discord 토큰처럼 구성에서 읽음(MobiLife:ApiKey·Enabled·BaseUrl)");
        var defaults = MobiLifeOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert(!defaults.HasApiKey && defaults.Enabled && defaults.BaseUrl.AbsoluteUri == MobiLifeOptions.DefaultBaseUrl, "미설정 시 키 없음·기본 주소");
        var insecure = false;
        try { MobiLifeOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MobiLife:BaseUrl"] = "http://x.com" }).Build()); }
        catch (ArgumentException) { insecure = true; }
        Assert(insecure, "https가 아닌 BaseUrl 거부");

        var none = new StubHandler();
        var notConfigured = await Create(none, new MobiLifeOptions()).GetAsync<MobiLifeCategoriesResponse>("market/categories");
        var disabled = await Create(none, new MobiLifeOptions { ApiKey = Key, Enabled = false }).GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(notConfigured.Status == MobiLifeStatus.NotConfigured && disabled.Status == MobiLifeStatus.Disabled && none.Requests == 0 &&
               notConfigured.UserMessage.Contains("꺼져"), "키 미설정·비활성화 시 요청하지 않고 안내만 반환");

        var ok = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, CategoriesJson) };
        var client = Create(ok);
        var categories = await client.GetAsync<MobiLifeCategoriesResponse>("/market/prices",
            [new("search", "무기 & 방어구"), new("limit", "5"), new("parent_category", null)]);
        Assert(ok.LastRequest!.Headers.Authorization?.ToString() == "Bearer " + Key, "Bearer 인증 헤더 전송");
        Assert(ok.LastRequest.RequestUri!.AbsoluteUri == "https://open.mabimobi.life/v1/market/prices?search=%EB%AC%B4%EA%B8%B0%20%26%20%EB%B0%A9%EC%96%B4%EA%B5%AC&limit=5",
            "기본 주소 기준 경로와 쿼리 인코딩, null 쿼리 생략");
        Assert(categories is { IsSuccess: true, Value.Data.Count: 2 } && categories.Value.Data[1] == new MobiLifeCategory("재료", 340),
            "snake_case 응답을 DTO로 변환");

        // 로컬 한도: 서버 한도에 닿기 전에 네트워크 호출 없이 막습니다.
        var limited = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, CategoriesJson) };
        var limitedClient = Create(limited, new MobiLifeOptions { ApiKey = Key, PerMinuteLimit = 2, PerDayLimit = 3 });
        await limitedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        await limitedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        var third = await limitedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(third is { Status: MobiLifeStatus.RateLimited, RetryAfter: not null } && limited.Requests == 2, "로컬 분당 한도 초과 시 요청하지 않음");
        now = now.AddMinutes(1);
        var afterMinute = await limitedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        var dayLimited = await limitedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(afterMinute.IsSuccess && dayLimited.Status == MobiLifeStatus.RateLimited && limited.Requests == 3, "1분 뒤 재개, 일일 한도는 별도로 적용");

        // 서버 429는 Retry-After만큼 쉽니다.
        var throttled = new StubHandler { Respond = _ => { var r = Json(HttpStatusCode.TooManyRequests, """{"code":429,"message":"slow down"}"""); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(90)); return r; } };
        var throttledClient = Create(throttled);
        var first429 = await throttledClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        now = now.AddSeconds(60);
        var during429 = await throttledClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(first429 is { Status: MobiLifeStatus.RateLimited, RetryAfter.TotalSeconds: 90 } && during429.Status == MobiLifeStatus.RateLimited &&
               throttled.Requests == 1 && first429.UserMessage.Contains("2분"), "429 응답 시 Retry-After 동안 호출 중단");
        throttled.Respond = _ => Json(HttpStatusCode.OK, CategoriesJson);
        now = now.AddSeconds(31);
        Assert((await throttledClient.GetAsync<MobiLifeCategoriesResponse>("market/categories")).IsSuccess && throttled.Requests == 2, "Retry-After 이후 재개");

        // 401은 키 폐기로 보고 재시작 전까지 다시 부르지 않습니다.
        var revoked = new StubHandler { Respond = _ => Json(HttpStatusCode.Unauthorized, """{"code": 401, "message": "Invalid or missing API key."}""") };
        var revokedClient = Create(revoked);
        var unauthorized = await revokedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        now = now.AddDays(2);
        var stillRevoked = await revokedClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(unauthorized.Status == MobiLifeStatus.Unauthorized && stillRevoked.Status == MobiLifeStatus.Unauthorized && revoked.Requests == 1 &&
               !revokedClient.IsAvailable && logs.Any(x => x.Contains("Invalid or missing API key")), "401 응답 시 키 폐기로 보고 호출 중단");

        // 404/410은 API 변경·종료 가능성으로 보고 길게 쉽니다.
        var gone = new StubHandler { Respond = _ => Json(HttpStatusCode.Gone, "") };
        var goneClient = Create(gone);
        var goneResult = await goneClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        now = now.AddMinutes(59);
        await goneClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(goneResult.Status == MobiLifeStatus.Unavailable && gone.Requests == 1 && !goneClient.IsAvailable, "404/410 응답 시 1시간 호출 중단");
        now = now.AddMinutes(1);
        Assert(goneClient.IsAvailable, "중단 시간이 지나면 다시 호출 가능");

        // 5xx·통신 오류·형식 오류가 연속되면 잠시 차단합니다.
        var failing = new StubHandler { Respond = _ => Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>") };
        var failingClient = Create(failing);
        var r1 = await failingClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        failing.Respond = _ => throw new HttpRequestException("connection refused");
        var r2 = await failingClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        failing.Respond = _ => Json(HttpStatusCode.OK, "not json");
        var r3 = await failingClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        var r4 = await failingClient.GetAsync<MobiLifeCategoriesResponse>("market/categories");
        Assert(r1.Status == MobiLifeStatus.Unavailable && r2.Status == MobiLifeStatus.Unavailable && r3.Status == MobiLifeStatus.InvalidResponse &&
               r4.Status == MobiLifeStatus.Unavailable && failing.Requests == 3, "연속 3회 실패 시 10분 동안 호출 중단");
        now = now.AddMinutes(10);
        failing.Respond = _ => Json(HttpStatusCode.OK, CategoriesJson);
        Assert((await failingClient.GetAsync<MobiLifeCategoriesResponse>("market/categories")).IsSuccess, "차단 해제 후 정상 응답으로 복구");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = false;
        try { await Create(ok).GetAsync<MobiLifeCategoriesResponse>("market/categories", ct: cts.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "호출자 취소는 실패로 삼키지 않고 전달");

        Assert(logs.All(x => !x.Contains(Key)) && !Create(ok).DescribeState().Contains(Key), "로그와 상태 설명에 API 키를 남기지 않음");

        var footer = new EmbedBuilder().WithFooter("몰리").WithMobiLifeAttribution().WithMobiLifeAttribution().Footer.Text;
        Assert(footer == "몰리 • " + MobiLifeAttribution.FooterText && footer.Contains("모비라이프 제공"), "Embed 푸터에 '모비라이프 제공' 출처를 한 번만 표기");
        Assert(MobiLifeAttribution.AppendTo("시세 ").EndsWith("모비라이프 제공 · mabimobi.life"), "텍스트 메시지에 출처 표기");
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => throw new InvalidOperationException("요청이 없어야 합니다.");
        public int Requests { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
