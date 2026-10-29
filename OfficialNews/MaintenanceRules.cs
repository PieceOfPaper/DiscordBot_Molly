using System.Text.RegularExpressions;

namespace Molly.OfficialNews;

/// <summary>점검 알림 이벤트 종류. 이벤트 ID와 DB의 kind 값으로 쓴다.</summary>
public static class MaintenanceEventKinds
{
    public const string Created = "created";
    public const string TitleChanged = "title_changed";
    public const string Completed = "completed";
}

/// <summary>
/// 공지 글 하나의 비교 기준(official_news_post_states). 제목·말머리는 파서가 정리한 원문이다.
/// Maintenance는 한 번이라도 점검 글로 식별했는지(이후 완료 등으로 바뀌어도 유지), Revision은 감지한 제목·말머리 변경 횟수다.
/// </summary>
public sealed record NewsPostState(long PostId, string Title, string? Category, bool Maintenance, int Revision);

/// <summary>목록에서 본 글 하나를 비교한 결과. Changed면 비교 기준을 바꾸고, Kind가 있으면 점검 알림 이벤트를 만든다.</summary>
public sealed record PostObservationResult(NewsPostState Next, bool Changed, string? Kind);

/// <summary>
/// 공지 목록의 제목·말머리만으로 점검 글과 그 변화를 판정한다. 본문·상세 페이지는 보지 않는 순수 함수다.
/// <list type="bullet">
/// <item>점검 글: 말머리가 `점검`, 또는 말머리가 `완료`이면서 제목이 정기·임시·긴급 점검 안내 형식인 글. 제목에 ‘점검’이 들어갔다는 것만으로는 점검 글로 보지 않는다.</item>
/// <item>새 글(처음 발견)의 말머리가 `점검`이면 새 점검 안내. 처음부터 `완료`인 글은 이전 상태를 몰라 완료 알림을 만들지 않는다.</item>
/// <item>기존 글: 이전 `점검` → 현재 `완료`면 완료(제목이 함께 바뀌어도 하나), 이전·현재 `점검`이고 제목이 바뀌면 제목 변경. 그 외 말머리 변경과 완료 뒤 변경은 기준만 갱신한다.</item>
/// </list>
/// </summary>
public static partial class MaintenanceRules
{
    public const string MaintenanceCategory = "점검";
    public const string CompletedCategory = "완료";

    /// <summary>제목·말머리 변경을 추적하는 게시판. 점검 알림은 공지 게시판에만 있다.</summary>
    public static bool Tracks(NewsBoard board) => board.Id == NewsBoards.Notice.Id;

    /// <summary>
    /// 처음 본 글이 점검 글인지. 말머리 `완료`는 끝난 상품 안내에도 붙으므로 제목이 점검 안내 형식일 때만 점검 글로 본다.
    /// 일반 공지 새 글 알림에서 제외할 때와 비교 기준의 점검 소속을 정할 때 쓴다.
    /// </summary>
    public static bool IsMaintenancePost(string title, string? category) =>
        category == MaintenanceCategory || category == CompletedCategory && LooksLikeMaintenanceTitle(title);

    /// <summary>`정기점검 안내`·`임시점검 안내`·`긴급점검 안내`(띄어쓰기 허용) 형식. `정기점검 미진행 안내`·`서버 불안정 현상 안내`는 아니다.</summary>
    public static bool LooksLikeMaintenanceTitle(string title) => MaintenanceTitleRegex().IsMatch(title);

    /// <param name="previous">저장된 비교 기준. 없으면 처음 보는 글이다.</param>
    /// <param name="isNew">새 글 판정(기준보다 새롭고 아직 본 적 없는 글)을 통과한 글인지. 기준이 없는 기존 글은 기준만 만든다.</param>
    public static PostObservationResult Evaluate(NewsPostState? previous, NewsListCard card, bool isNew)
    {
        if (previous is null)
        {
            var state = new NewsPostState(card.PostId, card.Title, card.Category, IsMaintenancePost(card.Title, card.Category), 0);
            return new(state, true, isNew && card.Category == MaintenanceCategory ? MaintenanceEventKinds.Created : null);
        }
        // 말머리를 읽지 못한 회차(구조 변경 등)는 말머리가 사라진 변화로 보지 않는다.
        if (card.Category is null && previous.Category is not null) return new(previous, false, null);
        if (card.Title == previous.Title && card.Category == previous.Category) return new(previous, false, null);

        var next = new NewsPostState(card.PostId, card.Title, card.Category,
            previous.Maintenance || card.Category == MaintenanceCategory, previous.Revision + 1);
        string? kind = null;
        if (previous.Category == MaintenanceCategory && card.Category == CompletedCategory) kind = MaintenanceEventKinds.Completed;
        else if (previous.Category == MaintenanceCategory && card.Category == MaintenanceCategory) kind = MaintenanceEventKinds.TitleChanged;
        return new(next, true, kind);
    }

    /// <summary>비교 결과를 점검 알림 이벤트로 바꾼다. 처음 발견한 점검 글은 변경 순번 0이다.</summary>
    public static NewsEvent ToEvent(NewsPostState? previous, PostObservationResult result, NewsListCard card, DateTimeOffset detectedAt)
    {
        var kind = result.Kind ?? throw new ArgumentException("알림을 만들지 않는 변화입니다.", nameof(result));
        return new NewsEvent(NewsBoards.MaintenanceEventIdFor(card.PostId, result.Next.Revision, kind), NewsBoards.Notice.Id,
            card.PostId.ToString(System.Globalization.CultureInfo.InvariantCulture), card.Title, NewsBoards.Notice.PostUrl(card.PostId),
            card.Category, card.ThumbnailUrl, detectedAt, kind, previous?.Title, previous?.Category, result.Next.Revision);
    }

    [GeneratedRegex(@"(?:정기|임시|긴급)\s*점검\s*안내")]
    private static partial Regex MaintenanceTitleRegex();
}
