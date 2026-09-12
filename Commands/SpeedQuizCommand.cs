using Discord;
using Discord.Interactions;
using Molly.Quiz;
using Molly.Runes;

namespace DiscordBot_Molly.Commands;

public class SpeedQuizCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("스피드퀴즈종료", "현재 진행 중인 스피드퀴즈를 강제로 종료하고 결과를 표시합니다.")]
    public async Task Stop()
    {
        if (Program.instance.Quizzes.ForceStop(Context.Guild.Id))
            await RespondAsync("🛑 진행 중인 스피드퀴즈를 종료하고 최종 결과를 정리하고 있어요.", ephemeral: false);
        else
            await RespondAsync("현재 진행 중인 스피드퀴즈가 없습니다.", ephemeral: true);
    }

    [SlashCommand("스피드퀴즈", "전용 채널에서 선착순 정답 맞히기 게임을 시작합니다.")]
    public async Task Start(
        [Summary("문제종목", "진행할 문제 종목")]
        [Choice("시즌2룬효과로이름", "시즌2룬효과로이름")]
        [Choice("시즌2무기룬효과로이름", "시즌2무기룬효과로이름")]
        [Choice("시즌2방어구룬효과로이름", "시즌2방어구룬효과로이름")]
        [Choice("시즌2앰블럼룬효과로이름", "시즌2앰블럼룬효과로이름")] string topic,
        [Summary("문제수", "출제할 문제 수 (기본 10개)"), MinValue(1)] int count = 10,
        [Summary("제한시간", "문제 하나당 제한시간 (기본 30초)"), MinValue(1)] int seconds = 30)
    {
        Console.WriteLine($"[스피드퀴즈] 명령 시작 guild={Context.Guild.Id} topic={topic} count={count} seconds={seconds}");
        var selectedTopic = topic switch
        {
            "시즌2룬효과로이름" => QuizTopic.Season2RuneEffect,
            "시즌2무기룬효과로이름" => QuizTopic.Season2WeaponRuneEffect,
            "시즌2방어구룬효과로이름" => QuizTopic.Season2ArmorRuneEffect,
            "시즌2앰블럼룬효과로이름" => QuizTopic.Season2EmblemRuneEffect,
            _ => (QuizTopic?)null
        };
        if (selectedTopic is null)
        {
            await RespondAsync("문제종목을 선택해주세요. 현재 네 가지 시즌2 룬 종목을 지원합니다.", ephemeral: true);
            return;
        }
        if (count < 1 || seconds < 1)
        {
            await RespondAsync("문제수와 제한시간은 최소 1이어야 합니다.", ephemeral: true);
            return;
        }
        // 채널 생성과 권한 확인은 Discord API 왕복이 필요하므로
        // 3초 Interaction 응답 제한을 넘기지 않도록 먼저 즉시 응답합니다.
        await RespondAsync("스피드퀴즈 채널을 준비하고 있어요. 잠시만 기다려주세요.", ephemeral: false);
        Console.WriteLine($"[스피드퀴즈] 초기 응답 완료 guild={Context.Guild.Id}");
        try
        {
            var questions = QuizQuestions.Pick(selectedTopic.Value, Program.instance.Runes.Current.Items, count);
            var result = await Program.instance.Quizzes.StartAsync(Context.Guild.Id, selectedTopic.Value, questions, seconds,
                async ct =>
                {
                    var options = new RequestOptions { CancelToken = ct };
                    ICategoryChannel? category = Context.Guild.CategoryChannels.FirstOrDefault(c => c.Name == "몰리퀴즈");
                    category ??= await Context.Guild.CreateCategoryChannelAsync("몰리퀴즈", options: options);
                    var channel = await Context.Guild.CreateTextChannelAsync($"몰리퀴즈-{MobiTime.now:yyyyMMddHHmm}", p =>
                    {
                        p.CategoryId = category.Id;
                        p.PermissionOverwrites = category.PermissionOverwrites.ToArray();
                    }, options);
                    Console.WriteLine($"[스피드퀴즈] 채널 생성 완료 channel={channel.Id}");
                    return new DiscordQuizRoom(channel);
                },
                async (channelId, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                        await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"스피드퀴즈 채널 <#{channelId}>에 입장해주세요! 해당 채널에서 {SpeedQuizService.StartDelaySeconds}초 뒤에 시작합니다.";
                        m.AllowedMentions = AllowedMentions.None;
                    });
                });
            if (!result.Started)
                await ModifyOriginalResponseAsync(m => m.Content = result.ChannelId == 0
                    ? "기존 퀴즈 준비가 취소되었습니다. 다시 시도해주세요."
                    : $"이 서버에서는 이미 <#{result.ChannelId}>에서 스피드퀴즈를 준비하거나 진행 중입니다.");
            Console.WriteLine($"[스피드퀴즈] 준비 완료 started={result.Started} channel={result.ChannelId}");
        }
        catch (ArgumentException ex)
        {
            await ModifyOriginalResponseAsync(m => m.Content = ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[스피드퀴즈] 시작 실패: {ex.Message}");
            await ModifyOriginalResponseAsync(m => m.Content = "퀴즈를 시작하지 못했어요. 봇의 채널 관리·채널 보기·메시지 보내기 권한을 확인해주세요.");
        }
    }
}

internal sealed class DiscordQuizRoom(ITextChannel channel) : IQuizRoom
{
    public ulong Id => channel.Id;
    public async Task<ulong> SendAsync(string text, CancellationToken ct)
    {
        ulong lastMessageId = 0;
        foreach (var chunk in RuneSearch.SplitMessages(text))
        {
            var message = await channel.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = ct });
            lastMessageId = message.Id;
        }
        return lastMessageId;
    }

    public async Task<ulong> SendEmbedAsync(string title, string description, uint color, CancellationToken ct)
    {
        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(new Color(color))
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
        var message = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None,
            options: new RequestOptions { CancelToken = ct });
        return message.Id;
    }

    public async Task EditEmbedAsync(ulong messageId, string title, string description, uint color, CancellationToken ct)
    {
        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(new Color(color))
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
        await channel.ModifyMessageAsync(messageId, m => m.Embed = embed,
            options: new RequestOptions { CancelToken = ct });
    }
}
