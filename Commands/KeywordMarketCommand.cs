using Discord;
using Discord.Interactions;
using Molly.KeywordMarket;

namespace DiscordBot_Molly.Commands;

// /상자시세모니터링·/패키지시세모니터링. 두 모니터링은 검색 조건만 다르고 동작은 같습니다.
public class KeywordMarketCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("상자시세모니터링", "이 채널로 거래소 '상자' 아이템 시세·신규·사라짐 알림을 받도록 등록합니다. (길드당 1채널)")]
    public Task Command_BoxRegister() => RegisterAsync(Program.instance.BoxMarket);

    [SlashCommand("상자시세모니터링해제", "이 서버의 상자 시세 알림 등록을 해제합니다.")]
    public Task Command_BoxUnregister() => UnregisterAsync(Program.instance.BoxMarket);

    [SlashCommand("상자시세모니터링테스트", "저장된 시세로 상자 시세 알림 예시(신규·사라짐·시세 변동)를 이 채널에 보냅니다.")]
    public Task Command_BoxTest() => TestAsync(Program.instance.BoxMarket);

    [SlashCommand("패키지시세모니터링", "이 채널로 거래소 '패키지' 아이템 시세·신규·사라짐 알림을 받도록 등록합니다. (길드당 1채널)")]
    public Task Command_PackageRegister() => RegisterAsync(Program.instance.PackageMarket);

    [SlashCommand("패키지시세모니터링해제", "이 서버의 패키지 시세 알림 등록을 해제합니다.")]
    public Task Command_PackageUnregister() => UnregisterAsync(Program.instance.PackageMarket);

    [SlashCommand("패키지시세모니터링테스트", "저장된 시세로 패키지 시세 알림 예시(신규·사라짐·시세 변동)를 이 채널에 보냅니다.")]
    public Task Command_PackageTest() => TestAsync(Program.instance.PackageMarket);

    [SlashCommand("상자시세", "마지막으로 저장한 거래소 '상자' 아이템 시세를 보여줍니다.")]
    public Task Command_BoxPrices() => PricesAsync(Program.instance.BoxMarket);

    [SlashCommand("패키지시세", "마지막으로 저장한 거래소 '패키지' 아이템 시세를 보여줍니다.")]
    public Task Command_PackagePrices() => PricesAsync(Program.instance.PackageMarket);

    private async Task RegisterAsync(KeywordMarketMonitor monitor)
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
        var definition = monitor.Definition;
        var previous = await monitor.Store.SetChannelAsync(guildId, channelId, DateTimeOffset.UtcNow);
        var latest = await monitor.Store.LoadLatestPricesAsync();

        var description = new System.Text.StringBuilder();
        description.AppendLine(previous is null || previous.ChannelId == channelId
            ? $"이 채널(<#{channelId}>)에 {definition.DisplayName} 시세 알림을 등록했어요."
            : $"알림 채널을 <#{previous.ChannelId}>에서 이 채널(<#{channelId}>)로 옮겼어요. 서버당 한 채널만 받을 수 있어요.");
        description.AppendLine();
        description.AppendLine($"매 정각 거래소에서 {definition.SearchDescription}(으)로 검색되는 아이템 시세를 모두 저장하고, 다음 경우 알려드려요.");
        description.AppendLine("- 새로운 아이템이 거래소에 올라옴");
        description.AppendLine($"- 추적하던 아이템이 {KeywordMarketRules.RemovalMissingRuns}회 연속 검색되지 않아 사라짐(판매 기간 종료 등)");
        description.AppendLine($"- 아이템 시세가 과거시세보다 {KeywordMarketRules.ChangeThreshold * 100:0.#}% 이상 변동");
        description.AppendLine();
        description.AppendLine(latest is null
            ? "아직 저장된 시세가 없어요. 첫 수집 시세를 기준으로 삼고, 그다음 수집부터 알려드려요."
            : $"마지막 갱신 {KeywordMarketMessages.Kst(latest.CollectedAtUtc)} 기준으로 {latest.Prices.Count}개 아이템을 추적하고 있어요.");
        if (!Program.instance.MobiLife.IsConfigured)
            description.AppendLine("\n⚠️ 모비라이프 데이터 연동이 꺼져 있어 지금은 시세를 수집하지 못해요. 관리자에게 알려 주세요.");

        var embed = new EmbedBuilder()
            .WithTitle($"{definition.Emoji} {definition.DisplayName} 시세 모니터링 등록")
            .WithDescription(description.ToString().TrimEnd())
            .WithColor(Color.Gold)
            .WithFooter(KeywordMarketMessages.Footer(definition, monitor.Attribution))
            .Build();
        await FollowupAsync(embed: embed);
    }

    private async Task UnregisterAsync(KeywordMarketMonitor monitor)
    {
        if (Context.Interaction.GuildId is not { } guildId)
        {
            await RespondAsync("서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var name = monitor.Definition.DisplayName;
        var removed = await monitor.Store.RemoveChannelAsync(guildId);
        await FollowupAsync(removed is null
            ? $"등록된 {name} 시세 알림이 없어요."
            : $"<#{removed.ChannelId}> 채널의 {name} 시세 알림을 해제했어요.");
    }

    private async Task TestAsync(KeywordMarketMonitor monitor)
    {
        await DeferAsync();
        var definition = monitor.Definition;
        var latest = await monitor.Store.LoadLatestPricesAsync();
        var evaluation = latest is null ? null : KeywordMarketMessages.BuildTestEvaluation(latest.Prices, Random.Shared);
        if (latest is null || evaluation is null)
        {
            await FollowupAsync($"아직 저장된 {definition.DisplayName} 시세가 없어 테스트 알림을 만들 수 없어요. 첫 수집 이후에 다시 시도해 주세요.");
            return;
        }

        const string note = "🧪 알림 테스트예요. 신규·사라짐은 저장된 아이템으로 만든 예시이고, 시세 변동의 현재시세는 마지막 저장 값·과거시세는 알림 기준을 넘도록 만든 가상 값이에요.";
        var embeds = KeywordMarketMessages.BuildAlertEmbeds(definition, evaluation, latest.CollectedAtUtc, monitor.Attribution, "[테스트] ");
        await FollowupAsync(note, embed: embeds[0]);
        foreach (var embed in embeds.Skip(1))
            await FollowupAsync(embed: embed);
    }

    private async Task PricesAsync(KeywordMarketMonitor monitor)
    {
        await DeferAsync();
        var definition = monitor.Definition;
        var latest = await monitor.Store.LoadLatestPricesAsync();
        if (latest is null)
        {
            await FollowupAsync($"아직 저장된 {definition.DisplayName} 시세가 없어요. 첫 수집 이후에 다시 시도해 주세요.");
            return;
        }

        var title = $"{definition.Emoji} {definition.DisplayName} 시세 · {KeywordMarketMessages.Kst(latest.CollectedAtUtc)} 기준 {latest.Prices.Count}개";
        var embeds = KeywordMarketMessages.BuildEmbeds(definition, title, KeywordMarketMessages.BuildPriceLines(latest.Prices), monitor.Attribution, latest.CollectedAtUtc);
        foreach (var embed in embeds)
            await FollowupAsync(embed: embed);
    }
}
