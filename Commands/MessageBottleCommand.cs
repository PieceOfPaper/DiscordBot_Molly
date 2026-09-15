using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public sealed class MessageBottleCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("병속에든쪽지", "병 속에 든 쪽지에서 무작위 메시지를 꺼냅니다.")]
    public async Task OpenAsync()
    {
        await DeferAsync();
        await Program.instance.MessageBottles.EnsureFreshAsync(TimeSpan.FromMinutes(10));
        var entries = Program.instance.MessageBottles.Current.Items;
        if (entries.Count == 0)
        {
            await ModifyOriginalResponseAsync(message => message.Content = "병 속에 든 쪽지를 아직 준비하지 못했어요. 잠시 후 다시 시도해주세요.");
            return;
        }

        var entry = entries[Random.Shared.Next(entries.Count)];
        var quotedMessage = string.Join('\n', entry.Message.Replace("\r\n", "\n").Split('\n').Select(line => $"> {line}"));
        var embed = new EmbedBuilder()
            .WithDescription(quotedMessage)
            .WithColor(new Color(226, 238, 232))
            .Build();
        await ModifyOriginalResponseAsync(message => message.Embed = embed);
    }
}
