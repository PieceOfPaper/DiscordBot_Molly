using Discord;

namespace Molly.OfficialNews;

/// <summary>
/// 공식 소식 게시판 하나. PathSegment는 공식 목록·글 URL의 경로(https://mabinogimobile.nexon.com/News/{PathSegment}/{postId})다.
/// ShowsCategory면 목록의 말머리(공지: 점검·안내…, 에린 노트: 개발자노트·CM노트)를 분류로 보여준다.
/// </summary>
public sealed record NewsBoard(string Id, string Name, string PathSegment, string Label, Color Color, string CommandName, bool ShowsCategory)
{
    public string ListUrl => $"{NewsBoards.Origin}/News/{PathSegment}";

    public string PostUrl(long postId) => $"{ListUrl}/{postId}";
}

/// <summary>몰리가 목록에서 새로 발견한 글 하나. DetectedAt은 게시 시각이 아니라 몰리가 글을 발견한 시각이다.</summary>
public sealed record NewsEvent(string EventId, string Board, string PostId, string Title, string Url,
    string? Category, string? ThumbnailUrl, DateTimeOffset DetectedAt);

public static class NewsBoards
{
    public const string Origin = "https://mabinogimobile.nexon.com";

    public static readonly NewsBoard Notice = new("notice", "공지", "Notice", "📢 공식 공지", new Color(0x3498DB), "공지알림", true);
    public static readonly NewsBoard Update = new("update", "업데이트", "Update", "🛠️ 업데이트", new Color(0x2ECC71), "업데이트알림", false);
    public static readonly NewsBoard Events = new("events", "이벤트", "Events", "🎉 이벤트", new Color(0x9B59B6), "이벤트알림", false);
    public static readonly NewsBoard Devnote = new("devnote", "에린 노트", "Devnote", "📝 에린 노트", new Color(0x1ABC9C), "에린노트알림", true);

    public static readonly IReadOnlyList<NewsBoard> All = [Notice, Update, Events, Devnote];

    public static NewsBoard? Find(string? id) => All.FirstOrDefault(b => b.Id == id);

    public static string EventIdFor(string board, long postId) => $"mabinogi-mobile:{board}:{postId}:created";
}
