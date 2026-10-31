using System.Globalization;
using Discord;
using Discord.Interactions;
using Molly.Crafting;
using Molly.HaeyeonCost;
using Molly.HaeyeonMarket;
using Molly.MagicResistance;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public sealed class HaeyeonCostCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const string Title = "해연 비용 계산";
    public const string EstimateNotice = "현재 부위별 상태는 비용 효율을 고려한 성장 경로로 추정합니다.";
    public const string OrderNotice = "반지 → 무기 → 방어구·목걸이 순서로 맞추는 것을 기준으로 합니다.";
    public const string PremiseNotice = "10성 룬 전체·초기 해연 세팅 기준이며 실제 장비 상태와 매물에 따라 비용이 달라질 수 있습니다.";
    public static string PremiseDetail(HaeyeonGrowth growth) =>
        $"10성 룬 10개(저항 {N(HaeyeonCostRules.RuneResistance)})와 무기·방어구 5부위·목걸이·반지 2개에 최초 수치 해연을 적용한 {N(growth.InitialResistance)}에서 출발해요. " +
        "엠블럼 해연, 미적용 소지품, 드래케인, 룬 교체, 제작, 골드 수수료는 계산하지 않아요.";

    // 범위 검사와 안내 문구를 직접 하려고 Discord 옵션에 최솟값·최댓값을 두지 않습니다.
    // 지원 범위는 게임 데이터의 해연 장비 마도저항으로 정해지므로 옵션 설명에 숫자 범위를 고정하지 않습니다.
    [SlashCommand("해연비용계산", "현재·목표 마도저항으로 해연 장비 추가 구매·감정에 드는 예상 데카를 계산합니다.")]
    public async Task CalculateAsync(
        [Summary("현재마도저항", "지금 마도저항 (예: 7200)")] int current,
        [Summary("목표마도저항", "목표 마도저항 (예: 7800)")] int target)
    {
        var catalog = Program.instance.MagicResistance;
        if (catalog.Data is not { } data)
        {
            await RespondAsync(UnpackDataMessages.NoData(catalog.Name), ephemeral: true);
            return;
        }
        var crafting = Program.instance.Crafting;
        if (crafting.Data is not { } table)
        {
            await RespondAsync(UnpackDataMessages.NoData(crafting.Name), ephemeral: true);
            return;
        }
        HaeyeonGrowth growth;
        try { growth = HaeyeonGrowth.From(data); }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"[해연비용계산] 게임 데이터가 계산 전제와 맞지 않음: {ex.Message}");
            await RespondAsync("게임 데이터의 해연 장비 수치가 계산 전제와 달라 지금은 계산할 수 없어요. 운영자에게 알려주세요.", ephemeral: true);
            return;
        }
        await DeferAsync();
        var monitor = Program.instance.HaeyeonMarket;
        var latest = await monitor.Store.LoadLatestPricesAsync();
        var response = Build(current, target, growth, table, latest, monitor.Attribution, DateTimeOffset.UtcNow);
        if (response.Embed is { } embed) await FollowupAsync(embed: embed);
        else await FollowupAsync(response.Message);
    }

    public sealed record Response(string? Message, Embed? Embed);

    /// <summary>시세 조회 없이 저장된 캐시(latest)만으로 응답을 만듭니다.</summary>
    public static Response Build(int current, int target, HaeyeonGrowth growth, CraftingTable crafting, HaeyeonLatestPrices? latest, string attribution, DateTimeOffset nowUtc)
    {
        switch (HaeyeonCostPlanner.Validate(growth, current, target))
        {
            case HaeyeonCostInputError.OutOfRange:
                return new($"현재·목표 마도저항은 {N(growth.MinInput)}~{N(growth.MaxInput)} 사이로 입력해 주세요.\n{PremiseDetail(growth)}", null);
            case HaeyeonCostInputError.OffGrid:
                return new($"현재 마도저항 {N(current)}은(는) 계산 전제와 맞지 않아요. 이 계산은 {N(growth.InitialResistance)}에서 " +
                    $"{HaeyeonCostRules.Step} 단위로 오르는 값만 다뤄요(예: {N(growth.InitialResistance + HaeyeonCostRules.Step)}). " +
                    $"다른 룬·장비 구성이 섞였을 수 있어요.\n{PremiseDetail(growth)}", null);
        }

        if (target <= current)
            return new(null, Base()
                .WithDescription($"현재 {N(current)} → 목표 {N(target)}\n\n이미 목표에 도달해 **추가 비용은 0데카**예요.\n\n{OrderNotice}\n{EstimateNotice}\n{PremiseNotice}")
                .Build());

        if (latest is null)
            return new("아직 해연 시세를 준비하지 못했어요. 정각 시세 수집 이후에 다시 시도해 주세요.", null);
        var prices = HaeyeonCostPricing.Average(HaeyeonMarketRules.SelectRecipes(crafting), latest);
        if (!prices.IsComplete)
            return new($"{string.Join("·", prices.Missing.Select(HaeyeonCostRules.Name))}의 유효한 해연 시세가 없어 비용을 추정할 수 없어요(매진·매물 부족 포함). " +
                $"다음 정각 수집 이후에 다시 시도해 주세요.\n-# 데이터: {attribution}", null);

        var plan = HaeyeonCostPlanner.Plan(growth, current, target, prices.Prices.ToDictionary(x => x.Key, x => x.Value.Average));
        var bought = Enum.GetValues<HaeyeonSlot>().Where(plan.Purchases.ContainsKey).ToArray();
        var collected = $"시세 수집: {HaeyeonMarketMessages.Kst(latest.CollectedAtUtc)} KST";
        if (HaeyeonCostPricing.IsStale(latest.CollectedAtUtc, nowUtc))
            collected += " · ⚠️ 최근 수집에 실패해 이전 시세를 사용했어요";

        return new(null, Base()
            .WithDescription($"현재 {N(current)} → 목표 {N(target)}\n예상 도달 저항: {N(plan.ReachedResistance)}\n\n예상 추가 비용: **약 {N(plan.Cost)}데카**")
            .AddField("추가 구매·감정", string.Join("\n", bought.Select(x => $"• {HaeyeonCostRules.Name(x)} {plan.Purchases[x]}개")))
            .AddField("적용 평균 단가", string.Join("\n", bought.Select(x => $"• {HaeyeonCostRules.Name(x)} {N(prices.Prices[x].Average)}데카")))
            .AddField("계산 전제", $"{OrderNotice}\n{EstimateNotice}\n{PremiseNotice}\n\n{collected}")
            .WithFooter($"몰리 • 해연 비용 계산 • 데이터: {attribution}")
            .WithTimestamp(latest.CollectedAtUtc)
            .Build());
    }

    private static EmbedBuilder Base() => new EmbedBuilder().WithTitle(Title).WithColor(Color.Gold);

    private static string N(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);
}
