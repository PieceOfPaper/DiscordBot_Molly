using System.Globalization;
using Discord;
using Discord.Interactions;
using Molly.MagicResistance;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public sealed class MagicResistanceCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const string BelowRequirementMark = "⚠️ 입장 기준 미달";
    public const string EstimateNotice = "커뮤니티 추정식 기준 · 마도저항 효과만 계산하며 다른 장비 효과·버프는 제외합니다.";
    public const string PartyNotice = "입장 기준 미달이어도 파티장이 조건을 충족하면 함께 입장할 수 있어요.";

    [SlashCommand("마도저항확인", "입력한 마도저항으로 어비스 지옥 난이도별 주는·받는 피해 증감을 계산합니다.")]
    public async Task CheckAsync([Summary("마도저항", "내 마도저항 수치 (0 이상, 예: 7200)"), MinValue(0)] int resistance)
    {
        // 난이도별 기준값은 게임 데이터 저장소에서 받습니다.
        var catalog = Program.instance.MagicResistance;
        if (catalog.Data is not { } data)
        {
            await RespondAsync(UnpackDataMessages.NoData(catalog.Name), ephemeral: true);
            return;
        }
        await RespondAsync(embed: BuildEmbed(resistance, data));
    }

    public static Embed BuildEmbed(int resistance, MagicResistanceData data)
    {
        var results = MagicResistanceCalculator.EvaluateAll(resistance, data.AbyssHell);
        var embed = new EmbedBuilder()
            .WithTitle($"마도저항 확인 · {resistance.ToString("N0", CultureInfo.InvariantCulture)}")
            .WithDescription($"{data.AbyssName} 난이도별 마도 압력과 비교한 결과예요.")
            .WithColor(Color.Blue);
        foreach (var result in results)
            embed.AddField(result.Content.Name + (result.BelowRequirement ? " " + BelowRequirementMark : ""),
                $"주는 피해 {MagicResistanceCalculator.FormatPercent(result.OutgoingPercent)} · " +
                $"받는 피해 {MagicResistanceCalculator.FormatPercent(result.IncomingPercent)}");
        var footer = EstimateNotice;
        if (results.Any(r => r.BelowRequirement)) footer += "\n" + PartyNotice;
        return embed.WithFooter(footer).Build();
    }
}
