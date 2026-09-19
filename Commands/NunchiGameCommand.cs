using Discord;
using Discord.Interactions;
using Molly.Nunchi;

namespace DiscordBot_Molly.Commands;

public sealed class NunchiGameCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("눈치게임", "대상자와 현재 채널에서 즉시 숫자 눈치게임을 시작합니다.")]
    public async Task Start(
        [Summary("대상자", "함께 할 사람들을 모두 멘션하세요. 예: @A @B @C")] string targets,
        [Summary("제한시간", "숫자를 외칠 제한시간(초, 기본 30·최대 120)"), MinValue(1), MaxValue(120)] int seconds = 30)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("이 명령은 서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }
        if (seconds is < 1 or > 120)
        {
            await RespondAsync("제한시간은 1초에서 120초 사이로 입력해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var targetIds = NunchiTargetParser.ParseMentions(targets).Where(id => id != Context.User.Id).ToArray();
        var humanTargets = new List<ulong>();
        foreach (var id in targetIds)
        {
            try
            {
                var user = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, id);
                if (!user.IsBot) humanTargets.Add(id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[눈치게임] 대상자 확인 실패 user={id}: {ex.Message}");
            }
        }
        if (humanTargets.Count == 0)
        {
            await ModifyOriginalResponseAsync(message => message.Content = "대상자에 봇이 아닌 사용자를 한 명 이상 멘션해주세요. 실행자는 자동으로 참가합니다.");
            return;
        }

        var participants = humanTargets.Append(Context.User.Id).Distinct().ToArray();
        var allowedMentions = new AllowedMentions { AllowedTypes = AllowedMentionTypes.None, UserIds = humanTargets };
        var started = Program.instance.NunchiGames.TryStart(Context.Guild.Id, Context.Channel.Id, participants, TimeSpan.FromSeconds(seconds),
            async outcome =>
            {
                var caught = string.Join(" ", outcome.CaughtUserIds.Select(id => $"<@{id}>"));
                await Context.Channel.SendMessageAsync($"🚨 **눈치게임 종료!** {outcome.Reason}\n걸린 사람: {caught}",
                    allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.None, UserIds = outcome.CaughtUserIds.ToList() });
            });
        if (!started)
        {
            await ModifyOriginalResponseAsync(message => message.Content = "이 서버에서는 이미 눈치게임이 진행 중이에요.");
            return;
        }

        var mentions = string.Join(" ", humanTargets.Select(id => $"<@{id}>"));
        await ModifyOriginalResponseAsync(message =>
        {
            message.Content = $"{mentions}\n🎲 **눈치게임 시작!** 참가자는 한 번씩만, 차례대로 `1`부터 숫자를 외쳐주세요. 제한시간은 **{seconds}초**예요.";
            message.AllowedMentions = allowedMentions;
        });
    }
}
