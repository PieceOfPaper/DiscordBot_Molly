using Discord;
using Discord.Interactions;
using Molly.Quiz;

namespace DiscordBot_Molly.Commands;

public sealed class TrueFalseQuizCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("진혹거퀴즈", "전용 채널에서 룬 이름과 효과의 진실·거짓을 맞히는 게임을 시작합니다.")]
    public async Task Start(
        [Summary("문제종목", "진행할 문제 종목")]
        [Choice("시즌2룬", "시즌2룬")]
        [Choice("시즌2무기룬", "시즌2무기룬")]
        [Choice("시즌2방어구룬", "시즌2방어구룬")]
        [Choice("시즌2앰블럼룬", "시즌2앰블럼룬")]
        [Choice("시즌2장신구룬", "시즌2장신구룬")]
        [Choice("시즌2장신구룬빼고", "시즌2장신구룬빼고")] string topic,
        [Summary("문제수", "출제할 문제 수 (기본 10개)"), MinValue(1)] int count = 10,
        [Summary("제한시간", "문제 하나당 제한시간 (기본 30초)"), MinValue(1)] int seconds = 30)
    {
        var selected = topic switch
        {
            "시즌2룬" => TrueFalseTopic.Season2Rune,
            "시즌2무기룬" => TrueFalseTopic.Season2WeaponRune,
            "시즌2방어구룬" => TrueFalseTopic.Season2ArmorRune,
            "시즌2앰블럼룬" => TrueFalseTopic.Season2EmblemRune,
            "시즌2장신구룬" => TrueFalseTopic.Season2AccessoryRune,
            "시즌2장신구룬빼고" => TrueFalseTopic.Season2NonAccessoryRune,
            _ => (TrueFalseTopic?)null
        };
        if (selected is null || count < 1 || seconds < 1)
        {
            await RespondAsync("문제종목을 선택하고 문제수와 제한시간을 1 이상으로 입력해주세요.", ephemeral: true);
            return;
        }
        await RespondAsync("진혹거퀴즈 채널을 준비하고 있어요. 잠시만 기다려주세요.");
        try
        {
            await Program.instance.Runes.EnsureFreshAsync(TimeSpan.FromMinutes(10));
            var questions = TrueFalseQuestions.Pick(selected.Value, Program.instance.Runes.Current.Items, count);
            var result = await Program.instance.TrueFalseQuizzes.StartAsync(Context.Guild.Id, selected.Value, questions, seconds,
                async ct =>
                {
                    var options = new RequestOptions { CancelToken = ct };
                    ICategoryChannel? category = Context.Guild.CategoryChannels.FirstOrDefault(c => c.Name == "몰리 놀이터");
                    category ??= await Context.Guild.CreateCategoryChannelAsync("몰리 놀이터", options: options);
                    var channel = await Context.Guild.CreateTextChannelAsync($"몰리놀이터-{MobiTime.now:yyyyMMddHHmm}", p =>
                    {
                        p.CategoryId = category.Id;
                        p.PermissionOverwrites = category.PermissionOverwrites.ToArray();
                    }, options);
                    return new DiscordQuizRoom(channel);
                },
                async (channelId, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"진혹거퀴즈 채널 <#{channelId}>에 입장해주세요! 해당 채널에서 {SpeedQuizService.StartDelaySeconds}초 뒤에 시작합니다.";
                        m.AllowedMentions = AllowedMentions.None;
                    });
                }, Context.Channel.Id);
            if (!result.Started)
                await ModifyOriginalResponseAsync(m => m.Content = result.ChannelId == 0 ? "기존 퀴즈 준비가 취소되었습니다. 다시 시도해주세요." : $"이 서버에서는 이미 <#{result.ChannelId}>에서 다른 퀴즈를 준비하거나 진행 중입니다.");
        }
        catch (ArgumentException ex) { await ModifyOriginalResponseAsync(m => m.Content = ex.Message); }
        catch (Exception ex)
        {
            Console.WriteLine($"[진혹거퀴즈] 시작 실패: {ex.Message}");
            await ModifyOriginalResponseAsync(m => m.Content = "퀴즈를 시작하지 못했어요. 봇의 채널 관리·채널 보기·메시지 보내기·반응 추가 권한을 확인해주세요.");
        }
    }
}
