using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public sealed class HelpCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("도움말", "몰리의 명령어를 기능별로 모아 보여줍니다.")]
    public async Task ShowAsync()
    {
        var embed = new EmbedBuilder()
            .WithTitle("📖 몰리 도움말")
            .WithDescription("명령어의 옵션은 채팅창에 `/명령어`를 입력하면 볼 수 있어요.")
            .WithColor(Color.Blue);
        foreach (var category in HelpCatalog.Categories)
            embed.AddField(category.Title, HelpCatalog.FormatCommands(category));
        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }
}
