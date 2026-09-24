using Discord;

namespace Molly.MobiLife;

/// <summary>
/// 모비라이프 OpenAPI 이용약관의 출처 표기. 모비라이프 데이터를 보여주는 모든 Discord 응답에 붙입니다.
/// </summary>
public static class MobiLifeAttribution
{
    // 약관이 요구하는 문구. 바꾸지 마세요.
    public const string Text = "모비라이프 제공";
    public const string SiteUrl = "https://mabimobi.life/";
    public const string FooterText = "데이터: " + Text + " · mabimobi.life";

    /// <summary>Embed 푸터에 출처를 붙입니다. 기존 푸터가 있으면 뒤에 이어 씁니다.</summary>
    public static EmbedBuilder WithMobiLifeAttribution(this EmbedBuilder embed)
    {
        var existing = embed.Footer?.Text;
        if (!string.IsNullOrEmpty(existing) && existing.Contains(Text, StringComparison.Ordinal))
            return embed;
        return embed.WithFooter(string.IsNullOrEmpty(existing) ? FooterText : $"{existing} • {FooterText}", embed.Footer?.IconUrl);
    }

    /// <summary>일반 텍스트 메시지 끝에 출처 줄을 붙입니다.</summary>
    public static string AppendTo(string message) => $"{message.TrimEnd()}\n-# {FooterText}";
}
