using Discord;

namespace Molly.OfficialNews;

/// <summary>보낼 알림 하나. AttachmentPath가 있으면 그 파일을 첨부하고 Embed 이미지는 attachment://파일명으로 참조한다.</summary>
public sealed record NewsMessage(Embed Embed, string? AttachmentPath);

/// <summary>
/// 썸네일이 없는 글에 쓰는 게시판·분류별 기본 이미지(assets/official_news). 공식 홈페이지 상단 배너의 말머리 이미지를 받아 봇과 함께 배포한다.
/// 이벤트 글은 모두 썸네일이 있어 기본 이미지가 없다.
/// </summary>
public static class OfficialNewsDefaultImages
{
    public static IReadOnlyList<string> FileNames { get; } = ["notice.png", "notice_shop.png", "notice_maintenance.png", "update.png", "devnote_developer.png", "devnote_cm.png"];

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "assets", "official_news");

    public static string? FileName(NewsBoard board, string? category) => board.Id switch
    {
        "notice" => category switch { "주요상품" => "notice_shop.png", "점검" => "notice_maintenance.png", _ => "notice.png" },
        "update" => "update.png",
        "devnote" => category == "CM노트" ? "devnote_cm.png" : "devnote_developer.png",
        _ => null,
    };

    /// <summary>기본 이미지 파일의 절대 경로. 해당 게시판에 기본 이미지가 없거나 파일이 없으면 null.</summary>
    public static string? GetPath(NewsBoard board, string? category)
    {
        if (FileName(board, category) is not { } fileName) return null;
        var path = Path.Combine(DirectoryPath, fileName);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>공식 소식 알림 Embed. 원문 저장값은 그대로 두고 표시값만 Discord 제한에 맞게 줄인다.</summary>
public static class OfficialNewsMessages
{
    public const int MaxTitleLength = 256;
    private const int MaxCategoryLength = 100;
    private const int MaxImageUrlLength = 2048;

    public static string Footer(NewsBoard board) => $"몰리 • 공식 {board.Name} 알림";

    /// <summary>알림 메시지. 글 썸네일이 없으면 게시판·분류별 기본 이미지를 첨부한다.</summary>
    /// <param name="isTest">테스트 출력이면 footer에 표시해 실제 새 글 알림과 구분한다.</param>
    public static NewsMessage Build(NewsBoard board, NewsEvent news, bool isTest = false)
    {
        var image = DisplayableImageUrl(news.ThumbnailUrl);
        string? attachment = null;
        if (image is null && OfficialNewsDefaultImages.GetPath(board, news.Category) is { } path)
        {
            attachment = path;
            image = "attachment://" + Path.GetFileName(path);
        }
        return new NewsMessage(BuildEmbed(board, news, image, isTest), attachment);
    }

    public static Embed BuildEmbed(NewsBoard board, NewsEvent news, bool isTest = false) => Build(board, news, isTest).Embed;

    private static Embed BuildEmbed(NewsBoard board, NewsEvent news, string? imageUrl, bool isTest)
    {
        var builder = new EmbedBuilder()
            .WithAuthor(board.Label)
            .WithTitle(Truncate(news.Title.Trim(), MaxTitleLength))
            .WithUrl(news.Url)
            .WithColor(board.Color)
            .WithFooter(isTest ? Footer(board) + " (테스트)" : Footer(board));
        if (news.Category is { } category)
            builder.AddField("분류", Format.Sanitize(Truncate(category, MaxCategoryLength)), inline: true);
        // 게시 시각이 아니라 몰리가 목록에서 글을 발견한 시각이다.
        builder.AddField("발견 시각", TimeZoneInfo.ConvertTime(news.DetectedAt, MobiTime.timezone).ToString("yyyy-MM-dd HH:mm") + " (KST)", inline: true);
        if (imageUrl is not null)
            builder.WithImageUrl(imageUrl);
        return builder.Build();
    }

    /// <summary>절대 HTTP(S) 주소만 Embed 이미지로 쓴다. 몰리는 이미지를 직접 내려받지 않는다.</summary>
    public static string? DisplayableImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxImageUrlLength) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.AbsoluteUri;
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
