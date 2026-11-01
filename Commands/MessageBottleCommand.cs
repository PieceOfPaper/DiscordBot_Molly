using Discord;
using Discord.Interactions;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public sealed class MessageBottleCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("병속에든쪽지", "병 속에 든 쪽지에서 무작위 메시지를 꺼냅니다.")]
    public async Task OpenAsync()
    {
        var catalog = Program.instance.MessageBottles;
        if (catalog.Data is not { } data)
        {
            await RespondAsync(UnpackDataMessages.NoData(catalog.Name), ephemeral: true);
            return;
        }

        var entry = data.Items[Random.Shared.Next(data.Items.Count)];
        var quotedMessage = string.Join('\n', entry.Message.Split('\n').Select(line => $"> {line}"));
        var embed = new EmbedBuilder()
            .WithDescription(quotedMessage)
            .WithColor(new Color(226, 238, 232))
            .Build();
        await RespondAsync(embed: embed);
    }
}
