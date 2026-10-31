using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Molly.Unpack;

public enum UnpackRepoStatus
{
    Success,
    // 토큰이 설정되지 않음(개발 환경 등). 기능은 마지막 정상 데이터로 계속 동작해야 합니다.
    NotConfigured,
    // Unpack:Enabled=false로 운영자가 끔.
    Disabled,
    // 401·403: 토큰 만료·폐기 또는 권한 부족. 재시작 전까지 호출하지 않습니다.
    Unauthorized,
    // 404: 경로가 없거나, 토큰이 이 private 저장소에 접근할 수 없음(GitHub은 권한 없는 저장소를 404로 숨깁니다).
    NotFound,
    // GitHub API 한도 초과. RetryAfter 뒤에 다시 시도할 수 있습니다.
    RateLimited,
    // 통신 오류·5xx.
    Unavailable,
    // 응답 형식이 예상과 다름.
    InvalidResponse,
}

public sealed record UnpackRepoResult<T>(UnpackRepoStatus Status, T? Value, string? Detail = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status == UnpackRepoStatus.Success && Value is not null;

    public static UnpackRepoResult<T> Ok(T value) => new(UnpackRepoStatus.Success, value);
    public static UnpackRepoResult<T> Fail(UnpackRepoStatus status, string? detail = null, TimeSpan? retryAfter = null) => new(status, default, detail, retryAfter);
}

/// <summary>저장소 루트의 version.json 중 갱신 판단과 표시에 쓰는 값.</summary>
public sealed record UnpackVersion(DateTimeOffset GeneratedAt, string AppVersion, string PatchDisplay, string BuildBranch);

/// <summary>
/// 게임 데이터 저장소(GitHub private) 공용 클라이언트. 전체를 받지 않고 커밋 SHA·version.json·필요한 파일만 REST API로 읽습니다.
/// 예외 대신 UnpackRepoResult로 실패를 돌려주며, 호출자 취소(OperationCanceledException)만 그대로 전달합니다.
/// 한 갱신 안에서는 GetHeadCommitAsync로 받은 SHA를 gitRef로 넘겨, 푸시 도중 바뀐 파일이 섞이지 않게 합니다.
/// </summary>
public sealed class UnpackRepoClient : IDisposable
{
    public const string VersionPath = "version.json";
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions s_Json = new(JsonSerializerDefaults.Web);

    private readonly UnpackRepoOptions m_Options;
    private readonly HttpClient m_Http;
    private readonly Func<DateTime> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly object m_Gate = new();

    private bool m_TokenRejected;
    private DateTime m_BlockedUntilUtc = DateTime.MinValue;

    public UnpackRepoClient(UnpackRepoOptions options, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        m_Options = options;
        m_Http = handler is null ? new HttpClient() : new HttpClient(handler);
        m_Http.BaseAddress = new Uri(UnpackRepoOptions.ApiBaseUrl);
        m_Http.Timeout = options.Timeout;
        m_Http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot_Molly/1.0 (+https://github.com/PieceOfPaper/DiscordBot_Molly)");
        m_Http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (options.HasToken)
            m_Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        m_UtcNow = utcNow ?? (() => DateTime.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[게임데이터] {msg}"));
    }

    public bool IsConfigured => m_Options.Enabled && m_Options.HasToken;

    public bool IsAvailable
    {
        get { lock (m_Gate) return IsConfigured && !m_TokenRejected && m_UtcNow() >= m_BlockedUntilUtc; }
    }

    // 시작 로그용 상태 설명. 토큰 값은 절대 포함하지 않습니다.
    public string DescribeState() =>
        !m_Options.Enabled ? "비활성화(Unpack:Enabled=false)"
        : !m_Options.HasToken ? "토큰 미설정 — 게임 데이터 저장소를 읽지 않습니다"
        : $"토큰 설정됨, {m_Options.Repository}@{m_Options.Branch}";

    /// <summary>브랜치의 최신 커밋 SHA. 응답 본문이 SHA 한 줄이라 가장 가벼운 변경 확인입니다.</summary>
    public async Task<UnpackRepoResult<string>> GetHeadCommitAsync(CancellationToken ct = default)
    {
        var result = await SendAsync($"repos/{m_Options.Repository}/commits/{Uri.EscapeDataString(m_Options.Branch)}", "application/vnd.github.sha", ct);
        if (!result.IsSuccess) return UnpackRepoResult<string>.Fail(result.Status, result.Detail, result.RetryAfter);
        var sha = Encoding.ASCII.GetString(result.Value!).Trim();
        return sha.Length == 40 && sha.All(char.IsAsciiHexDigitLower)
            ? UnpackRepoResult<string>.Ok(sha)
            : OnInvalid<string>($"커밋 SHA 형식이 아님: {Truncate(sha)}");
    }

    /// <summary>
    /// 저장소 파일 원본. path는 저장소 루트 기준 '/' 구분 경로, gitRef는 커밋 SHA(없으면 기본 브랜치)입니다.
    /// GitHub raw 응답 한도는 파일당 100MB입니다.
    /// </summary>
    public Task<UnpackRepoResult<byte[]>> GetFileAsync(string path, string? gitRef = null, CancellationToken ct = default)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new ArgumentException($"잘못된 저장소 경로: {path}", nameof(path));
        var relative = $"repos/{m_Options.Repository}/contents/{string.Join('/', segments.Select(Uri.EscapeDataString))}" +
                       $"?ref={Uri.EscapeDataString(gitRef ?? m_Options.Branch)}";
        return SendAsync(relative, "application/vnd.github.raw+json", ct);
    }

    public async Task<UnpackRepoResult<UnpackVersion>> GetVersionAsync(string? gitRef = null, CancellationToken ct = default)
    {
        var file = await GetFileAsync(VersionPath, gitRef, ct);
        if (!file.IsSuccess) return UnpackRepoResult<UnpackVersion>.Fail(file.Status, file.Detail, file.RetryAfter);
        VersionDto? dto;
        try { dto = JsonSerializer.Deserialize<VersionDto>(file.Value!, s_Json); }
        catch (JsonException ex) { return OnInvalid<UnpackVersion>($"{VersionPath} 해석 실패: {ex.Message}"); }
        if (dto is not { GeneratedAt: { } generatedAt, App.Version: { Length: > 0 } appVersion, Patch.Display: { Length: > 0 } patch })
            return OnInvalid<UnpackVersion>($"{VersionPath}에 generatedAt·app.version·patch.display가 없음");
        return UnpackRepoResult<UnpackVersion>.Ok(new UnpackVersion(generatedAt, appVersion, patch, dto.Build?.Branch ?? ""));
    }

    private async Task<UnpackRepoResult<byte[]>> SendAsync(string relative, string accept, CancellationToken ct)
    {
        if (!m_Options.Enabled) return UnpackRepoResult<byte[]>.Fail(UnpackRepoStatus.Disabled);
        if (!m_Options.HasToken) return UnpackRepoResult<byte[]>.Fail(UnpackRepoStatus.NotConfigured);
        lock (m_Gate)
        {
            if (m_TokenRejected) return UnpackRepoResult<byte[]>.Fail(UnpackRepoStatus.Unauthorized, "토큰이 거부되어 재시작 전까지 호출하지 않음");
            var now = m_UtcNow();
            if (now < m_BlockedUntilUtc) return UnpackRepoResult<byte[]>.Fail(UnpackRepoStatus.RateLimited, "GitHub API 한도 대기 중", m_BlockedUntilUtc - now);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        HttpResponseMessage response;
        try
        {
            response = await m_Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail(UnpackRepoStatus.Unavailable, $"{relative} 통신 실패: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            if (response.IsSuccessStatusCode) return UnpackRepoResult<byte[]>.Ok(body);

            var code = (int)response.StatusCode;
            var message = $"{relative} HTTP {code}: {Truncate(Encoding.UTF8.GetString(body))}";
            if (IsRateLimited(response, out var retryAfter))
            {
                lock (m_Gate) m_BlockedUntilUtc = m_UtcNow() + retryAfter;
                return Fail(UnpackRepoStatus.RateLimited, message, retryAfter);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                lock (m_Gate) m_TokenRejected = true;
                return Fail(UnpackRepoStatus.Unauthorized, message + " — 토큰 만료·폐기 또는 권한 부족, 재시작 전까지 호출 중단");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Fail(UnpackRepoStatus.NotFound, message + " — 경로가 없거나 토큰에 이 저장소 접근 권한이 없음");
            return Fail(UnpackRepoStatus.Unavailable, message);
        }
    }

    private bool IsRateLimited(HttpResponseMessage response, out TimeSpan retryAfter)
    {
        retryAfter = DefaultRetryAfter;
        var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        if (response.StatusCode != HttpStatusCode.TooManyRequests && !(response.StatusCode == HttpStatusCode.Forbidden && (exhausted || response.Headers.RetryAfter is not null)))
            return false;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = delta;
        else if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime - m_UtcNow();
            if (wait > TimeSpan.Zero) retryAfter = wait + TimeSpan.FromSeconds(5);
        }
        return true;
    }

    private UnpackRepoResult<byte[]> Fail(UnpackRepoStatus status, string detail, TimeSpan? retryAfter = null)
    {
        m_Log(detail);
        return UnpackRepoResult<byte[]>.Fail(status, detail, retryAfter);
    }

    private UnpackRepoResult<T> OnInvalid<T>(string detail)
    {
        m_Log(detail);
        return UnpackRepoResult<T>.Fail(UnpackRepoStatus.InvalidResponse, detail);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => m_Http.Dispose();

    private sealed record VersionDto(
        [property: JsonPropertyName("generatedAt")] DateTimeOffset? GeneratedAt,
        [property: JsonPropertyName("app")] VersionAppDto? App,
        [property: JsonPropertyName("build")] VersionBuildDto? Build,
        [property: JsonPropertyName("patch")] VersionPatchDto? Patch);
    private sealed record VersionAppDto([property: JsonPropertyName("version")] string? Version);
    private sealed record VersionBuildDto([property: JsonPropertyName("branch")] string? Branch);
    private sealed record VersionPatchDto([property: JsonPropertyName("display")] string? Display);
}
