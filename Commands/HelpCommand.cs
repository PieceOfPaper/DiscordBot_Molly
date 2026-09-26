using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public sealed class HelpCommand : InteractionModuleBase<SocketInteractionContext>
{
    // Choice 값은 HelpCatalog.Topics의 Name과 같아야 합니다(회귀 테스트가 검사).
    [SlashCommand("도움말", "몰리의 명령어를 기능별로 모아 보여주고, 주제를 고르면 자세한 사용법을 알려줍니다.")]
    public async Task ShowAsync(
        [Summary("주제", "자세한 사용법을 볼 컨텐츠 (비우면 전체 명령어 목록)")]
        [Choice("스피드퀴즈", "스피드퀴즈")]
        [Choice("자음퀴즈", "자음퀴즈")]
        [Choice("진혹거퀴즈", "진혹거퀴즈")]
        [Choice("눈치게임", "눈치게임")]
        [Choice("라이어게임", "라이어게임")]
        [Choice("당첨뽑기", "당첨뽑기")]
        [Choice("배틀", "배틀")]
        [Choice("랭킹", "랭킹")]
        [Choice("이벤트", "이벤트")]
        [Choice("해연시세", "해연시세")]
        [Choice("상자·패키지시세", "상자·패키지시세")] string? topic = null)
    {
        var embed = topic is null ? BuildOverview() : HelpCatalog.FindTopic(topic) is { } found ? BuildTopic(found) : null;
        if (embed is null)
        {
            await RespondAsync("알 수 없는 도움말 주제예요. `/도움말`로 주제 목록을 확인해주세요.", ephemeral: true);
            return;
        }
        await RespondAsync(embed: embed, ephemeral: true);
    }

    public static Embed BuildOverview()
    {
        var embed = new EmbedBuilder()
            .WithTitle("📖 몰리 도움말")
            .WithDescription("명령어의 옵션은 채팅창에 `/명령어`를 입력하면 볼 수 있어요.")
            .WithColor(Color.Blue);
        foreach (var category in HelpCatalog.Categories)
            embed.AddField(category.Title, HelpCatalog.FormatCommands(category));
        embed.AddField($"{HelpCatalog.TopicMarker} 상세 도움말", HelpCatalog.FormatTopicGuide());
        return embed.Build();
    }

    public static Embed BuildTopic(HelpTopic topic)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"{topic.Title} 도움말")
            .WithDescription(topic.Summary + "\n관련 명령: " + string.Join(" ", topic.Commands.Select(c => $"`/{c}`")))
            .WithColor(Color.Blue)
            .WithFooter("전체 명령어 목록은 /도움말");
        foreach (var (name, value) in topic.Fields)
            embed.AddField(name, value);
        return embed.Build();
    }
}
