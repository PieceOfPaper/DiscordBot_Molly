using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class SimpleCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("핑", "지연 확인")]
    public async Task Command_Ping() =>  await RespondAsync($"퐁! (지연: {Context.Client.Latency} ms)");

    [SlashCommand("시간", "현재 시간을 출력합니다.")]
    public async Task Command_Time() => await RespondAsync($"현재 시간은 **{MobiTime.now:yyyy-MM-dd HH:mm:ss}** 입니다 🕒");

    [SlashCommand("홀리몰리", "충격적이다.")]
    public async Task Command_HollyMolly() => await ProcessImageCommand("hollymolly.png");

    [SlashCommand("개발자", "누가 만들었나.")]
    public async Task Command_Developer()  =>  await RespondAsync($"칼릭스 서버 종잇장.");


    private async Task ProcessImageCommand(string fileName)
    {
        await DeferAsync();

        var path = Path.Combine(AppContext.BaseDirectory, "assets", fileName);
        if (!File.Exists(path))
        {
            await FollowupAsync("이미지 파일을 찾을 수 없어요.");
            return;
        }

        await using var fs = File.OpenRead(path);
        var embed = new EmbedBuilder()
            .WithImageUrl($"attachment://{fileName}")
            .WithColor(Color.Blue)
            .Build();
        
        await FollowupWithFileAsync(fs, fileName, embed: embed);
    }
}
