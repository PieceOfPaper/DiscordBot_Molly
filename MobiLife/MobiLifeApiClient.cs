using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Molly.MobiLife;

public enum MobiLifeStatus
{
    Success,
    // 키가 설정되지 않음(개발 환경 등). 기능은 안내만 하고 계속 동작해야 합니다.
    NotConfigured,
    // MobiLife:Enabled=false로 운영자가 끔.
    Disabled,
    // 로컬 한도 또는 서버 429. RetryAfter 뒤에 다시 시도할 수 있습니다.
    RateLimited,
    // 401: 키가 틀렸거나 폐기됨. 재시작 전까지 호출하지 않습니다.
    Unauthorized,
    // 통신 오류·5xx·404/410·차단. 잠시 호출을 멈춘 뒤 다시 시도합니다.
    Unavailable,
    // 응답 형식이 예상과 다름(API 변경 가능성).
    InvalidResponse,
}

public sealed record MobiLifeResult<T>(MobiLifeStatus Status, T? Value, string? Detail = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status == MobiLifeStatus.Success && Value is not null;

    // Discord 사용자에게 그대로 보여줄 수 있는 안내. Detail(서버 메시지)은 로그용이라 넣지 않습니다.
    public string UserMessage => Status switch
    {
        MobiLifeStatus.Success => "",
        MobiLifeStatus.NotConfigured or MobiLifeStatus.Disabled => "모비라이프 데이터 연동이 꺼져 있어 이 기능을 사용할 수 없습니다.",
        MobiLifeStatus.RateLimited => RetryAfter is { } wait
            ? $"요청이 많아 잠시 쉬고 있습니다. 약 {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))}분 뒤에 다시 시도해 주세요."
            : "요청이 많아 잠시 쉬고 있습니다. 잠시 뒤에 다시 시도해 주세요.",
        MobiLifeStatus.Unauthorized => "모비라이프 API 키를 사용할 수 없어 이 기능이 중단되었습니다. 관리자에게 알려 주세요.",
        _ => "모비라이프 데이터를 지금 가져올 수 없습니다. 잠시 뒤에 다시 시도해 주세요.",
    };

    public static MobiLifeResult<T> Ok(T value) => new(MobiLifeStatus.Success, value);
    public static MobiLifeResult<T> Fail(MobiLifeStatus status, string? detail = null, TimeSpan? retryAfter = null) => new(status, default, detail, retryAfter);
}

/// <summary>
/// 모비라이프 OpenAPI(https://open.mabimobi.life/docs) 공용 HTTP 클라이언트.
/// 인증 헤더, 로컬 요청 한도, 429·키 폐기·서비스 중단 감지와 일시 차단을 담당합니다.
/// 기능 코드는 이 클래스를 직접 쓰지 말고 기능별 데이터 공급 인터페이스의 구현에서만 사용하세요(docs/mobilife-openapi.md).
/// 예외 대신 MobiLifeResult로 실패를 돌려주며, 호출자 취소(OperationCanceledException)만 그대로 전달합니다.
/// </summary>
public sealed class MobiLifeApiClient : IDisposable
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(1);
    // 연속 실패가 이 횟수에 닿으면 FailureCooldown 동안 호출하지 않습니다.
    public const int FailureThreshold = 3;
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);
    // 404/410은 엔드포인트 변경·서비스 종료 가능성이 커서 더 오래 쉽니다.
    public static readonly TimeSpan GoneCooldown = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions s_Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MobiLifeOptions m_Options;
    private readonly HttpClient m_Http;
    private readonly Func<DateTime> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly object m_Gate = new();
    private readonly Queue<DateTime> m_MinuteWindow = new();
    private readonly Queue<DateTime> m_DayWindow = new();

    private bool m_KeyRejected;
    private int m_ConsecutiveFailures;
    private DateTime m_BlockedUntilUtc = DateTime.MinValue;
    private MobiLifeStatus m_BlockedStatus;

    public MobiLifeApiClient(MobiLifeOptions options, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        m_Options = options;
        m_Http = handler is null ? new HttpClient() : new HttpClient(handler);
        m_Http.BaseAddress = options.BaseUrl;
        m_Http.Timeout = options.Timeout;
        m_Http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot_Molly/1.0 (+https://github.com/PieceOfPaper/DiscordBot_Molly)");
        m_Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.HasApiKey)
            m_Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        m_UtcNow = utcNow ?? (() => DateTime.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[모비라이프] {msg}"));
    }

    public bool IsConfigured => m_Options.Enabled && m_Options.HasApiKey;

    // 지금 호출하면 네트워크까지 갈 수 있는 상태인지. 명령 목록 노출 여부 판단 등에 사용합니다.
    public bool IsAvailable
    {
        get { lock (m_Gate) return IsConfigured && !m_KeyRejected && m_UtcNow() >= m_BlockedUntilUtc; }
    }

    // 시작 로그용 상태 설명. 키 값은 절대 포함하지 않습니다.
    public string DescribeState() =>
        !m_Options.Enabled ? "비활성화(MobiLife:Enabled=false)"
        : !m_Options.HasApiKey ? "API 키 미설정 — 모비라이프 연동 기능은 안내 메시지만 표시합니다"
        : $"API 키 설정됨, {m_Options.BaseUrl}";

    /// <summary>
    /// GET 요청을 보내고 JSON 응답을 T로 변환합니다. path는 BaseUrl 기준 상대 경로(예: "market/categories")입니다.
    /// </summary>
    public async Task<MobiLifeResult<T>> GetAsync<T>(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, CancellationToken ct = default)
        where T : class
    {
        if (!m_Options.Enabled) return MobiLifeResult<T>.Fail(MobiLifeStatus.Disabled);
        if (!m_Options.HasApiKey) return MobiLifeResult<T>.Fail(MobiLifeStatus.NotConfigured);
        if (TryAcquire() is { } blocked) return MobiLifeResult<T>.Fail(blocked.Status, blocked.Detail, blocked.RetryAfter);

        var relative = BuildRelativeUri(path, query);
        HttpResponseMessage response;
        try
        {
            response = await m_Http.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException은 호출자 취소가 아니면 HttpClient 시간 초과입니다.
            return OnFailure<T>(MobiLifeStatus.Unavailable, $"{relative} 통신 실패: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                T? value = null;
                try { value = JsonSerializer.Deserialize<T>(body, s_Json); }
                catch (JsonException ex) { return OnFailure<T>(MobiLifeStatus.InvalidResponse, $"{relative} JSON 해석 실패: {ex.Message}"); }
                if (value is null) return OnFailure<T>(MobiLifeStatus.InvalidResponse, $"{relative} 빈 응답");
                lock (m_Gate) m_ConsecutiveFailures = 0;
                return MobiLifeResult<T>.Ok(value);
            }

            var detail = $"{relative} HTTP {code}: {ReadErrorMessage(body)}";
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    lock (m_Gate) m_KeyRejected = true;
                    m_Log($"API 키가 거부되었습니다. 키가 폐기되었을 수 있으니 확인 후 재시작하세요. ({detail})");
                    return MobiLifeResult<T>.Fail(MobiLifeStatus.Unauthorized, detail);
                case HttpStatusCode.TooManyRequests:
                    var wait = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date.UtcDateTime - m_UtcNow() : (TimeSpan?)null)
                        ?? DefaultRetryAfter;
                    if (wait <= TimeSpan.Zero) wait = DefaultRetryAfter;
                    Block(MobiLifeStatus.RateLimited, wait);
                    m_Log($"서버 요청 한도 초과, {wait.TotalSeconds:0}초 동안 호출을 멈춥니다. ({detail})");
                    return MobiLifeResult<T>.Fail(MobiLifeStatus.RateLimited, detail, wait);
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    Block(MobiLifeStatus.Unavailable, GoneCooldown);
                    m_Log($"엔드포인트를 찾을 수 없습니다. API 변경·종료 가능성이 있어 {GoneCooldown.TotalMinutes:0}분 동안 호출을 멈춥니다. ({detail})");
                    return MobiLifeResult<T>.Fail(MobiLifeStatus.Unavailable, detail, GoneCooldown);
                default:
                    return OnFailure<T>(MobiLifeStatus.Unavailable, detail);
            }
        }
    }

    private MobiLifeResult<T> OnFailure<T>(MobiLifeStatus status, string detail)
    {
        int failures;
        lock (m_Gate) failures = ++m_ConsecutiveFailures;
        if (failures >= FailureThreshold)
        {
            Block(MobiLifeStatus.Unavailable, FailureCooldown);
            m_Log($"연속 {failures}회 실패, {FailureCooldown.TotalMinutes:0}분 동안 호출을 멈춥니다. ({detail})");
            return MobiLifeResult<T>.Fail(status, detail, FailureCooldown);
        }
        m_Log(detail);
        return MobiLifeResult<T>.Fail(status, detail);
    }

    private void Block(MobiLifeStatus status, TimeSpan duration)
    {
        lock (m_Gate)
        {
            m_BlockedUntilUtc = m_UtcNow() + duration;
            m_BlockedStatus = status;
            m_ConsecutiveFailures = 0;
        }
    }

    // 호출 가능하면 한도 창에 기록하고 null, 아니면 차단 사유를 돌려줍니다.
    private (MobiLifeStatus Status, string Detail, TimeSpan? RetryAfter)? TryAcquire()
    {
        lock (m_Gate)
        {
            var now = m_UtcNow();
            if (m_KeyRejected) return (MobiLifeStatus.Unauthorized, "API 키가 거부된 뒤 재시작 전까지 호출하지 않습니다.", null);
            if (now < m_BlockedUntilUtc) return (m_BlockedStatus, "일시 차단 중", m_BlockedUntilUtc - now);

            Trim(m_MinuteWindow, now - TimeSpan.FromMinutes(1));
            Trim(m_DayWindow, now - TimeSpan.FromDays(1));
            if (m_MinuteWindow.Count >= m_Options.PerMinuteLimit)
                return (MobiLifeStatus.RateLimited, "로컬 분당 한도 도달", m_MinuteWindow.Peek() + TimeSpan.FromMinutes(1) - now);
            if (m_DayWindow.Count >= m_Options.PerDayLimit)
                return (MobiLifeStatus.RateLimited, "로컬 일일 한도 도달", m_DayWindow.Peek() + TimeSpan.FromDays(1) - now);
            m_MinuteWindow.Enqueue(now);
            m_DayWindow.Enqueue(now);
            return null;
        }
    }

    private static void Trim(Queue<DateTime> window, DateTime threshold)
    {
        while (window.Count > 0 && window.Peek() <= threshold) window.Dequeue();
    }

    private static string BuildRelativeUri(string path, IEnumerable<KeyValuePair<string, string?>>? query)
    {
        var relative = path.TrimStart('/');
        var pairs = query?.Where(x => x.Value is not null)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")
            .ToArray();
        return pairs is { Length: > 0 } ? relative + "?" + string.Join('&', pairs) : relative;
    }

    // 오류 응답 형식: {"code": 401, "message": "..."}. 다른 형식(HTML 차단 페이지 등)은 앞부분만 남깁니다.
    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var message))
                return message.ToString();
        }
        catch (JsonException) { }
        var trimmed = body.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120] + "…";
    }

    public void Dispose() => m_Http.Dispose();
}
