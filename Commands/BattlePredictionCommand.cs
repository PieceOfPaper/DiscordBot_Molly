using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Molly.Currency;
using Molly.Prediction;

namespace DiscordBot_Molly.Commands;

/// <summary>배틀 예측 버튼 처리. 버튼 ID에 예측 ID·진영·금액을 담아 메모리 상태 없이 DB만 보고 판정한다.</summary>
public sealed class BattlePredictionCommand : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction(BattlePredictionMessages.BetButtonPrefix + "*:*:*")]
    public async Task BetAsync(string predictionIdRaw, string sideRaw, string amountRaw)
    {
        if (Context.Guild is null || !ulong.TryParse(predictionIdRaw, CultureInfo.InvariantCulture, out var predictionId)
            || BattlePredictionMessages.ParseSide(sideRaw) is not { } side || !long.TryParse(amountRaw, CultureInfo.InvariantCulture, out var amount))
        {
            await RespondAsync("지금 쓸 수 없는 버튼이에요.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        try
        {
            var result = await Program.instance.Predictions.PlaceBetAsync(Context.Guild.Id, predictionId, Context.User.Id, side, amount, DateTimeOffset.UtcNow);
            await FollowupAsync(BattlePredictionMessages.BetReply(result), ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[배틀 예측] 베팅 처리 실패: " + ex.GetType().Name + ": " + ex.Message);
            await FollowupAsync("예측을 처리하지 못했어요. 잠시 후 다시 눌러주세요.", ephemeral: true);
        }
    }
}

/// <summary>배틀 예측의 버튼·안내 문구. 테스트에서 문구를 검사할 수 있도록 Discord 호출과 분리한다.</summary>
public static class BattlePredictionMessages
{
    public const string BetButtonPrefix = "bp:";
    private const string LabelButtonPrefix = "bp-label:";
    private const int MaxListedWinners = 20;

    public static string SideKey(PredictionSide side) => side == PredictionSide.Challenger ? "A" : "B";

    public static PredictionSide? ParseSide(string key) => key switch
    {
        "A" => PredictionSide.Challenger,
        "B" => PredictionSide.Opponent,
        _ => null,
    };

    /// <summary>
    /// 진영마다 한 줄: 누를 수 없는 진영 이름 버튼 + 금액 버튼. 색은 배틀 로그처럼 도전자 파랑, 상대 빨강.
    /// </summary>
    public static MessageComponent Buttons(ulong predictionId, string challengerName, string opponentName, PredictionRules rules)
    {
        var builder = new ComponentBuilder();
        AddRow(builder, 0, predictionId, PredictionSide.Challenger, challengerName, ButtonStyle.Primary, rules);
        AddRow(builder, 1, predictionId, PredictionSide.Opponent, opponentName, ButtonStyle.Danger, rules);
        return builder.Build();
    }

    private static void AddRow(ComponentBuilder builder, int row, ulong predictionId, PredictionSide side, string name, ButtonStyle style, PredictionRules rules)
    {
        var id = predictionId.ToString(CultureInfo.InvariantCulture);
        var label = name + " 승리";
        builder.WithButton(label.Length > 80 ? label[..80] : label, LabelButtonPrefix + id + ":" + SideKey(side), ButtonStyle.Secondary, disabled: true, row: row);
        foreach (var amount in rules.BetAmounts)
        {
            var count = amount.ToString("N0", CultureInfo.InvariantCulture) + "개";
            // 버튼 글자에는 서버 이모지를 쓸 수 없어 버튼 아이콘으로 붙인다. 이모지가 없으면 풀네임으로 대신한다.
            var button = new ButtonBuilder(MollyToken.Emote is null ? MollyToken.Name + " " + count : count,
                BetButtonPrefix + id + ":" + SideKey(side) + ":" + amount.ToString(CultureInfo.InvariantCulture), style);
            if (MollyToken.Emote is { } emote) button.WithEmote(emote);
            builder.WithButton(button, row);
        }
    }

    /// <summary>준비 메시지 아래에 붙는 예측 안내.</summary>
    public static string Intro(PredictionRules rules)
        => "\n\n🔮 **승부 예측**: 이길 쪽의 버튼을 눌러 " + MollyToken.Label + "를 걸어주세요. 같은 쪽을 다시 누르면 더 걸 수 있어요.\n"
            + "-# 맞히면 전체 판돈을 건 비율대로 나눠 받아요. 증표는 배틀이 끝난 뒤 정산돼요. 한 배틀에 가방의 "
            + (rules.MaxBalanceRatio * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%, 최대 " + MollyToken.Amount(rules.MaxBetAmount)
            + "까지 걸 수 있어요. 배틀 참가자도 예측할 수 있어요.";

    /// <summary>버튼을 누른 사람에게만 보여주는 응답. 본인에게만 보이므로 잔액을 써도 된다.</summary>
    public static string BetReply(PredictionBetResult result) => result.Status switch
    {
        PredictionBetStatus.Placed => result.SideName + "의 승리에 " + MollyToken.Named(result.Amount) + "를 걸었어요."
            + (result.Total > result.Amount ? " (누적 " + MollyToken.Amount(result.Total) + ")" : "")
            + "\n-# 이번 배틀 최대 " + MollyToken.Amount(result.Limit) + " · 증표는 배틀이 끝난 뒤 결과에 따라 정산돼요.",
        PredictionBetStatus.OtherSide => "이미 " + result.SideName + "의 승리에 걸었어요. 건 쪽은 바꿀 수 없어요.",
        PredictionBetStatus.OverLimit when result.Total == 0 => "가방의 " + MollyToken.Label + "가 모자라요. 이번 배틀에는 최대 "
            + MollyToken.Amount(result.Limit) + "까지 걸 수 있어요.\n-# 가방: " + MollyToken.Amount(result.Balance) + " · 출석부에서 매일 받을 수 있어요.",
        PredictionBetStatus.OverLimit => "이번 배틀에는 최대 " + MollyToken.Amount(result.Limit) + "까지 걸 수 있어요. (지금 "
            + MollyToken.Amount(result.Total) + ")",
        PredictionBetStatus.InvalidAmount => "지금 쓸 수 없는 버튼이에요.",
        _ => "예측이 마감되었어요.",
    };

    public const int GaugeCells = 10;

    /// <summary>
    /// 진영 비율 게이지. 버튼 색과 같게 도전자 🟦·상대 🟥 네모 10칸(한 칸 10%)이고, 모바일에서도 한 줄에 들어간다.
    /// 양쪽 모두 걸렸으면 적은 쪽도 최소 1칸은 보이게 한다.
    /// </summary>
    public static string Gauge(int challengerPercent, int opponentPercent)
    {
        var cells = (int)Math.Round(challengerPercent * GaugeCells / 100m, MidpointRounding.AwayFromZero);
        if (challengerPercent > 0 && opponentPercent > 0) cells = Math.Clamp(cells, 1, GaugeCells - 1);
        return string.Concat(Enumerable.Repeat("🟦", cells)) + string.Concat(Enumerable.Repeat("🟥", GaugeCells - cells));
    }

    /// <summary>전투 시작 메시지에 붙는 마감 안내. 실제 수량 대신 진영별 비율과 게이지만 보여준다.</summary>
    public static string Locked(PredictionLockResult result, string challengerName, string opponentName, PredictionRules rules)
    {
        if (PredictionPayout.SharePercent(result.ChallengerTotal, result.OpponentTotal) is not { } share)
            return "\n🔮 예측 마감 · 참여한 사람이 없어요.";
        var (a, b) = share;
        var text = "\n🔮 예측 마감\n" + Gauge(a, b) + "\n" + challengerName + " **" + a + "%** · " + opponentName + " **" + b + "%**";
        return result.InvalidReason switch
        {
            PredictionCancelReason.TooFewPredictors => text + "\n-# 참여 인원이 " + rules.MinPredictorCount + "명보다 적어 이번 예측은 무효예요. 증표는 오가지 않아요.",
            PredictionCancelReason.OneSided => text + "\n-# 한쪽에만 걸려서 이번 예측은 무효예요. 증표는 오가지 않아요.",
            _ => text,
        };
    }

    /// <summary>
    /// 정산·무효 결과. 참여자가 없었으면 null(아무것도 보내지 않음).
    /// 스레드에 공개되므로 적중자별로 받은 총액만 쓴다. 건 양·순이익·잔액은 보유량을 짐작하게 하므로 쓰지 않는다(증표 기획서 3.1 보유량 공개 범위).
    /// </summary>
    public static string? Settlement(PredictionSettlement settlement)
    {
        if (settlement.Entries.Count == 0) return null;
        if (settlement is not { Status: PredictionStatus.Resolved, WinningSide: { } winner })
            return "🔮 " + settlement.CancelReason switch
            {
                PredictionCancelReason.Draw => "무승부라 이번 예측은 무효예요.",
                PredictionCancelReason.Stopped => "배틀이 강제 종료되어 이번 예측은 무효예요.",
                PredictionCancelReason.SettlementFailed => "정산하지 못해 이번 예측은 무효예요.",
                PredictionCancelReason.TooFewPredictors or PredictionCancelReason.OneSided => "이번 예측은 성립하지 않았어요.",
                _ => "배틀이 중단되어 이번 예측은 무효예요.",
            } + " 증표는 오가지 않아요.";

        var winnerName = winner == PredictionSide.Challenger ? settlement.ChallengerName : settlement.OpponentName;
        var winners = settlement.Entries.Where(x => x.Side == winner).OrderByDescending(x => x.Payout).ToList();
        var losers = settlement.Entries.Count - winners.Count;
        var text = new StringBuilder("🔮 **" + winnerName + "**의 승리를 맞힌 " + winners.Count + "명이 판돈 " + MollyToken.Amount(settlement.Pool) + "를 나눠 받았어요.");
        foreach (var entry in winners.Take(MaxListedWinners))
        {
            var payout = entry.Payout ?? 0;
            text.Append("\n• <@").Append(entry.UserId.ToString(CultureInfo.InvariantCulture)).Append("> ").Append(MollyToken.Amount(payout));
        }
        if (winners.Count > MaxListedWinners) text.Append("\n… 외 ").Append(winners.Count - MaxListedWinners).Append("명");
        text.Append("\n-# 틀린 ").Append(losers).Append("명은 건 증표를 잃었어요.");
        return text.ToString();
    }
}
