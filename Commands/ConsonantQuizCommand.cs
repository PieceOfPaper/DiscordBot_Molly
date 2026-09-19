using Discord;
using Discord.Interactions;
using Molly.Quiz;

namespace DiscordBot_Molly.Commands;

public class ConsonantQuizCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("자음퀴즈", "이 채널에 만든 스레드에서 자음을 보고 정답을 맞히는 게임을 시작합니다.")]
    public async Task Start(
        [Summary("문제종목", "진행할 문제 종목")]
        [Choice("시즌2룬", "시즌2룬")]
        [Choice("시즌2무기룬", "시즌2무기룬")]
        [Choice("시즌2방어구룬", "시즌2방어구룬")]
        [Choice("시즌2앰블럼룬", "시즌2앰블럼룬")]
        [Choice("시즌2장신구룬", "시즌2장신구룬")]
        [Choice("시즌2장신구룬빼고", "시즌2장신구룬빼고")]
        [Choice("NPC", "NPC")]
        [Choice("클래스", "클래스")]
        [Choice("지역", "지역")]
        [Choice("NPC+클래스+지역", "NPC+클래스+지역")] string topic,
        [Summary("문제수", "출제할 문제 수 (기본 10개)"), MinValue(1)] int count = 10,
        [Summary("제한시간", "문제 하나당 제한시간 (기본 30초)"), MinValue(1)] int seconds = 30)
    {
        var selected = topic switch
        {
            "시즌2룬" => ConsonantTopic.Season2Rune,
            "시즌2무기룬" => ConsonantTopic.Season2WeaponRune,
            "시즌2방어구룬" => ConsonantTopic.Season2ArmorRune,
            "시즌2앰블럼룬" => ConsonantTopic.Season2EmblemRune,
            "시즌2장신구룬" => ConsonantTopic.Season2AccessoryRune,
            "시즌2장신구룬빼고" => ConsonantTopic.Season2NonAccessoryRune,
            "NPC" => ConsonantTopic.Npc,
            "클래스" => ConsonantTopic.Class,
            "지역" => ConsonantTopic.Region,
            "NPC+클래스+지역" => ConsonantTopic.NpcClassRegion,
            _ => (ConsonantTopic?)null
        };
        if (selected is null || count < 1 || seconds < 1)
        {
            await RespondAsync("문제종목을 선택하고 문제수와 제한시간을 1 이상으로 입력해주세요.", ephemeral: true);
            return;
        }
        await RespondAsync("자음퀴즈 스레드를 준비하고 있어요. 잠시만 기다려주세요.", ephemeral: false);
        try
        {
            await Task.WhenAll(
                Program.instance.Runes.EnsureFreshAsync(TimeSpan.FromMinutes(10)),
                Program.instance.Consonants.EnsureFreshAsync(TimeSpan.FromMinutes(10)));
            var questions = QuizQuestions.PickConsonant(selected.Value, Program.instance.Runes.Current.Items, Program.instance.Consonants.Current, count);
            var result = await Program.instance.Quizzes.StartAsync(Context.Guild.Id, "자음퀴즈",
                $"{topic}: 자음과 종류·힌트를 보고 정답을 맞혀주세요.", questions, seconds,
                async ct =>
                {
                    return new DiscordQuizRoom(await GameThreads.CreateAsync(Context.Channel, $"자음퀴즈-{MobiTime.now:yyyyMMddHHmm}", ct));
                },
                async (channelId, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"자음퀴즈 스레드 <#{channelId}>에서 {SpeedQuizService.StartDelaySeconds}초 뒤에 시작합니다.";
                        m.AllowedMentions = AllowedMentions.None;
                    });
                }, Context.Channel.Id);
            if (!result.Started)
                await ModifyOriginalResponseAsync(m => m.Content = result.ChannelId == 0
                    ? "기존 퀴즈 준비가 취소되었습니다. 다시 시도해주세요."
                    : $"이 서버에서는 이미 <#{result.ChannelId}>에서 다른 퀴즈를 준비하거나 진행 중입니다.");
        }
        catch (ArgumentException ex) { await ModifyOriginalResponseAsync(m => m.Content = ex.Message); }
        catch (Exception ex)
        {
            Console.WriteLine($"[자음퀴즈] 시작 실패: {ex.Message}");
            await ModifyOriginalResponseAsync(m => m.Content = "퀴즈를 시작하지 못했어요. 봇의 공개 스레드 만들기·채널 보기·메시지 보내기 권한을 확인해주세요.");
        }
    }
}
