using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Molly.RuneEnhance;
using Molly.Runes;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public sealed class RuneEnhanceCostCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const string Title = "룬 각인 강화 예상 소모량";
    public const string AverageNotice = "성공·실패 확률과 강화 포인트를 반영한 평균값입니다. 실제 소모량은 달라질 수 있습니다.";

    // 단계 범위는 게임 데이터의 최대 단계로 다시 검사합니다(현재 20). 범위 밖이면 보정하지 않고 안내합니다.
    [SlashCommand("룬강화비용", "룬 각인 강화(초월·승급 아님)의 골드·룬의 파편·촉매 평균 소모량을 촉매 사용 여부별로 계산합니다.")]
    public async Task CalculateAsync(
        [Summary("부위", "각인된 룬의 부위")]
        [Choice("무기", "무기")]
        [Choice("방어구", "방어구")]
        [Choice("엠블럼", "엠블럼")]
        [Choice("장신구", "장신구")] string part,
        [Summary("현재강화", "현재 각인 강화 단계 (예: 10)"), MinValue(0), MaxValue(20)] int current,
        [Summary("목표강화", "목표 각인 강화 단계 (예: 15)"), MinValue(0), MaxValue(20)] int target,
        [Summary("강화포인트", "현재 단계의 강화 포인트 진행률 % (소수 첫째 자리까지, 기본 0)"), MinValue(0), MaxValue(100)] double? points = null)
    {
        var catalog = Program.instance.RuneEnhance;
        if (catalog.Current is not { } snapshot)
        {
            await RespondAsync(UnpackDataMessages.NoData(catalog.Name), ephemeral: true);
            return;
        }
        if (RuneEnhanceRules.ParsePart(part) is not { } category || !snapshot.Data.Tables.TryGetValue(category, out var table))
        {
            await RespondAsync("부위는 무기·방어구·엠블럼·장신구 중에서 골라주세요.", ephemeral: true);
            return;
        }
        var input = RuneEnhanceRequest.Validate(table, current, target, points);
        if (input.Request is not { } request)
        {
            await RespondAsync(input.Message, ephemeral: input.IsError);
            return;
        }
        // 정수 포인트 상태 전체를 계산하므로(최대 수백만 상태) 응답을 먼저 미룹니다.
        await DeferAsync();
        var result = await Task.Run(() => RuneEnhanceEstimate.Calculate(snapshot.Data, request));
        await FollowupAsync(embed: BuildEmbed(snapshot.Data, result, snapshot.PatchDisplay));
    }

    public static Embed BuildEmbed(RuneEnhanceData data, RuneEnhanceEstimate result, string patch)
    {
        var request = result.Request;
        var table = data.Tables[request.Category];
        var start = request.StartTenths is { } tenths ? Percent(tenths) : "0% (생략 시 0% 기준)";
        if (request.EffectiveTenths >= 1000) start += " · 확정 강화 대기";
        var embed = new EmbedBuilder()
            .WithTitle(Title)
            .WithDescription($"**시즌 {data.Season} ★{data.Tier} 전설·신화 · {request.Category.Display()}**\n" +
                             $"각인 강화 +{request.Current} → +{request.Target}\n" +
                             $"시작 강화 포인트 {start}")
            .WithColor(Color.Orange);

        embed.AddField("촉매 미사용", Lines(data, result.WithoutCatalyst, catalyst: false), inline: true);
        embed.AddField("촉매 사용", Lines(data, result.WithCatalyst, catalyst: true), inline: true);
        var diff = result.WithCatalyst - result.WithoutCatalyst;
        embed.AddField("촉매 사용 시 차이", $"골드 {Signed(Gold(diff.Gold), "N0")}\n{data.ShardName} {Signed(Material(diff.Shards), "N1")}", inline: true);

        var basis = new StringBuilder();
        basis.Append(result.FirstCatalystLevel is { } first
            ? $"• 촉매 사용은 촉매를 쓸 수 있는 구간(+{first} → +{first + 1}부터)에서 **매번 사용** 기준이에요."
            : "• 이 구간은 촉매를 쓸 수 없어 일반 강화로만 계산했어요.");
        basis.Append("\n• 실패 시 강화 포인트, 성공 시 초과 포인트 이월, 포인트 100% 확정 강화(재료 소모 없음)를 반영했어요.");
        basis.Append($"\n• 각인 강화에 직접 쓰는 골드·{data.ShardName}·촉매만 계산해요(저주 해제·수리·시세 환산 제외).");
        basis.Append("\n• 보유 재료를 뺀 부족분이 아니라 총 예상 소모량이에요.");
        basis.Append($"\n• 게임 데이터 패치 {(patch.Length > 0 ? patch : "알 수 없음")} · 비용표 {table.GroupId}");
        embed.AddField("계산 기준", basis.ToString());
        return embed.WithFooter(AverageNotice).Build();
    }

    private static string Lines(RuneEnhanceData data, RuneEnhanceCost cost, bool catalyst)
    {
        var text = $"골드 {Gold(cost.Gold).ToString("N0", CultureInfo.InvariantCulture)}\n{data.ShardName} {Material(cost.Shards).ToString("N1", CultureInfo.InvariantCulture)}";
        if (catalyst) text += $"\n{data.CatalystName} {Material(cost.Catalysts).ToString("N1", CultureInfo.InvariantCulture)}";
        return text;
    }

    /// <summary>골드는 정수로 반올림합니다.</summary>
    public static decimal Gold(double value) => Math.Round((decimal)value, 0, MidpointRounding.AwayFromZero);

    /// <summary>재료·촉매는 소수 첫째 자리로 반올림합니다.</summary>
    public static decimal Material(double value) => Math.Round((decimal)value, 1, MidpointRounding.AwayFromZero);

    private static string Signed(decimal value, string format)
    {
        if (value == 0) return 0m.ToString(format, CultureInfo.InvariantCulture);
        var text = Math.Abs(value).ToString(format, CultureInfo.InvariantCulture);
        return value < 0 ? "−" + text : "+" + text;
    }

    private static string Percent(long tenths) => (tenths / 10m).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
