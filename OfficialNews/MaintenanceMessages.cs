using System.Globalization;
using Discord;

namespace Molly.OfficialNews;

/// <summary>
/// 점검 알림(새 점검·제목 변경·완료) Embed. 제목·링크는 원문을 쓰고, 시각은 몰리가 목록에서 알아챈 시각(KST)이라 공지 수정 시각·실제 종료 시각으로 표현하지 않는다.
/// 종료 시각을 따로 추정하거나 게임 서버 접속을 확인했다고 말하지 않는다. 표시만 Discord 제한에 맞게 줄이고 DB 원문은 그대로 둔다.
/// </summary>
public static class MaintenanceMessages
{
    public const string Footer = "몰리 • 점검 알림";
    private const int MaxQuotedTitleLength = 1000;
    private static readonly Color s_Active = new(0xE67E22);
    private static readonly Color s_Completed = new(0x2ECC71);

    public static string Label(string? kind) => kind switch
    {
        MaintenanceEventKinds.TitleChanged => "🛠️ 점검 공지가 변경되었습니다",
        MaintenanceEventKinds.Completed => "✅ 점검 완료 안내",
        _ => "🛠️ 점검 안내",
    };

    /// <param name="isTest">테스트 예시면 작성자·설명·footer에 테스트임을 표시하고 원문 대신 공지 목록으로 연결한다.</param>
    public static NewsMessage Build(NewsEvent change, bool isTest = false)
    {
        var image = OfficialNewsMessages.DisplayableImageUrl(change.ThumbnailUrl);
        string? attachment = null;
        if (image is null && OfficialNewsDefaultImages.GetPath(NewsBoards.Notice, MaintenanceRules.MaintenanceCategory) is { } path)
        {
            attachment = path;
            image = "attachment://" + Path.GetFileName(path);
        }

        var description = new List<string>();
        if (isTest) description.Add("🧪 **테스트 알림이에요.** 실제 점검 공지가 아닌 예시예요.");
        switch (change.Kind)
        {
            case MaintenanceEventKinds.TitleChanged:
                description.Add($"**변경 전:**\n{Quote(change.PreviousTitle ?? "")}\n\n**변경 후:**\n{Quote(change.Title)}");
                break;
            case MaintenanceEventKinds.Completed:
                description.Add("공식 공지의 말머리가 점검 → 완료로 변경되었습니다.");
                break;
        }
        description.Add(isTest ? $"[공식 공지 목록]({change.Url})" : $"[공식 점검 공지 링크]({change.Url})");

        var builder = new EmbedBuilder()
            .WithAuthor(isTest ? "🧪 [테스트] " + Label(change.Kind) : Label(change.Kind))
            .WithTitle(OfficialNewsMessages.Truncate(change.Title.Trim(), OfficialNewsMessages.MaxTitleLength))
            .WithUrl(change.Url)
            .WithDescription(string.Join("\n\n", description))
            .WithColor(change.Kind == MaintenanceEventKinds.Completed ? s_Completed : s_Active)
            .WithFooter(isTest ? Footer + " (테스트)" : Footer)
            .AddField(change.Kind is MaintenanceEventKinds.TitleChanged or MaintenanceEventKinds.Completed ? "변경 확인" : "발견 시각", Kst(change.DetectedAt), inline: true);
        if (image is not null) builder.WithImageUrl(image);
        return new NewsMessage(builder.Build(), attachment);
    }

    /// <summary>`10월 29일 09:50 KST` 형식.</summary>
    public static string Kst(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, MobiTime.timezone).ToString("M월 d일 HH:mm", CultureInfo.InvariantCulture) + " KST";

    /// <summary>테스트용 가상 예시. 실제 감지 기록과 무관하게 지금 시각 기준 다음 목요일 정기점검 제목을 만든다.</summary>
    public static NewsEvent Example(string kind, DateTimeOffset nowUtc)
    {
        var today = TimeZoneInfo.ConvertTime(nowUtc, MobiTime.timezone).Date;
        var thursday = today.AddDays(((int)DayOfWeek.Thursday - (int)today.DayOfWeek + 7) % 7);
        var day = thursday.ToString("M/d", CultureInfo.InvariantCulture) + "(목)";
        var original = $"{day} 정기점검 안내 (06:00 ~ 10:00)";
        var (title, previousTitle, category, previousCategory) = kind switch
        {
            MaintenanceEventKinds.TitleChanged => ($"(연장) {day} 정기점검 안내 (06:00 ~ 11:00)", original, "점검", "점검"),
            MaintenanceEventKinds.Completed => ($"(완료) {day} 정기점검 안내 (06:00 ~ 10:10)", original, "완료", "점검"),
            _ => (original, (string?)null, "점검", (string?)null),
        };
        return new NewsEvent("test:maintenance:" + kind, NewsBoards.Notice.Id, "0", title, NewsBoards.Notice.ListUrl, category, null, nowUtc,
            kind, previousTitle, previousCategory, kind == MaintenanceEventKinds.Created ? 0 : 1);
    }

    private static string Quote(string title) => Format.Sanitize(OfficialNewsMessages.Truncate(title.Trim(), MaxQuotedTitleLength));
}
