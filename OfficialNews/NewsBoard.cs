using Discord;

namespace Molly.OfficialNews;

/// <summary>
/// 공식 소식 게시판 하나, 또는 게시판 안의 별도 알림 종류(점검). PathSegment는 공식 목록·글 URL의 경로(https://mabinogimobile.nexon.com/News/{PathSegment}/{postId})다.
/// ShowsCategory면 목록의 말머리(공지: 점검·안내…, 에린 노트: 개발자노트·CM노트)를 분류로 보여준다.
/// Source가 있으면 그 게시판 목록 수집에 딸린 알림 종류라 따로 수집하지 않고, 구독·발송 작업만 Id로 나눈다.
/// </summary>
public sealed record NewsBoard(string Id, string Name, string PathSegment, string Label, Color Color, string CommandName, bool ShowsCategory, string? Source = null)
{
    public string ListUrl => $"{NewsBoards.Origin}/News/{PathSegment}";

    /// <summary>수집 상태를 기록하는 게시판 ID. 점검 알림은 공지 게시판의 수집 상태를 함께 쓴다.</summary>
    public string SourceBoard => Source ?? Id;

    public string PostUrl(long postId) => $"{ListUrl}/{postId}";
}

/// <summary>
/// 몰리가 목록에서 새로 발견한 글 하나, 또는 점검 공지의 변화 하나. DetectedAt은 게시·수정 시각이 아니라 몰리가 목록에서 알아챈 시각이다.
/// Kind가 없으면 새 글 알림이고, 점검 알림이면 <see cref="MaintenanceEventKinds"/> 중 하나와 이전 제목·말머리, 글의 변경 순번(Revision)을 함께 둔다.
/// </summary>
public sealed record NewsEvent(string EventId, string Board, string PostId, string Title, string Url,
    string? Category, string? ThumbnailUrl, DateTimeOffset DetectedAt,
    string? Kind = null, string? PreviousTitle = null, string? PreviousCategory = null, int? Revision = null);

public static class NewsBoards
{
    public const string Origin = "https://mabinogimobile.nexon.com";

    public static readonly NewsBoard Notice = new("notice", "공지", "Notice", "📢 공식 공지", new Color(0x3498DB), "공지알림", true);
    public static readonly NewsBoard Update = new("update", "업데이트", "Update", "🛠️ 업데이트", new Color(0x2ECC71), "업데이트알림", false);
    public static readonly NewsBoard Events = new("events", "이벤트", "Events", "🎉 이벤트", new Color(0x9B59B6), "이벤트알림", false);
    public static readonly NewsBoard Devnote = new("devnote", "에린 노트", "Devnote", "📝 에린 노트", new Color(0x1ABC9C), "에린노트알림", true);

    /// <summary>공지 게시판 안의 점검 알림. 게시판이 아니라 수집 대상(<see cref="All"/>)에는 넣지 않는다.</summary>
    public static readonly NewsBoard Maintenance = new("maintenance", "점검", "Notice", "🛠️ 점검", new Color(0xE67E22), "점검알림", true, Source: "notice");

    /// <summary>목록을 수집하는 게시판.</summary>
    public static readonly IReadOnlyList<NewsBoard> All = [Notice, Update, Events, Devnote];

    /// <summary>서버가 구독할 수 있는 알림 종류(게시판 넷 + 점검).</summary>
    public static readonly IReadOnlyList<NewsBoard> Subscribable = [Notice, Maintenance, Update, Events, Devnote];

    public static NewsBoard? Find(string? id) => Subscribable.FirstOrDefault(b => b.Id == id);

    public static string EventIdFor(string board, long postId) => $"mabinogi-mobile:{board}:{postId}:created";

    /// <summary>점검 알림 이벤트 ID. 새 글 eventId(…:created)와 겹치지 않고, 같은 글의 변경은 변경 순번으로 구분한다.</summary>
    public static string MaintenanceEventIdFor(long postId, int revision, string kind) => $"mabinogi-mobile:notice:{postId}:maintenance:{revision}:{kind}";
}
