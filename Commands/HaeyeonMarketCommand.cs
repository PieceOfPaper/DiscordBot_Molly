using Discord;
using Discord.Interactions;
using Molly.HaeyeonMarket;

namespace DiscordBot_Molly.Commands;

public class HaeyeonMarketCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("해연시세모니터링", "이 채널로 해연 제작 아이템·재료 시세 알림을 받도록 등록합니다. (길드당 1채널)")]
    public async Task Command_Register(
        [Summary("장비등락률", "해연 장비 시세가 과거시세보다 몇 % 이상 오르내리면 알릴지 (기본 10, 1~100)")]
        [MinValue(HaeyeonMarketRules.MinChangePercent), MaxValue(HaeyeonMarketRules.MaxChangePercent)] int? productPercent = null,
        [Summary("재료등락률", "재료 시세가 과거시세보다 몇 % 이상 오르내리면 알릴지 (기본 20, 1~100)")]
        [MinValue(HaeyeonMarketRules.MinChangePercent), MaxValue(HaeyeonMarketRules.MaxChangePercent)] int? materialPercent = null)
    {
        if (Context.Interaction.GuildId is not { } guildId || Context.Interaction.ChannelId is not { } channelId)
        {
            await RespondAsync("서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }
        if (Context.Channel is not IMessageChannel)
        {
            await RespondAsync("메시지를 보낼 수 있는 채널에서 등록해 주세요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var monitor = Program.instance.HaeyeonMarket;
        var previous = await monitor.Store.SetChannelAsync(guildId, channelId, DateTimeOffset.UtcNow, productPercent, materialPercent);
        var thresholds = new HaeyeonMonitorChannel(guildId, channelId, DateTimeOffset.UtcNow, productPercent, materialPercent).Thresholds;
        var latest = await monitor.Store.GetLatestCollectedAtAsync();
        var crafts = await monitor.Store.LoadCraftStatesAsync();

        var description = new System.Text.StringBuilder();
        description.AppendLine(previous is null || previous.ChannelId == channelId
            ? $"이 채널(<#{channelId}>)에 해연 시세 알림을 등록했어요."
            : $"알림 채널을 <#{previous.ChannelId}>에서 이 채널(<#{channelId}>)로 옮겼어요. 서버당 한 채널만 받을 수 있어요.");
        description.AppendLine();
        description.AppendLine("매 정각 제작 시트의 해연 제작 아이템과 재료 시세를 저장하고, 다음 경우 알려드려요.");
        description.AppendLine($"- 제작 아이템 시세가 과거시세보다 {Percent(thresholds.Product, productPercent)} 이상, 재료 시세가 {Percent(thresholds.Material, materialPercent)} 이상 변동");
        description.AppendLine("- 완제품 구매가와 재료 구매 합계 중 더 저렴한 쪽이 바뀜");
        if (latest is { } collectedAt)
        {
            var craftCount = crafts.Values.Count(x => x.Advantage == CraftAdvantage.Craft);
            description.AppendLine();
            description.AppendLine($"마지막 갱신 {TimeZoneInfo.ConvertTime(collectedAt, MobiTime.timezone):yyyy-MM-dd HH:mm} 기준, 재료를 사서 제작하는 쪽이 저렴한 아이템은 {crafts.Count}개 중 {craftCount}개예요.");
        }
        else
        {
            description.AppendLine();
            description.AppendLine("아직 저장된 시세가 없어요. 첫 수집 이후부터 변동을 비교해요.");
        }
        if (!Program.instance.MobiLife.IsConfigured)
            description.AppendLine("\n⚠️ 모비라이프 데이터 연동이 꺼져 있어 지금은 시세를 수집하지 못해요. 관리자에게 알려 주세요.");

        var embed = new EmbedBuilder()
            .WithTitle("💹 해연 시세 모니터링 등록")
            .WithDescription(description.ToString().TrimEnd())
            .WithColor(Color.Gold)
            .WithFooter($"몰리 • 해연 시세 모니터링 • 데이터: {monitor.Attribution}")
            .Build();
        await FollowupAsync(embed: embed);
    }

    private static string Percent(decimal rate, int? chosen) => $"{rate * 100:0.#}%" + (chosen is null ? "(기본값)" : "");

    [SlashCommand("해연시세모니터링해제", "이 서버의 해연 시세 알림 등록을 해제합니다.")]
    public async Task Command_Unregister()
    {
        if (Context.Interaction.GuildId is not { } guildId)
        {
            await RespondAsync("서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var removed = await Program.instance.HaeyeonMarket.Store.RemoveChannelAsync(guildId);
        await FollowupAsync(removed is null
            ? "등록된 해연 시세 알림이 없어요."
            : $"<#{removed.ChannelId}> 채널의 해연 시세 알림을 해제했어요.");
    }

    [SlashCommand("해연시세모니터링테스트", "저장된 시세로 해연 시세 알림 예시(시세 변동 또는 유불리 변동 중 무작위)를 이 채널에 보냅니다.")]
    public async Task Command_Test()
    {
        await DeferAsync();
        var monitor = Program.instance.HaeyeonMarket;
        var latest = await monitor.Store.LoadLatestPricesAsync();
        var recipes = HaeyeonMarketRules.SelectRecipes(Program.instance.Crafting.Current);
        // 등록된 서버면 그 서버의 등락률, 아니면 기본값으로 가상 과거시세를 만듭니다.
        var registered = Context.Interaction.GuildId is { } guildId ? await monitor.Store.GetChannelAsync(guildId) : null;
        var thresholds = registered?.Thresholds ?? HaeyeonThresholds.Default;
        var evaluation = latest is null ? null : HaeyeonMarketReport.BuildTestEvaluation(recipes, latest.Prices, Random.Shared, thresholds);
        if (latest is null || evaluation is null)
        {
            await FollowupAsync("아직 저장된 해연 시세가 없어 테스트 알림을 만들 수 없어요. 첫 수집 이후에 다시 시도해 주세요.");
            return;
        }

        var note = evaluation.PriceAlerts.Count > 0
            ? "🧪 알림 테스트예요. 현재시세는 마지막 저장 값이고, 과거시세는 알림 기준을 넘도록 만든 가상 값이에요."
            : "🧪 알림 테스트예요. 마지막 저장 시세로 계산한 현재 유불리를 전환 알림 형식으로 보여드려요.";
        var embeds = HaeyeonMarketMessages.BuildAlertEmbeds(evaluation, latest.CollectedAtUtc, monitor.Attribution, "[테스트] ");
        await FollowupAsync(note, embed: embeds[0]);
        foreach (var embed in embeds.Skip(1))
            await FollowupAsync(embed: embed);
    }

    [SlashCommand("해연시세", "마지막으로 저장한 해연 제작 아이템·재료 시세를 보여줍니다.")]
    public async Task Command_Prices(
        [Summary("종류", "해연무기·해연방어구·해연장신구: 분류별 아이템, 해연재료: 재료, 총해연재료: 아이템별 재료 합계 비교")]
        [Choice("해연무기", "해연무기")]
        [Choice("해연방어구", "해연방어구")]
        [Choice("해연장신구", "해연장신구")]
        [Choice("해연재료", "해연재료")]
        [Choice("총해연재료", "총해연재료")] string kind)
    {
        var view = kind switch
        {
            "해연무기" => HaeyeonPriceView.Weapons,
            "해연방어구" => HaeyeonPriceView.Armors,
            "해연장신구" => HaeyeonPriceView.Accessories,
            "해연재료" => HaeyeonPriceView.Materials,
            "총해연재료" => HaeyeonPriceView.ProductTotals,
            _ => (HaeyeonPriceView?)null,
        };
        if (view is null)
        {
            await RespondAsync("종류는 해연무기, 해연방어구, 해연장신구, 해연재료, 총해연재료 중에서 골라 주세요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var monitor = Program.instance.HaeyeonMarket;
        var latest = await monitor.Store.LoadLatestPricesAsync();
        var recipes = HaeyeonMarketRules.SelectRecipes(Program.instance.Crafting.Current);
        if (latest is null || recipes.Count == 0)
        {
            await FollowupAsync(latest is null ? "아직 저장된 해연 시세가 없어요. 첫 수집 이후에 다시 시도해 주세요." : "제작 시트의 해연 아이템을 불러오지 못했어요.");
            return;
        }

        var count = HaeyeonMarketReport.ItemCount(view.Value, recipes);
        if (count == 0)
        {
            await FollowupAsync($"제작 시트에서 분류가 '{HaeyeonMarketReport.ProductCategory(view.Value)}'인 해연 아이템을 찾지 못했어요.");
            return;
        }

        var lines = HaeyeonMarketReport.BuildLines(view.Value, recipes, latest.Prices);
        var title = $"{HaeyeonMarketReport.Title(view.Value)} · {HaeyeonMarketMessages.Kst(latest.CollectedAtUtc)} 기준 {count}개";
        var embeds = HaeyeonMarketMessages.BuildEmbeds(title, lines, monitor.Attribution, latest.CollectedAtUtc);
        foreach (var embed in embeds)
            await FollowupAsync(embed: embed);
    }
}
