using Molly.Status;

namespace Molly.OfficialNews;

public sealed class OfficialNewsFetchException(string message) : Exception(message);

/// <summary>
/// 공식 소식 네 게시판의 목록 첫 페이지를 주기적으로 HTTP로 읽어 새 글을 찾는다. 봇 전체에서 한 번만 수집하고 서버별 구독은 발송 작업으로 나눈다.
/// 처음 보는 게시판은 지금 목록을 기준으로만 기록하고 보내지 않는다. 확인에 실패하면 상태를 진행하지 않고 다음 주기에 다시 찾는다.
/// 공지 게시판은 같은 목록으로 점검 글의 등록·제목 변경·완료도 판정한다(<see cref="MaintenanceRules"/>). 점검 글은 일반 공지 새 글로 보내지 않는다.
/// 목록은 이벤트 수집과 같은 HTTP 경로로 받는다(랭킹과 달리 브라우저 보안 검사가 없다).
/// </summary>
public sealed class OfficialNewsCollector
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    /// <summary>한 번 확인할 때 읽는 최대 페이지 수. 한 페이지가 모두 새 글일 때만 다음 페이지를 읽는다.</summary>
    public const int MaxPagesPerCheck = 5;
    // 실패가 이어지면 처음과 약 1시간마다만 기록한다.
    private const int FailureLogEvery = 6;

    private readonly OfficialNewsStore m_Store;
    private readonly IMobiEventPageSource m_Source;
    private readonly Action m_OnDeliveriesCreated;
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly MollyStatusTracker? m_Status;
    private readonly Dictionary<string, int> m_ConsecutiveFailures = [];
    private int m_Started;

    public OfficialNewsCollector(OfficialNewsStore store, IMobiEventPageSource source, Action onDeliveriesCreated,
        Func<DateTimeOffset>? utcNow = null, Action<string>? log = null, MollyStatusTracker? status = null)
    {
        m_Store = store;
        m_Status = status;
        m_Source = source;
        m_OnDeliveriesCreated = onDeliveriesCreated;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[공식소식] {msg}"));
    }

    /// <summary>앱 수명 동안 한 번만 실행된다. 시작하자마자 한 번 확인하고 이후 <see cref="Interval"/>마다 확인한다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref m_Started, 1) != 0) return;
        m_Log($"공식 소식 수집 시작: {Interval.TotalMinutes:0}분마다 {NewsBoards.All.Count}개 게시판 첫 페이지 확인");
        while (!ct.IsCancellationRequested)
        {
            await CheckAllAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>모든 게시판을 차례로 확인하고 새로 찾은 글 수를 돌려준다. 한 게시판의 실패가 다른 게시판 확인을 막지 않는다.</summary>
    public async Task<int> CheckAllAsync(CancellationToken ct)
    {
        var found = 0;
        foreach (var board in NewsBoards.All)
        {
            if (ct.IsCancellationRequested) break;
            found += await CheckBoardAsync(board, ct).ConfigureAwait(false);
        }
        return found;
    }

    public async Task<int> CheckBoardAsync(NewsBoard board, CancellationToken ct)
    {
        // /몰리상태용 확인 결과. 첫 기준을 잡기 전의 실패도 여기에 남는다.
        var run = m_Status?.Begin(StatusJobs.News(board));
        try
        {
            var found = await CheckBoardCoreAsync(board, ct).ConfigureAwait(false);
            if (run is not null) await m_Status!.CompleteAsync(run, true, null).ConfigureAwait(false);
            if (m_ConsecutiveFailures.Remove(board.Id, out var failures))
                m_Log($"{board.Name} 목록 확인 복구 (연속 실패 {failures}번 뒤)");
            return found;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (run is not null) await m_Status!.CompleteAsync(run, false, StatusErrorKinds.ClassifyCollection(ex)).ConfigureAwait(false);
            var failures = m_ConsecutiveFailures[board.Id] = m_ConsecutiveFailures.GetValueOrDefault(board.Id) + 1;
            var error = ex is OfficialNewsFetchException or MobiEventFetchException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (failures % FailureLogEvery == 1)
                m_Log($"{board.Name} 목록 확인 실패({failures}번째), 다음 주기에 다시 시도: {error}");
            try { await m_Store.RecordCheckFailedAsync(board.Id, error, m_UtcNow(), ct).ConfigureAwait(false); }
            catch (Exception recordEx) when (!ct.IsCancellationRequested) { m_Log($"{board.Name} 확인 실패 기록 실패: {recordEx.GetType().Name}: {recordEx.Message}"); }
            return 0;
        }
    }

    private async Task<int> CheckBoardCoreAsync(NewsBoard board, CancellationToken ct)
    {
        var page = await FetchAsync(board, 1, "", "", ct).ConfigureAwait(false);
        var now = m_UtcNow();
        var tracks = MaintenanceRules.Tracks(board);
        var state = await m_Store.GetBoardStateAsync(board.Id, ct).ConfigureAwait(false);
        if (state is null)
        {
            if (await m_Store.BaselineBoardAsync(board.Id, page.Cards.Select(c => c.PostId).ToList(), tracks ? page.Cards : [], now, ct).ConfigureAwait(false))
                m_Log($"{board.Name} 첫 확인: 지금 목록의 글 {page.Cards.Count}개를 기준으로 기록(보내지 않음)");
            return 0;
        }

        var discovered = new List<NewsListCard>();
        // 이미 본 글이 있는 페이지도 모두 제목·말머리를 비교한다. 같은 글이 여러 페이지·카드로 와도 처음 것 하나만 본다.
        var observed = new List<NewsListCard>();
        for (var pageNo = 1; ; pageNo++)
        {
            if (tracks) observed.AddRange(page.Cards.Where(c => observed.All(o => o.PostId != c.PostId)));
            // 기준보다 오래된 글은 목록 위로 다시 올라와도 새 글이 아니다.
            var candidates = page.Cards.Where(c => c.PostId > state.FloorPostId && discovered.All(d => d.PostId != c.PostId)).ToList();
            var seen = await m_Store.GetSeenPostIdsAsync(board.Id, candidates.Select(c => c.PostId), ct).ConfigureAwait(false);
            var unseen = candidates.Where(c => !seen.Contains(c.PostId)).ToList();
            discovered.AddRange(unseen);

            var morePages = page.TotalCount > pageNo * page.Cards.Count;
            if (unseen.Count < page.Cards.Count || !morePages) break;
            if (pageNo >= MaxPagesPerCheck)
            {
                m_Log($"{board.Name} 새 글이 {MaxPagesPerCheck}페이지를 넘어 이번에는 {discovered.Count}개까지만 확인");
                break;
            }
            page = await FetchAsync(board, pageNo + 1, page.BlockStartNo, page.BlockStartKey, ct).ConfigureAwait(false);
        }

        // 점검 글(처음부터 완료된 점검 안내 포함)은 일반 공지 새 글로 보내지 않는다. 본 글 표시는 함께 한다.
        var events = discovered.Where(c => !tracks || !MaintenanceRules.IsMaintenancePost(c.Title, c.Category))
            .OrderBy(c => c.PostId).Select(c => ToEvent(board, c, now)).ToList();
        var record = await m_Store.RecordCheckAsync(board.Id, events, discovered.Select(c => c.PostId).ToList(), observed, now, ct).ConfigureAwait(false);
        if (events.Count > 0)
            m_Log($"{board.Name} 새 글 {events.Count}개 발견: {string.Join(", ", events.Select(e => e.PostId))}");
        if (discovered.Count > events.Count)
            m_Log($"{board.Name} 새 점검 글 {discovered.Count - events.Count}개는 공지 알림에서 제외: {string.Join(", ", discovered.Select(c => c.PostId).Except(events.Select(e => long.Parse(e.PostId, System.Globalization.CultureInfo.InvariantCulture))))}");
        foreach (var change in record.MaintenanceEvents)
            m_Log($"점검 알림 {change.Kind} 글 {change.PostId} 변경 {change.Revision}");
        if (events.Count > 0 || record.MaintenanceEvents.Count > 0)
            m_Log($"{board.Name} 확인: 발송 작업 {record.Deliveries}개");
        if (record.Deliveries > 0) m_OnDeliveriesCreated();
        return discovered.Count;
    }

    /// <summary>게시판 목록 첫 페이지를 지금 바로 읽는다. 상태는 바꾸지 않는다(구독 명령의 테스트 출력용).</summary>
    public Task<NewsListPage> FetchLatestAsync(NewsBoard board, CancellationToken ct) => FetchAsync(board, 1, "", "", ct);

    /// <summary>목록 글을 발견 시각 now의 알림 내용으로 바꾼다.</summary>
    public static NewsEvent ToEvent(NewsBoard board, NewsListCard card, DateTimeOffset detectedAt) => new(
        NewsBoards.EventIdFor(board.Id, card.PostId), board.Id, card.PostId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        card.Title, board.PostUrl(card.PostId), board.ShowsCategory ? card.Category : null, card.ThumbnailUrl, detectedAt);

    private async Task<NewsListPage> FetchAsync(NewsBoard board, int pageNo, string blockStartNo, string blockStartKey, CancellationToken ct)
    {
        var url = OfficialNewsParser.BuildListUrl(board, pageNo, blockStartNo, blockStartKey);
        var page = OfficialNewsParser.ParseListPage(await m_Source.FetchPageAsync(url, ct).ConfigureAwait(false));
        if (page.Cards.Count == 0)
            throw new OfficialNewsFetchException(page.HasPagination
                ? $"{pageNo}페이지에 글이 없음(구조 변경 의심)"
                : $"{pageNo}페이지에서 목록 구조를 찾지 못함(차단·지역 이동 페이지 또는 구조 변경 의심)");
        return page;
    }
}
