using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class SimpleCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("핑", "지연 확인")]
    public async Task Command_Ping() =>  await RespondAsync($"퐁! (지연: {Context.Client.Latency} ms)");
    
    [SlashCommand("시간", "현재 시간을 출력합니다.")]
    public async Task Command_Time()
    {
        // 한국 시간대 설정
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var dateTimeNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);

        // 포맷 예시: 2025-09-12 15:30:45
        var formatted = dateTimeNow.ToString("yyyy-MM-dd HH:mm:ss");
        await RespondAsync($"현재 시간은 **{formatted}** 입니다 🕒");
    }
}
