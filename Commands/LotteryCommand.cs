using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Molly.Lottery;
using Molly.Nunchi;

namespace DiscordBot_Molly.Commands;

public sealed class LotteryCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("당첨뽑기", "지정한 대상 중 당첨자를 뽑습니다.")]
    public async Task Start(
        [Summary("대상자", "참가할 사용자를 모두 멘션하세요. 실행자도 참가하려면 직접 멘션해야 합니다.")] string targets,
        [Summary("당첨수", "뽑을 당첨자 수 (기본 1, 참가자 수 이하)")] [MinValue(1)] int winnerCount = 1,
        [Summary("제한시간", "뽑기 제한시간(초, 기본 60; 0이면 즉시 전체 공개)")] [MinValue(0)] int seconds = 60)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("이 명령은 서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }
        if (seconds < 0 || winnerCount < 1)
        {
            await RespondAsync("당첨 수는 1 이상, 제한시간은 0초 이상으로 입력해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var targetIds = NunchiTargetParser.ParseMentions(targets);
        var participants = new List<ulong>();
        foreach (var id in targetIds)
        {
            try
            {
                var user = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, id);
                if (!user.IsBot) participants.Add(id);
            }
            catch (Exception ex) { Console.WriteLine($"[당첨뽑기] 대상자 확인 실패 user={id}: {ex.Message}"); }
        }
        if (participants.Count == 0)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "대상자에 봇이 아닌 사용자를 한 명 이상 멘션해주세요. 실행자는 자동으로 참가하지 않아요.");
            return;
        }
        if (winnerCount > participants.Count)
        {
            await ModifyOriginalResponseAsync(m => m.Content = $"당첨 수는 참가자 수({participants.Count}명) 이하로 입력해주세요.");
            return;
        }

        var started = Program.instance.Lotteries.TryStart(Context.Guild.Id, Context.Channel.Id, participants, winnerCount, TimeSpan.FromSeconds(seconds),
            outcome => AnnounceRemainingAsync(Context.Channel, outcome));
        if (!started)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "이 서버에서는 이미 당첨뽑기가 진행 중이에요.");
            return;
        }

        var mentions = string.Join(" ", participants.Select(id => $"<@{id}>"));
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"{mentions}\n🎟️ **당첨뽑기 시작!** 참가자 {participants.Count}명 중 **{winnerCount}명**을 뽑습니다. " +
                (seconds == 0 ? "결과를 바로 공개합니다." : $"**{seconds}초** 안에 아래 **뽑기** 버튼을 눌러 결과를 확인하세요.");
            m.Components = new ComponentBuilder().WithButton("뽑기", "lottery:draw", ButtonStyle.Primary).Build();
            m.AllowedMentions = new AllowedMentions { AllowedTypes = AllowedMentionTypes.None, UserIds = participants };
        });
        if (seconds == 0) await Program.instance.Lotteries.RevealImmediatelyAsync(Context.Guild.Id, "제한시간이 0초로 설정되어 즉시 결과를 공개합니다.");
    }

    [SlashCommand("당첨뽑기종료", "현재 길드의 당첨뽑기를 즉시 종료하고 남은 결과를 공개합니다.")]
    public async Task Stop()
    {
        if (Context.Guild is null) { await RespondAsync("이 명령은 서버 채널에서만 사용할 수 있어요.", ephemeral: true); return; }
        var ended = await Program.instance.Lotteries.EndAsync(Context.Guild.Id, "관리자가 당첨뽑기를 종료했어요.");
        await RespondAsync(ended ? "🛑 당첨뽑기를 종료하고 아직 공개되지 않은 결과를 출력했어요." : "현재 진행 중인 당첨뽑기가 없어요.", ephemeral: !ended);
    }

    [ComponentInteraction("lottery:draw")]
    public async Task Draw()
    {
        if (Context.Guild is null || Context.Interaction is not SocketMessageComponent)
        {
            await RespondAsync("이 버튼은 서버의 당첨뽑기에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }
        var result = await Program.instance.Lotteries.DrawAsync(Context.Guild.Id, Context.Channel.Id, Context.User.Id);
        var message = result switch
        {
            LotteryDrawResult.Winner => $"🎉 <@{Context.User.Id}>님의 뽑기 결과: **당첨!**",
            LotteryDrawResult.NotWinner => $"🎟️ <@{Context.User.Id}>님의 뽑기 결과: 아쉽지만 이번에는 미당첨입니다.",
            LotteryDrawResult.NotParticipant => "이 당첨뽑기의 참가자가 아니에요.",
            LotteryDrawResult.AlreadyDrawn => "이미 뽑기 결과를 확인했어요.",
            _ => "이 당첨뽑기는 이미 종료되었어요."
        };
        await RespondAsync(message, ephemeral: result is not (LotteryDrawResult.Winner or LotteryDrawResult.NotWinner),
            allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.None, UserIds = [Context.User.Id] });
    }

    private static Task AnnounceRemainingAsync(IMessageChannel channel, LotteryOutcome outcome)
    {
        var results = outcome.Results.Count == 0
            ? "모든 참가자가 이미 결과를 확인했어요."
            : string.Join("\n", outcome.Results.Select(x => x.IsWinner
                ? $"🎉 <@{x.UserId}>: **당첨!**"
                : $"🎟️ <@{x.UserId}>: 미당첨"));
        return channel.SendMessageAsync($"⏰ **당첨뽑기 종료** — {outcome.Reason}\n{results}",
            allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.None, UserIds = outcome.Results.Select(x => x.UserId).ToList() });
    }
}
