using System.Net;

// 이벤트 목록 HTML 한 페이지를 받아오는 경로. 기본은 HTTP, 막히면 Playwright로 대체합니다.
public interface IMobiEventPageSource
{
    string Name { get; }
    Task<string> FetchPageAsync(string url, CancellationToken ct);
}

public sealed class MobiEventFetchException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record MobiEventSnapshot(IReadOnlyList<MobiEventResult> Events, DateTime FetchedAtKst, string SourceName);

// IsStale이면 최신 수집에 실패해 마지막 정상 데이터를 돌려준 것입니다.
public sealed record MobiEventQueryResult(IReadOnlyList<MobiEventResult> Events, DateTime FetchedAtKst, bool IsStale);

/// <summary>
/// 이벤트 목록 수집과 캐시. 목록 페이지는 서버가 완성된 HTML로 내려주므로 평소에는 HTTP로만 읽고,
/// HTTP가 실패(차단·오류)할 때만 Playwright 대체 경로를 사용합니다.
/// 새 데이터는 검증을 통과했을 때만 캐시를 교체하고, 실패하면 마지막 정상 데이터를 유지합니다.
/// </summary>
public sealed class MobiEventService
{
    private const int MAX_PAGES = 20;

    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(1);
    // Playwright는 무거우므로 대체 경로까지 실패하면 이 시간 동안은 다시 시도하지 않습니다.
    public static readonly TimeSpan DefaultFallbackCooldown = TimeSpan.FromMinutes(10);

    private static readonly Lazy<MobiEventService> s_Shared = new(() => new MobiEventService(
        new MobiEventHttpSource(),
        () => new MobiEventBrowserSource()));
    public static MobiEventService Shared => s_Shared.Value;

    private readonly IMobiEventPageSource m_Primary;
    private readonly Func<IMobiEventPageSource>? m_FallbackFactory;
    private readonly Func<DateTime> m_UtcNow;
    private readonly TimeSpan m_CacheTtl;
    private readonly TimeSpan m_FallbackCooldown;
    private readonly Action<string> m_Log;
    private readonly SemaphoreSlim m_RefreshGate = new(1, 1);

    private volatile MobiEventSnapshot? m_Snapshot;
    private DateTime m_LastAttemptUtc = DateTime.MinValue;
    private DateTime m_FallbackBlockedUntilUtc = DateTime.MinValue;

    public MobiEventService(
        IMobiEventPageSource primary,
        Func<IMobiEventPageSource>? fallbackFactory,
        Func<DateTime>? utcNow = null,
        TimeSpan? cacheTtl = null,
        TimeSpan? fallbackCooldown = null,
        Action<string>? log = null)
    {
        m_Primary = primary;
        m_FallbackFactory = fallbackFactory;
        m_UtcNow = utcNow ?? (() => DateTime.UtcNow);
        m_CacheTtl = cacheTtl ?? DefaultCacheTtl;
        m_FallbackCooldown = fallbackCooldown ?? DefaultFallbackCooldown;
        m_Log = log ?? (msg => Console.WriteLine($"[MobiEvent] {msg}"));
    }

    public MobiEventSnapshot? LastSnapshot => m_Snapshot;

    /// <summary>
    /// 진행중 이벤트 목록. 캐시가 유효하면 그대로, 아니면 새로 수집합니다.
    /// 수집에 실패하면 마지막 정상 데이터를 IsStale=true로 돌려주고, 정상 데이터가 한 번도 없으면 null입니다.
    /// 반환 목록은 호출자 전용 복사본입니다.
    /// </summary>
    public async Task<MobiEventQueryResult?> GetCurrentEventsAsync(CancellationToken ct = default)
    {
        if (TryGetFresh() is { } fresh)
            return ToResult(fresh, false);

        // 동시 요청은 한 번의 수집을 기다렸다가 그 결과를 함께 사용합니다.
        await m_RefreshGate.WaitAsync(ct);
        try
        {
            if (TryGetFresh() is { } refreshedByOther)
                return ToResult(refreshedByOther, false);

            var ok = await RefreshCoreAsync(ct);
            var snapshot = m_Snapshot;
            return snapshot == null ? null : ToResult(snapshot, !ok);
        }
        finally
        {
            m_RefreshGate.Release();
        }
    }

    /// <summary>캐시 유효 시간과 무관하게 즉시 수집합니다. 성공해서 캐시를 교체했으면 true.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await m_RefreshGate.WaitAsync(ct);
        try
        {
            return await RefreshCoreAsync(ct);
        }
        finally
        {
            m_RefreshGate.Release();
        }
    }

    // 성공한 수집뿐 아니라 실패한 시도도 TTL 동안은 반복하지 않습니다(사이트가 막혔을 때 요청 폭주 방지).
    private MobiEventSnapshot? TryGetFresh()
    {
        var snapshot = m_Snapshot;
        if (snapshot == null) return null;
        return m_UtcNow() - m_LastAttemptUtc < m_CacheTtl ? snapshot : null;
    }

    private static MobiEventQueryResult ToResult(MobiEventSnapshot snapshot, bool isStale)
        => new(snapshot.Events.ToList(), snapshot.FetchedAtKst, isStale);

    private async Task<bool> RefreshCoreAsync(CancellationToken ct)
    {
        m_LastAttemptUtc = m_UtcNow();

        try
        {
            m_Snapshot = await CollectAsync(m_Primary, ct);
            m_Log($"{m_Primary.Name}로 이벤트 {m_Snapshot.Events.Count}건 갱신");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            m_Log($"{m_Primary.Name} 수집 실패: {ex.GetType().Name}: {ex.Message}");
        }

        if (m_FallbackFactory == null)
            return false;
        if (m_UtcNow() < m_FallbackBlockedUntilUtc)
        {
            m_Log($"대체 수집은 {m_FallbackBlockedUntilUtc:HH:mm:ss}(UTC)까지 보류, 마지막 정상 데이터를 유지");
            return false;
        }

        var fallback = m_FallbackFactory();
        try
        {
            m_Snapshot = await CollectAsync(fallback, ct);
            m_Log($"{fallback.Name}로 이벤트 {m_Snapshot.Events.Count}건 갱신 (대체 경로)");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            m_FallbackBlockedUntilUtc = m_UtcNow() + m_FallbackCooldown;
            m_Log($"{fallback.Name} 수집 실패, 마지막 정상 데이터를 유지: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (fallback is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// 1페이지의 pagination 정보로 전체 페이지를 순회해 검증된 스냅샷을 만듭니다.
    /// 한 페이지라도 받지 못하거나 구조가 달라 보이면 예외를 던져 캐시 교체를 막습니다.
    /// </summary>
    public async Task<MobiEventSnapshot> CollectAsync(IMobiEventPageSource source, CancellationToken ct)
    {
        var first = MobiEventParser.ParseListPage(await source.FetchPageAsync(MobiEventParser.FirstPageUrl, ct));
        if (first.Cards.Count == 0)
            throw new MobiEventFetchException(first.HasPagination
                ? "1페이지에 이벤트 카드가 없음(구조 변경 의심)"
                : "이벤트 목록 구조를 찾지 못함(차단 페이지 또는 구조 변경 의심)");

        var perPage = first.Cards.Count;
        var totalPages = first.TotalCount > perPage ? (int)Math.Ceiling((double)first.TotalCount / perPage) : 1;
        if (totalPages > MAX_PAGES)
            throw new MobiEventFetchException($"페이지 수가 비정상적으로 많음(totalCount={first.TotalCount}, perPage={perPage})");

        var cards = new List<MobiEventCard>(first.Cards);
        string blockStartNo = first.BlockStartNo, blockStartKey = first.BlockStartKey;
        for (var pageNo = 2; pageNo <= totalPages; pageNo++)
        {
            ct.ThrowIfCancellationRequested();
            var page = MobiEventParser.ParseListPage(
                await source.FetchPageAsync(MobiEventParser.BuildListUrl(pageNo, blockStartNo, blockStartKey), ct));
            if (page.Cards.Count == 0)
                throw new MobiEventFetchException($"{pageNo}페이지에 이벤트 카드가 없음");

            if (!string.IsNullOrEmpty(page.BlockStartNo)) blockStartNo = page.BlockStartNo;
            if (!string.IsNullOrEmpty(page.BlockStartKey)) blockStartKey = page.BlockStartKey;
            cards.AddRange(page.Cards);
        }

        var distinctCards = cards.DistinctBy(x => x.ThreadId).ToList();
        if (distinctCards.Count != first.TotalCount)
            m_Log($"수집 개수({distinctCards.Count})가 사이트 표시 개수({first.TotalCount})와 다름 - 수집 중 목록이 바뀌었을 수 있음");

        var events = new List<MobiEventResult>();
        foreach (var card in distinctCards)
        {
            if (MobiEventParser.TryParseRange(card.Range, out var start, out var end, out var isPerma))
                events.Add(new MobiEventResult(card.Title, card.Url, card.ThumbnailUrl, start, end, isPerma));
            else
                m_Log($"기간 해석 실패 - title:{card.Title}, range:{card.Range}, url:{card.Url}");
        }

        if (events.Count == 0)
            throw new MobiEventFetchException($"기간을 해석한 이벤트가 없음(카드 {distinctCards.Count}건)");

        return new MobiEventSnapshot(events, TimeZoneInfo.ConvertTimeFromUtc(m_UtcNow(), MobiTime.timezone), source.Name);
    }
}

/// <summary>브라우저 없이 목록 HTML을 직접 받습니다. 사이트 쿠키는 CookieContainer로 유지합니다.</summary>
public sealed class MobiEventHttpSource : IMobiEventPageSource
{
    private readonly HttpClient m_Http;

    public MobiEventHttpSource()
    {
        m_Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        m_Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        m_Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        m_Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9");
    }

    public string Name => "HTTP";

    public async Task<string> FetchPageAsync(string url, CancellationToken ct)
    {
        using var response = await m_Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new MobiEventFetchException($"HTTP {(int)response.StatusCode} ({url})");
        return await response.Content.ReadAsStringAsync(ct);
    }
}
