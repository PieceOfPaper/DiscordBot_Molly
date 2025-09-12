using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class SimpleCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("핑", "지연 확인")]
    public async Task Command_Ping() =>  await RespondAsync($"퐁! (지연: {Context.Client.Latency} ms)");

    [SlashCommand("시간", "현재 시간을 출력합니다.")]
    public async Task Command_Time() => await RespondAsync($"현재 시간은 **{MobiTime.now:yyyy-MM-dd HH:mm:ss}** 입니다 🕒");
}
