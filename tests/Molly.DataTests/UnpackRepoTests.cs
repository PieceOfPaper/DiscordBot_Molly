using System.Net;
using Microsoft.Extensions.Configuration;
using Molly.Unpack;

internal static class UnpackRepoTests
{
    private const string Token = "github_pat_test-secret";
    private const string Sha = "9b9fe9a0c1d2e3f405162738495a6b7c8d9e0f12";
    private const string VersionJson = """
        {"generatedAt":"2026-10-31T16:03:56+09:00","app":{"version":"3.4.40011","productName":"마비노기 모바일"},
         "build":{"branch":"KR3.4"},"patch":{"resource":"840cbf8","asset":"4525d21","display":"R840cbf8_A4525d21"}}
        """;

    public static async Task RunAsync()
    {
        var now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var logs = new List<string>();
        UnpackRepoClient Create(StubHandler handler, UnpackRepoOptions? options = null) =>
            new(options ?? new UnpackRepoOptions { Token = Token }, handler, () => now, msg => { lock (logs) logs.Add(msg); });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Unpack:Token"] = $" {Token} ", ["Unpack:Enabled"] = "false", ["Unpack:Repository"] = "Owner/Repo-1", ["Unpack:Branch"] = "data",
        }).Build();
        var parsed = UnpackRepoOptions.FromConfiguration(config);
        Assert(parsed is { Token: Token, Enabled: false, Repository: "Owner/Repo-1", Branch: "data" }, "저장소 설정을 구성에서 읽음(Unpack:Token·Enabled·Repository·Branch)");
        var defaults = UnpackRepoOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert(!defaults.HasToken && defaults.Enabled && defaults.Repository == UnpackRepoOptions.DefaultRepository && defaults.Branch == "main", "미설정 시 토큰 없음·기본 저장소");
        var badRepo = false;
        try { UnpackRepoOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Unpack:Repository"] = "a/b/../c" }).Build()); }
        catch (ArgumentException) { badRepo = true; }
        Assert(badRepo, "'소유자/저장소' 형식이 아닌 저장소 거부");

        var none = new StubHandler();
        var notConfigured = await Create(none, new UnpackRepoOptions()).GetHeadCommitAsync();
        var disabled = await Create(none, new UnpackRepoOptions { Token = Token, Enabled = false }).GetVersionAsync();
        Assert(notConfigured.Status == UnpackRepoStatus.NotConfigured && disabled.Status == UnpackRepoStatus.Disabled && none.Requests == 0, "토큰 미설정·비활성화 시 요청하지 않음");

        var ok = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("/commits/") ? Text(HttpStatusCode.OK, Sha + "\n") : Text(HttpStatusCode.OK, VersionJson),
        };
        var client = Create(ok);
        var head = await client.GetHeadCommitAsync();
        Assert(head is { IsSuccess: true, Value: Sha } &&
               ok.LastRequest!.RequestUri!.AbsoluteUri == "https://api.github.com/repos/PieceOfPaper/MabinogiMobileUnpack/commits/main" &&
               ok.LastRequest.Headers.Accept.ToString() == "application/vnd.github.sha" &&
               ok.LastRequest.Headers.Authorization?.ToString() == "Bearer " + Token, "브랜치 최신 커밋 SHA를 토큰 인증으로 조회");
        var version = await client.GetVersionAsync(head.Value);
        Assert(version is { IsSuccess: true, Value: { AppVersion: "3.4.40011", PatchDisplay: "R840cbf8_A4525d21", BuildBranch: "KR3.4" } } &&
               version.Value.GeneratedAt == new DateTimeOffset(2026, 10, 31, 16, 3, 56, TimeSpan.FromHours(9)) &&
               ok.LastRequest!.RequestUri!.AbsoluteUri == $"https://api.github.com/repos/PieceOfPaper/MabinogiMobileUnpack/contents/version.json?ref={Sha}" &&
               ok.LastRequest.Headers.Accept.ToString() == "application/vnd.github.raw+json", "같은 커밋 기준으로 version.json 원본을 읽어 해석");

        await client.GetFileAsync(@"Patch\Sheetdata\client\가 나.json", Sha);
        Assert(ok.LastRequest!.RequestUri!.AbsoluteUri ==
               $"https://api.github.com/repos/PieceOfPaper/MabinogiMobileUnpack/contents/Patch/Sheetdata/client/%EA%B0%80%20%EB%82%98.json?ref={Sha}",
            "역슬래시 경로를 '/'로 바꾸고 구간별로 인코딩");
        var traversal = false;
        try { await client.GetFileAsync("Patch/../secret"); }
        catch (ArgumentException) { traversal = true; }
        Assert(traversal, "'..' 경로 거부");

        var hidden = new StubHandler { Respond = _ => Text(HttpStatusCode.NotFound, """{"message":"Not Found"}""") };
        var notFound = await Create(hidden).GetHeadCommitAsync();
        Assert(notFound.Status == UnpackRepoStatus.NotFound && notFound.Detail!.Contains("접근 권한"), "404는 경로 없음 또는 private 저장소 권한 없음으로 안내");

        var invalid = new StubHandler { Respond = _ => Text(HttpStatusCode.OK, """{"app":{}}""") };
        Assert((await Create(invalid).GetVersionAsync()).Status == UnpackRepoStatus.InvalidResponse, "필수 값이 없는 version.json은 형식 오류");

        // 한도 초과: x-ratelimit-reset까지 호출하지 않습니다.
        var throttled = new StubHandler
        {
            Respond = _ =>
            {
                var r = Text(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""");
                r.Headers.Add("x-ratelimit-remaining", "0");
                r.Headers.Add("x-ratelimit-reset", new DateTimeOffset(now.AddMinutes(30)).ToUnixTimeSeconds().ToString());
                return r;
            },
        };
        var throttledClient = Create(throttled);
        var limited = await throttledClient.GetHeadCommitAsync();
        now = now.AddMinutes(20);
        var during = await throttledClient.GetHeadCommitAsync();
        Assert(limited is { Status: UnpackRepoStatus.RateLimited, RetryAfter.TotalMinutes: > 30 } && during.Status == UnpackRepoStatus.RateLimited && throttled.Requests == 1,
            "403 한도 초과는 토큰 거부가 아니라 reset 시각까지 대기");
        throttled.Respond = _ => Text(HttpStatusCode.OK, Sha);
        now = now.AddMinutes(11);
        Assert((await throttledClient.GetHeadCommitAsync()).IsSuccess && throttled.Requests == 2, "reset 이후 재개");

        // 401·권한 부족 403은 토큰 문제로 보고 재시작 전까지 다시 부르지 않습니다.
        var revoked = new StubHandler { Respond = _ => Text(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""") };
        var revokedClient = Create(revoked);
        var unauthorized = await revokedClient.GetHeadCommitAsync();
        now = now.AddDays(1);
        var stillRevoked = await revokedClient.GetVersionAsync();
        Assert(unauthorized.Status == UnpackRepoStatus.Unauthorized && stillRevoked.Status == UnpackRepoStatus.Unauthorized && revoked.Requests == 1 &&
               !revokedClient.IsAvailable && logs.Any(x => x.Contains("Bad credentials")), "401 응답 시 토큰 거부로 보고 호출 중단");

        var failing = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused") };
        Assert((await Create(failing).GetHeadCommitAsync()).Status == UnpackRepoStatus.Unavailable, "통신 오류는 예외 대신 실패 결과");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = false;
        try { await Create(ok).GetHeadCommitAsync(cts.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "호출자 취소는 실패로 삼키지 않고 전달");

        Assert(logs.All(x => !x.Contains(Token)) && !Create(ok).DescribeState().Contains(Token), "로그와 상태 설명에 토큰을 남기지 않음");
    }

    private static HttpResponseMessage Text(HttpStatusCode code, string body) =>
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
