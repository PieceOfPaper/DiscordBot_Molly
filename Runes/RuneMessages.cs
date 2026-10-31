using Discord;

namespace Molly.Runes;

/// <summary>
/// /룬 검색 결과 Embed. 룬 하나가 Embed 하나이며 등급별 색, 부위·등급 이모지, 클래스 이모지로 구분합니다.
/// Discord 제한(메시지당 Embed 10개·합계 6,000자)에 맞춰 여러 메시지로 나누고,
/// 메시지가 <see cref="MaxMessages"/>개를 넘으면 이름 목록 Embed와 전체 결과 파일로 대신합니다.
/// </summary>
public static class RuneMessages
{
    public const int MaxMessages = 5;
    public const int MaxEmbedsPerMessage = 10;
    // Discord 합계 한도 6,000자에서 여유를 둡니다.
    public const int MaxCharactersPerMessage = 5500;
    private const int MaxDescriptionLength = 4000;

    public static readonly Color Legendary = new(0xF39C12);
    public static readonly Color Mythic = new(0xF1C40F);

    public static string GradeEmoji(RuneGrade grade) => grade == RuneGrade.Mythic ? "🟨" : "🟧";

    public static string CategoryEmoji(RuneCategory category) => category switch
    {
        RuneCategory.Weapon => "⚔️",
        RuneCategory.Armor => "🛡️",
        RuneCategory.Emblem => "🔰",
        RuneCategory.Accessory => "💍",
        _ => "🔹",
    };

    public static string Header(string keyword, int count) => $"🔍 **{Format.Sanitize(keyword.Trim())}** 룬 검색 결과 **{count}개**";

    /// <param name="classLabel">클래스 이름을 이모지가 붙은 표기로 바꿉니다(예: 랭킹·배틀과 같은 클래스 이모지).</param>
    /// <param name="footer">마지막 Embed에 붙일 출처 문구(예: 게임 데이터 패치).</param>
    public static Embed Build(RuneData rune, Func<string, string> classLabel, string? footer = null)
    {
        var meta = $"{GradeEmoji(rune.Grade)} **{rune.Grade.Display()}** · {rune.Category.Display()} 룬 · 시즌 {rune.Season}";
        if (rune.Class.Length > 0) meta += $" · {classLabel(rune.Class)} 전용";
        var effect = string.Join("\n", rune.Effect.Split('\n').Select(line => "▸ " + line));
        var description = meta + "\n\n" + effect;
        if (rune.TranscendLevels > 0) description += $"\n-# 수치는 {RuneNames.TranscendLegend(rune.TranscendLevels)} 순서예요.";
        if (description.Length > MaxDescriptionLength) description = description[..(MaxDescriptionLength - 1)] + "…";
        var embed = new EmbedBuilder()
            .WithTitle($"{CategoryEmoji(rune.Category)} {rune.Name}")
            .WithDescription(description)
            .WithColor(rune.Grade == RuneGrade.Mythic ? Mythic : Legendary);
        if (footer is not null) embed.WithFooter(footer);
        return embed.Build();
    }

    /// <summary>메시지별 Embed 묶음. 메시지가 <see cref="MaxMessages"/>개를 넘으면 null입니다.</summary>
    public static IReadOnlyList<IReadOnlyList<Embed>>? BuildPages(IReadOnlyList<RuneData> runes, Func<string, string> classLabel, string? footer = null)
    {
        var pages = new List<IReadOnlyList<Embed>>();
        var page = new List<Embed>();
        var length = 0;
        for (var i = 0; i < runes.Count; i++)
        {
            var embed = Build(runes[i], classLabel, i == runes.Count - 1 ? footer : null);
            var size = embed.Length;
            if (page.Count > 0 && (page.Count == MaxEmbedsPerMessage || length + size > MaxCharactersPerMessage))
            {
                pages.Add(page);
                page = [];
                length = 0;
            }
            page.Add(embed);
            length += size;
        }
        if (page.Count > 0) pages.Add(page);
        return pages.Count <= MaxMessages ? pages : null;
    }

    /// <summary>결과가 너무 많을 때 보내는 이름 목록 Embed. 시즌·부위별로 묶고 전체 효과는 첨부 파일로 안내합니다.</summary>
    public static Embed BuildSummary(string keyword, IReadOnlyList<RuneData> runes, string? footer = null)
    {
        var lines = runes.GroupBy(r => (r.Season, r.Category))
            .Select(g => $"**시즌 {g.Key.Season} · {CategoryEmoji(g.Key.Category)} {g.Key.Category.Display()}** ({g.Count()}개)\n" +
                         string.Join(", ", g.Select(r => r.Grade == RuneGrade.Mythic ? $"{r.Name}({r.Grade.Display()})" : r.Name)));
        var description = "결과가 많아 이름만 보여드려요. 전체 효과는 첨부 파일에서 확인해주세요.\n\n" + string.Join("\n\n", lines);
        if (description.Length > MaxDescriptionLength) description = description[..(MaxDescriptionLength - 1)] + "…";
        var embed = new EmbedBuilder()
            .WithTitle($"🔍 '{keyword.Trim()}' 룬 {runes.Count}개")
            .WithDescription(description)
            .WithColor(Legendary);
        if (footer is not null) embed.WithFooter(footer);
        return embed.Build();
    }
}
