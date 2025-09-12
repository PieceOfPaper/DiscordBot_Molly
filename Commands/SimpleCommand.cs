using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class SimpleCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("핑", "지연 확인")]
    public async Task Command_Ping() =>  await RespondAsync($"퐁! (지연: {Context.Client.Latency} ms)");
}
