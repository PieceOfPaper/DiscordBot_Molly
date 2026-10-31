using Discord;
using Discord.Interactions;
using Molly.Quiz;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public class ImageQuizCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("이미지퀴즈", "이 채널에 만든 스레드에서 이미지를 보고 정답을 맞히는 게임을 시작합니다.")]
    public async Task Start(
        [Summary("문제종목", "진행할 문제 종목")]
        [Choice("클래스", "클래스")]
        [Choice("NPC", "NPC")] string topic,
        [Summary("문제수", "출제할 문제 수 (기본 10개)"), MinValue(1)] int count = 10,
        [Summary("제한시간", "문제 하나당 제한시간 (기본 30초)"), MinValue(1)] int seconds = 30)
    {
        var selected = topic switch
        {
            "클래스" => ImageQuizTopic.Class,
            "NPC" => ImageQuizTopic.Npc,
            _ => (ImageQuizTopic?)null
        };
        if (selected is null || count < 1 || seconds < 1)
        {
            await RespondAsync("문제종목을 선택하고 문제수와 제한시간을 1 이상으로 입력해주세요.", ephemeral: true);
            return;
        }
        // 클래스는 게임 데이터 클래스 목록과 배포 에셋의 클래스 아이콘, NPC는 NPC·지역 데이터와 받아 둔 NPC 이미지를 씁니다.
        var classes = Program.instance.Classes.Data;
        if (selected == ImageQuizTopic.Class && classes is null)
        {
            await RespondAsync(UnpackDataMessages.NoData(Program.instance.Classes.Name), ephemeral: true);
            return;
        }
        var world = Program.instance.World.Data;
        if (selected == ImageQuizTopic.Npc && world is null)
        {
            await RespondAsync(UnpackDataMessages.NoData(Program.instance.World.Name), ephemeral: true);
            return;
        }
        var portraits = Program.instance.NpcPortraits;
        if (selected == ImageQuizTopic.Npc && portraits.Count == 0)
        {
            await RespondAsync(UnpackDataMessages.NoData(portraits.Name), ephemeral: true);
            return;
        }
        await RespondAsync("이미지퀴즈 스레드를 준비하고 있어요. 잠시만 기다려주세요.", ephemeral: false);
        try
        {
            var questions = QuizQuestions.PickImage(selected.Value, classes?.Items ?? [], world?.Npcs ?? [],
                id => ClassIcons.FromCombatJob(id) is { } iconId ? TryRead(ClassIcons.GetPath(iconId)) : null,
                image => TryRead(portraits.GetPath(image)), count);
            var result = await Program.instance.Quizzes.StartAsync(Context.Guild.Id, "이미지퀴즈",
                $"{topic}: 이미지와 종류·힌트를 보고 이름을 맞혀주세요.", questions, seconds,
                async ct =>
                {
                    return new DiscordQuizRoom(await GameThreads.CreateAsync(Context.Channel, $"이미지퀴즈-{MobiTime.now:yyyyMMddHHmm}", ct));
                },
                async (channelId, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"이미지퀴즈 스레드 <#{channelId}>에서 {SpeedQuizService.StartDelaySeconds}초 뒤에 시작합니다.";
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
            Console.WriteLine($"[이미지퀴즈] 시작 실패: {ex.Message}");
            await ModifyOriginalResponseAsync(m => m.Content = "퀴즈를 시작하지 못했어요. 봇의 공개 스레드 만들기·채널 보기·메시지 보내기·파일 첨부 권한을 확인해주세요.");
        }
    }

    // 캐시 갱신으로 파일이 막 지워졌을 수 있어, 읽지 못하면 그 문제만 건너뜁니다.
    private static byte[]? TryRead(string? path)
    {
        if (path is null) return null;
        try { return File.ReadAllBytes(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
